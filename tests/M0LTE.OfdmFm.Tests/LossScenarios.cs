using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>One stream of datagrams: from one station to another, one at exponentially spaced moments of the given mean gap.</summary>
internal sealed record LossFlow(int From, int To, double MeanGapSeconds);

/// <summary>
/// One loss scenario: the stations and their paths, the traffic offered, how long it lasts, and the impairment put on the channel (none, for a control). <paramref name="Window"/> is the stretch of the run that the impairment
/// is on for, in seconds, when it is on for a stretch only.
/// </summary>
internal sealed record LossSpec(string Id, string Group, string Title, int Stations, Action<SimRig> Paths, LossFlow[] Flows, double TrafficSeconds, double DrainSeconds, Action<SimRig, int>? Impair, (double From, double Until)? Window = null);

/// <summary>What one seeded run of a <see cref="LossSpec"/> came to, by the stations' own events and the simulator's ground truth.</summary>
internal sealed record LossRun(
    string Id, int Seed, int Offered, int Delivered, int Dropped, int Queued, long DeliveredBytes, double Seconds, double[] AirSeconds, int DataBursts, int NotDecoded, int[] Causes, int ImpulseHit, int ImpulseHitNotDecoded,
    int Falls, int FallsWithoutWeakCause, int Silences, int AddressesLost, int Duplicates, int DeliveredTwice, int WindowOffered, int WindowDelivered, int WindowNotDecoded, int WindowDataBursts)
{
    /// <summary>The names of the air-time bands, slowest first.</summary>
    public static readonly string[] Bands = ["slowest format", "slow (under 1 kbit/s)", "middle (1 to 10 kbit/s)", "fast (10 kbit/s and over)"];

    /// <summary>The names of the causes, in the order of <see cref="LossCause"/>.</summary>
    public static readonly string[] CauseNames = ["path too weak for the format", "another station's burst", "a fade", "an impulse", "none of these"];

    /// <summary>A line a script can read back: every figure of the run, in order.</summary>
    public string Line() =>
        string.Create(CultureInfo.InvariantCulture,
            $"LOSS id={Id} seed={Seed} offered={Offered} delivered={Delivered} dropped={Dropped} queued={Queued} bytes={DeliveredBytes} seconds={Seconds:F1} air={string.Join("/", AirSeconds.Select(a => a.ToString("F1", CultureInfo.InvariantCulture)))} "
            + $"bursts={DataBursts} notdecoded={NotDecoded} causes={string.Join("/", Causes)} impulsehit={ImpulseHit} impulsehitnotdecoded={ImpulseHitNotDecoded} falls={Falls} fallsnoweak={FallsWithoutWeakCause} silences={Silences} addresseslost={AddressesLost} "
            + $"duplicates={Duplicates} twice={DeliveredTwice} winoffered={WindowOffered} windelivered={WindowDelivered} winnotdecoded={WindowNotDecoded} winbursts={WindowDataBursts}");
}

/// <summary>
/// The baseline scenarios (docs/channel-sharing.md, "Baseline: how the station behaves today"): steady traffic both ways for minutes of simulated time, with and without each impairment of the simulated channel, run over the same eight
/// seeds and measured the same way. Nothing here changes the station; it records what the station does. The hosts resend nothing, so every loss on the air shows as a datagram not delivered. The capture model is the default one
/// (assumed, never measured), the carrier indication is off (the station's own detector only), as in the other hostile scenarios.
/// </summary>
internal static class LossScenarios
{
    /// <summary>The seeds every comparison uses: 1 to 8.</summary>
    public static readonly int[] Seeds = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Bytes in each datagram offered.</summary>
    public const int DatagramBytes = 100;

    /// <summary>Mean gap between datagrams in each direction, two-station scenarios, seconds.</summary>
    public const double TwoStationGapSeconds = 8;

    /// <summary>The gain of the steady good path between two stations: -48 dB. The sims' usual noise is 0.004 against a burst of 0.44, so a gain of 1 is far more than any format needs; probing seed 1 of the control showed the station at its fastest formats down to about -46 dB, in the middle of the formats around -54 dB and without a working path at -66 dB, so this is a good path from which a 12 dB loss still leaves a usable one.</summary>
    public const double GoodPathGain = 0.004;

    /// <summary>Impulse levels in dB over the wanted signal on the good path, and rates per second, for light, moderate and heavy; one event in ten is a train.</summary>
    public static readonly (string Name, double Rate, double LevelOverWantedDb, double Trains)[] ImpulseGrades = [("light", 0.5, 6, 0.1), ("moderate", 2.0, 10, 0.1), ("heavy", 8.0, 16, 0.1)];

    private static LossFlow[] TwoWay(double gap = TwoStationGapSeconds) => [new LossFlow(1, 2, gap), new LossFlow(2, 1, gap)];

    private static void TwoStationPath(SimRig rig) => HostileScenarios.Pair(rig, 1, 2, GoodPathGain);

    private static void ThreeStationPaths(SimRig rig)
    {
        // As ThreeHearAll: unequal paths, so that where two bursts overlap at a receiver one is usually stronger.
        HostileScenarios.Pair(rig, 1, 2, 1.0);
        HostileScenarios.Pair(rig, 1, 3, SimChannel.Db(-10));
        HostileScenarios.Pair(rig, 2, 3, SimChannel.Db(-20));
    }

    private static void HiddenPaths(SimRig rig)
    {
        // As HiddenStation: A and C cannot hear each other; both hear and are heard by B, at the same strength.
        HostileScenarios.Pair(rig, 1, 2, 1.0);
        HostileScenarios.Pair(rig, 3, 2, 1.0);
    }

    private static Action<SimRig, int> Impulses(double rate, double levelDb, double trains) =>
        (rig, _) =>
        {
            foreach (SimNode n in rig.Nodes)
            {
                n.Impulses = new ImpulseModel(rate, levelDb, trains);
            }
        };

    /// <summary>Flutter on both directions of the A to B path, each with its own phase (from the seed); the episode starts at <paramref name="start"/> s and lasts <paramref name="seconds"/>.</summary>
    private static Action<SimRig, int> Flutter(double strength, double hz, bool swept, double start, double seconds) =>
        (rig, seed) =>
        {
            var phases = new Random(unchecked((seed * 977) + 5));
            rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Flutter = new FlutterModel(strength, hz, swept, start, seconds, phases.NextDouble() * 2 * Math.PI);
            rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Flutter = new FlutterModel(strength, hz, swept, start, seconds, phases.NextDouble() * 2 * Math.PI);
        };

    private static Action<SimRig, int> Step(double downAt, double upAt, double db) =>
        (rig, _) =>
        {
            rig.Channel.StepPath(rig.Nodes[0], rig.Nodes[1], (long)(downAt * 48000), -db);
            rig.Channel.StepPath(rig.Nodes[1], rig.Nodes[0], (long)(downAt * 48000), -db);
            rig.Channel.StepPath(rig.Nodes[0], rig.Nodes[1], (long)(upAt * 48000), +db);
            rig.Channel.StepPath(rig.Nodes[1], rig.Nodes[0], (long)(upAt * 48000), +db);
        };

    private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Every scenario, in the order of the document.</summary>
    public static List<LossSpec> All()
    {
        var list = new List<LossSpec>();

        // (a) a steady good path, impulses at three intensities, and the control.
        list.Add(new LossSpec("impulses-off", "impulses", "Control: a steady good path, no impulses", 2, TwoStationPath, TwoWay(), 300, 60, null));
        foreach ((string name, double rate, double over, double trains) in ImpulseGrades)
        {
            list.Add(new LossSpec($"impulses-{name}", "impulses", $"{char.ToUpperInvariant(name[0])}{name[1..]} impulses: {Num(rate)} a second, {Num(over)} dB over the wanted signal", 2, TwoStationPath, TwoWay(), 300, 60,
                Impulses(rate, ImpulseModel.LevelFor(over, GoodPathGain), trains)));
        }

        // (b) a flutter episode of 30 s, starting at 60 s.
        list.Add(new LossSpec("flutter-off", "flutter", "Control: a steady good path, no flutter", 2, TwoStationPath, TwoWay(), 150, 30, null, (60, 90)));
        foreach (double strength in new[] { 0.5, 0.9 })
        {
            string depth = strength == 0.5 ? "9.5" : "26";
            list.Add(new LossSpec($"flutter-3hz-{Num(strength)}", "flutter", $"Flutter at 3 Hz, reflected path at {Num(strength)} ({depth} dB fades), 30 s", 2, TwoStationPath, TwoWay(), 150, 30, Flutter(strength, 3, false, 60, 30), (60, 90)));
            list.Add(new LossSpec($"flutter-20hz-{Num(strength)}", "flutter", $"Flutter at 20 Hz, reflected path at {Num(strength)} ({depth} dB fades), 30 s", 2, TwoStationPath, TwoWay(), 150, 30, Flutter(strength, 20, false, 60, 30), (60, 90)));
            list.Add(new LossSpec($"flutter-sweep-{Num(strength)}", "flutter", $"Flutter sweeping from 20 Hz through zero and back, reflected path at {Num(strength)} ({depth} dB fades), 30 s", 2, TwoStationPath, TwoWay(), 150, 30, Flutter(strength, 20, true, 60, 30), (60, 90)));
        }

        // (c) a 12 dB step down for 60 s.
        list.Add(new LossSpec("step-off", "step", "Control: a steady good path, no step", 2, TwoStationPath, TwoWay(), 240, 30, null, (90, 150)));
        list.Add(new LossSpec("step-12db", "step", "The path loses 12 dB at 90 s and gets it back at 150 s", 2, TwoStationPath, TwoWay(), 240, 30, Step(90, 150, 12), (90, 150)));

        // (d) the three-station and hidden-station arrangements, with moderate impulses (2 a second, 10 dB over the signal on the strongest path) and without.
        LossFlow[] three = [.. new[] { (1, 2), (1, 3), (2, 1), (2, 3), (3, 1), (3, 2) }.Select(p => new LossFlow(p.Item1, p.Item2, 24))];
        LossFlow[] hidden = [new LossFlow(1, 2, 12), new LossFlow(2, 1, 12), new LossFlow(3, 2, 12), new LossFlow(2, 3, 12)];
        list.Add(new LossSpec("three-off", "three", "Control: three stations that all hear each other, no impulses", 3, ThreeStationPaths, three, 300, 60, null));
        list.Add(new LossSpec("three-impulses", "three", "Three stations that all hear each other, moderate impulses", 3, ThreeStationPaths, three, 300, 60, Impulses(2.0, 10, 0.1)));
        list.Add(new LossSpec("hidden-off", "hidden", "Control: a hidden station, no impulses", 3, HiddenPaths, hidden, 300, 60, null));
        list.Add(new LossSpec("hidden-impulses", "hidden", "A hidden station, moderate impulses", 3, HiddenPaths, hidden, 300, 60, Impulses(2.0, 10, 0.1)));
        return list;
    }

    /// <summary>The band a format's air time is counted in: 0 the slowest format (the first-contact one), then by information rate.</summary>
    public static int BandOf(int format)
    {
        if (format == StandardFormats.FirstContact)
        {
            return 0;
        }

        double rate = StandardFormats.Rate(format);
        return rate < 1.0 ? 1 : rate < 10.0 ? 2 : 3;
    }

    /// <summary>Runs one scenario at one seed.</summary>
    public static LossRun Run(LossSpec spec, int seed)
    {
        SimRig rig = HostileScenarios.Make(spec.Stations, seed, HostileScenarios.Default, false);
        rig.Channel.RecordDeliveries = true;
        spec.Paths(rig);
        spec.Impair?.Invoke(rig, seed);
        var ledger = new Ledger(rig, seed) { MaxAttempts = 1 };
        var rng = new Random(unchecked((seed * 4001) + 13));
        var plan = new List<Ledger.Plan>();
        foreach (LossFlow flow in spec.Flows)
        {
            double t = 6 + (-Math.Log(1 - rng.NextDouble()) * flow.MeanGapSeconds);
            while (t < spec.TrafficSeconds)
            {
                plan.Add(new Ledger.Plan((long)(t * 48000), flow.From, flow.To, DatagramBytes));
                t += -Math.Log(1 - rng.NextDouble()) * flow.MeanGapSeconds;
            }
        }

        plan.Sort((a, b) => a.Clock.CompareTo(b.Clock));
        ledger.RunWithHost(spec.TrafficSeconds + spec.DrainSeconds, 0.5, plan);
        return Measure(spec, seed, rig, ledger);
    }

    private static LossRun Measure(LossSpec spec, int seed, SimRig rig, Ledger ledger)
    {
        double seconds = rig.Channel.Clock / 48000.0;
        int offered = ledger.Items.Count;
        int delivered = ledger.Items.Count(ledger.Delivered);
        long bytes = ledger.Items.Where(ledger.Delivered).Sum(i => (long)i.Length);
        int dropped = ledger.DroppedCount() + ledger.Refused.Count;
        int queued = rig.Stations.Sum(s => s.QueuedTotal);
        int twice = ledger.Items.Count(i => i.To != 0 && ledger.ReceivedAt(i, i.To).Copies > 1);
        int winOffered = 0;
        int winDelivered = 0;
        if (spec.Window is { } w)
        {
            foreach (SentItem item in ledger.Items.Where(i => i.FirstSentClock >= w.From * 48000 && i.FirstSentClock < w.Until * 48000))
            {
                winOffered++;
                if (ledger.Delivered(item))
                {
                    winDelivered++;
                }
            }
        }

        // The air time of every burst, by the band of its format: a burst for the host's datagrams from its Transmitted event, and any other (a lone report, an announcement) from ModemSent, in order.
        var air = new double[4];
        foreach (SimNode n in rig.Nodes)
        {
            var formatByEnd = new Dictionary<long, int>();
            foreach ((long clock, TransmittedEvent e) in n.Transmitted)
            {
                formatByEnd[clock] = e.Format;
            }

            int lone = 0;
            foreach (SimTransmission t in n.Transmissions)
            {
                if (formatByEnd.TryGetValue(t.Start + t.Length, out int f))
                {
                    air[BandOf(f)] += t.Length / 48000.0;
                }
                else if (lone < n.ModemSent.Count)
                {
                    air[BandOf(n.ModemSent[lone++].Format)] += t.Length / 48000.0;
                }
            }
        }

        List<BurstFate> fates = LossGroundTruth.Fates(rig, ledger);
        var causes = new int[5];
        foreach (BurstFate f in fates.Where(f => !f.Decoded))
        {
            causes[(int)f.Cause!.Value]++;
        }

        int winBursts = 0;
        int winNotDecoded = 0;
        if (spec.Window is { } win)
        {
            foreach (BurstFate f in fates.Where(f => f.Start >= win.From * 48000 && f.Start < win.Until * 48000))
            {
                winBursts++;
                if (!f.Decoded)
                {
                    winNotDecoded++;
                }
            }
        }

        // Falls to the slowest format: a burst at the first-contact format to an address after one at another format to the same address; weak-signal cause when the path's steady gain was then below what the earlier format needs.
        int falls = 0;
        int fallsNoWeak = 0;
        foreach (SimNode x in rig.Nodes)
        {
            foreach (IGrouping<LinkAddress, (long Clock, TransmittedEvent Event)> toAddress in x.Transmitted.GroupBy(t => t.Event.Address))
            {
                int to = Enumerable.Range(1, rig.Nodes.Count).FirstOrDefault(k => SimRig.Address(k) == toAddress.Key) - 1;
                if (to < 0)
                {
                    continue;
                }

                SimNode y = rig.Nodes[to];
                int? previous = null;
                foreach (IGrouping<long, (long Clock, TransmittedEvent Event)> burst in toAddress.GroupBy(t => t.Clock).OrderBy(g => g.Key))
                {
                    int format = burst.First().Event.Format;
                    if (format == StandardFormats.FirstContact && previous is { } before && before != StandardFormats.FirstContact)
                    {
                        falls++;
                        SimTransmission? tx = x.Transmissions.FirstOrDefault(t => t.Start + t.Length == burst.Key);
                        SimDelivery? d = tx is null ? null : y.Deliveries.FirstOrDefault(v => v.From == x.Index && v.TxStart == tx.Start);
                        double need = FormatNeed.MinGain(before) * LossGroundTruth.NoiseScale(rig, y);
                        bool weak = d is not null && d.Gain < need;
                        if (!weak)
                        {
                            fallsNoWeak++;
                        }
                    }

                    previous = format;
                }
            }
        }

        int silences = rig.Nodes.Sum(n => n.SilenceCounts.Count(s => s.Count > 0));
        int lost = 0;
        foreach (SimNode n in rig.Nodes)
        {
            foreach (IGrouping<LinkAddress, LinkStateEvent> g in n.LinkStates.GroupBy(e => e.Address))
            {
                bool gone = false;
                foreach (LinkStateEvent e in g)
                {
                    if (e.Gone && !gone)
                    {
                        lost++;
                    }

                    gone = e.Gone;
                }
            }
        }

        return new LossRun(
            spec.Id, seed, offered, delivered, dropped, queued, bytes, seconds, air, fates.Count, fates.Count(f => !f.Decoded), causes, fates.Count(f => f.ImpulseHit), fates.Count(f => f.ImpulseHit && !f.Decoded), falls, fallsNoWeak, silences, lost,
            ledger.Duplicates().Count, twice, winOffered, winDelivered, winNotDecoded, winBursts);
    }
}
