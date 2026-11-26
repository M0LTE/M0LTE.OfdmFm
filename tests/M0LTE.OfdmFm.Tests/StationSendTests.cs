using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The host's side of the station on its own: names, refusals, queues, cancelling, the order datagrams go in, and what a burst holds. One station on a simulated channel with nobody else on it, so nothing is received and
/// the tests cost only the stepping. Everything is driven by the sample clock; nothing waits.
/// </summary>
public class StationSendTests
{
    private static StationName B => SimRig.Name(2);

    private static StationName C => SimRig.Name(3);

    private static SimRig Solo(int stations = 4) => new(stations, connect: false);

    // ---- names and refusals ----

    [Fact]
    public void A_Name_That_Is_Not_Bound_Is_Refused()
    {
        var rig = new SimRig(2, connect: false);
        rig.Stations[0].Send(new byte[] { 1 }, StationName.FromAscii("nobody"), new HostTag(1)).Should().Be(SendResult.NotBound);
        rig.Stations[0].Send(new byte[] { 1 }, default, new HostTag(1)).Should().Be(SendResult.NotBound);
        rig.Stations[0].Send(new byte[] { 1 }, B, new HostTag(1)).Should().Be(SendResult.Accepted);
        rig.Stations[0].QueuedFor(2).Should().Be(1);
    }

    [Fact]
    public void A_Datagram_Larger_Than_One_Burst_At_First_Contact_Is_Refused_And_One_That_Fits_Is_Not()
    {
        // The longest burst at the first-contact format carries 348 bytes; less the prelude's 4 and the datagram's own length of 2 leaves 342 (the room LinkState reports).
        var rig = Solo(2);
        Station a = rig.Stations[0];
        PayloadCodec.Capacity(StandardFormats.FirstContact, StandardFormats.MaxSymbols).Should().Be(348);
        a.Send(new byte[342], B, new HostTag(1)).Should().Be(SendResult.Accepted);
        a.Send(new byte[343], B, new HostTag(2)).Should().Be(SendResult.TooLarge);

        // For everyone there is no prelude, so two bytes more fit.
        a.Send(new byte[346], StationName.Everyone, new HostTag(3)).Should().Be(SendResult.Accepted);
        a.Send(new byte[347], StationName.Everyone, new HostTag(4)).Should().Be(SendResult.TooLarge);
    }

    [Fact]
    public void An_Empty_Or_Oversized_Datagram_Is_A_Programming_Error()
    {
        var rig = Solo(2);
        Action empty = () => rig.Stations[0].Send(ReadOnlyMemory<byte>.Empty, B, new HostTag(1));
        Action huge = () => rig.Stations[0].Send(new byte[65536], B, new HostTag(1));
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
                a.Send(new byte[] { 1 }, B, new HostTag(9));
            }
            catch (InvalidOperationException e)
            {
                thrown = e;
            }
        };
        a.Send(new byte[] { 1 }, B, new HostTag(1));
        a.Cancel(new HostTag(1)).Should().Be(1);
        thrown.Should().NotBeNull();
    }

    // ---- Bind ----

    [Fact]
    public void Bind_Is_Checked_And_A_Name_Moves_Between_Numbers()
    {
        var rig = Solo(3);
        Station a = rig.Stations[0];
        StationName x = StationName.FromAscii("X");
        Action ownNumber = () => a.Bind(x, 1);
        Action zero = () => a.Bind(x, 0);
        Action big = () => a.Bind(x, 64);
        Action everyone = () => a.Bind(StationName.Everyone, 5);
        Action empty = () => a.Bind(default, 5);
        Action placeholder = () => a.Bind(Station.PlaceholderName(7), 5);
        ownNumber.Should().Throw<ArgumentOutOfRangeException>();
        zero.Should().Throw<ArgumentOutOfRangeException>();
        big.Should().Throw<ArgumentOutOfRangeException>();
        everyone.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
        placeholder.Should().Throw<ArgumentException>();

        a.Bind(x, 5);
        a.Send(new byte[] { 1 }, x, new HostTag(1)).Should().Be(SendResult.Accepted);
        a.QueuedFor(5).Should().Be(1);

        // The name moves to another number: the datagram stays queued under the number it was queued for, and the new number is where the name goes now.
        a.Bind(x, 6);
        a.Send(new byte[] { 2 }, x, new HostTag(2)).Should().Be(SendResult.Accepted);
        a.QueuedFor(5).Should().Be(1);
        a.QueuedFor(6).Should().Be(1);

        // A number has one name: binding another name replaces it.
        StationName y = StationName.FromAscii("Y");
        a.Bind(y, 6);
        a.Send(new byte[] { 3 }, x, new HostTag(3)).Should().Be(SendResult.NotBound);
        a.Send(new byte[] { 3 }, y, new HostTag(3)).Should().Be(SendResult.Accepted);
    }

    [Fact]
    public void A_Station_Bound_After_It_Was_Heard_Is_Announced_Under_The_New_Name_And_Earlier_Events_Keep_The_Placeholder()
    {
        // Station 2 hears station 1 by number alone, then is told its name.
        var rig = new SimRig(2, connect: true);
        var third = new Station("M0NEW", 3, new Random(5), runJobsInline: true);
        Station s1 = rig.Stations[0];
        Station s2 = rig.Stations[1];
        var heardBy2 = new List<ReceivedEvent>();
        var links = new List<LinkStateEvent>();
        var paths = new List<PathReportEvent>();
        s2.Received += heardBy2.Add;
        s2.LinkState += links.Add;
        s2.PathReport += paths.Add;

        // Make 2 forget the binding for 1 by working from a fresh station that never had it.
        var fresh = new Station("M0FRESH", 4, new Random(7), runJobsInline: true);
        var channel = new SimChannel(4800, 3);
        SimNode n1 = channel.Add(s1, "S1");
        SimNode n4 = channel.Add(fresh, "S4");
        channel.Connect(n1, n4, 1.0, 0.004);
        s1.Bind(StationName.FromAscii("S4"), 4);
        fresh.Received += heardBy2.Add;
        fresh.LinkState += links.Add;
        channel.Run(6 * 48000);
        s1.Send(SimRig.Datagram(1, 30), StationName.FromAscii("S4"), new HostTag(1)).Should().Be(SendResult.Accepted);
        channel.Run(channel.Clock + (12 * 48000));

        heardBy2.Should().ContainSingle();
        heardBy2[0].Sender.Should().BeNull("station 1 has not been bound at 4 yet");
        links.Should().NotBeEmpty();
        links[^1].Station.Should().Be(Station.PlaceholderName(1));
        int before = links.Count;

        fresh.Bind(StationName.FromAscii("one"), 1);
        links.Count.Should().Be(before + 1, "a bind announces a station already heard once more");
        links[^1].Station.Should().Be(StationName.FromAscii("one"));
        links[^2].Station.Should().Be(Station.PlaceholderName(1), "events raised before the bind keep the placeholder");

        // After the bind the name is on received datagrams.
        s1.Send(SimRig.Datagram(2, 30), StationName.FromAscii("S4"), new HostTag(2));
        channel.Run(channel.Clock + (12 * 48000));
        heardBy2.Should().HaveCount(2);
        heardBy2[1].Sender.Should().Be(StationName.FromAscii("one"));
        _ = third;
    }

    [Fact]
    public void Snapshot_Lists_What_Is_Heard_Or_Bound()
    {
        var rig = new SimRig(3, connect: false);
        Station a = rig.Stations[0];
        IReadOnlyList<LinkStateEvent> snap = a.Snapshot();
        snap.Select(s => s.Station).Should().Equal(SimRig.Name(2), SimRig.Name(3));
        snap.Should().OnlyContain(s => s.Format == null && s.Knowledge == LinkKnowledge.FirstContact && s.DatagramRoom == 342 && !s.Gone);
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
            a.Send(new byte[100], B, new HostTag(i)).Should().Be(SendResult.Accepted);
        }

        congestion.Should().BeEmpty();
        a.QueuedAirtimeFor(2).Should().BeApproximately(28.85, 0.01);
        a.Send(new byte[100], B, new HostTag(10)).Should().Be(SendResult.Accepted, "the queue was not yet full, and this one takes it past the limit");
        congestion.Should().Equal(new CongestionEvent(B, false));
        a.Send(new byte[100], B, new HostTag(11)).Should().Be(SendResult.QueueFull);
        a.Send(new byte[1], C, new HostTag(12)).Should().Be(SendResult.Accepted, "the bound is for each station's queue");

        // Dropping five leaves five, 16 s, still full; the sixth takes it under 15 s and the queue is writable again, once.
        for (int i = 1; i <= 5; i++)
        {
            a.Cancel(new HostTag(i)).Should().Be(1);
        }

        congestion.Should().Equal(new CongestionEvent(B, false));
        a.Cancel(new HostTag(6)).Should().Be(1);
        congestion.Should().Equal(new CongestionEvent(B, false), new CongestionEvent(B, true));
        a.Send(new byte[100], B, new HostTag(13)).Should().Be(SendResult.Accepted);
    }

    [Fact]
    public void A_Queue_Drains_By_Sending_And_Becomes_Writable_Again()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        for (int i = 1; i <= 10; i++)
        {
            a.Send(new byte[100], B, new HostTag(i));
        }

        node.Congestion.Should().Equal(new CongestionEvent(B, false));
        rig.RunTo(200);
        node.Congestion.Should().Equal(new CongestionEvent(B, false), new CongestionEvent(B, true));
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(Enumerable.Range(1, 10).Select(i => (long)i));
        node.Dropped.Should().BeEmpty();
    }

    // ---- cancelling ----

    [Fact]
    public void Cancel_By_Tag_And_By_Name_Drops_What_Is_Queued_And_Raises_Dropped_For_Each()
    {
        var rig = Solo(3);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        a.Send(new byte[10], B, new HostTag(1));
        a.Send(new byte[10], B, new HostTag(2));
        a.Send(new byte[10], B, new HostTag(2), priority: SendPriority.Expedited);
        a.Send(new byte[10], C, new HostTag(3));
        a.Send(new byte[10], B, new HostTag(4));

        a.Cancel(new HostTag(2)).Should().Be(2, "two datagrams share the tag");
        a.Cancel(new HostTag(99)).Should().Be(0);
        node.Dropped.Select(d => (d.Tag.Value, d.Reason)).Should().Equal((2L, DropReason.Cancelled), (2L, DropReason.Cancelled));

        a.Cancel(B).Should().Be(2);
        node.Dropped.Skip(2).Select(d => d.Tag.Value).Should().Equal(1L, 4L);
        a.Cancel(StationName.FromAscii("nobody")).Should().Be(0);
        a.Cancel(B).Should().Be(0);
        a.QueuedFor(2).Should().Be(0);
        a.QueuedFor(3).Should().Be(1);

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
        a.Send(new byte[20], B, new HostTag(1));
        a.Send(new byte[342], B, new HostTag(2));
        while (!a.BurstBuilt)
        {
            rig.Run(0.1);
        }

        a.QueuedFor(2).Should().Be(1, "the second datagram is too big to share the first one's burst");
        a.Cancel(new HostTag(1)).Should().Be(0);
        a.Cancel(B).Should().Be(1, "only the one still queued");
        rig.Run(30);
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(1L);
        node.Dropped.Select(d => d.Tag.Value).Should().Equal(2L);
    }

    // ---- order ----

    [Fact]
    public void Expedited_Datagrams_Go_Ahead_Of_Ordinary_Ones_To_The_Same_Station()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        a.Send(new byte[8], B, new HostTag(1));
        a.Send(new byte[8], B, new HostTag(2));
        a.Send(new byte[8], B, new HostTag(3), priority: SendPriority.Expedited);
        a.Send(new byte[8], B, new HostTag(4), priority: SendPriority.Expedited);
        rig.RunTo(40);
        node.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(3L, 4L, 1L, 2L);
        node.Transmitted.Select(t => t.Event.Burst).Distinct().Should().HaveCount(1, "they share one burst");
    }

    [Fact]
    public void An_Expedited_Datagram_Takes_The_First_Slot_And_An_Ordinary_One_Waits_For_The_Priority_Window()
    {
        var expedited = Solo(2);
        expedited.Stations[0].Send(new byte[8], B, new HostTag(1), priority: SendPriority.Expedited);
        expedited.RunTo(20);
        var ordinary = Solo(2);
        ordinary.Stations[0].Send(new byte[8], B, new HostTag(1));
        ordinary.RunTo(20);

        // Nothing goes in the first 5 s; then the first slot is 50 ms of clear channel, and ordinary traffic waits 0.8 s and a slot's draw more.
        expedited.Nodes[0].Transmissions.Should().ContainSingle().Which.Start.Should().Be(ChannelAccess.StartUpListen + ChannelAccess.FirstSlotClear);
        ordinary.Nodes[0].Transmissions.Should().ContainSingle().Which.Start.Should().BeGreaterThanOrEqualTo(ChannelAccess.StartUpListen + ChannelAccess.OrdinaryClear);
    }

    [Fact]
    public void An_Expedited_Datagram_Sent_After_An_Ordinary_One_To_Another_Station_Goes_First()
    {
        var rig = Solo(3);
        Station a = rig.Stations[0];
        a.Send(new byte[8], B, new HostTag(1));
        a.Send(new byte[8], C, new HostTag(2), priority: SendPriority.Expedited);
        rig.RunTo(40);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(2L, 1L);
        rig.Nodes[0].Transmissions[0].Start.Should().Be(ChannelAccess.StartUpListen + ChannelAccess.FirstSlotClear, "the waiting request took the expedited class");
    }

    [Fact]
    public void Stations_With_Traffic_Take_Turns_The_One_Served_Longest_Ago_First_And_Order_Within_One_Is_First_In_First_Out()
    {
        var rig = Solo(4);
        Station a = rig.Stations[0];
        StationName d = SimRig.Name(4);
        // 50 bytes: one to a burst at first contact (a burst holds 57), so each is a turn.
        a.Send(new byte[50], B, new HostTag(1));
        a.Send(new byte[50], B, new HostTag(2));
        a.Send(new byte[50], C, new HostTag(3));
        a.Send(new byte[50], C, new HostTag(4));
        a.Send(new byte[50], d, new HostTag(5));
        rig.RunTo(90);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(1L, 3L, 5L, 2L, 4L);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Station).Should().Equal(B, C, d, B, C);
    }

    // ---- what a burst holds ----

    [Fact]
    public void A_Burst_Takes_Datagrams_In_Order_While_They_Fit_Up_To_The_Ceiling_Of_Airtime()
    {
        // The first-contact format at the ceiling (2.5 s, 44 payload symbols) carries 57 bytes; the prelude is 4 and each 8-byte datagram 10, so five go (54) and the sixth (64) starts the next burst.
        var rig = Solo(2);
        Station a = rig.Stations[0];
        for (int i = 1; i <= 6; i++)
        {
            a.Send(new byte[8], B, new HostTag(i));
        }

        rig.RunTo(40);
        List<TransmittedEvent> sent = [.. rig.Nodes[0].Transmitted.Select(t => t.Event)];
        sent.Select(t => t.Tag.Value).Should().Equal(1L, 2L, 3L, 4L, 5L, 6L);
        sent.GroupBy(t => t.Burst).Select(g => g.Count()).Should().Equal(5, 1);
        sent[0].AirtimeSeconds.Should().BeLessThan(2.6);
    }

    [Fact]
    public void A_Datagram_That_Needs_A_Longer_Burst_Than_The_Ceiling_Goes_Alone_And_Whole()
    {
        var rig = Solo(2);
        Station a = rig.Stations[0];
        a.Send(new byte[100], B, new HostTag(1));
        a.Send(new byte[2], B, new HostTag(2));
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
            a.Send(new byte[50], B, new HostTag(i));
        }

        rig.RunTo(200);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Burst).Should().Equal(Enumerable.Range(0, 18).Select(i => i % 16));
    }

    [Fact]
    public void Transmitted_Says_Station_Format_Rate_Airtime_And_When()
    {
        var rig = Solo(2);
        rig.Stations[0].Send(new byte[10], B, new HostTag(7));
        rig.RunTo(30);
        (long clock, TransmittedEvent e) = rig.Nodes[0].Transmitted.Should().ContainSingle().Subject;
        SimTransmission tx = rig.Nodes[0].Transmissions.Single();
        e.Tag.Should().Be(new HostTag(7));
        e.Station.Should().Be(B);
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
        var station = new Station("M0ALLOC", 1, new Random(3), runJobsInline: true);
        station.Bind(B, 2);
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
        long[] allocated = new long[6];
        SendResult[] results = new SendResult[6];
        StationName b = B;
        for (int round = 0; round < 6; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            results[round] = station.Send(datagram, b, new HostTag(round));
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
            allocated[round] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        results.Should().OnlyContain(r => r == SendResult.Accepted);
        allocated.Skip(3).Should().OnlyContain(b => b == 0, $"allocated per round: {string.Join(", ", allocated)}");
    }

    [Fact]
    public void A_Burst_The_Host_Did_Not_Ask_For_Raises_ModemSent_When_It_Starts()
    {
        // The seam for lone reports and identification (steps 6 and 7): nothing in step 5 sends one, so a burst is enqueued by hand.
        var rig = Solo(2);
        Station a = rig.Stations[0];
        rig.RunTo(1);
        a.Enqueue(new float[2000], TransmitClass.Report, new ModemSentEvent(ModemSentKind.Report, B, 12));
        rig.RunTo(7);
        rig.Nodes[0].ModemSent.Should().Equal(new ModemSentEvent(ModemSentKind.Report, B, 12));
        rig.Nodes[0].Transmitted.Should().BeEmpty("it carried no datagram");
    }
}
