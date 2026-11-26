using System.Diagnostics;
using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Times the tracked receiver on two long bursts and prints the figures; it asserts nothing about time. Set SM_BENCH=1 and run it in Release:
/// <c>dotnet run -c Release --project tests/M0LTE.OfdmFm.Tests -- -class M0LTE.OfdmFm.Tests.TrackedReceiverBenchmark</c>
/// (the output goes to the console and, when SM_BENCH_OUT names a file, is appended there). SM_BENCH_REPS sets the timed receives of each case (default 8).
/// </summary>
public class TrackedReceiverBenchmark
{
    internal const int Pad = 4096;

    [Fact]
    public void Time_The_Tracked_Receive()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_BENCH") != "1", "set SM_BENCH=1 to time the tracked receiver (prints figures, asserts none)");
        int reps = int.TryParse(Environment.GetEnvironmentVariable("SM_BENCH_REPS"), out int r) && r > 0 ? r : 8;
        Say($"load average {File.ReadAllText("/proc/loadavg").Trim()} (before), cores {Environment.ProcessorCount}");

        // A dense burst: 8 bits a carrier on the full band, 52 payload symbols (2.4 s of airtime), a clock 250 ppm off drifting 40 ppm each second.
        Run("dense: format 27, 8 bits, full band, 52 symbols", 27, 52, 250, 40, 48, reps);

        // The longest burst at a low rate on the 1.5 kHz opening: 252 symbols of BPSK.
        Run("long: format 192, 1 bit, 1.5 kHz opening, 252 symbols", 192, 252, -200, 15, 12, Math.Max(2, reps / 4));
        Say($"load average {File.ReadAllText("/proc/loadavg").Trim()} (after)");
    }

    /// <summary>
    /// Writes everything the tracked receiver finds, bit for bit, for every vector case and a few more long ones, to the file SM_DUMP names (set SM_DUMP_FULL=1 to include
    /// the heavy synthetic bursts): one line a case, doubles as the hex of their bits. Two builds that are meant to receive the same compare their files.
    /// </summary>
    [Fact]
    public void Dump_Everything_Received()
    {
        string? path = Environment.GetEnvironmentVariable("SM_DUMP");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "set SM_DUMP=<file> to write what every case receives, for comparing two builds");
        var lines = new List<string>();
        foreach (TrackedCase c in TrackedVectors.Cases.Value)
        {
            lines.Add(Describe(c.Name, c.Capture, c.Start, c.Header, c.HeaderBits));
        }

        if (Environment.GetEnvironmentVariable("SM_DUMP_FULL") == "1")
        {
            foreach ((int format, int symbols, double ppm, double drift, double snr) in new (int, int, double, double, double)[]
            {
                (27, 52, 250, 40, 48), (27, 52, -300, -50, 40), (27, 52, 120, 0, 36), (27, 52, 0, 0, 55), (27, 28, 300, 30, 34),
                (26, 40, -150, 20, 30), (28, 52, 200, 10, 60), (96, 36, 180, 25, 22), (192, 252, -200, 15, 12), (192, 120, 300, -40, 4),
                (160, 100, 90, 0, 14), (96, 60, -250, 35, 10),
            })
            {
                TestBurst burst = TestBurst.Data(1, 0, format, [TestBurst.Datagram(7, Math.Min(1200, PayloadCodec.Capacity(format, symbols) - 4))], symbols: symbols);
                float[] capture = Impair(burst, ppm, drift, snr);
                var headerBits = new byte[BurstHeaderCodec.HeaderBits];
                BurstHeaderCodec.Write(burst.Header, headerBits);
                lines.Add(Describe($"synthetic format {format} symbols {symbols} ppm {ppm} drift {drift} snr {snr}", capture, Pad + burst.Lead, burst.Header, headerBits));
            }
        }

        File.WriteAllLines(path!, lines, System.Text.Encoding.ASCII);
    }

    private static string Describe(string name, float[] capture, int start, in BurstHeader header, byte[] headerBits)
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[PayloadReceiver.PayloadRoom(header)];
        PayloadReceiveResult r = receiver.Receive(capture, start, header, headerBits, payload);
        string Bits(ReadOnlySpan<double> v)
        {
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                parts[i] = BitConverter.DoubleToInt64Bits(v[i]).ToString("x16", CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        string Hash(ReadOnlySpan<double> v) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v)))[..16];
        string payloadHash = r.CheckPassed ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload.AsSpan(0, r.PayloadBytes)))[..16] : "-";
        return string.Join("|", name, $"accepted={r.Accepted}", $"all={r.AllBlocksSatisfied}", $"check={r.CheckPassed}", $"bytes={r.PayloadBytes}", $"guarded={r.GuardedCarriers}/{receiver.GuardedCarriers}",
            $"passes={receiver.Passes}", $"symbolPasses={receiver.SymbolPasses}", $"fine={receiver.FineTrackRan}", $"sudden={receiver.Sudden}", $"payload={payloadHash}",
            $"decidedRe={Hash(receiver.DecidedReal)}", $"decidedIm={Hash(receiver.DecidedImaginary)}", $"curve={Bits(receiver.Curve)}", $"fineKnots={Bits(receiver.FineKnots)}",
            $"fineDelays={Bits(receiver.FineDelays)}", $"delays={Bits(receiver.Delays)}", $"miss={Bits(receiver.Miss)}", $"quality={Bits(receiver.Quality)}");
    }

    private static void Run(string name, int format, int symbols, double ppm, double driftPpmPerSecond, double snrDb, int reps)
    {
        TestBurst burst = TestBurst.Data(1, 0, format, [TestBurst.Datagram(7, Math.Min(1200, PayloadCodec.Capacity(format, symbols) - 4))], symbols: symbols);
        float[] capture = Impair(burst, ppm, driftPpmPerSecond, snrDb);
        int start = Pad + burst.Lead;
        var headerBits = new byte[BurstHeaderCodec.HeaderBits];
        BurstHeaderCodec.Write(burst.Header, headerBits);
        var payload = new byte[PayloadReceiver.PayloadRoom(burst.Header)];
        var receiver = new TrackedReceiver();
        Say($"{name}: airtime {burst.Length / 48000.0:F2} s");

        PayloadReceiveResult result = default;
        for (int i = 0; i < 2; i++)
        {
            result = receiver.Receive(capture, start, burst.Header, headerBits, payload);
        }

        Say($"  check {result.CheckPassed}, passes {receiver.Passes} ({receiver.SymbolPasses} symbol passes, fine track {receiver.FineTrackRan}), guarded {receiver.GuardedCarriers}");

        var times = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            receiver.Receive(capture, start, burst.Header, headerBits, payload);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(times);
        Say($"  tracked receive: best {times[0]:F0} ms, median {times[reps / 2]:F0} ms, worst {times[^1]:F0} ms over {reps} ({times[0] / (burst.Length / 48.0):P0} of airtime at best)");

        // Pieces on their own, for the same capture (one resample along the burst's own curve; one plain receive of the settled capture).
        int n = PayloadReceiver.SamplesNeeded(burst.Header) / BurstRenderer.SymbolSamples;
        var knots = new double[n];
        var delays = new double[n];
        for (int i = 0; i < n; i++)
        {
            knots[i] = ((i * BurstRenderer.SymbolSamples) + (BurstRenderer.SymbolSamples / 2.0)) / BurstRenderer.SampleRate;
            delays[i] = -((ppm * 1e-6 * knots[i]) + (driftPpmPerSecond * 1e-6 * knots[i] * knots[i] / 2));
        }

        int length = BurstRenderer.DefaultLeadSamples + PayloadReceiver.SamplesNeeded(burst.Header);
        var wave = new float[length];
        double resample = Best(reps, () => DelayResampler.Resample(capture, start - BurstRenderer.DefaultLeadSamples, length, knots, delays, BurstRenderer.DefaultLeadSamples, wave));
        var plain = new PayloadReceiver();
        plain.Receive(wave.AsSpan(0, length), BurstRenderer.DefaultLeadSamples, burst.Header, headerBits, payload);
        double receive = Best(reps, () => plain.Receive(wave.AsSpan(0, length), BurstRenderer.DefaultLeadSamples, burst.Header, headerBits, payload));
        Say($"  alone, best of {reps}: one resample {resample:F1} ms, one plain receive {receive:F1} ms");
    }

    private static double Best(int reps, Action action)
    {
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }

        return best;
    }

    // The burst through a sample clock that is ppm off and drifts, then white noise: the capture, padded with silence.
    internal static float[] Impair(TestBurst burst, double ppm, double driftPpmPerSecond, double snrDb)
    {
        float[] audio = burst.Audio;
        var padded = new float[audio.Length + (2 * Pad)];
        audio.CopyTo(padded, Pad);
        double power = 0;
        foreach (float v in audio)
        {
            power += (double)v * v;
        }

        double sigma = Math.Sqrt(power / audio.Length) / Math.Pow(10, snrDb / 20);
        int count = (int)Math.Ceiling(audio.Length / (double)BurstRenderer.SymbolSamples) + 1;
        var knots = new double[count];
        var delays = new double[count];
        for (int i = 0; i < count; i++)
        {
            double t = i * BurstRenderer.SymbolSamples / BurstRenderer.SampleRate;
            knots[i] = t;
            delays[i] = (ppm * 1e-6 * t) + (driftPpmPerSecond * 1e-6 * t * t / 2);
        }

        var output = new float[padded.Length];
        DelayResampler.Resample(padded, 0, padded.Length, knots, delays, Pad + burst.Lead, output);
        var rng = new Random(12345);
        for (int i = 0; i < output.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            output[i] += (float)(sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return output;
    }

    internal static void Say(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SM_BENCH_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.ASCII);
        }
    }
}
