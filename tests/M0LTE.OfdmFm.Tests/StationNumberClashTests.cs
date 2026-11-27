using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step two of docs/station-numbers.md through the simulated channel: two stations that hold one number in hearing of each other (the one that hears the other moves), and two that hold one number and cannot hear each other (a third
/// that hears both resets what it knew, sends one Clash, and the later station moves). The stations are given numbers and ids by the tests' seam so that the clash is the one under test; after it they choose their own numbers by the rule.
/// Each scenario runs over seeds 1 to 8 and prints what each seed did; only what holds on every seed is asserted.
/// </summary>
public class StationNumberClashTests
{
    /// <summary>Seeds 1 to 8 in the gated tier (SM_MASK_GATE=1), where the figures in the pull request come from, and 1 to 3 otherwise: each seed is minutes of simulated traffic.</summary>
    private static readonly int[] Seeds = Hostile.Gated ? [1, 2, 3, 4, 5, 6, 7, 8] : [1, 2, 3];

    private static void Indicate(SimRig rig, bool indication)
    {
        if (indication)
        {
            foreach (SimNode n in rig.Nodes)
            {
                n.CarrierDelaySamples = 4800;
            }
        }
    }

    private static string Mode(bool indication) => indication ? "carrier indication" : "detection alone";

    /// <summary>The datagrams accepted, less what the ledger refused, plus the ones a test sent by hand and was accepted.</summary>
    private sealed class Accounts(SimRig rig, Ledger ledger)
    {
        public int Direct;

        public int Accepted => ledger.Items.Sum(i => i.Attempts) - ledger.Refused.Count + Direct;

        public int Transmitted => rig.Nodes.Sum(n => n.Transmitted.Count);

        public int Dropped => rig.Nodes.Sum(n => n.Dropped.Count);

        public int Queued => rig.Stations.Sum(s => s.QueuedTotal);

        public int Send(int from, int to, int tag, int length = 30)
        {
            if (rig.Send(from, to, tag, length) == SendResult.Accepted)
            {
                Direct++;
            }

            return Direct;
        }
    }

    // ---- no false moves ----

    [Fact]
    public void Stations_With_Different_Numbers_Never_Move_Or_Clash_Whatever_They_Say_To_Each_Other()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var rig = new SimRig(3, seed: seed, numbers: [11, 22, 33], ids: [0x01010101, 0x02020202, 0x03030303], locates: (_, _) => false);
            foreach (Station s in rig.Stations)
            {
                s.IdentificationSuspended = false;
                s.IdentificationInterval = 20L * 48000;
            }

            var ledger = new Ledger(rig, seed);
            var plan = new List<Ledger.Plan>();
            for (int k = 0; k < 4; k++)
            {
                for (int from = 1; from <= 3; from++)
                {
                    plan.Add(new Ledger.Plan((long)((8 + (k * 25) + (from * 4)) * 48000L), from, (from % 3) + 1, 30));
                    plan.Add(new Ledger.Plan((long)((9 + (k * 25) + (from * 4)) * 48000L), from, 0, 30));
                }
            }

            plan.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            rig.RunTo(6);
            ledger.RunWithHost(150, 0.5, plan);
            rig.Stations.Select(s => s.NumberChanges).Should().OnlyContain(c => c == 0, $"seed {seed}");
            rig.Stations.Select(s => s.ClashesSent).Should().OnlyContain(c => c == 0);
            rig.Stations.Select(s => s.StationNumber).Should().Equal([11, 22, 33]);
            ledger.Duplicates().Should().BeEmpty();
            rig.Nodes.Sum(n => n.Identified.Count).Should().BeGreaterThan(0, "identification bursts were sent and read");
            rig.Nodes.Sum(n => n.Dropped.Count).Should().Be(0);
        }
    }

    // ---- two stations with one number, in hearing of each other ----

    private sealed record Direct(int Seed, bool Indication, int[] Numbers, int[] Changes, int Offered, int Delivered, int Duplicates, int Accepted, int Transmitted, int Dropped, int Queued, bool FirstDelivered, int ClashesSent);

    /// <summary>
    /// A (1) and B (2) both hold number 5 and hear each other; C (3) holds 9 and hears both. A sends C one datagram: B reads a header with its own number and moves. Then every station sends datagrams to every other for a while.
    /// </summary>
    private static Direct DirectClash(int seed, bool indication)
    {
        var rig = new SimRig(3, seed: seed, numbers: [5, 5, 9], ids: [0xA1A1A1A1, 0xB2B2B2B2, 0xC3C3C3C3], locates: (_, _) => false);
        Indicate(rig, indication);
        foreach (Station s in rig.Stations)
        {
            s.IdentificationSuspended = false;
        }

        var ledger = new Ledger(rig, seed);
        var acc = new Accounts(rig, ledger);
        rig.RunTo(6);
        acc.Send(1, 3, 1).Should().Be(1);
        rig.Run(14);
        int firstNumber = rig.Stations[1].StationNumber;
        rig.Stations[0].NumberChanges.Should().Be(0, "A sent the burst: it is not told what it said itself");
        rig.Stations[1].NumberChanges.Should().BeGreaterThanOrEqualTo(1, "B read a header with its own number");
        firstNumber.Should().NotBe(5);
        bool first = rig.Nodes[2].Received.Any(r => r.Data.AsSpan().SequenceEqual(SimRig.Datagram(1, 30)));

        var plan = new List<Ledger.Plan>();
        for (int k = 0; k < 2; k++)
        {
            for (int from = 1; from <= 3; from++)
            {
                for (int to = 1; to <= 3; to++)
                {
                    if (from != to)
                    {
                        plan.Add(new Ledger.Plan((long)((25 + (k * 15) + (from * 2) + to) * 48000L), from, to, 30));
                    }
                }
            }
        }

        plan.Sort((a, b) => a.Clock.CompareTo(b.Clock));
        ledger.RunWithHost(100, 0.5, plan);
        int delivered = ledger.Items.Count(ledger.Delivered);
        return new Direct(seed, indication, [.. rig.Stations.Select(s => s.StationNumber)], [.. rig.Stations.Select(s => (int)s.NumberChanges)], ledger.Items.Count, delivered, ledger.Duplicates().Count, acc.Accepted, acc.Transmitted, acc.Dropped, acc.Queued, first, rig.Stations.Sum(s => (int)s.ClashesSent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_Stations_With_One_Number_In_Hearing_Of_Each_Other_The_One_That_Hears_The_Other_Moves_And_Traffic_Carries_On(bool indication)
    {
        var results = Seeds.Select(seed => DirectClash(seed, indication)).ToList();
        foreach (Direct r in results)
        {
            Hostile.Say($"direct clash, {Mode(indication)}, seed {r.Seed}: numbers {string.Join("/", r.Numbers)} after {string.Join("/", r.Changes)} moves; the first datagram reached C {r.FirstDelivered}; delivered {r.Delivered}/{r.Offered}, duplicates {r.Duplicates}, accepted {r.Accepted} = transmitted {r.Transmitted} + dropped {r.Dropped} + queued {r.Queued}, Clash bursts {r.ClashesSent}");
            r.Duplicates.Should().Be(0, $"seed {r.Seed}: no datagram is delivered twice");
            (r.Transmitted + r.Dropped + r.Queued).Should().Be(r.Accepted, $"seed {r.Seed}: every datagram went out, was reported dropped or is queued");
            r.Queued.Should().Be(0);
            r.Numbers.Distinct().Should().HaveCount(3, $"seed {r.Seed}: the stations end with numbers of their own");
            r.Numbers.Should().OnlyContain(n => n >= 1 && n <= 63);
            r.Changes[0].Should().Be(0, "the station that was heard did not move");
            r.Changes[1].Should().BeGreaterThanOrEqualTo(1);
            r.FirstDelivered.Should().BeTrue($"seed {r.Seed}: the burst that told B was not harmed");
        }

        // What holds on every seed is above. Delivery of all that the hosts offered after the move is a total, not a rule (the host resends what the station says it could not decode; a burst can be lost to a collision).
        Hostile.Say($"direct clash, {Mode(indication)}: seeds {string.Join(",", Seeds)}: delivered {results.Sum(r => r.Delivered)} of {results.Sum(r => r.Offered)} over seeds 1 to 8; stations that moved more than once: {results.Count(r => r.Changes.Any(c => c > 1))} of 8 seeds");
        results.Sum(r => r.Delivered).Should().BeGreaterThan(results.Sum(r => r.Offered) * 3 / 4);
    }

    // ---- both notice ----

    [Fact]
    public void When_Two_Stations_Both_Hear_A_Third_With_Their_Number_Both_Move_And_They_End_With_Different_Numbers()
    {
        var table = new List<string>();
        foreach (int seed in Seeds)
        {
            // A (1) and B (2) listen; C (3) holds the same number and talks: both read a header with their own number in the same burst.
            var rig = new SimRig(3, seed: seed, numbers: [5, 5, 5], ids: [0xA1A1A1A1, 0xB2B2B2B2, 0xC3C3C3C3], locates: (_, _) => false);
            rig.RunTo(6);
            rig.Send(3, LinkAddress.Everyone, 1).Should().Be(SendResult.Accepted);
            rig.Run(10);
            rig.Stations[0].NumberChanges.Should().Be(1, $"seed {seed}");
            rig.Stations[1].NumberChanges.Should().Be(1);
            rig.Stations[2].NumberChanges.Should().Be(0);
            int[] firstPick = [rig.Stations[0].StationNumber, rig.Stations[1].StationNumber];
            firstPick.Should().NotContain(5);

            // Then each of them says something, twice over, so that anyone left holding another's number hears it.
            for (int round = 0; round < 3; round++)
            {
                for (int s = 1; s <= 3; s++)
                {
                    rig.Send(s, LinkAddress.Everyone, 10 + (round * 3) + s).Should().Be(SendResult.Accepted);
                    rig.Run(9);
                }
            }

            int[] end = [.. rig.Stations.Select(s => s.StationNumber)];
            table.Add($"seed {seed}: picked {firstPick[0]} and {firstPick[1]}{(firstPick[0] == firstPick[1] ? " (the same)" : string.Empty)}, end {string.Join("/", end)}, moves {string.Join("/", rig.Stations.Select(s => s.NumberChanges))}");
            end.Distinct().Should().HaveCount(3, $"seed {seed}: different numbers at the end");
        }

        Hostile.Say("both noticing: " + string.Join("; ", table));
    }

    // ---- two stations with one number, hidden from each other ----

    private sealed record Hidden(
        int Seed, bool Indication, int Observers, int ClashesSent, int[] ClashesByObserver, bool ClashNamedLaterId, int AMoves, int CMoves, int ANumber, int CNumber, bool ResetNeighbour, bool AddressesForgotten, bool DroppedForgotten, bool GoneTold,
        int ClashesOwed, int Offered, int Delivered, int Duplicates, int Accepted, int Transmitted, int Dropped, int Queued);

    /// <summary>
    /// A (1) and C (3) hold number 5 and cannot hear each other; B (2), and with <paramref name="observers"/> 2 also D (4), hear both. A sends B something, then C does, and B has a datagram queued for A's address when C's identification is heard.
    /// After the Clash the hosts at all four exchange datagrams.
    /// </summary>
    private static Hidden HiddenClash(int seed, bool indication, int observers, bool afterwards = true)
    {
        int stations = 2 + observers;
        int[] numbers = observers == 1 ? [5, 9, 5] : [5, 9, 5, 10];
        uint[] ids = observers == 1 ? [0xA1A1A1A1, 0xB2B2B2B2, 0xC3C3C3C3] : [0xA1A1A1A1, 0xB2B2B2B2, 0xC3C3C3C3, 0xD4D4D4D4];
        var rig = new SimRig(stations, seed: seed, connect: false, numbers: numbers, ids: ids, locates: (_, _) => false);
        Indicate(rig, indication);
        HostileScenarios.Pair(rig, 1, 2, 1.0);
        HostileScenarios.Pair(rig, 3, 2, 1.0);
        if (observers == 2)
        {
            HostileScenarios.Pair(rig, 1, 4, 1.0);
            HostileScenarios.Pair(rig, 3, 4, 1.0);
            HostileScenarios.Pair(rig, 2, 4, 1.0);
        }

        rig.Stations[0].IdentificationSuspended = false;
        rig.Stations[2].IdentificationSuspended = false;
        var clashPayloads = new List<byte[]>();
        for (int o = 0; o < observers; o++)
        {
            int index = o == 0 ? 1 : 3;
            Station observer = rig.Stations[index];
            observer.IdentificationPayloadBuilt = p =>
            {
                // A Clash is an identification payload with item 2.
                if (PayloadCodec.TryUnpackIdentification(p, out _, out _, out ExtensionReader items))
                {
                    while (items.TryNext(out byte tag, out _))
                    {
                        if (tag == PayloadCodec.ClashTag)
                        {
                            clashPayloads.Add(p);
                        }
                    }
                }
            };
        }

        var ledger = new Ledger(rig, seed);
        var acc = new Accounts(rig, ledger);
        Station b = rig.Stations[1];
        rig.RunTo(6);
        acc.Send(1, 2, 1);
        rig.Run(24);

        b.Adaptation.TryGetNeighbour(5, out LinkNeighbour? before).Should().BeTrue();
        b.LearnedAddresses.TryGet(SimRig.Address(1).Key, out _).Should().BeTrue("B learned A's address at 5");
        rig.RunTo(40);
        acc.Send(3, 2, 2);
        rig.Run(1);
        acc.Send(2, 1, 3); // B's host sends to A's address, located at 5: it is queued while C's burst is on the air
        rig.Run(35);

        bool reset = b.Adaptation.TryGetNeighbour(5, out LinkNeighbour? after) && !ReferenceEquals(before, after);
        bool forgotten = !b.LearnedAddresses.TryGet(SimRig.Address(1).Key, out _) || !b.LearnedAddresses.TryGet(SimRig.Address(3).Key, out _);
        bool dropped = rig.Nodes[1].Dropped.Any(d => d.Reason == DropReason.LinkForgotten);
        bool gone = rig.Nodes[1].LinkStates.Any(l => l.Address == SimRig.Address(1) && l.Gone);
        bool laterId = clashPayloads.Count > 0 && clashPayloads.All(p =>
        {
            PayloadCodec.TryUnpackIdentification(p, out _, out _, out ExtensionReader items);
            while (items.TryNext(out byte tag, out ReadOnlySpan<byte> value))
            {
                if (tag == PayloadCodec.ClashTag)
                {
                    return value[0] == 5 && PayloadCodec.ReadKey(value[1..]) == 0xC3C3C3C3;
                }
            }

            return false;
        });
        int[] byObserver = [.. Enumerable.Range(0, observers).Select(o => (int)rig.Stations[o == 0 ? 1 : 3].ClashesSent)];

        // After it: the hosts at every station send to the others.
        var plan = new List<Ledger.Plan>();
        int when = 0;
        for (int from = 1; from <= stations && afterwards; from++)
        {
            for (int to = 1; to <= stations; to++)
            {
                // A and C cannot hear each other: nothing between them can be delivered.
                if (from != to && !(from + to == 4 && from != 2))
                {
                    plan.Add(new Ledger.Plan(rig.Channel.Clock + ((10 + (when++ * 3)) * 48000L), from, to, 30));
                }
            }
        }

        ledger.RunWithHost(afterwards ? 20 + (when * 3) + 40 : 1, 0.5, plan);
        return new Hidden(
            seed, indication, observers, byObserver.Sum(), byObserver, laterId, (int)rig.Stations[0].NumberChanges, (int)rig.Stations[2].NumberChanges, rig.Stations[0].StationNumber, rig.Stations[2].StationNumber, reset, forgotten, dropped, gone,
            (int)(rig.Stations[1].ClashesOwedCount + (observers == 2 ? rig.Stations[3].ClashesOwedCount : 0)), ledger.Items.Count, ledger.Items.Count(ledger.Delivered), ledger.Duplicates().Count, acc.Accepted, acc.Transmitted, acc.Dropped, acc.Queued);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_Stations_With_One_Number_Hidden_From_Each_Other_A_Third_That_Hears_Both_Resets_Sends_One_Clash_And_The_Later_Station_Moves(bool indication)
    {
        var results = Seeds.Select(seed => HiddenClash(seed, indication, 1)).ToList();
        foreach (Hidden r in results)
        {
            Hostile.Say($"hidden clash, {Mode(indication)}, seed {r.Seed}: Clash bursts {r.ClashesSent}, naming the later id {r.ClashNamedLaterId}; A moved {r.AMoves}x (now {r.ANumber}), C moved {r.CMoves}x (now {r.CNumber}); B reset what it knew of 5 {r.ResetNeighbour}, forgot an address {r.AddressesForgotten}, dropped a datagram as forgotten {r.DroppedForgotten}, told the host {r.GoneTold}; afterwards delivered {r.Delivered}/{r.Offered}, duplicates {r.Duplicates}, accepted {r.Accepted} = transmitted {r.Transmitted} + dropped {r.Dropped} + queued {r.Queued}");
            r.ClashesSent.Should().Be(1, $"seed {r.Seed}: B sends one Clash");
            r.ClashNamedLaterId.Should().BeTrue("it names number 5 and the id heard more recently, C's");
            r.AMoves.Should().Be(0, "A was not named");
            r.CMoves.Should().BeGreaterThanOrEqualTo(1, "C was named");
            r.CNumber.Should().NotBe(5);
            r.ResetNeighbour.Should().BeTrue("B forgot what it measured for 5 when the id changed");
            r.AddressesForgotten.Should().BeTrue();
            r.Duplicates.Should().Be(0);
            (r.Transmitted + r.Dropped + r.Queued).Should().Be(r.Accepted);
            r.Queued.Should().Be(0);
            if (indication)
            {
                // With a carrier indication B cannot start the queued datagram before C's identification has been heard.
                r.DroppedForgotten.Should().BeTrue($"seed {r.Seed}: what was queued for the forgotten address was dropped with its reason");
                r.GoneTold.Should().BeTrue("and the host was told the address has gone");
                r.Dropped.Should().BeGreaterThanOrEqualTo(1);
            }
        }

        Hostile.Say($"hidden clash, {Mode(indication)}: delivered {results.Sum(r => r.Delivered)} of {results.Sum(r => r.Offered)} afterwards over seeds {string.Join(",", Seeds)} (B and each of the two reach each other; A and C cannot hear each other and are not offered anything)");
        results.Sum(r => r.Delivered).Should().BeGreaterThan(results.Sum(r => r.Offered) * 3 / 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_Observers_Of_One_Clash_Send_One_Between_Them_Or_Say_How_Often_They_Send_Two(bool indication)
    {
        var results = Seeds.Select(seed => HiddenClash(seed, indication, 2, afterwards: false)).ToList();
        foreach (Hidden r in results)
        {
            Hostile.Say($"two observers, {Mode(indication)}, seed {r.Seed}: Clashes owed {r.ClashesOwed}, sent {r.ClashesSent} (by B {r.ClashesByObserver[0]}, by D {r.ClashesByObserver[1]}); C moved {r.CMoves}x (now {r.CNumber}), A moved {r.AMoves}x");
            r.ClashesOwed.Should().Be(2, "both observers heard the second id");
            r.ClashesSent.Should().BeInRange(1, 2, $"seed {r.Seed}: at least one goes, and no more than the observers");
            r.AMoves.Should().Be(0);
            r.Duplicates.Should().Be(0);
            r.ClashNamedLaterId.Should().BeTrue();
        }

        int two = results.Count(r => r.ClashesSent == 2);
        int moved = results.Count(r => r.CMoves >= 1);
        Hostile.Say($"two observers, {Mode(indication)}: over seeds {string.Join(",", Seeds)}, two Clash bursts went out in {two} of {results.Count} and one in {results.Count - two}; C heard one and moved in {moved} of {results.Count} (when both go at once they overlap on the air and neither is decoded)");
    }

    /// <summary>
    /// A finding, recorded as built and not worked round (docs/station-design.md, "Station numbers as built"): after a Clash the stations that heard it still hold the moved station's id as the last one heard on the number, so the id of the station that stayed,
    /// at its next identification (at least ten minutes after its last, so always inside ten minutes of the moved station's), looks like a change and is named in a Clash of its own. The identification interval is shortened here (a seam) so that the run is minutes of the sample clock; the ten minutes of the rule are not.
    /// </summary>
    [Fact]
    public void A_Station_Left_On_A_Number_After_A_Clash_Is_Told_To_Move_At_Its_Next_Identification()
    {
        var rig = new SimRig(3, seed: 1, connect: false, numbers: [5, 9, 5], ids: [0xA1A1A1A1, 0xB2B2B2B2, 0xC3C3C3C3], locates: (_, _) => false);
        Indicate(rig, true);
        HostileScenarios.Pair(rig, 1, 2, 1.0);
        HostileScenarios.Pair(rig, 3, 2, 1.0);
        foreach (int i in new[] { 0, 2 })
        {
            rig.Stations[i].IdentificationSuspended = false;
            rig.Stations[i].IdentificationInterval = 100L * 48000;
        }

        rig.RunTo(6);
        rig.Send(1, 2, 1).Should().Be(SendResult.Accepted);
        rig.RunTo(40);
        rig.Send(3, 2, 2).Should().Be(SendResult.Accepted);
        rig.RunTo(90);
        rig.Stations[1].ClashesSent.Should().Be(1);
        rig.Stations[2].NumberChanges.Should().BeGreaterThanOrEqualTo(1, "C was named and moved");
        rig.Stations[0].NumberChanges.Should().Be(0, "A stayed");

        rig.RunTo(130);
        rig.Send(1, 2, 3).Should().Be(SendResult.Accepted);
        rig.RunTo(180);
        rig.Stations[0].IdentificationsSent.Should().Be(2, "A identified again, more than its interval after the first");
        rig.Stations[1].ClashesSent.Should().Be(2, "B heard A's id on a number whose last id was C's, heard 90 s before");
        rig.Stations[0].NumberChanges.Should().Be(1, "A, which was never in the way once C had gone, is told to move too");
    }
}
