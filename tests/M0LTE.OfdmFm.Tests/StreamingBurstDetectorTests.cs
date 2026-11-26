using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class StreamingBurstDetectorTests
{
    private static readonly Lazy<(JsonElement Root, float[] Samples)> Vectors = new(Load);

    private static (JsonElement, float[]) Load()
    {
        JsonElement root = LinkVectorFile.Load("find_vectors.json");
        string path = Path.Combine(AppContext.BaseDirectory, "Vectors", "find_captures.bin.gz");
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return (root, samples);
    }

    private static IEnumerable<(string Name, float[] Capture)> Captures()
    {
        (JsonElement root, float[] all) = Vectors.Value;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            yield return (c.GetProperty("name").GetString()!, all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray());
        }
    }

    public enum NoiseKind
    {
        White,

        /// <summary>White noise rising 20 dB a decade above 300 Hz, as an FM receiver's audio noise does.</summary>
        Rising,

        /// <summary>White noise confined to the modem's band (about 300 Hz to 8 kHz): the worst case, since all of its energy is where the reference is.</summary>
        InBand,
    }

    /// <summary>A seeded noise source that can be drawn from in blocks of any size, so that minutes of it never exist at once.</summary>
    private sealed class Noise
    {
        private const double RiseAlpha = 48000.0 / (2 * Math.PI * 300);

        private readonly Random _rng;
        private readonly NoiseKind _kind;
        private readonly double[] _state = new double[8];
        private readonly (double B0, double B1, double B2, double A1, double A2)[] _sections;
        private readonly double _scale;
        private double _previous;
        private bool _haveSpare;
        private double _spare;

        /// <summary>A source of the given shape and (roughly) standard deviation; the in-band shape is scaled by its measured gain.</summary>
        public Noise(int seed, NoiseKind kind, double rms = 0.1)
        {
            _rng = new Random(seed);
            _kind = kind;
            _scale = kind switch
            {
                NoiseKind.Rising => rms / Math.Sqrt(((1 + RiseAlpha) * (1 + RiseAlpha)) + (RiseAlpha * RiseAlpha)),
                NoiseKind.InBand => rms / Math.Sqrt(0.3),
                _ => rms,
            };

            _sections = kind == NoiseKind.InBand
                ? [Biquad(300, high: true), Biquad(300, high: true), Biquad(8000, high: false), Biquad(8000, high: false)]
                : [];
        }

        private static (double, double, double, double, double) Biquad(double hz, bool high)
        {
            double w = 2 * Math.PI * hz / 48000;
            double alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            double cos = Math.Cos(w);
            double a0 = 1 + alpha;
            double b0 = high ? (1 + cos) / 2 : (1 - cos) / 2;
            double b1 = high ? -(1 + cos) : 1 - cos;
            return (b0 / a0, b1 / a0, b0 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        private double Gauss()
        {
            if (_haveSpare)
            {
                _haveSpare = false;
                return _spare;
            }

            double u1 = 1 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1));
            _spare = r * Math.Sin(2 * Math.PI * u2);
            _haveSpare = true;
            return r * Math.Cos(2 * Math.PI * u2);
        }

        public void Fill(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                double x = Gauss();
                double y = x;
                if (_kind == NoiseKind.Rising)
                {
                    y = ((1 + RiseAlpha) * x) - (RiseAlpha * _previous);
                    _previous = x;
                }
                else if (_kind == NoiseKind.InBand)
                {
                    for (int k = 0; k < _sections.Length; k++)
                    {
                        (double b0, double b1, double b2, double a1, double a2) = _sections[k];
                        double o = (b0 * y) + _state[k * 2];
                        _state[k * 2] = (b1 * y) - (a1 * o) + _state[(k * 2) + 1];
                        _state[(k * 2) + 1] = (b2 * y) - (a2 * o);
                        y = o;
                    }
                }

                samples[i] = (float)(y * _scale);
            }
        }
    }

    private static float[] StrongBurst(out int symbols, out int lead)
    {
        int format = StandardFormats.MostRobust;
        byte[] datagram = new byte[240];
        new Random(77).NextBytes(datagram);
        byte[] payload = new byte[256];
        int length = PayloadCodec.Pack(null, new[] { new ReadOnlyMemory<byte>(datagram) }, payload);
        symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        var header = BurstHeader.Data(sender: 1, destination: 0, format, symbols, burstNumber: 0);
        var renderer = new BurstRenderer();
        float[] audio = new float[renderer.SamplesFor(format, symbols)];
        renderer.RenderData(header, payload.AsSpan(0, length), audio);
        lead = renderer.LeadSamples;
        return audio;
    }

    private static float[] WeakReport(FormatOpening opening, out int lead)
    {
        var renderer = new BurstRenderer();
        float[] burst = new float[renderer.ReportSamples(opening)];
        renderer.RenderReport(BurstHeader.Report(5, 6, 100, false), opening, 0.0, burst);
        lead = renderer.LeadSamples;
        return burst;
    }

    /// <summary>
    /// Pushes audio in blocks of <paramref name="block"/> samples, or when it is 0 in random sizes (log-uniform from 1 to 40000) from a
    /// seeded source, and returns everything that came out; <paramref name="finish"/> ends the stream and takes the rest.
    /// </summary>
    private static List<DetectedBurst> Run(StreamingBurstDetector d, float[] audio, int block, bool finish = true)
    {
        var got = new List<DetectedBurst>();
        var rng = block == 0 ? new Random(2024) : null;
        int at = 0;
        while (at < audio.Length)
        {
            int n = rng is null ? block : Math.Max(1, (int)Math.Exp(rng.NextDouble() * Math.Log(40000)));
            n = Math.Min(n, audio.Length - at);
            d.Push(audio.AsSpan(at, n));
            at += n;
            while (d.TryTake(out DetectedBurst b))
            {
                got.Add(b);
            }
        }

        if (finish)
        {
            d.Finish();
            while (d.TryTake(out DetectedBurst b))
            {
                got.Add(b);
            }
        }

        return got;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<BurstCandidate>[]> OfflineCache = new();

    /// <summary>What <c>BurstFinder.Find</c> reports at a fixed threshold, for the three openings (cached: the offline form is the slow side).</summary>
    private static List<BurstCandidate>[] Offline(string key, float[] audio, double threshold) =>
        OfflineCache.GetOrAdd($"{key}@{threshold}", k => Enum.GetValues<FormatOpening>().Select(o => BurstFinder.Find(audio, o, out _, fixedThreshold: threshold)).ToArray());

    private static double AssertSameAsOffline(List<DetectedBurst> got, List<BurstCandidate>[] offline, string where, double tolerance = 1e-9)
    {
        double worst = 0;
        foreach (FormatOpening o in Enum.GetValues<FormatOpening>())
        {
            List<DetectedBurst> mine = got.Where(g => g.Opening == o).ToList();
            List<BurstCandidate> expected = offline[(int)o];
            mine.Select(g => g.Index).Should().Equal(expected.Select(e => (long)e.Index), $"{where} {o}");
            for (int i = 0; i < mine.Count; i++)
            {
                double diff = Math.Abs(mine[i].Peak - expected[i].Peak);
                worst = Math.Max(worst, diff);
                diff.Should().BeLessThan(tolerance, $"{where} {o} peak {i}");
            }
        }

        return worst;
    }

    private static float[] Concatenated() => Captures().SelectMany(c => c.Capture).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(480)]
    [InlineData(100000)]
    [InlineData(0)]
    public void At_A_Fixed_Threshold_The_Candidates_Are_Those_Of_The_Offline_Finder_In_Every_Capture(int block)
    {
        int checks = 0;
        int candidates = 0;
        double worst = 0;
        foreach (double threshold in new[] { 0.1, 0.04 })
        {
            foreach ((string name, float[] capture) in Captures())
            {
                List<BurstCandidate>[] offline = Offline(name, capture, threshold);
                List<DetectedBurst> got = Run(new StreamingBurstDetector(threshold), capture, block);
                worst = Math.Max(worst, AssertSameAsOffline(got, offline, $"{name} at {threshold} in blocks of {block}"));
                checks++;
                candidates += got.Count;
            }
        }

        checks.Should().Be(14);
        candidates.Should().BeGreaterThan(150);
        TestContext.Current.SendDiagnosticMessage($"blocks of {(block == 0 ? "random sizes" : block)}: {checks} captures, {candidates} candidates, largest peak difference {worst:E2}");
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void The_Same_Holds_Over_A_Stream_Of_Many_Frames(int block)
    {
        // 21 s: every capture end to end, so a burst may sit across a frame edge and a capture join.
        float[] all = Concatenated();
        List<DetectedBurst> got = Run(new StreamingBurstDetector(0.1), all, block);
        List<BurstCandidate>[] offline = Offline("concatenated", all, 0.1);

        // Over a million samples the offline form's running sum of squares, from which it takes the energy under each window by a
        // difference, has drifted by a few parts in 10^9 of a loud burst's energy, which a quiet window after it shows. The streaming
        // energy is summed afresh in every frame. So the peaks are held to 1e-7 of the offline form's, and, to show which side the
        // difference is on, to 1e-10 of a value computed directly from the window.
        double worst = AssertSameAsOffline(got, offline, $"concatenated in blocks of {block}", 1e-7);
        got.Count.Should().BeGreaterThan(30);
        double worstStreaming = 0;
        double worstOffline = 0;
        foreach (DetectedBurst g in got)
        {
            double[] reference = BurstFinder.Reference(g.Opening);
            double dot = 0;
            double energy = 0;
            double referenceEnergy = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                double x = all[(int)g.Index + i];
                dot += x * reference[i];
                energy += x * x;
                referenceEnergy += reference[i] * reference[i];
            }

            double direct = Math.Abs(dot) / Math.Sqrt(Math.Max(energy, 1e-12) * referenceEnergy);
            worstStreaming = Math.Max(worstStreaming, Math.Abs(g.Peak - direct));
            BurstCandidate o = offline[(int)g.Opening].Single(c => c.Index == g.Index);
            worstOffline = Math.Max(worstOffline, Math.Abs(o.Peak - direct));
        }

        worstStreaming.Should().BeLessThan(1e-10);
        TestContext.Current.SendDiagnosticMessage($"21 s, blocks of {(block == 0 ? "random sizes" : block)}: {got.Count} candidates; peak difference from the offline form {worst:E2}; from a direct sum: streaming {worstStreaming:E2}, offline {worstOffline:E2}");
    }

    [Theory]
    [InlineData(4800)]
    [InlineData(0)]
    public void Dense_Peaks_In_Noise_Follow_The_Greedy_Rule_Of_The_Offline_Finder(int block)
    {
        // A threshold far below what anyone would use, on noise confined to the band: hundreds of peaks, many of them closer than a symbol,
        // so the chains of a lower peak beside a removed higher one that the suppression rule makes are all met.
        var noise = new Noise(31, NoiseKind.InBand, 0.05);
        float[] audio = new float[20 * 48000];
        noise.Fill(audio);
        List<DetectedBurst> got = Run(new StreamingBurstDetector(0.06), audio, block);
        List<BurstCandidate>[] offline = Offline("inband20", audio, 0.06);
        double worst = AssertSameAsOffline(got, offline, $"in-band noise in blocks of {block}");
        got.Count.Should().BeGreaterThan(500);
        TestContext.Current.SendDiagnosticMessage($"in-band noise at 0.06, blocks of {(block == 0 ? "random sizes" : block)}: {got.Count} candidates, largest peak difference {worst:E2}");
    }

    [Fact]
    public void Peaks_Closer_Than_A_Symbol_Keep_The_Greedy_Chain_Not_Only_The_Largest_In_Reach()
    {
        // The offline rule removes a peak only when a higher peak that is itself kept lies within a symbol. In the chain 5, 4, 3 spaced
        // 2000 apart the 4 goes (5 is kept and 2000 < 2112) and the 3 stays (its only higher neighbour was removed). The dense noise test
        // meets such chains; this names the case in the offline form's own terms.
        double[] x = new double[10000];
        x[1000] = 0.5;
        x[3000] = 0.4;
        x[5000] = 0.3;
        BurstFinder.Peaks(x, 0.1, 2112).Should().Equal(1000, 5000);
    }

    [Fact]
    public void The_Running_Threshold_On_The_Captures_Pushed_Whole_Finds_What_The_Offline_Finder_Finds()
    {
        // Each capture is 3 s, so the estimate has not started (5 s): the floor of 0.1 applies. The offline rule's own threshold is also 0.1 on every
        // capture (7 times the median is under it), so the candidates are the vectors' own, peaks and all.
        (JsonElement root, float[] all) = Vectors.Value;
        int searches = 0;
        int found = 0;
        int hits = 0;
        int offlineHits = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
            var d = new StreamingBurstDetector();
            List<DetectedBurst> got = Run(d, capture, capture.Length, finish: false);
            d.Finish();
            while (d.TryTake(out DetectedBurst b))
            {
                got.Add(b);
            }

            foreach (FormatOpening o in Enum.GetValues<FormatOpening>())
            {
                d.EstimateWarm(o).Should().BeFalse();
                d.ThresholdOf(o).Should().Be(BurstFinder.DefaultLeast);
            }

            foreach (JsonElement r in c.GetProperty("results").EnumerateArray())
            {
                var opening = (FormatOpening)r.GetProperty("opening").GetInt32();
                var expected = r.GetProperty("found").EnumerateArray().Select(p => (Index: p[0].GetInt32(), Peak: p[1].GetDouble())).ToList();
                List<DetectedBurst> mine = got.Where(g => g.Opening == opening).ToList();
                mine.Select(g => g.Index).Should().Equal(expected.Select(e => (long)e.Index), $"{name} {opening}");
                for (int i = 0; i < mine.Count; i++)
                {
                    Math.Abs(mine[i].Peak - expected[i].Peak).Should().BeLessThan(1e-6, $"{name} {opening} peak {i}");
                }

                searches++;
                found += mine.Count;
            }

            foreach (JsonElement b in c.GetProperty("bursts").EnumerateArray())
            {
                if (name == "two_close_bursts")
                {
                    continue;
                }

                var o = (FormatOpening)b.GetProperty("opening").GetInt32();
                long at = b.GetProperty("first_training_sample").GetInt32();
                bool streaming = got.Any(g => g.Opening == o && Math.Abs(g.Index - at) <= 2);
                bool offlineFound = BurstFinder.Find(capture, o).Any(g => Math.Abs(g.Index - at) <= 2);
                streaming.Should().Be(offlineFound, $"{name} burst at {at} ({o})");
                hits += streaming ? 1 : 0;
                offlineHits += offlineFound ? 1 : 0;
            }
        }

        searches.Should().Be(21);
        found.Should().BeGreaterThan(30);
        hits.Should().Be(offlineHits);
        hits.Should().BeGreaterThan(15);
    }

    [Fact]
    public void The_Running_Threshold_Over_The_Captures_End_To_End_Finds_Every_Offline_Candidate_And_One_More()
    {
        // 21 s, so the estimate is in use for the last 16 s. On every capture the median is so low that 7 times it is under the 0.1 floor, so the
        // running threshold stays at 0.1 and what is found is what the offline finder finds capture by capture. The one extra is a burst the first capture
        // set cuts off at its end (the 6-symbol reference does not fit there, so the offline finder cannot see it) that the next capture's audio completes.
        var expected = new HashSet<(FormatOpening, long)>();
        long offset = 0;
        foreach ((string _, float[] capture) in Captures())
        {
            foreach (FormatOpening o in Enum.GetValues<FormatOpening>())
            {
                foreach (BurstCandidate c in BurstFinder.Find(capture, o))
                {
                    expected.Add((o, offset + c.Index));
                }
            }

            offset += capture.Length;
        }

        var d = new StreamingBurstDetector();
        List<DetectedBurst> got = Run(d, Concatenated(), 4096);
        var gotSet = got.Select(g => (g.Opening, g.Index)).ToHashSet();
        expected.Except(gotSet).Should().BeEmpty();
        gotSet.Except(expected).Should().Equal((FormatOpening.UpTo1500Hz, 278224L));
        foreach (FormatOpening o in Enum.GetValues<FormatOpening>())
        {
            d.EstimateWarm(o).Should().BeTrue();
            d.ThresholdOf(o).Should().Be(BurstFinder.DefaultLeast);
        }
    }

    [Fact]
    public void A_Burst_In_The_First_Five_Seconds_Is_Found_At_The_Floor_And_The_Same_Burst_Later_Is_Not_When_The_Estimate_Stands_Above_It()
    {
        // Noise confined to the band, whose correlation median is high: once 5 s of it have been taken the threshold is 0.175. The same weak
        // report is put in at 2 s and at 9 s; its peak is about 0.16.
        var noise = new Noise(21, NoiseKind.InBand, 0.01);
        float[] audio = new float[14 * 48000];
        noise.Fill(audio);
        float[] report = WeakReport(FormatOpening.Full, out int lead);
        float gain = (float)Math.Pow(10, -45.0 / 20);
        int early = 2 * 48000;
        int late = 9 * 48000;
        foreach (int at in new[] { early, late })
        {
            for (int i = 0; i < report.Length; i++)
            {
                audio[at + i] += report[i] * gain;
            }
        }

        var d = new StreamingBurstDetector();
        var got = new List<DetectedBurst>();
        d.Push(audio.AsSpan(0, 4 * 48000));
        d.EstimateWarm(FormatOpening.Full).Should().BeFalse();
        d.ThresholdOf(FormatOpening.Full).Should().Be(BurstFinder.DefaultLeast);
        while (d.TryTake(out DetectedBurst b))
        {
            got.Add(b);
        }

        got.Should().Contain(g => g.Opening == FormatOpening.Full && Math.Abs(g.Index - (early + lead)) <= 2 && g.Peak > 0.15 && g.Peak < 0.18);

        d.Push(audio.AsSpan(4 * 48000));
        d.Finish();
        while (d.TryTake(out DetectedBurst b))
        {
            got.Add(b);
        }

        d.EstimateWarm(FormatOpening.Full).Should().BeTrue();
        d.ThresholdOf(FormatOpening.Full).Should().BeGreaterThan(0.17);
        got.Where(g => g.Opening == FormatOpening.Full && Math.Abs(g.Index - (late + lead)) <= 2).Should().BeEmpty();

        // It is there: the offline finder at the floor sees it.
        BurstFinder.Find(audio, FormatOpening.Full, out _, fixedThreshold: 0.1).Should().Contain(c => Math.Abs(c.Index - (late + lead)) <= 2);

        // Not a handful of stray candidates: the start-up on noise like this costs candidates, which is the price of having no estimate yet.
        int startup = got.Count(g => g.Opening == FormatOpening.Full && g.Index < 5 * 48000);
        TestContext.Current.SendDiagnosticMessage($"in-band noise: {startup} Full candidates in the first 5 s at the floor, {got.Count(g => g.Opening == FormatOpening.Full && g.Index >= 6 * 48000)} in the 8 s after the estimate took over");
    }

    private sealed record StrongThenWeak(float[] Audio, int StrongAt, int StrongEnd, int WeakAt, int WeakLead);

    private static StrongThenWeak StrongThenWeakScenario()
    {
        // 40 s of white noise, then a long burst about 33 dB over it, then 2 s later a weak report that stands only a little above the noise.
        float[] strong = StrongBurst(out _, out _);
        float[] weak = WeakReport(FormatOpening.Full, out int weakLead);
        int strongAt = 40 * 48000;
        int weakAt = strongAt + strong.Length + (2 * 48000);
        float[] audio = new float[weakAt + weak.Length + 48000];
        new Noise(9, NoiseKind.White, 0.01).Fill(audio);
        for (int i = 0; i < strong.Length; i++)
        {
            audio[strongAt + i] += strong[i];
        }

        float g = (float)Math.Pow(10, -46.5 / 20);
        for (int i = 0; i < weak.Length; i++)
        {
            audio[weakAt + i] += weak[i] * g;
        }

        return new StrongThenWeak(audio, strongAt, strongAt + strong.Length, weakAt, weakLead);
    }

    private static (List<DetectedBurst> Got, double ThresholdBefore, double ThresholdAtWeak) RunStrongThenWeak(StrongThenWeak s, bool pause)
    {
        var d = new StreamingBurstDetector();
        var got = new List<DetectedBurst>();
        void Push(int from, int to)
        {
            for (int a = from; a < to; a += 4800)
            {
                d.Push(s.Audio.AsSpan(a, Math.Min(4800, to - a)));
                while (d.TryTake(out DetectedBurst b))
                {
                    got.Add(b);
                }
            }
        }

        Push(0, s.StrongAt);
        double before = d.ThresholdOf(FormatOpening.Full);
        if (pause)
        {
            d.PauseEstimate();
        }

        Push(s.StrongAt, s.StrongEnd);
        if (pause)
        {
            d.ResumeEstimate();
        }

        Push(s.StrongEnd, s.WeakAt);
        double atWeak = d.ThresholdOf(FormatOpening.Full);
        Push(s.WeakAt, s.Audio.Length);
        d.Finish();
        while (d.TryTake(out DetectedBurst b))
        {
            got.Add(b);
        }

        return (got, before, atWeak);
    }

    [Fact]
    public void A_Strong_Burst_Does_Not_Hide_A_Weak_One_After_It_When_The_Estimate_Is_Paused_For_It()
    {
        StrongThenWeak s = StrongThenWeakScenario();
        (List<DetectedBurst> got, double before, double atWeak) = RunStrongThenWeak(s, pause: true);
        atWeak.Should().Be(before);
        DetectedBurst weak = got.Single(g => g.Opening == FormatOpening.Full && Math.Abs(g.Index - (s.WeakAt + s.WeakLead)) <= 2);
        weak.Peak.Should().BeGreaterThan(atWeak);
        TestContext.Current.SendDiagnosticMessage($"paused: Full threshold {before:F4} before the strong burst and {atWeak:F4} at the weak one, weak peak {weak.Peak:F4}");
    }

    [Fact]
    public void Without_The_Pause_A_Long_Strong_Burst_Lifts_The_Estimate_Over_A_Weak_Burst_After_It()
    {
        // Pinned, so that the pause's reason is on record: the burst's own payload, all in the band the reference lives in, correlates higher than the
        // noise (mostly out of band) does, and 8 s of it among 30 s moves the median. The weak burst here has a peak between the two thresholds.
        StrongThenWeak s = StrongThenWeakScenario();
        (List<DetectedBurst> pausedGot, double before, _) = RunStrongThenWeak(s, pause: true);
        double weakPeak = pausedGot.Single(g => g.Opening == FormatOpening.Full && Math.Abs(g.Index - (s.WeakAt + s.WeakLead)) <= 2).Peak;

        (List<DetectedBurst> got, double beforeUnpaused, double atWeak) = RunStrongThenWeak(s, pause: false);
        beforeUnpaused.Should().Be(before);
        atWeak.Should().BeGreaterThan(before);
        atWeak.Should().BeGreaterThan(weakPeak);
        got.Where(g => g.Opening == FormatOpening.Full && Math.Abs(g.Index - (s.WeakAt + s.WeakLead)) <= 2).Should().BeEmpty();
        TestContext.Current.SendDiagnosticMessage($"unpaused: Full threshold {before:F4} before the strong burst, {atWeak:F4} at the weak one; weak peak {weakPeak:F4} is lost");
    }

    private sealed record NoiseRun(long Samples, int[] Startup, int[] Steady, double[] Threshold);

    /// <summary>Ten minutes or so of noise through the detector with the running threshold; candidates in the first 6 s (5 s of estimate start-up and a second) are counted apart.</summary>
    private static NoiseRun RunNoise(NoiseKind kind, int seed, double seconds)
    {
        var d = new StreamingBurstDetector();
        var noise = new Noise(seed, kind);
        var block = new float[4800];
        var startup = new int[3];
        var steady = new int[3];
        long total = (long)(seconds * 48000);
        for (long done = 0; done < total; done += block.Length)
        {
            noise.Fill(block);
            d.Push(block);
            while (d.TryTake(out DetectedBurst b))
            {
                (b.Index < 6 * 48000 ? startup : steady)[(int)b.Opening]++;
            }
        }

        return new NoiseRun(total, startup, steady, Enum.GetValues<FormatOpening>().Select(d.ThresholdOf).ToArray());
    }

    [Fact]
    public void Receiver_Shaped_Noise_Makes_No_False_Candidates_In_A_Minute_And_White_Noise_Only_On_The_Full_Opening()
    {
        NoiseRun fm = RunNoise(NoiseKind.Rising, 41, 60);
        fm.Startup.Concat(fm.Steady).Should().OnlyContain(n => n == 0);
        NoiseRun white = RunNoise(NoiseKind.White, 42, 60);
        white.Steady[1].Should().Be(0);
        white.Steady[2].Should().Be(0);
        white.Steady[0].Should().BeLessThan(60);
    }

    [Theory]
    [InlineData(NoiseKind.White)]
    [InlineData(NoiseKind.Rising)]
    [InlineData(NoiseKind.InBand)]
    public void The_False_Candidate_Rate_On_Noise_Is_Measured_Over_Twenty_Minutes_Per_Opening(NoiseKind kind)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1 for the twenty-minute noise runs");
        var steady = new int[3];
        var startup = new int[3];
        double seconds = 0;
        foreach (int seed in new[] { 101, 102 })
        {
            NoiseRun r = RunNoise(kind, seed, 600);
            for (int i = 0; i < 3; i++)
            {
                steady[i] += r.Steady[i];
                startup[i] += r.Startup[i];
            }

            seconds += (r.Samples / 48000.0) - 6;
        }

        double[] perHour = steady.Select(n => n * 3600.0 / seconds).ToArray();
        TestContext.Current.SendDiagnosticMessage($"{kind}: steady-state candidates per hour (Full, 3 kHz, 1.5 kHz) {perHour[0]:F0}, {perHour[1]:F0}, {perHour[2]:F0} (counts {string.Join("/", steady)} in {seconds / 60:F1} min); first 6 s of each run: {string.Join("/", startup)}");

        // Bounds, over 19.8 minutes: where nothing was seen, at most 3 (9 an hour, the 95 percent upper limit for none seen in a third of an hour);
        // where something was, twice what the seeded sources gave (so the bound is not the measurement, and a change that doubles the rate fails it).
        // The Full opening in white noise sits at its floor of 0.1 (7 times the median is 0.103), where the correlation of noise reaches by chance;
        // in noise confined to the band the 3 kHz opening's median is higher and its own threshold is 0.11. Noise shaped as an FM receiver's is, rises
        // out of the band and never reaches any threshold.
        int[] bound = kind switch
        {
            NoiseKind.White => [210, 3, 3],
            NoiseKind.Rising => [3, 3, 3],
            _ => [160, 130, 3],
        };
        for (int i = 0; i < 3; i++)
        {
            steady[i].Should().BeLessThanOrEqualTo(bound[i], $"{kind} opening {i}");
        }

        // The first 5 s have only the floor, and noise in the band reaches it often: the price of an estimate that has not started.
        startup.Sum().Should().BeLessThanOrEqualTo(kind == NoiseKind.InBand ? 330 : 10, $"{kind} start-up");
    }

    [Fact]
    public void A_Candidate_Is_Confirmed_Within_A_Symbol_And_A_Frame_Of_Its_Reference_Being_In()
    {
        // The earliest a peak can be known is when its reference and one more symbol have been heard; the transform adds up to one frame step.
        (JsonElement root, float[] all) = Vectors.Value;
        JsonElement c = root.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("name").GetString() == "strong_mixed");
        float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
        var d = new StreamingBurstDetector();
        long worst = 0;
        long best = long.MaxValue;
        int n = 0;
        for (int a = 0; a < capture.Length; a += 480)
        {
            d.Push(capture.AsSpan(a, Math.Min(480, capture.Length - a)));
            while (d.TryTake(out DetectedBurst b))
            {
                long late = d.SamplesPushed - (b.Index + BurstFinder.ReferenceLength(b.Opening));
                worst = Math.Max(worst, late);
                best = Math.Min(best, late);
                n++;
            }
        }

        n.Should().BeGreaterThan(2);
        best.Should().BeGreaterThanOrEqualTo(BurstRenderer.SymbolSamples);
        worst.Should().BeLessThanOrEqualTo(BurstRenderer.SymbolSamples + StreamingBurstDetector.Hop + 480);
        TestContext.Current.SendDiagnosticMessage($"samples pushed past the reference's end when a candidate came out: {best} to {worst} ({worst / 48000.0:F2} s)");
    }

    [Fact]
    public void Pushing_Allocates_Nothing_Once_Warm()
    {
        var d = new StreamingBurstDetector();
        float[] audio = new float[12 * 48000];
        new Noise(7, NoiseKind.InBand, 0.1).Fill(audio);
        void PushAll(int from, int to, int block)
        {
            for (int a = from; a < to; a += block)
            {
                d.Push(audio.AsSpan(a, Math.Min(block, to - a)));
                while (d.TryTake(out _))
                {
                }
            }
        }

        PushAll(0, 4 * 48000, 480);
        AllocationMeasure.Minimum(() =>
        {
            PushAll(4 * 48000, 8 * 48000, 480);
            d.PauseEstimate();
            d.ResumeEstimate();
            PushAll(8 * 48000, 10 * 48000, 100000);
            PushAll(10 * 48000, 12 * 48000, 1);
        }).Should().Be(0);
        d.Available.Should().Be(0);
    }

    [Fact]
    public void The_Estimate_Is_The_Floor_Until_Five_Seconds_Of_Values_And_Then_Seven_Times_The_Median_Of_The_Last_Thirty()
    {
        var t = new RunningThreshold();
        for (int i = 0; i < RunningThreshold.WarmUpValues - 1; i++)
        {
            t.Add(0.5);
        }

        t.Warm.Should().BeFalse();
        t.Threshold.Should().Be(0.1);
        t.Add(0.5);
        t.Warm.Should().BeTrue();
        t.Threshold.Should().BeApproximately(3.5, 1e-3);

        for (int i = 0; i < RunningThreshold.WindowValues; i++)
        {
            t.Add(0.02);
        }

        t.Threshold.Should().BeApproximately(0.14, 1e-3);

        // Thirty seconds on, the 0.02s are gone.
        for (int i = 0; i < RunningThreshold.WindowValues; i++)
        {
            t.Add(0.0001);
        }

        t.Threshold.Should().Be(0.1);
        t.Count.Should().Be(RunningThreshold.WindowValues);
    }

    [Fact]
    public void The_Median_Follows_A_Mixed_Window_Within_A_Bin()
    {
        var rng = new Random(5);
        var t = new RunningThreshold();
        var window = new Queue<double>();
        for (int i = 0; i < 200000; i++)
        {
            double v = Math.Abs((rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.08);
            t.Add(v);
            window.Enqueue(v);
            if (window.Count > RunningThreshold.WindowValues)
            {
                window.Dequeue();
            }

            if (i % 20011 == 0 && window.Count >= RunningThreshold.WarmUpValues)
            {
                double[] sorted = window.Order().ToArray();
                double median = sorted[sorted.Length / 2];
                t.Threshold.Should().BeApproximately(Math.Max(0.1, 7 * median), 7.0 / RunningThreshold.Bins, $"at {i}");
            }
        }
    }

    [Fact]
    public void Pause_Marks_Must_Not_Go_Backwards_And_A_Fixed_Detector_Ignores_Them()
    {
        var d = new StreamingBurstDetector();
        d.PauseEstimate(5000);
        d.Invoking(x => x.ResumeEstimate(4000)).Should().Throw<ArgumentOutOfRangeException>();
        var fixedOne = new StreamingBurstDetector(0.2);
        for (int i = 0; i < 100; i++)
        {
            fixedOne.PauseEstimate(i);
        }

        fixedOne.ThresholdOf(FormatOpening.Full).Should().Be(0.2);
        fixedOne.EstimateWarm(FormatOpening.UpTo1500Hz).Should().BeTrue();
    }

    [Fact]
    public void A_Finished_Stream_Takes_No_More_Audio_And_Finishing_Twice_Is_Harmless()
    {
        var d = new StreamingBurstDetector(0.1);
        d.Push(new float[1000]);
        d.Finish();
        d.Finish();
        d.Invoking(x => x.Push(new float[10])).Should().Throw<InvalidOperationException>();
        d.TryTake(out _).Should().BeFalse();
        d.SamplesPushed.Should().Be(1000);
    }

    [Fact]
    public void The_Frame_Size_Leaves_The_Longest_Reference_Its_Overlap()
    {
        StreamingBurstDetector.TransformSize.Should().Be(32768);
        StreamingBurstDetector.Hop.Should().Be(32768 - BurstFinder.ReferenceLength(FormatOpening.UpTo1500Hz) + 1);
        StreamingBurstDetector.Hop.Should().Be(20097);
    }
}
