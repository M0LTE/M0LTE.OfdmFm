using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The host's side of the station on its own: names, refusals, queues, cancelling, the order datagrams go in, and what a burst holds. One station on a simulated channel with nobody else on it, so nothing is received and
/// the tests cost only the stepping. Everything is driven by the sample clock; nothing waits.
/// </summary>
public class StationSendTests
{
    private static LinkAddress B => SimRig.Address(2);

    private static LinkAddress C => SimRig.Address(3);

    private static SimRig Solo(int stations = 4) => new(stations, connect: false);

    // ---- addresses and refusals ----

    [Fact]
    public void An_Address_Nothing_Is_Known_Of_Is_Accepted_And_An_Empty_One_Is_An_Error()
    {
        var rig = new SimRig(2, connect: false);
        rig.Stations[0].Send(new byte[] { 1 }, LinkAddress.FromAscii("nobody"), SimRig.Address(1), new HostTag(1)).Should().Be(SendResult.Accepted, "it goes to everyone, with a For");
        rig.Stations[0].Send(new byte[] { 1 }, B, SimRig.Address(1), new HostTag(1)).Should().Be(SendResult.Accepted);
        rig.Stations[0].QueuedFor(B).Should().Be(1);
        rig.Stations[0].QueuedFor(LinkAddress.FromAscii("nobody")).Should().Be(1);
        Action noTo = () => rig.Stations[0].Send(new byte[] { 1 }, default, SimRig.Address(1), new HostTag(1));
        Action noFrom = () => rig.Stations[0].Send(new byte[] { 1 }, B, default, new HostTag(1));
        Action everyoneFrom = () => rig.Stations[0].Send(new byte[] { 1 }, B, LinkAddress.Everyone, new HostTag(1));
        noTo.Should().Throw<ArgumentException>();
        noFrom.Should().Throw<ArgumentException>();
        everyoneFrom.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Datagram_Larger_Than_One_Burst_At_First_Contact_Is_Refused_And_One_That_Fits_Is_Not()
    {
        // The longest burst at the first-contact format carries 348 bytes. To a located address: less the prelude's 4, the block with one From key (1 + 2 + 4 = 7) and the datagram's own length of 2 leaves 335 (the room LinkState reports).
        var rig = Solo(2);
        Station a = rig.Stations[0];
        PayloadCodec.Capacity(StandardFormats.FirstContact, StandardFormats.MaxSymbols).Should().Be(348);
        a.Send(new byte[335], B, SimRig.Address(1), new HostTag(1)).Should().Be(SendResult.Accepted);
        a.Send(new byte[336], B, SimRig.Address(1), new HostTag(2)).Should().Be(SendResult.TooLarge);

        // To an address not located there is no prelude but the block also carries a For key (2 + 4 more): 348 - 2 - 13 = 333.
        LinkAddress x = LinkAddress.FromAscii("X");
        a.Send(new byte[333], x, SimRig.Address(1), new HostTag(3)).Should().Be(SendResult.Accepted);
        a.Send(new byte[334], x, SimRig.Address(1), new HostTag(4)).Should().Be(SendResult.TooLarge);

        // For everyone, no For and no prelude: 348 - 2 - 7 = 339.
        a.Send(new byte[339], LinkAddress.Everyone, SimRig.Address(1), new HostTag(5)).Should().Be(SendResult.Accepted);
        a.Send(new byte[340], LinkAddress.Everyone, SimRig.Address(1), new HostTag(6)).Should().Be(SendResult.TooLarge);
    }

    [Fact]
    public void An_Empty_Or_Oversized_Datagram_Is_A_Programming_Error()
    {
        var rig = Solo(2);
        Action empty = () => rig.Stations[0].Send(ReadOnlyMemory<byte>.Empty, B, SimRig.Address(1), new HostTag(1));
        Action huge = () => rig.Stations[0].Send(new byte[65536], B, SimRig.Address(1), new HostTag(1));
        empty.Should().Throw<ArgumentException>();
        huge.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Send_Cannot_Be_Called_From_An_Event_Handler()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        InvalidOperationException? thrown = null;
        a.Dropped += _ =>
        {
            try
            {
                a.Send(new byte[] { 1 }, B, SimRig.Address(1), new HostTag(9));
            }
            catch (InvalidOperationException e)
            {
                thrown = e;
            }
        };
        a.Send(new byte[] { 1 }, B, SimRig.Address(1), new HostTag(1));
        a.Cancel(new HostTag(1)).Should().Be(1);
        thrown.Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_Lists_The_Addresses_Sent_To_In_The_Order_They_Were_First_Sent_To()
    {
        var rig = new SimRig(3, connect: false);
        Station a = rig.Stations[0];
        a.Snapshot().Should().BeEmpty("the host has sent to nobody");
        LinkAddress x = LinkAddress.FromAscii("X");
        a.Send(new byte[] { 1 }, C, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[] { 2 }, x, SimRig.Address(1), new HostTag(2));
        a.Send(new byte[] { 3 }, B, SimRig.Address(1), new HostTag(3));
        a.Send(new byte[] { 4 }, C, SimRig.Address(1), new HostTag(4));
        a.Send(new byte[] { 5 }, LinkAddress.Everyone, SimRig.Address(1), new HostTag(5));
        IReadOnlyList<LinkStateEvent> snap = a.Snapshot();
        snap.Select(s => s.Address).Should().Equal(C, x, B);
        snap.Select(s => s.Located).Should().Equal(true, false, true);
        snap.Should().OnlyContain(s => s.Format == null && s.Knowledge == LinkKnowledge.FirstContact && !s.Gone);
        snap.Select(s => s.DatagramRoom).Should().Equal(335, 333, 335);
    }

    // ---- queues ----

    [Fact]
    public void The_Queue_Is_Bounded_In_Airtime_And_Reports_Full_And_Writable()
    {
        // At the first-contact format 0.2545 kbit/s a 100-byte datagram (102 with its length) is 3.2 s of airtime. The tenth takes the queue past 30 s, which is full; it goes writable below 15 s.
        var rig = Solo(3);
        Station a = rig.Stations[0];
        var congestion = new List<CongestionEvent>();
        a.Congestion += congestion.Add;
        for (int i = 1; i <= 9; i++)
        {
            a.Send(new byte[100], B, SimRig.Address(1), new HostTag(i)).Should().Be(SendResult.Accepted);
        }

        congestion.Should().BeEmpty();
        a.QueuedAirtimeFor(B).Should().BeApproximately(7400 / 254.5, 0.01);
        a.Send(new byte[100], B, SimRig.Address(1), new HostTag(10)).Should().Be(SendResult.Accepted, "the queue was not yet full, and this one takes it past the limit");
        congestion.Should().Equal(new CongestionEvent(B, false));
        a.Send(new byte[100], B, SimRig.Address(1), new HostTag(11)).Should().Be(SendResult.QueueFull);
        a.Send(new byte[1], C, SimRig.Address(1), new HostTag(12)).Should().Be(SendResult.Accepted, "the bound is for each address's queue");

        // Dropping five leaves five, 16 s, still full; the sixth takes it under 15 s and the queue is writable again, once.
        for (int i = 1; i <= 5; i++)
        {
            a.Cancel(new HostTag(i)).Should().Be(1);
        }

        congestion.Should().Equal(new CongestionEvent(B, false));
        a.Cancel(new HostTag(6)).Should().Be(1);
        congestion.Should().Equal(new CongestionEvent(B, false), new CongestionEvent(B, true));
        a.Send(new byte[100], B, SimRig.Address(1), new HostTag(13)).Should().Be(SendResult.Accepted);
    }

    [Fact]
    public void A_Queue_Drains_By_Sending_And_Becomes_Writable_Again()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        for (int i = 1; i <= 10; i++)
        {
            a.Send(new byte[100], B, SimRig.Address(1), new HostTag(i));
        }

        node.Congestion.Should().Equal(new CongestionEvent(B, false));
        rig.RunTo(200);
        node.Congestion.Should().Equal(new CongestionEvent(B, false), new CongestionEvent(B, true));
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(Enumerable.Range(1, 10).Select(i => (long)i));
        node.Dropped.Should().BeEmpty();
    }

    // ---- cancelling ----

    [Fact]
    public void Cancel_By_Tag_And_By_Address_Drops_What_Is_Queued_And_Raises_Dropped_For_Each()
    {
        var rig = Solo(3);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        a.Send(new byte[10], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[10], B, SimRig.Address(1), new HostTag(2));
        a.Send(new byte[10], B, SimRig.Address(1), new HostTag(2), priority: SendPriority.Expedited);
        a.Send(new byte[10], C, SimRig.Address(1), new HostTag(3));
        a.Send(new byte[10], B, SimRig.Address(1), new HostTag(4));

        a.Cancel(new HostTag(2)).Should().Be(2, "two datagrams share the tag");
        a.Cancel(new HostTag(99)).Should().Be(0);
        node.Dropped.Select(d => (d.Tag.Value, d.Reason)).Should().Equal((2L, DropReason.Cancelled), (2L, DropReason.Cancelled));

        a.Cancel(B).Should().Be(2);
        node.Dropped.Skip(2).Select(d => d.Tag.Value).Should().Equal(1L, 4L);
        a.Cancel(LinkAddress.FromAscii("nobody")).Should().Be(0);
        a.Cancel(B).Should().Be(0);
        a.QueuedFor(B).Should().Be(0);
        a.QueuedFor(C).Should().Be(1);

        // What is left still goes.
        rig.RunTo(60);
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(3L);
    }

    [Fact]
    public void A_Datagram_In_A_Burst_Handed_To_The_Audio_Layer_Is_Not_Recalled()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        a.Send(new byte[20], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[335], B, SimRig.Address(1), new HostTag(2));
        while (!a.BurstBuilt)
        {
            rig.Run(0.1);
        }

        a.QueuedFor(B).Should().Be(1, "the second datagram is too big to share the first one's burst");
        a.Cancel(new HostTag(1)).Should().Be(0);
        a.Cancel(B).Should().Be(1, "only the one still queued");
        rig.Run(30);
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(1L);
        node.Dropped.Select(d => d.Tag.Value).Should().Equal(2L);
    }

    // ---- order ----

    [Fact]
    public void Expedited_Datagrams_Go_Ahead_Of_Ordinary_Ones_To_The_Same_Address()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        a.Send(new byte[8], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[8], B, SimRig.Address(1), new HostTag(2));
        a.Send(new byte[8], B, SimRig.Address(1), new HostTag(3), priority: SendPriority.Expedited);
        a.Send(new byte[8], B, SimRig.Address(1), new HostTag(4), priority: SendPriority.Expedited);
        rig.RunTo(40);
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(3L, 4L, 1L, 2L);
        node.Transmitted.Select(t => t.Event.Burst).Distinct().Should().HaveCount(1, "they share one burst");
    }

    [Fact]
    public void An_Expedited_Datagram_Takes_The_First_Slot_And_An_Ordinary_One_Waits_For_The_Priority_Window()
    {
        var expedited = Solo(2);
        expedited.Stations[0].Send(new byte[8], B, SimRig.Address(1), new HostTag(1), priority: SendPriority.Expedited);
        expedited.RunTo(20);
        var ordinary = Solo(2);
        ordinary.Stations[0].Send(new byte[8], B, SimRig.Address(1), new HostTag(1));
        ordinary.RunTo(20);

        // Nothing goes in the first 5 s; then the first slot is 50 ms of clear channel, and ordinary traffic waits 0.8 s and a slot's draw more.
        expedited.Nodes[0].Transmissions.Should().ContainSingle().Which.Start.Should().Be(ChannelAccess.StartUpListen + ChannelAccess.FirstSlotClear);
        ordinary.Nodes[0].Transmissions.Should().ContainSingle().Which.Start.Should().BeGreaterThanOrEqualTo(ChannelAccess.StartUpListen + ChannelAccess.OrdinaryClear);
    }

    [Fact]
    public void An_Expedited_Datagram_Sent_After_An_Ordinary_One_To_Another_Address_Goes_First()
    {
        var rig = Solo(3);
        Station a = rig.Stations[0];
        a.Send(new byte[8], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[8], C, SimRig.Address(1), new HostTag(2), priority: SendPriority.Expedited);
        rig.RunTo(40);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(2L, 1L);
        rig.Nodes[0].Transmissions[0].Start.Should().Be(ChannelAccess.StartUpListen + ChannelAccess.FirstSlotClear, "the waiting request took the expedited class");
    }

    [Fact]
    public void Addresses_With_Traffic_Take_Turns_The_One_Served_Longest_Ago_First_And_Order_Within_One_Is_First_In_First_Out()
    {
        var rig = Solo(4);
        Station a = rig.Stations[0];
        LinkAddress d = SimRig.Address(4);
        // 50 bytes: one to a burst at first contact (a burst holds 57), so each is a turn.
        a.Send(new byte[50], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[50], B, SimRig.Address(1), new HostTag(2));
        a.Send(new byte[50], C, SimRig.Address(1), new HostTag(3));
        a.Send(new byte[50], C, SimRig.Address(1), new HostTag(4));
        a.Send(new byte[50], d, SimRig.Address(1), new HostTag(5));
        rig.RunTo(90);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(1L, 3L, 5L, 2L, 4L);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Address).Should().Equal(B, C, d, B, C);
    }

    // ---- what a burst holds ----

    [Fact]
    public void A_Burst_Takes_Datagrams_In_Order_While_They_Fit_Up_To_The_Ceiling_Of_Airtime()
    {
        // The first-contact format at the ceiling (2.5 s, 44 payload symbols) carries 57 bytes; the prelude is 4, the block with its one From key 7 and each 8-byte datagram 10, so four go (51) and the fifth (61) starts the next burst.
        var rig = Solo(2);
        Station a = rig.Stations[0];
        for (int i = 1; i <= 6; i++)
        {
            a.Send(new byte[8], B, SimRig.Address(1), new HostTag(i));
        }

        rig.RunTo(40);
        List<TransmittedEvent> sent = [.. rig.Nodes[0].Transmitted.Select(t => t.Event)];
        sent.Select(t => t.Tag.Value).Should().Equal(1L, 2L, 3L, 4L, 5L, 6L);
        sent.GroupBy(t => t.Burst).Select(g => g.Count()).Should().Equal(4, 2);
        sent[0].AirtimeSeconds.Should().BeLessThan(2.6);
    }

    [Fact]
    public void A_Datagram_That_Needs_A_Longer_Burst_Than_The_Ceiling_Goes_Alone_And_Whole()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        a.Send(new byte[100], B, SimRig.Address(1), new HostTag(1));
        a.Send(new byte[2], B, SimRig.Address(1), new HostTag(2));
        rig.RunTo(60);
        List<TransmittedEvent> sent = [.. rig.Nodes[0].Transmitted.Select(t => t.Event)];
        sent.Select(t => t.Burst).Should().Equal(0, 1);
        sent[0].AirtimeSeconds.Should().BeGreaterThan(2.5, "a datagram of 100 bytes at 0.25 kbit/s is longer than the ceiling");
    }

    [Fact]
    public void The_Burst_Number_Counts_Up_Modulo_Sixteen()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        for (int i = 1; i <= 18; i++)
        {
            a.Send(new byte[50], B, SimRig.Address(1), new HostTag(i));
        }

        rig.RunTo(200);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Burst).Should().Equal(Enumerable.Range(0, 18).Select(i => i % 16));
    }

    [Fact]
    public void Transmitted_Says_Address_Format_Rate_Airtime_And_When()
    {
        var rig = Solo(2);
        rig.Stations[0].Send(new byte[10], B, SimRig.Address(1), new HostTag(7));
        rig.RunTo(30);
        (long clock, TransmittedEvent e) = rig.Nodes[0].Transmitted.Should().ContainSingle().Subject;
        SimTransmission tx = rig.Nodes[0].Transmissions.Single();
        e.Tag.Should().Be(new HostTag(7));
        e.Address.Should().Be(B);
        e.Burst.Should().Be(0);
        e.Format.Should().Be(StandardFormats.FirstContact);
        e.RateBitsPerSecond.Should().Be(StandardFormats.Rate(StandardFormats.FirstContact) * 1000.0);
        e.AirtimeSeconds.Should().Be(tx.Length / 48000.0);
        e.TimeOutSeconds.Should().Be((tx.Start + tx.Length) / 48000.0);
        clock.Should().Be(tx.Start + tx.Length, "raised in the call that ends the transmission");
    }

    [Fact]
    public void A_Burst_Handed_Over_Does_Not_Allocate_Once_Its_Shape_Has_Been_Seen()
    {
        // One station and a bare audio layer (no simulation around it, which allocates its own copies). The same datagram a few times: the first sends grow every pool and buffer; later ones allocate nothing.
        var station = (new Station(1, new Random(3), runJobsInline: true) { IdentificationSuspended = true }).WithIdentity("M0ALLOC");
        station.Locate(B, 2);
        long id = 0;
        long earliest = 0;
        int length = 0;
        station.TransmitRequest += r =>
        {
            id = r.Id;
            earliest = r.EarliestSample;
            length = r.Samples.Length;
        };
        var silence = new float[4800];
        byte[] datagram = new byte[40];
        const int warmUp = 3;
        SendResult[] results = new SendResult[warmUp + AllocationMeasure.Attempts];
        LinkAddress b = B;
        LinkAddress me = SimRig.Address(1);
        int next = 0;
        void Round()
        {
            int round = next++;
            results[round] = station.Send(datagram, b, me, new HostTag(round));
            id = 0;
            while (id == 0)
            {
                station.Push(silence);
            }

            long start = Math.Max(earliest, station.SampleCount);
            station.TransmitStarted(id, start);
            while (station.SampleCount < start + length)
            {
                station.Push(silence.AsSpan(0, (int)Math.Min(silence.Length, start + length - station.SampleCount)));
            }

            station.TransmitEnded(id, start + length);
        }

        for (int round = 0; round < warmUp; round++)
        {
            Round();
        }

        // Each further round is the same send again, so a retry after a stray reading measures the same work.
        long allocated = AllocationMeasure.Minimum(Round);

        results.Take(next).Should().OnlyContain(r => r == SendResult.Accepted);
        allocated.Should().Be(0, "a round after the shape has been seen");
    }

    [Fact]
    public void A_Burst_The_Host_Did_Not_Ask_For_Raises_ModemSent_When_It_Starts()
    {
        // The seam for lone reports and identification (steps 6 and 7): nothing in step 5 sends one, so a burst is enqueued by hand.
        var rig = Solo(2);
        Station a = rig.Stations[0];
        rig.RunTo(1);
        a.Enqueue(new float[2000], TransmitClass.Report, new ModemSentEvent(ModemSentKind.Report, 12));
        rig.RunTo(7);
        rig.Nodes[0].ModemSent.Should().Equal(new ModemSentEvent(ModemSentKind.Report, 12));
        rig.Nodes[0].Transmitted.Should().BeEmpty("it carried no datagram");
    }
}
