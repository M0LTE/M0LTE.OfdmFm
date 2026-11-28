using System.Collections.Concurrent;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The simulator's own account of why a burst did or did not get through, to set beside what the station makes of it (docs/channel-sharing.md, "Baseline: how the station behaves today"). The station never sees any of this.
/// </summary>
internal enum LossCause
{
    /// <summary>The path's steady gain was below what the burst's format needs, with nothing else going on.</summary>
    PathTooWeak,

    /// <summary>Another station's burst, or the receiver's own transmission, overlapped it.</summary>
    OtherStation,

    /// <summary>The path was strong enough on average but the flutter took it below what the format needs.</summary>
    Fade,

    /// <summary>An impulse overlapped it, and none of the above applies.</summary>
    Impulse,

    /// <summary>None of the above: a burst the simulator can see no reason to lose.</summary>
    None,
}

/// <summary>What happened to one data burst, by the simulator's ground truth.</summary>
/// <param name="From">The sending station's index.</param>
/// <param name="To">The index of the station the burst was for.</param>
/// <param name="Start">The sample it went on the air at.</param>
/// <param name="Length">Its length, samples.</param>
/// <param name="Format">The format it was sent at.</param>
/// <param name="Decoded">Whether the station it was for delivered any datagram of it.</param>
/// <param name="Cause">When it was not decoded, why, by the order in <see cref="LossGroundTruth"/>.</param>
/// <param name="ImpulseHit">Whether an impulse overlapped it at the receiver, decoded or not.</param>
/// <param name="GainDb">The path's steady gain when it went on the air, dB against 1.</param>
/// <param name="NeedDb">The gain its format needs, dB against 1 (infinite when no gain does).</param>
internal sealed record BurstFate(int From, int To, long Start, int Length, int Format, bool Decoded, LossCause? Cause, bool ImpulseHit, double GainDb, double NeedDb);

/// <summary>
/// What a format needs from the path, found by asking the receiver itself: the lowest gain, in 0.3 dB steps, at which a burst of that format over the sims' usual noise (FM-shaped, 0.004) is decoded in at least 5 of 6 tries.
/// Measured once for each format that comes up, in this process, with the same renderer and receiver the stations use; the receiver's own measurement, with its margin, is not consulted.
/// </summary>
internal static class FormatNeed
{
    /// <summary>The noise the sims' paths carry unless a test says otherwise.</summary>
    public const double NoiseRms = 0.004;

    private static readonly ConcurrentDictionary<int, double> Cache = new();

    /// <summary>The lowest linear path gain at which <paramref name="format"/> decodes over <see cref="NoiseRms"/>; infinite when none up to +6 dB does.</summary>
    public static double MinGain(int format) => Cache.GetOrAdd(format, Calibrate);

    private static double Calibrate(int format)
    {
        double lo = -70;
        double hi = 6;
        if (!Decodes(format, hi))
        {
            return double.PositiveInfinity;
        }

        if (Decodes(format, lo))
        {
            return SimChannel.Db(lo);
        }

        while (hi - lo > 0.3)
        {
            double mid = (lo + hi) / 2;
            if (Decodes(format, mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return SimChannel.Db(hi);
    }

    private static bool Decodes(int format, double db)
    {
        int good = 0;
        for (int trial = 0; trial < 6; trial++)
        {
            TestBurst burst = TestBurst.Data(2, 1, format, [TestBurst.Datagram(1, 30)]);
            var builder = new StreamBuilder().Silence(1.0);
            builder.Add(burst, 0, SimChannel.Db(db));
            builder.Silence(1.5);
            float[] stream = builder.ToArray();
            new FmNoise(1000 + trial, NoiseRms).AddTo(stream);
            using var rig = new ReceiveRig(0);
            rig.Push(stream, () => 4800);
            rig.Finish();
            if (rig.Datagrams.Count > 0)
            {
                good++;
            }
        }

        return good >= 5;
    }
}

/// <summary>
/// Works out, for every data burst of a run, whether it was decoded and, if not, what the simulator says hit it. A burst counts as decoded when the station it was for delivered at least one of its datagrams. Causes are
/// exclusive and taken in this order: the path too weak for the format (steady gain below what the format needs, with the receiver's own noise as the sims carry it); another station's burst, or the receiver's own transmission,
/// overlapping it; a fade (the flutter taking the gain below what the format needs, the steady gain being enough); an impulse overlapping it; none of these. Overlap is of the part of the burst after its lead-in of silence. Needs
/// <see cref="SimChannel.RecordDeliveries"/> and a <see cref="Ledger"/> that carried every datagram.
/// </summary>
internal static class LossGroundTruth
{
    /// <summary>The fate of every data burst any station of the rig sent, in the order of the stations and then of the air.</summary>
    public static List<BurstFate> Fates(SimRig rig, Ledger ledger)
    {
        var fates = new List<BurstFate>();
        var received = rig.Nodes.Select(n => n.Received.Where(r => r.Data.Length >= 4).Select(r => Ledger.Identify(r.Data)).ToHashSet()).ToArray();
        foreach (SimNode x in rig.Nodes)
        {
            var byEnd = x.Transmissions.ToDictionary(t => t.Start + t.Length);
            foreach (IGrouping<long, (long Clock, TransmittedEvent Event)> burst in x.Transmitted.GroupBy(t => t.Clock))
            {
                if (!byEnd.TryGetValue(burst.Key, out SimTransmission? tx))
                {
                    continue;
                }

                TransmittedEvent first = burst.First().Event;
                int to = Enumerable.Range(1, rig.Nodes.Count).FirstOrDefault(k => SimRig.Address(k) == first.Address) - 1;
                if (to < 0)
                {
                    continue;
                }

                SimNode y = rig.Nodes[to];
                bool decoded = false;
                foreach ((long _, TransmittedEvent e) in burst)
                {
                    SentItem? item = ledger.ItemOf(e.Tag.Value);
                    if (item is not null && received[to].Contains((item.From, item.Seq, (int)(e.Tag.Value % 100))))
                    {
                        decoded = true;
                    }
                }

                SimDelivery? d = y.Deliveries.FirstOrDefault(v => v.From == x.Index && v.TxStart == tx.Start);
                if (d is null)
                {
                    continue;
                }

                long from = d.At + BurstRenderer.DefaultLeadSamples;
                long until = d.At + d.Length;
                double noiseScale = NoiseScale(rig, y);
                double need = FormatNeed.MinGain(first.Format) * noiseScale;
                bool impulse = y.ImpulseOverlaps(from, until);
                LossCause? cause = null;
                if (!decoded)
                {
                    if (d.Gain < need)
                    {
                        cause = LossCause.PathTooWeak;
                    }
                    else if (OtherOverlaps(rig, x, y, tx))
                    {
                        cause = LossCause.OtherStation;
                    }
                    else if (d.Gain * d.FlutterMin < need)
                    {
                        cause = LossCause.Fade;
                    }
                    else if (impulse)
                    {
                        cause = LossCause.Impulse;
                    }
                    else
                    {
                        cause = LossCause.None;
                    }
                }

                fates.Add(new BurstFate(x.Index, to, tx.Start, tx.Length, first.Format, decoded, cause, impulse, 20 * Math.Log10(d.Gain), 20 * Math.Log10(need)));
            }
        }

        return fates;
    }

    /// <summary>How much more receiver noise there is at <paramref name="y"/> than the one-path noise the formats were measured over: every path into a receiver adds its noise.</summary>
    public static double NoiseScale(SimRig rig, SimNode y)
    {
        double power = 0;
        foreach (SimNode other in rig.Nodes)
        {
            if (other != y)
            {
                power += Math.Pow(rig.Channel.Link(other, y).NoiseRms, 2);
            }
        }

        return Math.Max(1.0, Math.Sqrt(power) / FormatNeed.NoiseRms);
    }

    private static bool OtherOverlaps(SimRig rig, SimNode x, SimNode y, SimTransmission tx)
    {
        long from = tx.Start + BurstRenderer.DefaultLeadSamples;
        long until = tx.Start + tx.Length;
        foreach (SimNode o in rig.Nodes)
        {
            if (o == x || (o != y && rig.Channel.Link(o, y).Gain == 0))
            {
                continue;
            }

            foreach (SimTransmission u in o.Transmissions)
            {
                if (u.Start < until && from < u.Start + u.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
