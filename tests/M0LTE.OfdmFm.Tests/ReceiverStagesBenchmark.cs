using System.Diagnostics;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Times every stage of the receive path on four bursts and prints the figures; it asserts nothing about time, so it cannot fail on speed. Set SM_BENCH=1 and run it in Release:
/// <c>dotnet run -c Release --project tests/M0LTE.OfdmFm.Tests -- -class M0LTE.OfdmFm.Tests.ReceiverStagesBenchmark</c>
/// (the output goes to the console and, when SM_BENCH_OUT names a file, is appended there). SM_BENCH_REPS sets the timed repeats of each measurement (default 10),
/// SM_BENCH_THREADS the receives at once in the parallel check (default 4), SM_BENCH_SECONDS the audio the streaming detector is timed over (default 60).
/// A published self-contained build of the test project runs the same on a machine with no SDK (<c>./M0LTE.OfdmFm.Tests -class ...</c>).
/// </summary>
public class ReceiverStagesBenchmark
{
    private const int Pad = TrackedReceiverBenchmark.Pad;
    private const int Symbol = BurstRenderer.SymbolSamples;
    private const int Lead = BurstRenderer.DefaultLeadSamples;

    private sealed record Case(string Name, int Format, int Symbols, double Ppm, double Drift, double Snr);

    private static readonly Case[] Cases =
    [
        new("a dense: format 27, 8 bits, full band, 52 symbols", 27, 52, 250, 40, 48),
        new("b mid-rate: format 20, 4 bits, full band, 52 symbols", 20, 52, 250, 40, 40),
        new("c first contact: format 192 (most robust), 1.5 kHz opening, 20 symbols", StandardFormats.FirstContact, 20, -200, 15, 12),
        new("d longest: format 192, 1.5 kHz opening, 252 symbols", StandardFormats.FirstContact, 252, -200, 15, 12),
    ];

    private static int Reps => int.TryParse(Environment.GetEnvironmentVariable("SM_BENCH_REPS"), out int r) && r > 0 ? r : 10;

    private static void Say(string line) => TrackedReceiverBenchmark.Say(line);

    [Fact]
    public void Time_Every_Stage()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_BENCH") != "1", "set SM_BENCH=1 to time every stage of the receive path (prints figures, asserts none)");
        int reps = Math.Max(3, Reps);
        Say($"stages benchmark: {reps} repeats, cores {Environment.ProcessorCount}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {Environment.Version}");
        Say($"load average {File.ReadAllText("/proc/loadavg").Trim()} (before)");

        foreach (Case c in Cases)
        {
            Stages(c, reps);
        }

        Openings(reps);
        Detector();
        Renderer(reps);
        Parallel(reps);
        Say($"load average {File.ReadAllText("/proc/loadavg").Trim()} (after)");
    }

    private static (TestBurst Burst, float[] Capture, int Start, byte[] HeaderBits, byte[] Payload) Make(Case c)
    {
        TestBurst burst = TestBurst.Data(1, 0, c.Format, [TestBurst.Datagram(7, Math.Min(1200, PayloadCodec.Capacity(c.Format, c.Symbols) - 4))], symbols: c.Symbols);
        float[] capture = TrackedReceiverBenchmark.Impair(burst, c.Ppm, c.Drift, c.Snr);
        var headerBits = new byte[BurstHeaderCodec.HeaderBits];
        BurstHeaderCodec.Write(burst.Header, headerBits);
        return (burst, capture, Pad + burst.Lead, headerBits, new byte[PayloadReceiver.PayloadRoom(burst.Header)]);
    }

    private static double Stat(Action action, int reps, out double median)
    {
        action();
        var t = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            t[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(t);
        median = t[reps / 2];
        return t[0];
    }

    private static void Stages(Case c, int reps)
    {
        (TestBurst burst, float[] capture, int start, byte[] headerBits, byte[] payload) = Make(c);
        BurstHeader header = burst.Header;
        double airtime = burst.Length / 48000.0;
        Say($"[{c.Name}] airtime {airtime:F3} s (ppm {c.Ppm}, drift {c.Drift}, snr {c.Snr} dB)");

        // 1. The tracked receive.
        var tracked = new TrackedReceiver();
        PayloadReceiveResult result = tracked.Receive(capture, start, header, headerBits, payload);
        double best = Stat(() => tracked.Receive(capture, start, header, headerBits, payload), reps, out double median);
        int symbolPasses = tracked.SymbolPasses;
        int fine = tracked.Passes - symbolPasses;
        Say($"  tracked: best {best:F1} ms, median {median:F1} ms; ratio to airtime best {best / (airtime * 1000):F3}, median {median / (airtime * 1000):F3}; passes {tracked.Passes} ({symbolPasses} symbol passes, {fine} block-tracker rounds), check {result.CheckPassed}");

        // The same capture's pieces on their own: the settled capture (resampled along the burst's own curve), then each stage.
        int n = PayloadReceiver.SamplesNeeded(header) / Symbol;
        int length = Lead + PayloadReceiver.SamplesNeeded(header);
        var knots = new double[n];
        var delays = new double[n];
        for (int i = 0; i < n; i++)
        {
            knots[i] = ((i * Symbol) + (Symbol / 2.0)) / BurstRenderer.SampleRate;
            delays[i] = -((c.Ppm * 1e-6 * knots[i]) + (c.Drift * 1e-6 * knots[i] * knots[i] / 2));
        }

        var wave = new float[length];
        double resample = Stat(() => DelayResampler.Resample(capture, start - Lead, length, knots, delays, Lead, wave), reps, out _);

        // 2. The plain receive of the settled capture.
        var plain = new PayloadReceiver();
        double plainBest = Stat(() => plain.Receive(wave.AsSpan(0, length), Lead, header, headerBits, payload), reps, out double plainMedian);
        Say($"  plain receive of the settled capture: best {plainBest:F1} ms, median {plainMedian:F1} ms; ratio to airtime best {plainBest / (airtime * 1000):F3}");

        // Its two halves: Prepare (transform, channel, noise, each symbol's delay) and Finish (demapping, decoding, check, measurement).
        double prepare = Stat(() => plain.Prepare(wave.AsSpan(0, length), Lead, header, headerBits, payload.Length), reps, out _);
        double finish = Stat(() =>
        {
            plain.Prepare(wave.AsSpan(0, length), Lead, header, headerBits, payload.Length);
            plain.Finish(header, payload);
        }, reps, out _) - prepare;
        plain.Receive(wave.AsSpan(0, length), Lead, header, headerBits, payload);

        // The block tracker on the settled capture against the rebuilt burst, and the rebuilding.
        double trackerMs = 0;
        double reference = 0;
        if (StandardFormats.Describe(header.Format).ConstellationBits > 2)
        {
            FormatSpec spec = StandardFormats.Describe(header.Format);
            var gridRe = new double[(9 + StandardFormats.MaxSymbols) * BurstRenderer.Carriers];
            var gridIm = new double[gridRe.Length];
            var body = new double[(9 + StandardFormats.MaxSymbols) * Symbol];
            var fftRe = new double[BurstRenderer.FftSize];
            var fftIm = new double[BurstRenderer.FftSize];
            var loading = new byte[BurstRenderer.Carriers];
            var headerCoded = new byte[666];
            var refSamples = new double[length];
            double Build()
            {
                int band = HeaderReader.OpeningCarriers(spec.Opening);
                int known = plain.KnownSymbols;
                double gain = Math.Sqrt((double)spec.Carriers / band);
                BurstGrid.BuildOpening(spec.Opening, headerBits, gain, gridRe, gridIm, loading, headerCoded);
                plain.DecidedReal.CopyTo(gridRe.AsSpan(known * BurstRenderer.Carriers));
                plain.DecidedImaginary.CopyTo(gridIm.AsSpan(known * BurstRenderer.Carriers));
                BurstGrid.Transform(gridRe, gridIm, n, fftRe, fftIm, body);
                refSamples.AsSpan(0, Lead).Clear();
                body.AsSpan(0, n * Symbol).CopyTo(refSamples.AsSpan(Lead));
                return 0;
            }

            reference = Stat(() => Build(), reps, out _);
            var tracker = new BlockTracker(length + Symbol);
            var times = new double[tracker.MaxBlocks];
            var taus = new double[tracker.MaxBlocks];
            trackerMs = Stat(() => tracker.Track(wave.AsSpan(0, length), refSamples.AsSpan(0, length), times, taus), reps, out _);
        }

        // The estimated split of one tracked receive: the pieces times how often each runs (each symbol pass resamples and prepares, the last also finishes; each block-tracker round
        // rebuilds and tracks, resamples and receives in full, and the first also resamples before tracking).
        int resamples = symbolPasses + (fine > 0 ? fine + 1 : 0);
        double estimate = (resamples * resample) + (symbolPasses * prepare) + finish + (fine * (plainBest + reference + trackerMs));
        Say($"  one of each, best: resample {resample:F1} ms, prepare (transform, channel, noise, delay search) {prepare:F1} ms, finish (demap, decode, check, measure) {finish:F1} ms, rebuild reference {reference:F1} ms, block tracker {trackerMs:F1} ms");
        Say($"  estimated split of a tracked receive: resampling {resamples * resample:F0} ms ({resamples}x), delay search and transform {symbolPasses * prepare:F0} ms ({symbolPasses}x), demap/decode/measure {finish + (fine * (plainBest - prepare)):F0} ms, "
            + $"prepare in block-tracker rounds {fine * prepare:F0} ms, reference {fine * reference:F0} ms, block tracker {fine * trackerMs:F0} ms; sum {estimate:F0} ms against {best:F0} measured");
    }

    private static void Openings(int reps)
    {
        foreach ((FormatOpening opening, int format) in new[] { (FormatOpening.Full, 27), (FormatOpening.UpTo3000Hz, 4 * 32), (FormatOpening.UpTo1500Hz, StandardFormats.FirstContact) })
        {
            Case c = new("opening", format, 8, 0, 0, 40);
            (TestBurst burst, float[] capture, int start, _, _) = Make(c);
            var reader = new HeaderReader();
            bool ok = reader.TryRead(capture, start, opening, out BurstHeader h);
            double best = Stat(() => reader.TryRead(capture, start, opening, out _), Math.Max(reps, 20), out double median);
            Say($"[header reader, opening {opening}] opening {HeaderReader.OpeningSamples(opening) / 48.0:F1} ms of audio ({HeaderReader.TrainingSymbolCount(opening)} training + {HeaderReader.HeaderSymbolCount(opening)} header symbols), read {ok} (format {(ok ? h.Format : -1)}): best {best:F2} ms, median {median:F2} ms");
        }
    }

    private static void Detector()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("SM_BENCH_SECONDS"), out int s) && s > 0 ? s : 60;
        var detector = new StreamingBurstDetector();
        var rng = new Random(4242);
        var chunk = new float[4800];
        void Feed(int samples)
        {
            for (int done = 0; done < samples; done += chunk.Length)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    double u1 = 1.0 - rng.NextDouble();
                    chunk[i] = (float)(0.02 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble()));
                }

                detector.Push(chunk);
                while (detector.TryTake(out _))
                {
                }
            }
        }

        Feed(48000 * 10);

        // Pre-make the audio so only the detector is timed.
        int total = 48000 * seconds;
        var audio = new float[total];
        for (int i = 0; i < total; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            audio[i] = (float)(0.02 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        }

        TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        long t0 = Stopwatch.GetTimestamp();
        for (int at = 0; at < total; at += chunk.Length)
        {
            detector.Push(audio.AsSpan(at, Math.Min(chunk.Length, total - at)));
            while (detector.TryTake(out _))
            {
            }
        }

        double wall = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        Say($"[streaming burst detector, all three openings] {seconds} s of noise after a 10 s warm-up, {detector.FramesProcessed} frames: {wall / seconds:F1} ms of wall time and {cpu / seconds:F1} ms of process CPU per second of audio ({wall / (seconds * 1000.0):P2} of one core)");
    }

    private static void Renderer(int reps)
    {
        foreach (Case c in new[] { Cases[0], Cases[3] })
        {
            BurstHeader header = BurstHeader.Data(1, 0, c.Format, c.Symbols, 0);
            var packed = new byte[200_000];
            int bytes = PayloadCodec.Pack(null, [new ReadOnlyMemory<byte>(TestBurst.Datagram(7, Math.Min(1200, PayloadCodec.Capacity(c.Format, c.Symbols) - 4)))], packed);
            var renderer = new BurstRenderer();
            var samples = new float[renderer.SamplesFor(c.Format, c.Symbols)];
            renderer.RenderData(header, packed.AsSpan(0, bytes), samples);
            double best = Stat(() => renderer.RenderData(header, packed.AsSpan(0, bytes), samples), reps, out double median);
            double airtime = (samples.Length - renderer.LeadSamples) / 48000.0;
            Say($"[burst renderer, {c.Name}] airtime {airtime:F3} s: best {best:F1} ms, median {median:F1} ms; ratio to airtime best {best / (airtime * 1000):F4}");
        }
    }

    private static void Parallel(int reps)
    {
        int threads = int.TryParse(Environment.GetEnvironmentVariable("SM_BENCH_THREADS"), out int t) && t > 0 ? t : 4;
        Case c = Cases[0];
        (TestBurst burst, float[] capture, int start, byte[] headerBits, _) = Make(c);
        BurstHeader header = burst.Header;
        var one = new TrackedReceiver();
        var onePayload = new byte[PayloadReceiver.PayloadRoom(header)];
        one.Receive(capture, start, header, headerBits, onePayload);
        double singleBest = Stat(() => one.Receive(capture, start, header, headerBits, onePayload), reps, out double singleMedian);

        var times = new double[threads][];
        var receivers = new TrackedReceiver[threads];
        var payloads = new byte[threads][];
        for (int i = 0; i < threads; i++)
        {
            receivers[i] = new TrackedReceiver();
            payloads[i] = new byte[PayloadReceiver.PayloadRoom(header)];
            receivers[i].Receive(capture, start, header, headerBits, payloads[i]);
            times[i] = new double[reps];
        }

        using var ready = new Barrier(threads + 1);
        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            int k = i;
            workers[i] = new Thread(() =>
            {
                ready.SignalAndWait();
                for (int r = 0; r < reps; r++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    receivers[k].Receive(capture, start, header, headerBits, payloads[k]);
                    times[k][r] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
            });
            workers[i].Start();
        }

        ready.SignalAndWait();
        long w0 = Stopwatch.GetTimestamp();
        foreach (Thread w in workers)
        {
            w.Join();
        }

        double wall = Stopwatch.GetElapsedTime(w0).TotalMilliseconds;
        double[] all = times.SelectMany(x => x).Order().ToArray();
        double throughput = threads * reps / (wall / 1000.0);
        double singleThroughput = 1000.0 / singleBest;
        Say($"[{threads} receives at once on burst a, one receiver per thread, {reps} each] single thread best {singleBest:F0} ms, median {singleMedian:F0} ms; "
            + $"with {threads} threads time per burst best {all[0]:F0} ms, median {all[all.Length / 2]:F0} ms, worst {all[^1]:F0} ms; "
            + $"throughput {throughput:F2} bursts/s against {singleThroughput:F2} (best single) = {throughput / singleThroughput:F2}x for {threads} threads (wall {wall:F0} ms for {threads * reps} receives)");
    }
}
