using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The audio-layer additions of 27 November 2026 (docs/station-design.md, "Audio-layer additions"): a refused transmission goes back to waiting and is handed over again unchanged after the usual clear time;
/// the carrier indication can be taken away with <c>SetExternalBusy(null)</c>. Everything runs on the sample clock.
/// </summary>
public class StationAudioLayerTests
{
    private static LinkAddress S(int n) => SimRig.Address(n);

    private static readonly int DetectionSlot = Station.DetectionLatencySamples;

    private static void OwnTheAddress(SimRig rig, int station)
    {
        // The host has sent from its own address once, and the datagram was cancelled before it went: the station now answers a flood for that address with an announcement.
        Station s = rig.Stations[station - 1];
        s.Send(SimRig.Datagram(900, 10), LinkAddress.FromAscii("ELSEWHERE"), S(station), LinkProtocol.NoLayer3, new HostTag(900)).Should().Be(SendResult.Accepted);
        s.Cancel(new HostTag(900)).Should().Be(1);
    }

    private static void GapsAreOneClearTime(SimNode node, int refusals, long firstSlotOnly = ChannelAccess.FirstSlotClear)
    {
        for (int i = 0; i < refusals; i++)
        {
            (node.Requests[i + 1].Clock - node.Refused[i].Clock).Should().Be(firstSlotOnly, "refusal {0} counts as the channel busy up to then, and the first slot is the whole wait", i + 1);
        }
    }

    // ---- L1: the audio layer refuses ----

    [Fact]
    public void A_Refused_Expedited_Data_Burst_Is_Handed_Over_Again_Unchanged_After_The_First_Slot_And_Delivered_Once()
    {
        var rig = new SimRig(2, seed: 2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        a.RefuseRequest = n => n == 1;
        rig.RunTo(6);
        rig.Send(1, 2, 7, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(30);

        a.Refused.Should().ContainSingle();
        a.Requests.Should().HaveCount(2);
        a.Requests[1].Checksum.Should().Be(a.Requests[0].Checksum, "the burst is kept, not built again");
        a.Requests[1].Length.Should().Be(a.Requests[0].Length);
        GapsAreOneClearTime(a, 1);
        a.Transmissions.Should().ContainSingle().Which.Id.Should().Be(a.Requests[1].Id, "the first hand-over was never keyed");
        b.Received.Select(r => r.Data).Should().BeEquivalentTo(new[] { SimRig.Datagram(7, 30) }, o => o.WithStrictOrdering());
        a.Transmitted.Should().ContainSingle("the datagram went out once, at the transmission that really happened");
        a.Transmitted[0].Clock.Should().Be(a.Transmissions[0].Start + a.Transmissions[0].Length);
        a.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void Without_The_Call_A_Refused_Burst_Leaves_The_Station_Waiting_For_Ever_And_It_Never_Transmits_Again()
    {
        var rig = new SimRig(2, seed: 2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        a.RefuseRequest = n => n == 1;
        a.ReportRefusals = false;
        rig.RunTo(6);
        rig.Send(1, 2, 7, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(60);
        rig.Send(1, 2, 8, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(60);

        a.Refused.Should().ContainSingle();
        a.Requests.Should().ContainSingle("the station hands over one request at a time and is still waiting for the first");
        a.Transmissions.Should().BeEmpty();
        a.Transmitted.Should().BeEmpty();
        b.Received.Should().BeEmpty();
    }

    [Fact]
    public void A_Refused_Ordinary_Burst_Waits_The_Ordinary_Clear_Time_And_Its_Slots_Before_It_Goes_Again_Unchanged()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            var rig = new SimRig(2, seed: seed);
            SimNode a = rig.Nodes[0];
            SimNode b = rig.Nodes[1];
            a.RefuseRequest = n => n == 1;
            rig.RunTo(6);
            rig.Send(1, 2, 7).Should().Be(SendResult.Accepted);
            rig.Run(40);

            a.Requests.Should().HaveCount(2);
            a.Requests[1].Checksum.Should().Be(a.Requests[0].Checksum);
            long gap = a.Requests[1].Clock - a.Refused[0].Clock;
            gap.Should().BeOneOf(ChannelAccess.OrdinaryClear, ChannelAccess.OrdinaryClear + DetectionSlot);
            b.Received.Should().ContainSingle().Which.Data.Should().Equal(SimRig.Datagram(7, 30));
            a.Transmitted.Should().ContainSingle();
        }
    }

    [Fact]
    public void A_Refused_Lone_Report_Goes_Out_Later_Unchanged_After_The_First_Slot()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        b.RefuseRequest = n => n == 1;
        rig.RunTo(6);
        rig.Send(1, 2, 101, reportWanted: true).Should().Be(SendResult.Accepted);
        rig.Run(30);

        b.Refused.Should().ContainSingle();
        b.Requests.Should().HaveCount(2, "the report asked for goes alone, refused once");
        b.Requests[1].Checksum.Should().Be(b.Requests[0].Checksum);
        GapsAreOneClearTime(b, 1);
        b.Transmissions.Should().ContainSingle();
        b.ModemSent.Should().ContainSingle().Which.Kind.Should().Be(ModemSentKind.Report);
        a.PathReports.Should().ContainSingle("the report was heard once");
    }

    [Fact]
    public void A_Refused_Identification_Goes_Out_Later_Unchanged_After_The_First_Slot()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Stations[0].IdentificationSuspended = false;
        a.RefuseRequest = n => n == 2;
        rig.RunTo(6);
        rig.Send(1, 2, 102, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(30);

        a.IdentificationsDue.Should().ContainSingle();
        a.Refused.Should().ContainSingle();
        a.Requests.Should().HaveCount(3, "the data burst, the identification refused, the identification again");
        a.Refused[0].Id.Should().Be(a.Requests[1].Id);
        a.Requests[2].Checksum.Should().Be(a.Requests[1].Checksum);
        (a.Requests[2].Clock - a.Refused[0].Clock).Should().Be(ChannelAccess.FirstSlotClear);
        a.ModemSent.Should().ContainSingle(m => m.Kind == ModemSentKind.Identification);
        b.Identified.Should().ContainSingle().Which.Event.Callsign.Should().Be("M0S1");
        a.Transmitted.Should().ContainSingle();
    }

    [Fact]
    public void A_Refused_Announcement_Goes_Out_Later_Unchanged_And_The_Address_Is_Still_Learned()
    {
        var rig = new SimRig(2, locates: (_, _) => false);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        OwnTheAddress(rig, 2);
        b.RefuseRequest = n => n == 1;
        rig.RunTo(6);
        rig.Send(1, 2, 110).Should().Be(SendResult.Accepted);
        rig.Run(30);

        b.Refused.Should().ContainSingle();
        b.Requests.Count.Should().BeGreaterThanOrEqualTo(2);
        b.Requests[1].Checksum.Should().Be(b.Requests[0].Checksum);
        (b.Requests[1].Clock - b.Refused[0].Clock).Should().Be(ChannelAccess.FirstSlotClear);
        b.ModemSent.Should().ContainSingle(m => m.Kind == ModemSentKind.Announcement);
        rig.Stations[0].LocatedNow(S(2)).Should().Be(2, "A learned where the address lives from the announcement that went out second");
    }

    [Fact]
    public void A_Layer_That_Refuses_Five_Times_Running_Costs_Five_Clear_Time_Waits_And_Then_The_Burst_Goes()
    {
        var rig = new SimRig(2, seed: 2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        a.RefuseRequest = n => n <= 5;
        rig.RunTo(6);
        rig.Send(1, 2, 7, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(30);

        a.Refused.Should().HaveCount(5);
        a.Requests.Should().HaveCount(6, "one hand-over for each wait and nothing else: no spin");
        GapsAreOneClearTime(a, 5);
        a.Requests.Select(r => r.Checksum).Distinct().Should().ContainSingle("the same burst every time");
        a.Transmissions.Should().ContainSingle();
        a.Transmitted.Should().ContainSingle();
        b.Received.Should().ContainSingle().Which.Data.Should().Equal(SimRig.Datagram(7, 30));
    }

    [Fact]
    public void Five_Refusals_Of_Ordinary_Traffic_Each_Cost_The_Ordinary_Clear_Time_And_A_Drawn_Number_Of_Slots()
    {
        var rig = new SimRig(2, seed: 3);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        a.RefuseRequest = n => n <= 5;
        rig.RunTo(6);
        rig.Send(1, 2, 7).Should().Be(SendResult.Accepted);
        rig.Run(60);

        a.Requests.Should().HaveCount(6);
        for (int i = 0; i < 5; i++)
        {
            (a.Requests[i + 1].Clock - a.Refused[i].Clock).Should().BeOneOf(ChannelAccess.OrdinaryClear, ChannelAccess.OrdinaryClear + DetectionSlot);
        }

        b.Received.Should().ContainSingle();
        a.Transmitted.Should().ContainSingle();
    }

    // ---- L1 at the edges, on a bare station ----

    private static (Station Station, FakeAudioLayer Layer) Bare()
    {
        var station = new Station(1, new Random(1), runJobsInline: true) { IdentificationSuspended = true }.WithIdentity("M0LTE");
        var layer = new FakeAudioLayer(station);
        layer.Run(6 * 48000);
        station.Send(new byte[] { 1, 2, 3 }, LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        layer.Run(station.SampleCount + 4000);
        layer.Requests.Should().ContainSingle();
        return (station, layer);
    }

    [Fact]
    public void A_Refusal_After_TransmitStarted_Is_Ignored_And_The_Transmission_Ends_As_Usual()
    {
        (Station station, FakeAudioLayer layer) = Bare();
        long id = layer.Requests[0].Id;
        station.TransmitStarted(id, station.SampleCount);
        station.TransmitRefused(id);
        station.StateOf(id).Should().Be(TransmitState.Started);
        station.IsTransmitting.Should().BeTrue();

        layer.Run(station.SampleCount + 3000);
        layer.Requests.Should().ContainSingle("nothing was asked for again");
        var transmitted = new List<TransmittedEvent>();
        station.Transmitted += transmitted.Add;
        station.TransmitEnded(id, station.SampleCount);
        transmitted.Should().ContainSingle();
        station.TransmitRefused(id);
        layer.Run(station.SampleCount + 200000);
        layer.Requests.Should().ContainSingle("a refusal after the end changes nothing either");
    }

    [Fact]
    public void A_Refusal_Of_A_Stale_Or_Unknown_Id_Changes_Nothing()
    {
        (Station station, FakeAudioLayer layer) = Bare();
        long first = layer.Requests[0].Id;

        station.TransmitRefused(first + 100);
        station.TransmitRefused(0);
        station.TransmitRefused(-5);
        station.StateOf(first).Should().Be(TransmitState.Requested, "ids never issued are ignored");

        // A real refusal, and the second hand-over.
        station.TransmitRefused(first);
        station.StateOf(first).Should().Be(TransmitState.Withdrawn);
        layer.Withdrawals.Should().BeEmpty("the layer refused: it is not told to withdraw");
        layer.Run(station.SampleCount + ChannelAccess.FirstSlotClear);
        layer.Requests.Should().HaveCount(2);
        long second = layer.Requests[1].Id;
        layer.Requests[1].Samples.ToArray().Should().Equal(layer.Requests[0].Samples.ToArray());

        // The same id again is stale now: the second request is the one outstanding, and it is not disturbed.
        station.TransmitRefused(first);
        station.StateOf(second).Should().Be(TransmitState.Requested);
        layer.Run(station.SampleCount + 100000);
        layer.Requests.Should().HaveCount(2, "no third hand-over: the stale refusal counted for nothing");

        // An id the station withdrew itself is stale as well.
        station.TransmitStarted(second, station.SampleCount);
        layer.Run(station.SampleCount + 3000);
        station.TransmitEnded(second, station.SampleCount);
        station.TransmitRefused(second);
        station.TransmitRefused(first);
    }

    [Fact]
    public void A_Refusal_Of_A_Request_The_Station_Has_Withdrawn_Itself_Is_Ignored()
    {
        (Station station, FakeAudioLayer layer) = Bare();
        long first = layer.Requests[0].Id;
        station.NoteBurst(station.SampleCount + 20000);
        layer.Withdrawals.Should().Equal(first);
        long clearSince = station.ClearSince;

        station.TransmitRefused(first);
        station.ClearSince.Should().Be(clearSince, "the refusal of a withdrawn request is not a busy channel");
        station.StateOf(first).Should().Be(TransmitState.Withdrawn);
        station.AccessWaiting.Should().Be(1);
        layer.Run(station.SampleCount + 40000);
        layer.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void A_Refusal_Raised_From_An_Event_Handler_Is_Refused_As_Every_Audio_Layer_Call_Is()
    {
        var station = new Station(1, new Random(1), runJobsInline: true) { IdentificationSuspended = true }.WithIdentity("M0LTE");
        var layer = new FakeAudioLayer(station);
        Exception? raised = null;
        station.TransmitRequest += r =>
        {
            try
            {
                station.TransmitRefused(r.Id);
            }
            catch (InvalidOperationException e)
            {
                raised = e;
            }
        };
        layer.Run(6 * 48000);
        station.Send(new byte[] { 1 }, LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        layer.Run(station.SampleCount + 4000);
        raised.Should().NotBeNull();
        station.StateOf(layer.Requests[0].Id).Should().Be(TransmitState.Requested);
    }

    // ---- L2: the carrier indication can be taken away ----

    private static Station Plain() => new Station(1, new Random(1), runJobsInline: true) { IdentificationSuspended = true }.WithIdentity("M0LTE");

    [Fact]
    public void The_Slot_Is_The_Detection_Latency_Before_Any_Call_The_Short_Slot_After_A_Value_And_The_Detection_Latency_Again_After_Null()
    {
        Station station = Plain();
        station.NoticeSamples.Should().Be(DetectionSlot);

        station.SetExternalBusy(true);
        station.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);
        station.SetExternalBusy(false);
        station.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);

        station.SetExternalBusy(null);
        station.NoticeSamples.Should().Be(DetectionSlot);
        station.SetExternalBusy(null);
        station.NoticeSamples.Should().Be(DetectionSlot, "null twice is the same as once");

        station.SetExternalBusy(false);
        station.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);
        station.SetExternalBusy(null);
        station.SetExternalBusy(true);
        station.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);
    }

    [Fact]
    public void Null_Before_Any_Value_Changes_Nothing()
    {
        Station station = Plain();
        var events = new List<bool>();
        station.ChannelBusy += e => events.Add(e.Busy);
        long clear = station.ClearSince;
        station.SetExternalBusy(null);
        station.NoticeSamples.Should().Be(DetectionSlot);
        station.IsChannelBusy.Should().BeFalse();
        station.ClearSince.Should().Be(clear);
        events.Should().BeEmpty();
    }

    [Fact]
    public void A_Channel_Held_Busy_By_The_Indication_Is_Released_By_Null_At_Once_Where_A_Clear_Holds_It_For_A_Tenth_Of_A_Second()
    {
        // The old way out: a value of false. The channel stays busy for 100 ms and the slot stays short.
        Station cleared = Plain();
        cleared.Push(new float[250000]);
        cleared.SetExternalBusy(true);
        cleared.IsChannelBusy.Should().BeTrue();
        cleared.SetExternalBusy(false);
        cleared.IsChannelBusy.Should().BeTrue("100 ms of hold after the indication drops");
        cleared.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);

        // The new way: null.
        Station taken = Plain();
        var events = new List<(long At, bool Busy)>();
        taken.ChannelBusy += e => events.Add((taken.SampleCount, e.Busy));
        taken.Push(new float[250000]);
        taken.SetExternalBusy(true);
        taken.IsChannelBusy.Should().BeTrue();
        taken.SetExternalBusy(null);
        taken.IsChannelBusy.Should().BeFalse("the indication is gone: the channel is not held by it");
        events.Should().Equal((250000, true), (250000, false));
        taken.ClearSince.Should().Be(250000, "it counts as busy by the indication up to the call, so a wait counted from it is not cut short");
        taken.NoticeSamples.Should().Be(DetectionSlot);

        // A hold already running after a clear is released as well, and the clear time is counted from the call at the latest.
        Station holding = Plain();
        holding.Push(new float[250000]);
        holding.SetExternalBusy(true);
        holding.SetExternalBusy(false);
        holding.Push(new float[1000]);
        holding.IsChannelBusy.Should().BeTrue();
        holding.SetExternalBusy(null);
        holding.IsChannelBusy.Should().BeFalse();
        holding.ClearSince.Should().Be(251000);
    }

    [Fact]
    public void A_Request_Waiting_For_The_Indication_To_Clear_Is_Not_Held_For_Ever_When_The_Indication_Dies_While_Set()
    {
        var station = Plain();
        var layer = new FakeAudioLayer(station);
        layer.Run(6 * 48000);
        station.SetExternalBusy(true);
        station.Send(new byte[] { 1 }, LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        layer.Run(station.SampleCount + 100000);
        layer.Requests.Should().BeEmpty("the radio says the channel is busy");

        long at = station.SampleCount;
        station.SetExternalBusy(null);
        layer.Run(at + ChannelAccess.FirstSlotClear + 10);
        layer.Requests.Should().ContainSingle("the dead indication no longer holds the station, and the first slot counts from the call");
    }

    [Fact]
    public void The_Draw_Of_Ordinary_Traffic_Follows_The_Slot_Short_With_An_Indication_Long_Without_And_Long_Again_After_Null()
    {
        // The wait is the clear time plus a drawn number of slots. With an indication the slots are 100 ms (0 to 3 of them); with detection alone they are the detection latency (0 or 1). Over several seeds both
        // draws are seen, and the two sets never overlap beyond zero slots.
        var shortSeen = new HashSet<long>();
        var longSeen = new HashSet<long>();
        var longAfterNull = new HashSet<long>();
        for (int seed = 1; seed <= 24; seed++)
        {
            shortSeen.Add(GrantOffset(seed, supplied: true, thenNull: false));
            longSeen.Add(GrantOffset(seed, supplied: false, thenNull: false));
            longAfterNull.Add(GrantOffset(seed, supplied: true, thenNull: true));
        }

        shortSeen.Should().BeEquivalentTo(new long[] { 0, 4800, 9600, 14400 });
        longSeen.Should().BeEquivalentTo(new long[] { 0, DetectionSlot });
        longAfterNull.Should().BeEquivalentTo(longSeen, "after null the station draws as one that never had an indication");
    }

    /// <summary>The offset of the grant of one ordinary datagram from the ordinary clear time, in samples, for a station given a value (or not) and then possibly null.</summary>
    private static long GrantOffset(int seed, bool supplied, bool thenNull)
    {
        var station = new Station(1, new Random(seed), runJobsInline: true) { IdentificationSuspended = true }.WithIdentity("M0LTE");
        var layer = new FakeAudioLayer(station);
        var grantedAt = new List<long>();
        station.TransmitRequest += _ => grantedAt.Add(station.SampleCount);
        if (supplied)
        {
            station.SetExternalBusy(false);
            if (thenNull)
            {
                station.SetExternalBusy(null);
                station.SetExternalBusy(false);
                station.SetExternalBusy(null);
            }
        }

        layer.Run(ChannelAccess.StartUpListen + 10);
        station.Send(new byte[] { 1 }, LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        layer.Run(ChannelAccess.StartUpListen + ChannelAccess.OrdinaryClear + (2 * DetectionSlot) + 10);
        return grantedAt.Should().ContainSingle().Subject - ChannelAccess.StartUpListen - ChannelAccess.OrdinaryClear;
    }

    [Fact]
    public void Callers_That_Only_Ever_Pass_True_Or_False_See_The_Same_Busy_Changes_As_Before()
    {
        Station station = Plain();
        var events = new List<(long, bool)>();
        station.ChannelBusy += e => events.Add((station.SampleCount, e.Busy));
        foreach (bool value in new[] { true, true, false, true, false, false, true })
        {
            station.Push(new float[2500]);
            station.SetExternalBusy(value);
        }

        station.Push(new float[20000]);

        // Set at 2500; false at 7500 holds to 12300, but it is set again at 10000; false at 12500 holds to 17300; set again at 17500, after the hold ended at 17300.
        events.Should().Equal((2500, true), (17300, false), (17500, true));
        station.NoticeSamples.Should().Be(ChannelAccess.ExternalNotice);
    }
}
