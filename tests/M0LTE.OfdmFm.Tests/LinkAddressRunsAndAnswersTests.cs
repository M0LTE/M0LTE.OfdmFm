using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The From item as runs (a key, a protocol byte and a count), the protocol byte on every datagram, a reply to a key alone, the rate both ways in <see cref="LinkStateEvent"/>, and a station answering for its own addresses
/// (a burst to everyone for one of them is answered with a lone report, from which the sender binds the address).
/// </summary>
public class LinkAddressRunsAndAnswersTests
{
    private static LinkAddress S(int n) => SimRig.Address(n);

    private static SimRig Nothing(int stations, int seed = 1) => new(stations, seed: seed, locates: (_, _) => false);

    private static (BurstHeader Header, byte[] Payload) DecodeBurst(float[] audio)
    {
        FormatOpening opening = StandardFormats.Describe(StandardFormats.FirstContact).Opening;
        BurstCandidate found = BurstFinder.Find(audio, opening).Single();
        var headerReader = new HeaderReader();
        headerReader.TryRead(audio, found.Index, opening, out BurstHeader header).Should().BeTrue();
        var receiver = new PayloadReceiver();
        byte[] payload = new byte[PayloadReceiver.PayloadRoom(header)];
        receiver.Receive(audio, found.Index, header, headerReader.Bits, payload).CheckPassed.Should().BeTrue();
        return (header, payload);
    }

    private static List<(uint Key, byte Protocol, int Count)> RunsOf(byte[] payload, bool toEveryone)
    {
        PayloadCodec.TryUnpack(payload, toEveryone, out _, out ExtensionReader ext, out _).Should().BeTrue();
        var runs = new List<(uint, byte, int)>();
        while (ext.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.FromTag)
            {
                for (int i = 0; i < value.Length; i += PayloadCodec.RunBytes)
                {
                    runs.Add((PayloadCodec.ReadKey(value[i..]), value[i + 4], value[i + 5]));
                }
            }
        }

        return runs;
    }

    // ---- runs on the wire ----

    [Fact]
    public void Datagrams_From_One_Address_With_One_Protocol_Are_One_Run_And_A_Change_Of_Either_Starts_A_New_One_In_The_Order_They_Go()
    {
        var rig = new SimRig(2, locates: (i, j) => i == 1 && j == 2);
        var audio = new List<float[]>();
        rig.Stations[0].TransmitRequest += r => audio.Add(r.Samples.ToArray());
        LinkAddress x = LinkAddress.FromAscii("RUN-X");
        LinkAddress y = LinkAddress.FromAscii("RUN-Y");
        rig.RunTo(1);
        Station a = rig.Stations[0];

        // Six small datagrams: x/IP, x/IP, x/ARP (the protocol changes), y/ARP (the address changes), y/ARP, x/IP (back again: a new run, not a merge).
        (LinkAddress From, byte Protocol)[] plan = [(x, LinkProtocol.Ip), (x, LinkProtocol.Ip), (x, LinkProtocol.Arp), (y, LinkProtocol.Arp), (y, LinkProtocol.Arp), (x, LinkProtocol.Ip)];
        for (int i = 0; i < plan.Length; i++)
        {
            a.Send(new byte[] { (byte)(10 + i) }, S(2), plan[i].From, plan[i].Protocol, new HostTag(i)).Should().Be(SendResult.Accepted);
        }

        rig.Run(40);
        audio.Should().HaveCount(1, "six one-byte datagrams fit one burst");
        (_, byte[] payload) = DecodeBurst(audio[0]);
        List<(uint Key, byte Protocol, int Count)> runs = RunsOf(payload, false);
        runs.Should().Equal(
            (x.Key, LinkProtocol.Ip, 2),
            (x.Key, LinkProtocol.Arp, 1),
            (y.Key, LinkProtocol.Arp, 2),
            (x.Key, LinkProtocol.Ip, 1));
        runs.Sum(r => r.Count).Should().Be(6, "the counts add up to the datagrams");

        // The receiving host gets each datagram with the key and protocol of its run.
        rig.Nodes[1].Received.Select(r => (r.Data[0], r.FromKey, r.Protocol)).Should().Equal(
            ((byte)10, x.Key, LinkProtocol.Ip), ((byte)11, x.Key, LinkProtocol.Ip), ((byte)12, x.Key, LinkProtocol.Arp), ((byte)13, y.Key, LinkProtocol.Arp), ((byte)14, y.Key, LinkProtocol.Arp), ((byte)15, x.Key, LinkProtocol.Ip));
    }

    [Fact]
    public void Expedited_Datagrams_Are_In_The_Runs_Where_They_Go_Ahead_Of_The_Ordinary_Ones()
    {
        var rig = new SimRig(2, locates: (i, j) => i == 1 && j == 2);
        var audio = new List<float[]>();
        rig.Stations[0].TransmitRequest += r => audio.Add(r.Samples.ToArray());
        LinkAddress x = LinkAddress.FromAscii("RUN-X");
        rig.RunTo(1);
        Station a = rig.Stations[0];
        a.Send(new byte[] { 1 }, S(2), S(1), LinkProtocol.Ip, new HostTag(1)).Should().Be(SendResult.Accepted);
        a.Send(new byte[] { 2 }, S(2), x, LinkProtocol.Arp, new HostTag(2), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(40);
        audio.Should().HaveCount(1);
        RunsOf(DecodeBurst(audio[0]).Payload, false).Should().Equal((x.Key, LinkProtocol.Arp, 1), (S(1).Key, LinkProtocol.Ip, 1));
    }

    [Fact]
    public void A_Run_Never_Passes_255_Datagrams()
    {
        // 300 one-byte datagrams from one address: a recorded burst to everyone, built the way a station builds it, is read as 300 datagrams; the run builder starts a second run at 255.
        using var rig = new ReceiveRig(0);
        var seen = new List<(uint Key, byte Protocol)>();
        rig.Station.Received += e => seen.Add((e.FromKey, e.Protocol));
        byte[][] grams = [.. Enumerable.Range(0, 300).Select(i => new[] { (byte)i })];
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 0, 31, grams, null, 0, 0, from: [LinkAddress.FromAscii("BIG").Key]));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();
        seen.Should().HaveCount(300).And.OnlyContain(x => x.Key == LinkAddress.FromAscii("BIG").Key && x.Protocol == LinkProtocol.NoLayer3);
    }

    // ---- received: the From key and protocol of each datagram, and what is malformed ----

    [Fact]
    public void Received_Carries_The_From_Key_And_Protocol_Of_Each_Datagram_In_A_Mixed_Burst_And_How_It_Was_Addressed()
    {
        using var rig = new ReceiveRig(0);
        var got = new List<ReceivedEvent>();
        rig.Station.Received += e => got.Add(e with { Datagram = e.Datagram.ToArray() });
        uint k1 = LinkAddress.FromAscii("ONE").Key;
        uint k2 = LinkAddress.FromAscii("TWO").Key;
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 1, 12, [[1, 1], [2, 2], [3, 3], [4, 4]], new PayloadPrelude(false, null, false, null), 0, 12,
            from: [k1, k1, k2, k1], protocols: [LinkProtocol.Ip, LinkProtocol.Ip, LinkProtocol.NetRom, LinkProtocol.Ax25Frame]));
        b.Silence(1.0);
        b.Add(TestBurst.Data(8, 0, 12, [[5, 5]], null, 1, 12, from: [k2], protocols: [LinkProtocol.Arp], forKey: LinkAddress.FromAscii("SOMEONE").Key));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();

        got.Select(e => (e.Datagram.Span[0], e.FromKey, e.Protocol, e.Addressing)).Should().Equal(
            ((byte)1, k1, LinkProtocol.Ip, Addressing.ToThisStation),
            ((byte)2, k1, LinkProtocol.Ip, Addressing.ToThisStation),
            ((byte)3, k2, LinkProtocol.NetRom, Addressing.ToThisStation),
            ((byte)4, k1, LinkProtocol.Ax25Frame, Addressing.ToThisStation),
            ((byte)5, k2, LinkProtocol.Arp, Addressing.ForAddressNotYetLocated));
        got[4].ForKey.Should().Be(LinkAddress.FromAscii("SOMEONE").Key);
        got[0].ForKey.Should().BeNull();
        rig.Station.LocatedNow(LinkAddress.FromAscii("ONE")).Should().Be(7);
        rig.Station.LocatedNow(LinkAddress.FromAscii("TWO")).Should().Be(8);
    }

    [Theory]
    [InlineData("counts that add up to fewer datagrams")]
    [InlineData("counts that add up to more datagrams")]
    [InlineData("a count of zero")]
    [InlineData("a length that is not whole runs")]
    [InlineData("no From item")]
    [InlineData("an empty From item")]
    public void A_Burst_Whose_Runs_Do_Not_Add_Up_To_Its_Datagrams_Delivers_Nothing_And_Teaches_Nothing(string case_)
    {
        using var rig = new ReceiveRig(0);
        var got = new List<ReceivedEvent>();
        rig.Station.Received += e => got.Add(e);
        uint key = LinkAddress.FromAscii("BAD").Key;
        byte[] Runs(params (int Count, bool _)[] counts)
        {
            var bytes = new byte[counts.Length * PayloadCodec.RunBytes];
            for (int i = 0; i < counts.Length; i++)
            {
                PayloadCodec.WriteRun(key, LinkProtocol.NoLayer3, counts[i].Count, bytes.AsSpan(i * PayloadCodec.RunBytes));
            }

            return bytes;
        }

        ExtensionItem[]? extra = case_ switch
        {
            "counts that add up to fewer datagrams" => [new ExtensionItem(PayloadCodec.FromTag, Runs((1, false)))],
            "counts that add up to more datagrams" => [new ExtensionItem(PayloadCodec.FromTag, Runs((2, false), (1, false)))],
            "a count of zero" => [new ExtensionItem(PayloadCodec.FromTag, Runs((0, false), (2, false)))],
            "a length that is not whole runs" => [new ExtensionItem(PayloadCodec.FromTag, new byte[7])],
            "an empty From item" => [new ExtensionItem(PayloadCodec.FromTag, Array.Empty<byte>())],
            _ => null,
        };
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 1, 12, [[1, 1], [2, 2]], new PayloadPrelude(false, 77, false, null), 0, 12, noFrom: true, extra: extra));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();

        got.Should().BeEmpty("nothing is delivered from a burst whose runs are not whole");
        rig.Station.LearnedAddresses.Count.Should().Be(0, "and nothing is learned from it");
        rig.Calls.Should().ContainSingle().Which.Decoded.Should().BeTrue("the burst itself decoded and was measured as any other");
    }

    // ---- a reply to a key alone ----

    [Fact]
    public void A_Reply_Sent_To_A_Key_Alone_Reaches_The_Sender_Addressed_Not_To_Everyone()
    {
        SimRig rig = Nothing(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(1, 2, 100).Should().Be(SendResult.Accepted);
        rig.Run(14);
        SimReceived first = b.Received.Should().ContainSingle().Subject;
        first.FromKey.Should().Be(S(1).Key);

        // B's host knows the sender of that datagram only by its key, and answers to it.
        LinkAddress sender = LinkAddress.FromKey(first.FromKey);
        rig.Stations[1].Send(SimRig.Datagram(200, 30), sender, S(2), LinkProtocol.NoLayer3, new HostTag(200)).Should().Be(SendResult.Accepted);
        rig.Run(14);

        SimReceived reply = a.Received.Should().ContainSingle().Subject;
        reply.Addressing.Should().Be(Addressing.ToThisStation, "B had learned where that key lives from the burst's From, so the reply is addressed");
        reply.FromKey.Should().Be(S(2).Key);
        b.Transmitted.Single().Event.Address.Should().Be(sender);
        rig.Stations[1].LocatedNow(sender).Should().Be(1);

        // The same queue is found by the full address, and by the key: Cancel takes either.
        rig.Stations[1].Send(SimRig.Datagram(201, 30), S(1), S(2), LinkProtocol.NoLayer3, new HostTag(201)).Should().Be(SendResult.Accepted);
        rig.Stations[1].Send(SimRig.Datagram(202, 30), S(1), S(2), LinkProtocol.NoLayer3, new HostTag(202)).Should().Be(SendResult.Accepted);
        rig.Stations[1].QueuedFor(sender).Should().Be(1, "the first went into a burst at once, the second waits");
        rig.Stations[1].Cancel(sender).Should().Be(1);
        rig.Stations[1].QueuedFor(S(1)).Should().Be(0);
    }

    // ---- the rate both ways ----

    [Fact]
    public void LinkState_Gives_The_Rate_To_The_Address_And_The_Rate_Recommended_To_It_And_Both_Are_There_After_Traffic_Each_Way()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        rig.RunTo(6);
        for (int i = 0; i < 4; i++)
        {
            rig.Send(1, 2, 100 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
            rig.Send(2, 1, 200 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
        }

        a.LinkStates.First().RecommendedRateBitsPerSecond.Should().BeNull("nothing has been recommended to B yet");
        LinkStateEvent last = a.LinkStates.Last();
        last.RateBitsPerSecond.Should().NotBeNull().And.BeGreaterThan(10 * SimRig.FirstContactRate, "the rate A sends to B at");
        last.RecommendedRateBitsPerSecond.Should().NotBeNull().And.BeGreaterThan(10 * SimRig.FirstContactRate, "the rate of the format A has recommended to B");
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? nb).Should().BeTrue();
        last.RecommendedRateBitsPerSecond.Should().Be(StandardFormats.Rate(nb!.Told!.Value) * 1000.0);
        a.LinkStates.Select(l => l.RecommendedRateBitsPerSecond).Where(r => r is not null).Should().NotBeEmpty();
        rig.Stations[0].Snapshot().Single().RecommendedRateBitsPerSecond.Should().Be(last.RecommendedRateBitsPerSecond);
    }

    // ---- answering for its own addresses ----

    private static void OwnTheAddress(SimRig rig, int station)
    {
        // The host has sent from its own address once, and the datagram was cancelled before it went: the station now answers for that address.
        Station s = rig.Stations[station - 1];
        s.Send(SimRig.Datagram(900, 10), LinkAddress.FromAscii("ELSEWHERE"), S(station), LinkProtocol.NoLayer3, new HostTag(900)).Should().Be(SendResult.Accepted);
        s.Cancel(new HostTag(900)).Should().Be(1);
    }

    [Fact]
    public void A_Flood_For_An_Address_Whose_Host_Has_Sent_From_It_Is_Answered_At_Modem_Level_So_One_Way_Traffic_Becomes_Addressed_And_Climbs()
    {
        SimRig rig = Nothing(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        OwnTheAddress(rig, 2);
        rig.RunTo(6);
        for (int i = 0; i < 8; i++)
        {
            rig.Send(1, 2, 110 + i).Should().Be(SendResult.Accepted);
            rig.Run(10);
        }

        // B's host never sent a datagram. The first burst went to everyone; B answered it with a lone report; A bound the address and every later burst was addressed.
        b.Transmitted.Should().BeEmpty();
        b.ModemSent.Select(m => m.Kind).Should().StartWith([ModemSentKind.Report]);
        b.Received.Select(r => r.Addressing).Should().Equal([Addressing.ForAddressNotYetLocated, .. Enumerable.Repeat(Addressing.ToThisStation, 7)]);
        rig.Stations[0].LocatedNow(S(2)).Should().Be(2);
        a.LinkStates.Should().Contain(l => l.Located);
        a.Transmitted.Skip(1).Select(t => t.Event.RateBitsPerSecond).Last().Should().BeGreaterThan(50 * SimRig.FirstContactRate, "the rate climbs although nothing ever comes back from the host");
        b.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(110, 8).Select(t => SimRig.Datagram(t, 30)), o => o.WithStrictOrdering());
    }

    [Fact]
    public void After_Three_Silences_Have_Forgotten_The_Address_The_Next_Flood_Relearns_It_Without_A_Word_From_The_Far_Host()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        OwnTheAddress(rig, 2);
        rig.RunTo(6);
        rig.Send(1, 2, 1).Should().Be(SendResult.Accepted);
        rig.Run(12);
        rig.Channel.Link(a, b).Gain = 0;
        rig.Channel.Link(b, a).Gain = 0;
        for (int k = 0; k < 7; k++)
        {
            rig.Send(1, 2, 10 + k).Should().Be(SendResult.Accepted);
            while (a.Transmitted.Count < k + 2)
            {
                rig.Run(0.5);
            }

            SimTransmission tx = a.Transmissions[^1];
            rig.Channel.Run(tx.Start + tx.Length + Station.AnswerWaitSamples(tx.Length) + 10);
        }

        rig.Stations[0].LocatedNow(S(2)).Should().Be(0, "forgotten after three silences");
        rig.Channel.Link(a, b).Gain = 1.0;
        rig.Channel.Link(b, a).Gain = 1.0;
        rig.Send(1, 2, 20).Should().Be(SendResult.Accepted);
        rig.Run(14);
        b.Received.Last().Addressing.Should().Be(Addressing.ForAddressNotYetLocated);
        rig.Stations[0].LocatedNow(S(2)).Should().Be(2, "B answered the flood with a report, and A bound the address from it");
        rig.Send(1, 2, 21).Should().Be(SendResult.Accepted);
        rig.Run(14);
        b.Received.Last().Addressing.Should().Be(Addressing.ToThisStation);
        b.Transmitted.Should().BeEmpty("B's host sent nothing");
    }

    [Fact]
    public void A_Station_Whose_Host_Has_Never_Sent_From_The_Address_Does_Not_Answer()
    {
        SimRig rig = Nothing(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(1, 2, 100).Should().Be(SendResult.Accepted);
        rig.Run(20);
        b.Received.Should().ContainSingle().Which.Addressing.Should().Be(Addressing.ForAddressNotYetLocated);
        b.ModemSent.Should().BeEmpty();
        b.Transmissions.Should().BeEmpty();
        rig.Stations[0].LocatedNow(S(2)).Should().Be(0);
        rig.Stations[1].ReportsOwedCount.Should().Be((0L, 0L, 0L));
    }

    [Fact]
    public void No_Report_Is_Sent_For_A_True_Broadcast_Or_For_A_Key_That_Is_Not_The_Stations_Own()
    {
        SimRig rig = Nothing(2);
        SimNode b = rig.Nodes[1];
        OwnTheAddress(rig, 2);
        rig.RunTo(6);
        rig.Send(1, LinkAddress.Everyone, 1).Should().Be(SendResult.Accepted);
        rig.Run(14);
        rig.Send(1, LinkAddress.FromAscii("NOBODY"), 2).Should().Be(SendResult.Accepted);
        rig.Run(14);
        b.Received.Select(r => r.Addressing).Should().Equal(Addressing.ToEveryone, Addressing.ForAddressNotYetLocated);
        b.ModemSent.Should().BeEmpty();
        b.Transmissions.Should().BeEmpty();
    }

    [Fact]
    public void A_Lone_Report_Binds_The_Address_Of_The_One_Flood_Outstanding_And_Nothing_When_There_Is_None_Or_Two_Keys()
    {
        LinkAddress one = LinkAddress.FromAscii("FLOOD-1");
        LinkAddress two = LinkAddress.FromAscii("FLOOD-2");
        foreach ((string label, uint[] floods, int expectedAtOne) in new (string, uint[], int)[]
        {
            ("one flood outstanding", [one.Key], 6),
            ("the same address flooded twice", [one.Key, one.Key], 6),
            ("none outstanding", [], 0),
            ("two different keys outstanding", [one.Key, two.Key], 0),
        })
        {
            using var rig = new ReceiveRig(0);
            foreach (uint key in floods)
            {
                rig.Station.NoteFlood(key, 0, long.MaxValue / 2);
            }

            var b = new StreamBuilder().Silence(1.2);
            b.Add(TestBurst.Report(6, 1, 140, false, FormatOpening.Full));
            b.Silence(2.5).Noise(0.01, 3);
            rig.Push(b.ToArray(), () => 4800);
            rig.Finish();

            rig.Station.LocatedNow(one).Should().Be(expectedAtOne, label);
            rig.Station.LocatedNow(two).Should().Be(0, label);
            rig.Station.Adaptation.TryGetNeighbour(6, out LinkNeighbour? nb).Should().BeTrue(label);
            nb!.ReportFormat.Should().Be(140, "the report is applied to the station whatever is learned from it: " + label);
        }
    }

    [Fact]
    public void A_Flood_Whose_Answer_Wait_Has_Run_Out_Binds_Nothing()
    {
        LinkAddress one = LinkAddress.FromAscii("FLOOD-1");
        using var rig = new ReceiveRig(0);
        rig.Station.NoteFlood(one.Key, 0, 1000);
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Report(6, 1, 140, false, FormatOpening.Full));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();
        rig.Station.LocatedNow(one).Should().Be(0);
    }
}
