using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The scenarios of step 8 as functions that run one seeded simulation and return what happened, so that the always-on tests (one setting of the capture model) and the gated sweeps (several) ask the same thing.
/// Everything here is on the sample clock and the seeded sources; nothing reads a wall clock.
/// </summary>
internal static class HostileScenarios
{
    public static readonly CaptureModel Default = new();

    /// <summary>The capture margins the gated sweeps try, dB. The first is the default.</summary>
    public static readonly double[] Margins = [3.0, 1.0, 6.0];

    /// <summary>A rig of <paramref name="stations"/> stations with no paths yet (connect them with <see cref="Pair"/>).</summary>
    /// <summary>Lone reports a station sent to station <paramref name="to"/>.</summary>
    private static int Reports(SimNode n, int to) => n.ModemSent.Where((m, i) => m.Kind == ModemSentKind.Report && n.ModemSentTo[i] == to).Count();

    public static SimRig Make(int stations, int seed, CaptureModel? capture, bool indication, Func<int, int, bool>? locates = null)
    {
        var rig = new SimRig(stations, 4800, seed, connect: false, locates: locates);
        rig.Channel.Capture = capture;
        if (indication)
        {
            foreach (SimNode n in rig.Nodes)
            {
                n.CarrierDelaySamples = 4800;
            }
        }

        return rig;
    }

    /// <summary>A path between two stations, both ways, with the same gain and the usual noise.</summary>
    public static void Pair(SimRig rig, int a, int b, double gain, double noise = 0.004) => rig.Channel.Connect(rig.Nodes[a - 1], rig.Nodes[b - 1], gain, noise);

    public static void SetGain(SimRig rig, int a, int b, double gain)
    {
        rig.Channel.Link(rig.Nodes[a - 1], rig.Nodes[b - 1]).Gain = gain;
        rig.Channel.Link(rig.Nodes[b - 1], rig.Nodes[a - 1]).Gain = gain;
    }

    public static string Label(CaptureModel? c) => c is null ? "additive" : string.Create(CultureInfo.InvariantCulture, $"margin {c.MarginDb:F0} dB");

    public static double Kbit(double bitsPerSecond) => bitsPerSecond / 1000.0;

    public static string Formats(SimNode n, int count = 12) => string.Join(",", n.Transmitted.Take(count).Select(t => t.Event.Format));

    // ---- 1. three stations that all hear each other ----

    public sealed record ThreeAll(
        string Label, int Items, int Delivered, int DeliveredFirstTry, int Recovered, int Undelivered, int Duplicates, int[] BytesBySender, double[] AirSeconds,
        int Bursts, int CollidedBursts, int CollidingPairs, int ResendsByHint, int ResendsByTimeout, int Hints, int Dropped, long LastDeliveryClock, int DeliveredTwice, List<string> Log);

    public static ThreeAll ThreeHearAll(CaptureModel? capture, bool indication, int seed, int perPair = 3, int length = 40, double seconds = 120, int spreadSeconds = 25)
    {
        SimRig rig = Make(3, seed, capture, indication);

        // Paths of unequal strength (A to B 0 dB, A to C -10 dB, B to C -20 dB), so that where two bursts overlap at a receiver one is usually stronger and the capture model has something to decide.
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, SimChannel.Db(-10));
        Pair(rig, 2, 3, SimChannel.Db(-20));
        var ledger = new Ledger(rig, seed);
        var rng = new Random(seed * 31 + 7);
        var plan = new List<Ledger.Plan>();
        for (int k = 0; k < perPair; k++)
        {
            for (int from = 1; from <= 3; from++)
            {
                for (int to = 1; to <= 3; to++)
                {
                    if (from != to)
                    {
                        plan.Add(new Ledger.Plan(5L * 48000 + rng.Next(0, spreadSeconds * 48000), from, to, length));
                    }
                }
            }
        }

        plan.Sort((x, y) => x.Clock.CompareTo(y.Clock));
        ledger.RunWithHost(seconds, 0.5, plan);
        var bytes = new int[3];
        int delivered = 0, first = 0, recovered = 0;
        long last = 0;
        foreach (SentItem item in ledger.Items)
        {
            if (ledger.Delivered(item))
            {
                delivered++;
                bytes[item.From - 1] += item.Length;
                if (item.Attempts == 1)
                {
                    first++;
                }
                else
                {
                    recovered++;
                }
            }
        }

        foreach (SimNode n in rig.Nodes)
        {
            foreach (SimReceived r in n.Received)
            {
                last = Math.Max(last, r.Clock);
            }
        }

        (int cb, int cp) = Hostile.Collisions(rig.Channel);
        return new ThreeAll(
            Label(capture) + (indication ? ", carrier indication" : ", detection alone"), ledger.Items.Count, delivered, first, recovered, ledger.Items.Count - delivered, ledger.Duplicates().Count, bytes,
            [.. rig.Nodes.Select(Hostile.OnAir)], rig.Nodes.Sum(n => n.Transmissions.Count), cb, cp, ledger.ResendsByHint, ledger.ResendsByTimeout, rig.Nodes.Sum(n => n.NotDecoded.Count), ledger.DroppedCount(), last, ledger.Items.Count(i => i.To != 0 && ledger.ReceivedAt(i, i.To).Copies > 1), rig.Channel.Log);
    }

    public static string Describe(ThreeAll r) =>
        $"three all hear all [{r.Label}]: {r.Delivered}/{r.Items} delivered ({r.DeliveredFirstTry} first try, {r.Recovered} after a resend, {r.Undelivered} never), duplicates {r.Duplicates}, bytes by sender {string.Join("/", r.BytesBySender)}, "
        + $"air s {string.Join("/", r.AirSeconds.Select(a => Hostile.F(a)))}, bursts {r.Bursts}, collided bursts {r.CollidedBursts} in {r.CollidingPairs} pairs, resends by hint {r.ResendsByHint} and by timeout {r.ResendsByTimeout}, hints {r.Hints}, last delivery at {Hostile.Seconds(r.LastDeliveryClock)} s";

    // ---- 2. a hidden station ----

    public sealed record Hidden(
        string Label, string Case, int SentA, int SentC, int DeliveredA, int DeliveredC, int BurstsA, int BurstsC, int CollidedBursts, int ReportsToA, int ReportsToC,
        int FailedReportsAtA, int FailedReportsAtC, int HintsAtA, int HintsAtC, int SilencesA, int SilencesC, string FormatsA, string FormatsC, List<string> Log);

    /// <summary>
    /// A (1) and C (3) cannot hear each other; both hear and are heard by B (2). Each sends B a datagram every 12 s for <paramref name="rounds"/> rounds, the same instant. <paramref name="cDb"/> is C's path to B in dB against A's.
    /// </summary>
    public static Hidden HiddenStation(CaptureModel? capture, bool indication, int seed, double cDb, string name, int rounds = 5, double seconds = 90)
    {
        SimRig rig = Make(3, seed, capture, indication);
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 3, 2, SimChannel.Db(cDb));
        var ledger = new Ledger(rig, seed);
        var rng = new Random(seed * 17 + 5);
        rig.RunTo(6);
        for (int k = 0; k < rounds; k++)
        {
            // C's datagram comes up to 3 s after A's, so that some rounds overlap from the start and some part-way.
            long a = rig.Channel.Clock;
            long c = a + rng.Next(0, 3 * 48000);
            ledger.Send(1, 2);
            rig.Channel.Run(c);
            ledger.Send(3, 2);
            rig.Channel.Run(a + (12L * 48000));
        }

        rig.Run(Math.Max(0, seconds - (rounds * 12)));
        int dA = ledger.Items.Count(i => i.From == 1 && ledger.Delivered(i));
        int dC = ledger.Items.Count(i => i.From == 3 && ledger.Delivered(i));
        (int cb, _) = Hostile.Collisions(rig.Channel);
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? a2);
        rig.Stations[2].Adaptation.TryGetNeighbour(2, out LinkNeighbour? c2);
        SimNode b = rig.Nodes[1];
        return new Hidden(
            Label(capture) + (indication ? ", carrier indication" : ", detection alone"), name, rounds, rounds, dA, dC, rig.Nodes[0].Transmissions.Count, rig.Nodes[2].Transmissions.Count, cb,
            Reports(b, 1), Reports(b, 3),
            rig.Nodes[0].PathReports.Count(p => p.Failed), rig.Nodes[2].PathReports.Count(p => p.Failed), rig.Nodes[0].NotDecoded.Count, rig.Nodes[2].NotDecoded.Count,
            a2?.Silences ?? -1, c2?.Silences ?? -1, Formats(rig.Nodes[0]), Formats(rig.Nodes[2]), rig.Channel.Log);
    }

    public static string Describe(Hidden r) =>
        $"hidden station [{r.Label}] {r.Case}: A sent {r.SentA} delivered {r.DeliveredA} in {r.BurstsA} bursts (formats {r.FormatsA}, silences {r.SilencesA}); C sent {r.SentC} delivered {r.DeliveredC} in {r.BurstsC} bursts (formats {r.FormatsC}, silences {r.SilencesC}); "
        + $"collided bursts {r.CollidedBursts}; B sent lone reports to A {r.ReportsToA} and to C {r.ReportsToC}; failed reports heard at A {r.FailedReportsAtA} and at C {r.FailedReportsAtC}; hints at A {r.HintsAtA} and at C {r.HintsAtC}";

    /// <summary>One round of the hidden-station case: what B heard, said and did, and what A and C made of it.</summary>
    public sealed record HiddenRound(
        string Case, bool ADelivered, bool CDelivered, string BHeard, int ReportsToA, int ReportsToC, bool FailedReportAtA, bool FailedReportAtC, int HintsAtA, int HintsAtC, int SilencesA, int SilencesC, int? FormatAtB, double? MarginForA, double? MarginForC, int? CapForA, int? RecommendationForA, double StartA, double StartC, List<string> Log);

    /// <summary>
    /// A (1) and C (3) cannot hear each other; both are heard by B (2) at the given gains (dB against 0 dB). A's datagram goes at 20 s (the channel has been idle since the start, so it goes at once), C's <paramref name="offsetSeconds"/> later (both first contact, 2 s bursts). 40 s are given for everything to play out.
    /// </summary>
    public static HiddenRound HiddenOne(CaptureModel? capture, int seed, double aDb, double cDb, double offsetSeconds, string name)
    {
        SimRig rig = Make(3, seed, capture, false);
        Pair(rig, 1, 2, SimChannel.Db(aDb));
        Pair(rig, 3, 2, SimChannel.Db(cDb));
        var ledger = new Ledger(rig, seed);
        var callsAtB = new List<Station.AdaptationCall>();
        rig.Stations[1].AdaptationCalled = callsAtB.Add;
        rig.RunTo(20);
        SentItem a = ledger.Send(1, 2);
        rig.Run(offsetSeconds);
        SentItem c = ledger.Send(3, 2);
        rig.Run(40);
        SimNode b = rig.Nodes[1];
        rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? bOfA);
        rig.Stations[1].Adaptation.TryGetNeighbour(3, out LinkNeighbour? bOfC);
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? a2);
        rig.Stations[2].Adaptation.TryGetNeighbour(2, out LinkNeighbour? c2);
        string heard = callsAtB.Count == 0 ? "nothing" : string.Join("; ", callsAtB.Select(x => $"from {x.Sender} fmt {x.Format} decoded {x.Decoded} sudden {x.Sudden} returned {x.Returned?.ToString() ?? "-"}"));
        return new HiddenRound(
            name, ledger.Delivered(a), ledger.Delivered(c), heard, Reports(b, 1), Reports(b, 3),
            rig.Nodes[0].PathReports.Any(p => p.Failed), rig.Nodes[2].PathReports.Any(p => p.Failed), rig.Nodes[0].NotDecoded.Count, rig.Nodes[2].NotDecoded.Count, a2?.Silences ?? -1, c2?.Silences ?? -1,
            bOfA?.Recommended, bOfA?.Margin, bOfC?.Margin, bOfA?.Cap, bOfA?.Recommended, rig.Nodes[0].Transmissions.FirstOrDefault()?.Start / 48000.0 ?? -1, rig.Nodes[2].Transmissions.FirstOrDefault()?.Start / 48000.0 ?? -1, rig.Channel.Log);
    }

    public static string Describe(HiddenRound r) =>
        $"hidden station, one round, {r.Case}: A's datagram delivered {r.ADelivered}, C's {r.CDelivered}; B's receiver: {r.BHeard}; lone reports from B to A {r.ReportsToA} and to C {r.ReportsToC}; failed report heard at A {r.FailedReportAtA} and at C {r.FailedReportAtC}; "
        + $"hints at A {r.HintsAtA} and at C {r.HintsAtC}; silences counted at A {r.SilencesA} and at C {r.SilencesC}; B's margin for A {(r.MarginForA is { } ma ? Hostile.F(ma, "F2") : "-")} and for C {(r.MarginForC is { } mc ? Hostile.F(mc, "F2") : "-")} (3.00 at the start), B's cap for A {r.CapForA?.ToString(CultureInfo.InvariantCulture) ?? "none"}; started at {Hostile.F(r.StartA, "F2")} s and {Hostile.F(r.StartC, "F2")} s";

    // ---- 3. interference part-way through a burst ----

    /// <summary>What B and A did about a burst of A's that a third signal came over part-way.</summary>
    public sealed record Interference(
        string Label, double Fraction, double RelativeDb, int FormatBefore, int PayloadSymbols, bool BurstFound, bool Decoded, bool Sudden, bool ReportedFailed, int? ReportedFormat, bool RecommendationUnchanged,
        bool EstimateUnchanged, bool MarginUnchanged, bool CapUnchanged, bool GoodUnchanged, int? NextFormat, bool NextDelivered, bool BigArrived, string Detail, List<string> Log);

    public static Interference Interfered(CaptureModel? capture, double fraction, double relativeDb, int seed, bool indication = false)
    {
        SimRig rig = Make(3, seed, capture, indication);
        Pair(rig, 1, 2, 0.05);
        rig.Channel.Link(rig.Nodes[2], rig.Nodes[1]).Gain = 0.05 * SimChannel.Db(relativeDb);
        rig.Channel.Link(rig.Nodes[2], rig.Nodes[1]).NoiseRms = 0.004;
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        var callsAtB = new List<Station.AdaptationCall>();
        rig.Stations[1].AdaptationCalled = callsAtB.Add;
        rig.RunTo(6);
        for (int i = 0; i < 4; i++)
        {
            rig.Send(1, 2, 100 + i);
            rig.Run(7);
            rig.Send(2, 1, 200 + i);
            rig.Run(7);
        }

        int format = a.Transmitted[^1].Event.Format;
        rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? atB).Should().BeTrue();
        int length = 200;
        while (PayloadCodec.SymbolsFor(format, 4 + 2 + length) is { } s && s < 24 && length < 20000)
        {
            length += 100;
        }

        int symbols = PayloadCodec.SymbolsFor(format, 4 + 2 + length)!.Value;
        int? recBefore = atB!.Recommended;
        double[] estimateBefore = atB.EstimateDb.ToArray();
        double marginBefore = atB.Margin;
        int? capBefore = atB.Cap;
        int goodBefore = atB.Good;
        int callsBefore = callsAtB.Count;

        // The interfering burst is another station's data burst for everyone, at the same format (so the same drive), cut off at the end of A's burst and put on from its first training sample.
        TestBurst other = TestBurst.Data(3, 0, format, [Ledger.Bytes(3, 0, 0, Ledger.DefaultLength)], symbols: 252);
        bool armed = true;
        rig.Channel.TransmissionStarted += (node, tx) =>
        {
            if (armed && node == a && tx.Length > 4800 * 3)
            {
                armed = false;
                long at = tx.Start + (long)(tx.Length * fraction);
                int remaining = (int)(tx.Start + tx.Length - at);
                int take = Math.Min(remaining, other.Audio.Length - other.Lead);
                float[] cut = other.Audio.AsSpan(other.Lead, take).ToArray();
                rig.Channel.Interfere(rig.Nodes[2], cut, at);
            }
        };
        SendDirect(rig, 1, 2, 900, length);

        // Until the failed report is heard at A (at most 30 s), then a moment more; everything is compared as it stands then, before A sends anything else.
        for (int t = 0; t < 120 && !a.PathReports.Any(p => p.Failed) && !Arrived(rig, 900); t++)
        {
            rig.Run(0.25);
        }

        rig.Run(2);
        bool found = callsAtB.Count > callsBefore;
        Station.AdaptationCall call = found ? callsAtB[callsBefore] : default;
        PathReportEvent? failedReport = a.PathReports.Where(p => p.Address == SimRig.Address(2) && p.Failed).Cast<PathReportEvent?>().LastOrDefault();
        bool decoded = found && call.Decoded;
        int? reportedFormat = failedReport?.RecommendedFormat;
        bool recUnchanged = atB.Recommended == recBefore;
        bool estimateSame = estimateBefore.AsSpan().SequenceEqual(atB.EstimateDb);
        bool marginSame = marginBefore == atB.Margin;
        bool capSame = capBefore == atB.Cap;
        bool goodSame = goodBefore == atB.Good;
        string detail = string.Join(" | ", callsAtB.Skip(callsBefore).Select(c => $"{c.Sender}->{c.Dest} fmt {c.Format} decoded {c.Decoded} sudden {c.Sudden} returned {c.Returned}"));
        bool bigArrived = Arrived(rig, 900);

        // A's next datagram, as soon as it has heard the report (well inside the 30 s a report stays fresh).
        int nextCount = a.Transmitted.Count;
        SendDirect(rig, 1, 2, 901, Ledger.DefaultLength);
        rig.Run(20);
        int? nextFormat = a.Transmitted.Count > nextCount ? a.Transmitted[nextCount].Event.Format : null;
        bool nextDelivered = Arrived(rig, 901);
        return new Interference(
            Label(capture), fraction, relativeDb, format, symbols, found, decoded, found && call.Sudden, failedReport is not null, reportedFormat, recUnchanged,
            estimateSame, marginSame, capSame, goodSame, nextFormat, nextDelivered, bigArrived, detail, rig.Channel.Log);
    }

    private static bool Arrived(SimRig rig, int seq) => rig.Nodes[1].Received.Any(r => r.Data.Length >= 4 && Ledger.Identify(r.Data) == (1, seq, 0));

    private static SentItem SendDirect(SimRig rig, int from, int to, int seq, int length)
    {
        rig.Stations[from - 1].Send(Ledger.Bytes(from, seq, 0, length), SimRig.Address(to), SimRig.Address(from), new HostTag(Ledger.TagOf(from, seq, 0))).Should().Be(SendResult.Accepted);
        return new SentItem { From = from, To = to, Seq = seq, Length = length };
    }

    public static string Describe(Interference r) =>
        $"interference [{r.Label}] at {r.Fraction:P0} of the burst, interferer {r.RelativeDb:+0;-0} dB against A at B: A's burst {r.PayloadSymbols} symbols at format {r.FormatBefore}; found {r.BurstFound}, decoded {r.Decoded}, sudden {r.Sudden}; "
        + $"failed report heard at A {r.ReportedFailed} (format {r.ReportedFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"}); recommendation unchanged {r.RecommendationUnchanged}, estimate {r.EstimateUnchanged}, margin {r.MarginUnchanged}, cap {r.CapUnchanged}, good count {r.GoodUnchanged}; "
        + $"A's next burst at format {r.NextFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"} delivered {r.NextDelivered}; the big datagram arrived {r.BigArrived}; {r.Detail}";

    // ---- 4. overhearing ----

    public sealed record Overheard(
        int HeardMeasures, int? Recommended, int FirstFormat, int ExpectedFormat, double FirstRate, double RecommendedRate, double SupportedRate, int? SupportedFormat, bool FirstDecoded, bool AskedForReport, int FirstContactFormat, List<string> Log);

    /// <summary>C listens to A and B, who exchange traffic, then sends A its first datagram.</summary>
    public static Overheard Overhearing(CaptureModel? capture, int seed, double cGain = 0.3, int pairs = 4)
    {
        SimRig rig = Make(3, seed, capture, false);
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, cGain);
        Pair(rig, 2, 3, cGain);
        SimNode a = rig.Nodes[0];
        SimNode c = rig.Nodes[2];
        rig.RunTo(6);
        for (int i = 0; i < pairs; i++)
        {
            rig.Send(1, 2, 100 + i);
            rig.Run(8);
            rig.Send(2, 1, 200 + i);
            rig.Run(8);
        }

        rig.Stations[2].Adaptation.TryGetNeighbour(1, out LinkNeighbour? ofA).Should().BeTrue();
        int measures = ofA!.Measures;
        int? rec = ofA.Recommended;
        c.Transmissions.Should().BeEmpty("C only listened");
        rig.Send(3, 1, 300);
        rig.Run(30);
        TransmittedEvent first = c.Transmitted[0].Event;
        int expected = rec is { } r ? StandardFormats.Slower(r, LinkAdaptation.Caution) : StandardFormats.FirstContact;
        rig.Stations[0].Adaptation.TryGetNeighbour(3, out LinkNeighbour? aOfC);
        int? supported = aOfC?.Recommended;
        return new Overheard(
            measures, rec, first.Format, expected, first.RateBitsPerSecond, rec is { } rr ? StandardFormats.Rate(rr) * 1000.0 : 0, supported is { } sf ? StandardFormats.Rate(sf) * 1000.0 : 0, supported,
            a.Received.Any(x => x.Sender == 3), true, StandardFormats.FirstContact, rig.Channel.Log);
    }

    public static string Describe(Overheard r) =>
        $"overhearing: C measured A {r.HeardMeasures} times and recommended format {r.Recommended?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({Hostile.F(Kbit(r.RecommendedRate))} kbit/s); its first burst to A went at format {r.FirstFormat} ({Hostile.F(Kbit(r.FirstRate), "F2")} kbit/s; "
        + $"two rungs slower than the recommendation is format {r.ExpectedFormat}; first contact would be {Hostile.F(Kbit(SimRig.FirstContactRate), "F2")} kbit/s); A decoded it {r.FirstDecoded} and then recommended format {r.SupportedFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({Hostile.F(Kbit(r.SupportedRate))} kbit/s)";

    // ---- 5. a burst for everyone ----

    public sealed record Broadcast(double[] GainsDb, bool[] Decoded, int[] Copies, int[] PathReportsAt, int[] ModemSentAt, int Hints, int Transmitted, int Format, List<string> Log);

    /// <summary>A sends one datagram to everyone, with three listeners at the given path gains (dB against 0 dB).</summary>
    public static Broadcast BroadcastTo(CaptureModel? capture, int seed, double[] gainsDb)
    {
        SimRig rig = Make(1 + gainsDb.Length, seed, capture, false);
        for (int i = 0; i < gainsDb.Length; i++)
        {
            Pair(rig, 1, i + 2, SimChannel.Db(gainsDb[i]));
        }

        var ledger = new Ledger(rig);
        rig.RunTo(6);
        SentItem item = ledger.Send(1, 0, Ledger.DefaultLength, reportWanted: true);
        rig.Run(40);
        var decoded = new bool[gainsDb.Length];
        var copies = new int[gainsDb.Length];
        for (int i = 0; i < gainsDb.Length; i++)
        {
            (copies[i], _) = ledger.ReceivedAt(item, i + 2);
            decoded[i] = copies[i] > 0;
        }

        return new Broadcast(
            gainsDb, decoded, copies, [.. rig.Nodes.Select(n => n.PathReports.Count)], [.. rig.Nodes.Select(n => n.ModemSent.Count)], rig.Nodes[0].NotDecoded.Count, rig.Nodes[0].Transmitted.Count, rig.Nodes[0].Transmitted[0].Event.Format, rig.Channel.Log);
    }

    public static string Describe(Broadcast r) =>
        $"broadcast at format {r.Format}: listeners at {string.Join("/", r.GainsDb.Select(g => Hostile.F(g, "F0")))} dB decoded {string.Join("/", r.Decoded)}; path reports at each station {string.Join("/", r.PathReportsAt)}; lone reports or other bursts sent by each {string.Join("/", r.ModemSentAt)}; hints at the sender {r.Hints}";

    // ---- 6. a path cut and restored among three ----

    public sealed record Cut(
        double CutStart, double CutEnd, string AToBBefore, string AToBCut, string AToBAfter, string BToABefore, string BToACut, string BToAAfter, string AToCBefore, string AToCCut, string AToCAfter, string BToCCut,
        int[] DeliveredByPeriod, int[] SentByPeriod, int SilencesAtCutEnd, int MaxSilencesA, int MaxSilencesB, int CRecommendsForABefore, int CRecommendsForAAfterCut, int? BurstsToRecover, double? SecondsToRecover, double PreCutRateKbit, int[] BurstsByPeriod, int[] CollidedByPeriod, int ToCSentInCut, int ToCDeliveredInCut, List<string> Log);

    /// <summary>
    /// Every <paramref name="spacing"/> seconds A sends B and C and B sends A and C, each datagram at a seeded moment within the round (C only answers). Warm-up for <paramref name="warm"/> rounds, then A and B cannot hear each other
    /// for <paramref name="cutRounds"/> rounds, then the path is restored for <paramref name="after"/> rounds.
    /// </summary>
    public static Cut PathCut(CaptureModel? capture, int seed, int warm = 5, int cutRounds = 5, int after = 7, double spacing = 15, bool cutIt = true)
    {
        SimRig rig = Make(3, seed, capture, false);
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, 1.0);
        Pair(rig, 2, 3, 1.0);
        var ledger = new Ledger(rig, seed) { MaxAttempts = 1 };
        var rng = new Random(seed * 13 + 1);
        long t0 = 5L * 48000 + 24000;
        long round = (long)(spacing * 48000);
        long cutStart = t0 + (warm * round);
        long cutEnd = cutStart + (cutRounds * round);
        long end = cutEnd + (after * round);
        var plan = new List<Ledger.Plan>();
        for (int r = 0; r < warm + cutRounds + after; r++)
        {
            foreach ((int from, int to) in new[] { (1, 2), (2, 1), (1, 3), (2, 3) })
            {
                plan.Add(new Ledger.Plan(t0 + (r * round) + rng.Next(0, (int)(round * 0.8)), from, to, 40));
            }
        }

        plan.Sort((x, y) => x.Clock.CompareTo(y.Clock));
        ledger.Schedule(plan);
        int maxA = 0;
        int maxB = 0;
        int silencesAtEnd = 0;
        int cRecBefore = -1;
        int cRecAfterCut = -1;
        void Watch()
        {
            if (rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? ab))
            {
                maxA = Math.Max(maxA, ab!.Silences);
            }

            if (rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? ba))
            {
                maxB = Math.Max(maxB, ba!.Silences);
            }
        }

        rig.RunTo(5.5);
        ledger.RunWithHost((cutStart - rig.Channel.Clock) / 48000.0, 0.5, null, Watch);
        if (rig.Stations[2].Adaptation.TryGetNeighbour(1, out LinkNeighbour? c1))
        {
            cRecBefore = c1!.Recommended ?? -1;
        }

        SetGain(rig, 1, 2, cutIt ? 0 : 1.0);
        ledger.RunWithHost((cutEnd - rig.Channel.Clock) / 48000.0, 0.5, null, Watch);
        if (rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? ab2))
        {
            silencesAtEnd = ab2!.Silences;
        }

        if (rig.Stations[2].Adaptation.TryGetNeighbour(1, out LinkNeighbour? c2))
        {
            cRecAfterCut = c2!.Recommended ?? -1;
        }

        SetGain(rig, 1, 2, 1.0);
        ledger.RunWithHost(((end - rig.Channel.Clock) / 48000.0) + 30, 0.5, null, Watch);

        string Fmts(SimNode n, int to, long from, long until) =>
            string.Join(",", n.Transmitted.Where(t => t.Event.Address == SimRig.Address(to) && t.Clock >= from && t.Clock < until).Select(t => t.Event.Format));
        var delivered = new int[3];
        var sent = new int[3];
        foreach (SentItem item in ledger.Items)
        {
            int period = item.FirstSentClock < cutStart ? 0 : item.FirstSentClock < cutEnd ? 1 : 2;
            sent[period]++;
            if (ledger.Delivered(item))
            {
                delivered[period]++;
            }
        }

        // The rate A was sending B at before the cut (its best of the warm-up), and how long after the restoration it takes to be back at nine tenths of it.
        double pre = rig.Nodes[0].Transmitted.Where(t => t.Event.Address == SimRig.Address(2) && t.Clock < cutStart).Select(t => t.Event.RateBitsPerSecond).DefaultIfEmpty(0).Max();
        int bursts = 0;
        int? burstsToRecover = null;
        double? secondsToRecover = null;
        foreach ((long clock, TransmittedEvent e) in rig.Nodes[0].Transmitted.Where(t => t.Event.Address == SimRig.Address(2) && t.Clock >= cutEnd))
        {
            bursts++;
            if (e.RateBitsPerSecond >= 0.9 * pre)
            {
                burstsToRecover = bursts;
                secondsToRecover = (clock - cutEnd) / 48000.0;
                break;
            }
        }

        long[] edges = [0, cutStart, cutEnd, long.MaxValue];
        int[] bursts3 = [.. Enumerable.Range(0, 3).Select(k => rig.Nodes.Sum(n => n.Transmissions.Count(t => t.Start >= edges[k] && t.Start < edges[k + 1])))];
        int[] collided3 = [.. Enumerable.Range(0, 3).Select(k => Hostile.Collisions(rig.Channel, edges[k], edges[k + 1]).Bursts)];
        return new Cut(
            cutStart / 48000.0, cutEnd / 48000.0, Fmts(rig.Nodes[0], 2, 0, cutStart), Fmts(rig.Nodes[0], 2, cutStart, cutEnd), Fmts(rig.Nodes[0], 2, cutEnd, long.MaxValue),
            Fmts(rig.Nodes[1], 1, 0, cutStart), Fmts(rig.Nodes[1], 1, cutStart, cutEnd), Fmts(rig.Nodes[1], 1, cutEnd, long.MaxValue),
            Fmts(rig.Nodes[0], 3, 0, cutStart), Fmts(rig.Nodes[0], 3, cutStart, cutEnd), Fmts(rig.Nodes[0], 3, cutEnd, long.MaxValue), Fmts(rig.Nodes[1], 3, cutStart, cutEnd),
            delivered, sent, silencesAtEnd, maxA, maxB, cRecBefore, cRecAfterCut, burstsToRecover, secondsToRecover, pre / 1000.0, bursts3, collided3, ledger.Items.Count(i => i.To == 3 && i.FirstSentClock >= cutStart && i.FirstSentClock < cutEnd), ledger.Items.Count(i => i.To == 3 && i.FirstSentClock >= cutStart && i.FirstSentClock < cutEnd && ledger.Delivered(i)), rig.Channel.Log);
    }

    public static string Describe(Cut r) =>
        $"path cut among three (A to B cut from {Hostile.F(r.CutStart)} s to {Hostile.F(r.CutEnd)} s): A to B formats before [{r.AToBBefore}] during [{r.AToBCut}] after [{r.AToBAfter}]; B to A before [{r.BToABefore}] during [{r.BToACut}] after [{r.BToAAfter}]; "
        + $"A to C before [{r.AToCBefore}] during [{r.AToCCut}] after [{r.AToCAfter}]; B to C during [{r.BToCCut}]; delivered/sent by the period a datagram was sent in (warm-up, cut, after) {string.Join(" ", r.DeliveredByPeriod.Zip(r.SentByPeriod, (d, s) => $"{d}/{s}"))}; "
        + $"silences at A for B at the end of the cut {r.SilencesAtCutEnd}, most at A {r.MaxSilencesA} and at B {r.MaxSilencesB}; C's recommendation for A before the cut {r.CRecommendsForABefore} and after {r.CRecommendsForAAfterCut}; "
        + $"bursts by all stations in the three periods {string.Join("/", r.BurstsByPeriod)}, of which collided at C {string.Join("/", r.CollidedByPeriod)}; datagrams to C sent in the cut {r.ToCSentInCut}, delivered {r.ToCDeliveredInCut}; "
        + $"A back at 90 percent of its pre-cut {Hostile.F(r.PreCutRateKbit)} kbit/s after {r.BurstsToRecover?.ToString(CultureInfo.InvariantCulture) ?? "never"} bursts and {(r.SecondsToRecover is { } sr ? Hostile.F(sr) : "-")} s";

    // ---- 7. identification among three ----

    public sealed record Idents(
        int[] Sent, int[] Due, string[][] HeardAt, string[][] NamesAt, double[][] StartsSeconds, int IdentBursts, int IdentsOverlapped, int TrafficOverlappedByIdent, int TrafficBursts, int[] DeliveredToAll, string Log0, List<string> Log);

    /// <summary>
    /// A and B send each other a datagram every 20 s; C hears them, sends A one datagram at 60 s and nothing else. Identification is on, with an interval of <paramref name="intervalSeconds"/>. A and B know each other by the host's names and
    /// know nothing of C; C knows both.
    /// </summary>
    public static Idents Identification(CaptureModel? capture, int seed, double intervalSeconds, double seconds, bool indication = false)
    {
        SimRig rig = Make(3, seed, capture, indication, (i, j) => !(j == 3 && i != 3));
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, 1.0);
        Pair(rig, 2, 3, 1.0);
        foreach (Station st in rig.Stations)
        {
            st.IdentificationSuspended = false;
            st.IdentificationInterval = (long)(intervalSeconds * 48000);
        }

        var ledger = new Ledger(rig, seed) { MaxAttempts = 1 };
        var plan = new List<Ledger.Plan>();
        for (long t = 5L * 48000 + 24000; t < (seconds - 20) * 48000; t += 20L * 48000)
        {
            plan.Add(new Ledger.Plan(t, 1, 2, Ledger.DefaultLength));
            plan.Add(new Ledger.Plan(t + (10L * 48000) + 12345, 2, 1, Ledger.DefaultLength));
        }

        plan.Add(new Ledger.Plan(60L * 48000 + 777, 3, 1, Ledger.DefaultLength));
        plan.Sort((x, y) => x.Clock.CompareTo(y.Clock));
        rig.RunTo(5.5);
        ledger.RunWithHost(seconds - 5.5, 0.5, plan);

        // Every identification burst: the node, the start and the length (the audio layer's own reports).
        var idents = new List<(int Node, SimTransmission T)>();
        foreach (string line in rig.Channel.Log.Where(l => l.Contains(" modem sent Identification", StringComparison.Ordinal)))
        {
            string[] w = line.Split(' ');
            int node = int.Parse(w[1].AsSpan(1), CultureInfo.InvariantCulture) - 1;
            long clock = long.Parse(w[0], CultureInfo.InvariantCulture);
            SimTransmission? t = rig.Nodes[node].Transmissions.FirstOrDefault(x => x.Start == clock);
            if (t is not null)
            {
                idents.Add((node, t));
            }
        }

        bool Overlaps(SimTransmission a, SimTransmission b) => a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;
        int overlapped = 0;
        int trafficHit = 0;
        int traffic = 0;
        foreach (SimNode n in rig.Nodes)
        {
            foreach (SimTransmission t in n.Transmissions)
            {
                bool isIdent = idents.Any(i => i.T == t);
                if (!isIdent)
                {
                    traffic++;
                }

                bool hit = rig.Nodes.Any(o => o != n && o.Transmissions.Any(u => Overlaps(t, u)));
                if (hit && isIdent)
                {
                    overlapped++;
                }
                else if (hit && idents.Any(i => i.Node != n.Index && Overlaps(t, i.T)))
                {
                    trafficHit++;
                }
            }
        }

        string[][] heard = [.. rig.Nodes.Select(n => n.Identified.Select(i => i.Event.Callsign + "@" + i.Sender).ToArray())];
        string[][] names = [.. rig.Nodes.Select(n => n.Identified.Select(i => i.Event.Callsign).ToArray())];
        double[][] starts = [.. rig.Nodes.Select(n => idents.Where(i => i.Node == n.Index).Select(i => i.T.Start / 48000.0).ToArray())];
        int[] deliveredToAll = [.. rig.Nodes.Select(n => n.Received.Count)];
        return new Idents(
            [.. rig.Stations.Select(s => (int)s.IdentificationsSent)], [.. rig.Stations.Select(s => (int)s.IdentificationsDue)], heard, names, starts, idents.Count, overlapped, trafficHit, traffic, deliveredToAll, "", rig.Channel.Log);
    }

    public static string Describe(Idents r) =>
        $"identification among three: sent {string.Join("/", r.Sent)}, due {string.Join("/", r.Due)}; starts (s) A [{string.Join(",", r.StartsSeconds[0].Select(x => Hostile.F(x)))}] B [{string.Join(",", r.StartsSeconds[1].Select(x => Hostile.F(x)))}] C [{string.Join(",", r.StartsSeconds[2].Select(x => Hostile.F(x)))}]; "
        + $"heard at A [{string.Join(",", r.HeardAt[0])}] B [{string.Join(",", r.HeardAt[1])}] C [{string.Join(",", r.HeardAt[2])}]; names at A [{string.Join(",", r.NamesAt[0].Distinct())}] B [{string.Join(",", r.NamesAt[1].Distinct())}]; "
        + $"identification bursts {r.IdentBursts}, overlapped by another burst {r.IdentsOverlapped}; of {r.TrafficBursts} other bursts {r.TrafficOverlappedByIdent} overlapped by an identification";

    // ---- 8. the soak ----

    public sealed record Soak(
        int Offered, int Refused, int Accepted, int Transmitted, int Dropped, int StillQueued, int Delivered, int Lost, int LostOverlapped, int LostByPath, int Duplicates, int DeliveredWithoutEvent, int Bursts, int CollidedBursts, int[] BytesBySender,
        double MaxWaitSeconds, int MaxBurstsInFlight, int MaxCandidatesWaiting, int MaxTimers, int MaxQueued, int ReceivedByB, string FormatsFromA, int Hints, int WrongHints, int MisattributedReports, int Congestions, int Reordered, int ClocksBackwards, int Cancelled, int NoLongerFit, List<string> Log);

    /// <summary>
    /// Three stations, mixed traffic (to one station or to everyone, short and long, ordinary and expedited) at seeded moments for <paramref name="seconds"/>, with the path between A and B fading in steps (0, -20, -40, -20, 0 dB, then cut
    /// for 20 s). The hosts resend nothing. After the traffic stops the stations are left <paramref name="drain"/> seconds to empty their queues.
    /// </summary>
    public static Soak Soaking(CaptureModel? capture, int seed, double seconds = 240, double drain = 90, double meanGapSeconds = 9, bool indication = false)
    {
        SimRig rig = Make(3, seed, capture, indication);
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, 1.0);
        Pair(rig, 2, 3, 1.0);
        var ledger = new Ledger(rig, seed) { MaxAttempts = 1 };
        var rng = new Random(seed * 101 + 9);
        var plan = new List<Ledger.Plan>();
        for (int from = 1; from <= 3; from++)
        {
            double t = 5.5 + (rng.NextDouble() * meanGapSeconds);
            while (t < seconds)
            {
                int to = rng.Next(10) == 0 ? 0 : (from + rng.Next(1, 3) - 1) % 3 + 1;
                if (to == from)
                {
                    to = (from % 3) + 1;
                }

                int length = rng.Next(8) == 0 ? rng.Next(150, 400) : rng.Next(10, 60);
                plan.Add(new Ledger.Plan((long)(t * 48000), from, to, length, rng.Next(7) == 0 ? SendPriority.Expedited : SendPriority.Ordinary, rng.Next(12) == 0 ? (long)(rng.Next(1, 20) * 48000) : 0));
                t += rng.NextDouble() * 2 * meanGapSeconds;
            }
        }

        plan.Sort((x, y) => x.Clock.CompareTo(y.Clock));
        double[] fade = [0, -20, -40, -20, 0];
        double stepSeconds = seconds / (fade.Length + 0.0);
        int maxFlight = 0, maxCand = 0, maxTimers = 0, maxQueued = 0;
        double maxWait = 0;
        var waitingSince = new long[3];
        var seenTransmissions = new int[3];
        void Watch()
        {
            for (int i = 0; i < 3; i++)
            {
                Station st = rig.Stations[i];
                maxFlight = Math.Max(maxFlight, st.BurstsInFlight);
                maxCand = Math.Max(maxCand, st.CandidatesWaiting);
                maxTimers = Math.Max(maxTimers, st.PendingTimers);
                int queued = st.QueuedTotal;
                maxQueued = Math.Max(maxQueued, queued);
                long now = rig.Channel.Clock;
                if (rig.Nodes[i].Transmissions.Count != seenTransmissions[i] || queued == 0)
                {
                    seenTransmissions[i] = rig.Nodes[i].Transmissions.Count;
                    waitingSince[i] = now;
                }

                maxWait = Math.Max(maxWait, (now - waitingSince[i]) / 48000.0);
            }
        }

        rig.RunTo(5.5);
        ledger.Schedule(plan);
        for (int k = 0; k < fade.Length; k++)
        {
            SetGain(rig, 1, 2, SimChannel.Db(fade[k]));
            ledger.RunWithHost(stepSeconds, 0.5, null, Watch);
        }

        SetGain(rig, 1, 2, 0);
        ledger.RunWithHost(20, 0.5, null, Watch);
        SetGain(rig, 1, 2, 1.0);
        ledger.RunWithHost(drain, 0.5, null, Watch);

        int accepted = ledger.Items.Count - ledger.Refused.Count;
        int transmitted = ledger.Items.Count(i => i.TransmittedClocks.Count > 0);
        int delivered = ledger.Items.Count(ledger.Delivered);
        int lost = 0, lostOverlap = 0;
        foreach (SentItem item in ledger.Items.Where(i => i.To != 0 && i.TransmittedClocks.Count > 0 && !ledger.Delivered(i)))
        {
            lost++;
            long end = item.TransmittedClocks[0];
            SimTransmission? tx = rig.Nodes[item.From - 1].Transmissions.FirstOrDefault(t => t.Start + t.Length == end);
            bool overlapped = tx is not null && rig.Nodes.Any(o => o.Index != item.From - 1 && o.Transmissions.Any(u => u.Start < tx.Start + tx.Length && tx.Start < u.Start + u.Length));
            if (overlapped)
            {
                lostOverlap++;
            }
        }

        var bytes = new int[3];
        foreach (SentItem item in ledger.Items.Where(ledger.Delivered))
        {
            bytes[item.From - 1] += item.Length;
        }

        (int cb, _) = Hostile.Collisions(rig.Channel);
        // A hint must name datagrams that this station sent to the station that said so, and that did not arrive.
        int wrongHints = 0;
        foreach (SimNode n in rig.Nodes)
        {
            foreach ((long _, LinkAddress who, long[] tags) in n.NotDecoded)
            {
                int number = Enumerable.Range(1, 3).First(k => SimRig.Address(k) == who);
                foreach (long tag in tags)
                {
                    SentItem? item = ledger.ItemOf(tag);
                    if (item is null || item.From != n.Index + 1 || item.To != number || ledger.Delivered(item))
                    {
                        wrongHints++;
                        Hostile.Say($"wrong hint at {n.Name}: station {number} tags {string.Join(",", tags)}; tag {tag} is " + (item is null ? "unknown" : $"{item.From}->{item.To} seq {item.Seq} delivered {ledger.Delivered(item)} transmitted at {string.Join(",", item.TransmittedClocks.Select(Hostile.Seconds))}"));
                    }
                }
            }
        }

        // A path report heard at x about y needs a burst or a lone report from y to x to have gone.
        int misattributed = 0;
        for (int x = 1; x <= 3; x++)
        {
            for (int y = 1; y <= 3; y++)
            {
                if (x == y)
                {
                    continue;
                }

                int heard = rig.Nodes[x - 1].PathReports.Count(p => p.Address == SimRig.Address(y));
                int sentLone = Reports(rig.Nodes[y - 1], x);
                int sentData = rig.Nodes[y - 1].Transmitted.Where(t => t.Event.Address == SimRig.Address(x)).Select(t => t.Clock).Distinct().Count();
                if (heard > sentLone + sentData)
                {
                    misattributed++;
                }
            }
        }

        int stillQueued = Enumerable.Range(0, 3).Sum(i => rig.Stations[i].QueuedTotal);
        return new Soak(
            ledger.Items.Count, ledger.Refused.Count, accepted, transmitted, ledger.DroppedCount(), stillQueued, delivered, lost, lostOverlap, lost - lostOverlap, ledger.Duplicates().Count, ledger.DeliveredWithoutTransmittedEvent(),
            rig.Nodes.Sum(n => n.Transmissions.Count), cb, bytes, maxWait, maxFlight, maxCand, maxTimers, maxQueued, rig.Nodes[1].Received.Count, Formats(rig.Nodes[0], 40), rig.Nodes.Sum(n => n.NotDecoded.Count), wrongHints, misattributed, rig.Nodes.Sum(n => n.Congestion.Count), ledger.Reordered(), Ledger.ClocksGoingBackwards(rig.Channel.Log), rig.Nodes.Sum(n => n.Dropped.Count(d => d.Reason == DropReason.Cancelled)), rig.Nodes.Sum(n => n.Dropped.Count(d => d.Reason == DropReason.NoLongerFits)), rig.Channel.Log);
    }

    public static string Describe(Soak r) =>
        $"soak: offered {r.Offered} (refused {r.Refused}), transmitted {r.Transmitted}, dropped {r.Dropped} ({r.Cancelled} cancelled by the host, {r.NoLongerFit} no longer fit after the format fell), still queued {r.StillQueued}, delivered {r.Delivered}; addressed datagrams lost on the air {r.Lost} ({r.LostOverlapped} in a burst another station overlapped, {r.LostByPath} not); "
        + $"duplicates {r.Duplicates}, delivered with no Transmitted event {r.DeliveredWithoutEvent}, hints {r.Hints} (wrong {r.WrongHints}), path reports with no burst or report to explain them {r.MisattributedReports}, ordinary datagrams reordered {r.Reordered}, log clocks going backwards {r.ClocksBackwards}; bursts {r.Bursts}, collided {r.CollidedBursts}; bytes delivered by sender {string.Join("/", r.BytesBySender)}; "
        + $"longest a station with traffic went without transmitting {Hostile.F(r.MaxWaitSeconds)} s; at most in flight {r.MaxBurstsInFlight}, candidates waiting {r.MaxCandidatesWaiting}, timers {r.MaxTimers}, queued {r.MaxQueued}; A's formats {r.FormatsFromA}";

    // ---- two stations asked to send at nearly the same moment on an idle channel ----

    public sealed record IdleStart(bool Indication, double OffsetSeconds, bool Collided, bool BothDelivered, double StartGapSeconds, List<string> Log);

    /// <summary>A (1) and B (2) hear each other and a silent C (3) hears both; the channel has been idle since the start; each is given a datagram for the other, B's <paramref name="offsetSeconds"/> after A's.</summary>
    public static IdleStart IdleStartOffset(CaptureModel? capture, bool indication, double offsetSeconds, int seed = 1)
    {
        SimRig rig = Make(3, seed, capture, indication);
        Pair(rig, 1, 2, 1.0);
        Pair(rig, 1, 3, 1.0);
        Pair(rig, 2, 3, 1.0);
        var ledger = new Ledger(rig, seed);
        rig.RunTo(20);
        SentItem a = ledger.Send(1, 2);
        rig.Run(offsetSeconds);
        SentItem b = ledger.Send(2, 1);
        rig.Run(30);
        (_, int pairs) = Hostile.Collisions(rig.Channel);
        double gap = rig.Nodes[0].Transmissions.Count > 0 && rig.Nodes[1].Transmissions.Count > 0 ? (rig.Nodes[1].Transmissions[0].Start - rig.Nodes[0].Transmissions[0].Start) / 48000.0 : -1;
        return new IdleStart(indication, offsetSeconds, pairs > 0, ledger.Delivered(a) && ledger.Delivered(b), gap, rig.Channel.Log);
    }

    // ---- a flood ----

    public sealed record Flood(int Offered, int Accepted, int Refused, int Transmitted, int Dropped, int Delivered, int Congested, int Writable, int StillQueued, double DrainedAtSeconds, int MaxQueued);

    /// <summary>A hands B <paramref name="count"/> datagrams of <paramref name="length"/> bytes at once (more than its queue takes) and keeps offering them as it drains; B sends nothing.</summary>
    public static Flood Flooding(CaptureModel? capture, int seed, int count = 60, int length = 300, double seconds = 400)
    {
        SimRig rig = Make(2, seed, capture, false);
        Pair(rig, 1, 2, 1.0);
        var ledger = new Ledger(rig, seed) { MaxAttempts = 1 };
        rig.RunTo(6);
        int maxQueued = 0;
        long drainedAt = -1;
        int offered = 0;
        for (int step = 0; step < (int)(seconds * 2); step++)
        {
            if (offered < count && (step % 4 == 0))
            {
                // keep offering: 5 datagrams every two seconds until the lot has been offered
                for (int i = 0; i < 5 && offered < count; i++, offered++)
                {
                    ledger.Send(1, 2, length);
                }
            }

            rig.Run(0.5);
            ledger.HostStep();
            int queued = rig.Stations[0].QueuedFor(SimRig.Address(2));
            maxQueued = Math.Max(maxQueued, queued);
            if (offered >= count && queued == 0 && drainedAt < 0)
            {
                drainedAt = rig.Channel.Clock;
            }
        }

        SimNode a = rig.Nodes[0];
        return new Flood(
            ledger.Items.Count, ledger.Items.Count - ledger.Refused.Count, ledger.Refused.Count, ledger.Items.Count(i => i.TransmittedClocks.Count > 0), ledger.DroppedCount(), ledger.Items.Count(ledger.Delivered),
            a.Congestion.Count(c => !c.Writable), a.Congestion.Count(c => c.Writable), rig.Stations[0].QueuedFor(SimRig.Address(2)), drainedAt < 0 ? -1 : drainedAt / 48000.0, maxQueued);
    }
}
