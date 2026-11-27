using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Bursts for measuring the receiver's judgement that a burst's quality collapsed part-way (packet-net/pdn-soundmodem#898): clean ones over a range of signal-to-noise ratios, ones with another burst
/// landing part-way, and ones on a path too weak throughout. Every burst is made from seeded sources, so a run is repeatable.
/// </summary>
internal static class SuddenBench
{
    /// <summary>The sims' own link: gain 1 and FM-shaped noise of 0.004 rms. Far quieter than a radio gives; the "unrealistically clean" case.</summary>
    public const double SimClean = double.PositiveInfinity;

    /// <summary>What a burst was and what the receiver made of it.</summary>
    public sealed record Heard(int Format, int Symbols, bool Decoded, int Known, double[] Miss);

    /// <summary>
    /// The noise standard deviation (white, over the whole 24 kHz) that gives a per-carrier signal-to-noise ratio of <paramref name="snrDb"/> on the payload's carriers of <paramref name="format"/>: the burst's power
    /// at its drive shared among its carriers, against the noise's share of one of the transform's 1024 bins.
    /// </summary>
    public static double Sigma(int format, double snrDb)
    {
        FormatSpec spec = StandardFormats.Describe(format);
        double rms = BurstRenderer.RmsFull * Math.Pow(10, spec.DriveDb / 20);
        return Math.Sqrt(rms * rms * 1024 / spec.Carriers / Math.Pow(10, snrDb / 10));
    }

    /// <summary>A data burst of <paramref name="format"/> with a random datagram (seeded by <paramref name="seed"/>), 8 to 48 payload symbols (a multiple of four).</summary>
    public static TestBurst Burst(int format, int sender, int seed, int fewest = 8, int most = 48)
    {
        var rng = new Random(seed);
        // A burst to one station carries a prelude, the block of link addresses and the datagram's length besides the datagram.
        int overhead = 4 + PayloadCodec.AddressBlockSize(1, false) + 2;
        int symbols = Math.Max(4 * rng.Next(fewest / 4, (most / 4) + 1), PayloadCodec.SymbolsFor(format, 1 + overhead)!.Value);
        int longest = 1;
        while (PayloadCodec.SymbolsFor(format, longest + 1 + overhead) is { } s && s <= symbols)
        {
            longest++;
        }

        var datagram = new byte[rng.Next((longest + 1) / 2, longest + 1)];
        rng.NextBytes(datagram);
        return TestBurst.Data(sender, 1, format, [datagram], symbols: symbols);
    }

    /// <summary>
    /// The capture of burst <paramref name="a"/> heard at <paramref name="snrDb"/> (white noise; <see cref="SimClean"/> for the sims' own link), with <paramref name="other"/>, if given, landing
    /// <paramref name="fraction"/> of the way through A's payload from its first training sample, <paramref name="relativeDb"/> against A, and cut off where A ends. Returns the capture and A's first training sample.
    /// </summary>
    public static (float[] Capture, int Start) Capture(TestBurst a, double snrDb, int noiseSeed, TestBurst? other = null, double fraction = 0, double relativeDb = 0)
    {
        int pad = 2400;
        var s = new float[a.Audio.Length + pad];
        a.Audio.CopyTo(s, 0);
        int start = a.Lead;
        if (other is not null)
        {
            FormatSpec spec = StandardFormats.Describe(a.Header.Format);
            int known = HeaderReader.TrainingSymbolCount(spec.Opening) + HeaderReader.HeaderSymbolCount(spec.Opening);
            int at = start + (known * BurstRenderer.SymbolSamples) + (int)(fraction * a.Header.PayloadSymbols * BurstRenderer.SymbolSamples);
            double g = Math.Pow(10, relativeDb / 20);
            for (int i = 0; at + i < a.Audio.Length && other.Lead + i < other.Audio.Length; i++)
            {
                s[at + i] += (float)(other.Audio[other.Lead + i] * g);
            }
        }

        if (double.IsPositiveInfinity(snrDb))
        {
            new FmNoise(noiseSeed, 0.004).AddTo(s);
        }
        else
        {
            double sigma = Sigma(a.Header.Format, snrDb);
            var rng = new Random(noiseSeed);
            for (int i = 0; i < s.Length; i++)
            {
                double u = 1.0 - rng.NextDouble();
                double v = rng.NextDouble();
                s[i] += (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
            }
        }

        return (s, start);
    }

    /// <summary>Receives a capture with the station's receiver.</summary>
    public static Heard Receive(TrackedReceiver receiver, TestBurst a, float[] capture, int start)
    {
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        BurstHeaderCodec.Write(a.Header, bits);
        var payload = new byte[PayloadReceiver.PayloadRoom(a.Header)];
        PayloadReceiveResult r = receiver.Receive(capture, start, a.Header, bits, payload);
        return new Heard(a.Header.Format, a.Header.PayloadSymbols, r.CheckPassed, receiver.KnownSymbols, receiver.Miss.ToArray());
    }

    /// <summary>
    /// The signal-to-noise ratio (per carrier, dB, to the nearest half dB) at which half of 32 seeded bursts of <paramref name="format"/> decode: the format's threshold, as this bench measures it.
    /// </summary>
    public static double Threshold(int format)
    {
        double lo = -20;
        double hi = 50;
        while (hi - lo > 0.5)
        {
            double mid = (lo + hi) / 2;
            int ok = 0;
            Parallel.For(0, 32, () => new TrackedReceiver(), (k, _, receiver) =>
            {
                TestBurst a = Burst(format, 2, 900_000 + k);
                (float[] c, int st) = Capture(a, mid, 800_000 + k);
                if (Receive(receiver, a, c, st).Decoded)
                {
                    Interlocked.Increment(ref ok);
                }

                return receiver;
            }, _ => { });
            if (ok >= 16)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return Math.Round(hi * 2) / 2;
    }

    public static string F(double v, string format = "F2") => v.ToString(format, CultureInfo.InvariantCulture);
}
