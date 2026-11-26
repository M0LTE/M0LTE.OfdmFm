using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>A rendered burst, with what a test needs to know about it.</summary>
internal sealed class TestBurst
{
    // A renderer is not thread-safe and tests run side by side: one for each thread.
    [ThreadStatic]
    private static BurstRenderer? t_renderer;

    private static BurstRenderer Renderer => t_renderer ??= new BurstRenderer();

    public BurstHeader Header { get; private init; }

    /// <summary>The audio, the lead-in's silence first.</summary>
    public float[] Audio { get; private init; } = [];

    /// <summary>Samples of lead-in before the first training sample.</summary>
    public int Lead { get; private init; }

    /// <summary>Samples from the first training sample to the burst's last.</summary>
    public int Length { get; private init; }

    /// <summary>The datagrams a data burst carries.</summary>
    public byte[][] Datagrams { get; private init; } = [];

    /// <summary>A data burst. <paramref name="symbols"/> of 0 picks the fewest the datagrams need.</summary>
    public static TestBurst Data(int sender, int destination, int format, byte[][] datagrams, PayloadPrelude? prelude = null, int burstNumber = 0, int symbols = 0)
    {
        if (destination != 0 && prelude is null)
        {
            prelude = new PayloadPrelude(false, null, false, null);
        }

        if (destination == 0)
        {
            prelude = null;
        }

        var packed = new byte[200_000];
        int length = PayloadCodec.Pack(prelude, [.. datagrams.Select(d => new ReadOnlyMemory<byte>(d))], packed);
        if (length < 0)
        {
            throw new ArgumentException("The datagrams do not fit.");
        }

        int n = symbols > 0 ? symbols : PayloadCodec.SymbolsFor(format, length)!.Value;
        BurstHeader header = BurstHeader.Data(sender, destination, format, n, burstNumber);
        var audio = new float[Renderer.SamplesFor(format, n)];
        Renderer.RenderData(header, packed.AsSpan(0, length), audio);
        return new TestBurst { Header = header, Audio = audio, Lead = Renderer.LeadSamples, Length = audio.Length - Renderer.LeadSamples, Datagrams = datagrams };
    }

    /// <summary>A lone report.</summary>
    public static TestBurst Report(int sender, int destination, int recommended, bool failed, FormatOpening opening)
    {
        BurstHeader header = BurstHeader.Report(sender, destination, recommended, failed);
        var audio = new float[Renderer.ReportSamples(opening)];
        Renderer.RenderReport(header, opening, 0.0, audio);
        return new TestBurst { Header = header, Audio = audio, Lead = Renderer.LeadSamples, Length = audio.Length - Renderer.LeadSamples };
    }

    /// <summary>A datagram of <paramref name="length"/> bytes that no other of the same <paramref name="tag"/> and length equals.</summary>
    public static byte[] Datagram(int tag, int length)
    {
        var d = new byte[length];
        for (int i = 0; i < length; i++)
        {
            d[i] = (byte)((i * 31) + (tag * 17) + 5);
        }

        return d;
    }
}

/// <summary>Where a burst went in a stream.</summary>
/// <param name="Burst">The burst.</param>
/// <param name="Start">The stream index of its first training sample.</param>
internal sealed record Placed(TestBurst Burst, long Start)
{
    /// <summary>The index after the burst's last sample.</summary>
    public long End => Start + Burst.Length;
}

/// <summary>A small in-memory channel: bursts and silence laid end to end, each optionally resampled by a clock offset and scaled, then seeded noise over the lot.</summary>
internal sealed class StreamBuilder
{
    private float[] _samples = new float[1 << 20];

    /// <summary>Samples so far.</summary>
    public int Count { get; private set; }

    /// <summary>The bursts laid down, in order.</summary>
    public List<Placed> Placed { get; } = [];

    /// <summary>Appends silence.</summary>
    public StreamBuilder Silence(double seconds)
    {
        Reserve((int)(seconds * 48000));
        Count += (int)(seconds * 48000);
        return this;
    }

    /// <summary>
    /// Appends a burst whose sender's clock runs <paramref name="ppm"/> parts per million fast: the burst is read along a delay that grows linearly from its first sample. Scaled by <paramref name="gain"/>.
    /// </summary>
    public Placed Add(TestBurst burst, double ppm = 0, double gain = 1.0)
    {
        float[] audio = burst.Audio;
        if (ppm != 0)
        {
            // A little silence after the burst, so that a clock running late reads silence there and not a repeat of the last sample.
            var padded = new float[audio.Length + 300];
            audio.CopyTo(padded, 0);
            double seconds = audio.Length / 48000.0;
            var output = new float[padded.Length];
            DelayResampler.Resample(padded, 0, padded.Length, [0.0, seconds], [0.0, ppm * 1e-6 * seconds], burst.Lead, output);
            audio = output;
        }

        Reserve(audio.Length);
        for (int i = 0; i < audio.Length; i++)
        {
            _samples[Count + i] = (float)(audio[i] * gain);
        }

        var placed = new Placed(burst, Count + burst.Lead);
        Count += audio.Length;
        Placed.Add(placed);
        return placed;
    }

    /// <summary>Adds white noise of standard deviation <paramref name="sigma"/> to samples from <paramref name="from"/> up to <paramref name="to"/>, drawn from a generator seeded by <paramref name="seed"/>.</summary>
    public StreamBuilder Noise(double sigma, int seed, long from = 0, long to = long.MaxValue)
    {
        var rng = new Random(seed);
        long end = Math.Min(to, Count);
        for (long i = from; i < end; i += 2)
        {
            double u = 1.0 - rng.NextDouble();
            double v = rng.NextDouble();
            double r = sigma * Math.Sqrt(-2.0 * Math.Log(u));
            _samples[i] += (float)(r * Math.Cos(2 * Math.PI * v));
            if (i + 1 < end)
            {
                _samples[i + 1] += (float)(r * Math.Sin(2 * Math.PI * v));
            }
        }

        return this;
    }

    /// <summary>The stream so far.</summary>
    public float[] ToArray() => _samples.AsSpan(0, Count).ToArray();

    private void Reserve(int more)
    {
        if (Count + more > _samples.Length)
        {
            Array.Resize(ref _samples, Math.Max(_samples.Length * 2, Count + more));
        }
    }
}

/// <summary>
/// A stream of bursts of mixed formats and lengths from two senders (2 and 3) to a station numbered 1: some for it, some for everyone, some for a third station, with reports in between,
/// each from a clock a little off and at a level of its own, over a floor of seeded noise.
/// </summary>
internal static class MixedStream
{
    /// <summary>The station the stream is for.</summary>
    public const int Me = 1;

    /// <summary>Builds the first <paramref name="count"/> bursts of the stream.</summary>
    public static (float[] Samples, List<Placed> Bursts) Build(int count = 12, int noiseSeed = 21)
    {
        var b = new StreamBuilder().Silence(1.2);
        var plan = new List<(TestBurst Burst, double Ppm, double Gain, double Gap)>
        {
            (TestBurst.Data(2, 1, 12, [TestBurst.Datagram(1, 40)], new PayloadPrelude(true, 100, false, 0x1234), 3), 150, 1.0, 1.1),
            (TestBurst.Data(3, 0, 76, [TestBurst.Datagram(2, 30)], null, 4), -90, 0.6, 1.3),
            (TestBurst.Data(2, 3, (4 * 32) + 8, [TestBurst.Datagram(3, 64)], null, 5), 0, 1.0, 1.0),
            (TestBurst.Report(3, 1, 150, false, FormatOpening.Full), 40, 1.0, 1.2),
            (TestBurst.Data(2, 1, (6 * 32) + 10, [TestBurst.Datagram(4, 50)], null, 6), -60, 0.8, 1.4),
            (TestBurst.Data(3, 1, (5 * 32) + 14, [TestBurst.Datagram(5, 20), TestBurst.Datagram(6, 35)], new PayloadPrelude(false, 120, false, null), 7), 120, 0.7, 1.0),
            (TestBurst.Report(2, 1, 90, true, FormatOpening.UpTo3000Hz), 0, 1.0, 1.5),
            (TestBurst.Data(2, 0, (7 * 32) + 20, [TestBurst.Datagram(7, 25)], null, 8), 30, 0.9, 1.1),
            (TestBurst.Data(3, 2, 12, [TestBurst.Datagram(8, 80)], null, 9), -150, 1.0, 1.2),
            (TestBurst.Data(2, 1, 12, [TestBurst.Datagram(9, 100), TestBurst.Datagram(10, 90)], new PayloadPrelude(true, 80, false, null), 10), 100, 0.5, 1.3),
            (TestBurst.Report(3, 5, 77, false, FormatOpening.UpTo1500Hz), 0, 1.0, 1.0),
            (TestBurst.Data(3, 1, (4 * 32) + 8, [TestBurst.Datagram(11, 45)], null, 11), -30, 0.8, 1.6),
        };
        foreach ((TestBurst burst, double ppm, double gain, double gap) in plan.Take(count))
        {
            b.Add(burst, ppm, gain);
            b.Silence(gap);
        }

        b.Silence(1.0).Noise(0.01, noiseSeed);
        return (b.ToArray(), b.Placed);
    }

    /// <summary>The datagrams a station numbered <see cref="Me"/> should deliver for these bursts, in order: those for it and those for everyone.</summary>
    public static List<(int Sender, bool Everyone, byte[] Data)> Expected(IEnumerable<Placed> bursts)
    {
        var list = new List<(int, bool, byte[])>();
        foreach (Placed p in bursts.OrderBy(p => p.Start))
        {
            BurstHeader h = p.Burst.Header;
            if (h.Kind == BurstKind.Data && (h.Destination == Me || h.Destination == 0))
            {
                list.AddRange(p.Burst.Datagrams.Select(d => (h.Sender, h.Destination == 0, d)));
            }
        }

        return list;
    }

    /// <summary>The calls on the adaptation rules the stream should cause, in order: every data burst heard, and each report for the station.</summary>
    public static List<(bool IsReport, int Sender, int Dest, long End)> ExpectedCalls(IEnumerable<Placed> bursts) =>
        [.. bursts.OrderBy(p => p.Start).Where(p => p.Burst.Header.Kind == BurstKind.Data || p.Burst.Header.Destination == Me)
            .Select(p => (p.Burst.Header.Kind == BurstKind.Report, p.Burst.Header.Sender, p.Burst.Header.Destination, p.End))];
}
