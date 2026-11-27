using System.Reflection;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Link addresses, learning where an address lives and sending and receiving by address (docs/station-numbers.md, step one): stations that start with nothing located, the From and For items, what is forgotten when, and events by address.
/// Every test steps the sample clock; none waits.
/// </summary>
public class LinkAddressStationTests
{
    private static LinkAddress S(int n) => SimRig.Address(n);

    /// <summary>Stations that know of no address but their own hosts', on one channel, all pairs connected.</summary>
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

    private static (List<(byte Tag, string Value)> Items, List<string> Datagrams) Read(byte[] payload, bool toEveryone)
    {
        PayloadCodec.TryUnpack(payload, toEveryone, out _, out ExtensionReader ext, out DatagramReader reader).Should().BeTrue();
        var items = new List<(byte, string)>();
        while (ext.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            items.Add((tag, Convert.ToHexString(value)));
        }

        var grams = new List<string>();
        while (reader.TryNext(out ReadOnlySpan<byte> g))
        {
            grams.Add(Convert.ToHexString(g));
        }

        return (items, grams);
    }

    private static string Key(LinkAddress a)
    {
        var wire = new byte[4];
        PayloadCodec.WriteKey(a.Key, wire);
        return Convert.ToHexString(wire);
    }

    // ---- the wire: From and For, and an identification untouched ----

    [Fact]
    public void A_Burst_To_One_Station_Carries_From_In_Its_Extension_Block_And_One_To_Everyone_Carries_It_In_Its_Leading_Block_With_For_When_The_Address_Is_Not_Located()
    {
        var rig = new SimRig(2, locates: (i, j) => i == 1 && j == 2);
        var audio = new List<float[]>();
        rig.Stations[0].TransmitRequest += r => audio.Add(r.Samples.ToArray());
        rig.RunTo(6);
        rig.Stations[0].Send(SimRig.Datagram(1, 20), S(2), S(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        rig.Run(12);
        LinkAddress unknown = LinkAddress.FromAscii("NOBODY");
        rig.Stations[0].Send(SimRig.Datagram(2, 20), unknown, S(1), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        rig.Run(20);
        rig.Stations[0].Send(SimRig.Datagram(3, 20), LinkAddress.Everyone, S(1), LinkProtocol.NoLayer3, new HostTag(3)).Should().Be(SendResult.Accepted);
        rig.Run(20);

        audio.Should().HaveCount(3);
        (BurstHeader h1, byte[] p1) = DecodeBurst(audio[0]);
        h1.Destination.Should().Be(2);
        Read(p1, false).Items.Should().Equal((PayloadCodec.FromTag, Key(S(1)) + "F001"));

        (BurstHeader h2, byte[] p2) = DecodeBurst(audio[1]);
        h2.Destination.Should().Be(0);
        (List<(byte Tag, string Value)> items2, List<string> grams2) = Read(p2, true);
        items2.Should().Equal((PayloadCodec.FromTag, Key(S(1)) + "F001"), (PayloadCodec.ForTag, Key(unknown)));
        grams2.Should().Equal(Convert.ToHexString(SimRig.Datagram(2, 20)));

        (BurstHeader h3, byte[] p3) = DecodeBurst(audio[2]);
        h3.Destination.Should().Be(0);
        Read(p3, true).Items.Should().Equal((PayloadCodec.FromTag, Key(S(1)) + "F001"));
    }

    [Fact]
    public void Datagrams_Sent_From_Several_Addresses_In_One_Burst_Are_Listed_As_Runs_In_Their_Order()
    {
        var rig = new SimRig(2, locates: (i, j) => i == 1 && j == 2);
        var audio = new List<float[]>();
        rig.Stations[0].TransmitRequest += r => audio.Add(r.Samples.ToArray());
        LinkAddress x = LinkAddress.FromAscii("ALIAS-X");
        LinkAddress y = LinkAddress.FromAscii("ALIAS-Y");
        rig.RunTo(1);
        Station a = rig.Stations[0];
        a.Send(SimRig.Datagram(1, 8), S(2), x, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        a.Send(SimRig.Datagram(2, 8), S(2), y, LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        a.Send(SimRig.Datagram(3, 8), S(2), x, LinkProtocol.NoLayer3, new HostTag(3)).Should().Be(SendResult.Accepted);
        rig.Run(30);

        audio.Should().HaveCount(1, "all three datagrams fit one burst");
        (_, byte[] payload) = DecodeBurst(audio[0]);
        (List<(byte Tag, string Value)> items, List<string> grams) = Read(payload, false);
        items.Should().Equal((PayloadCodec.FromTag, Key(x) + "F001" + Key(y) + "F001" + Key(x) + "F001"));
        grams.Should().Equal(Convert.ToHexString(SimRig.Datagram(1, 8)), Convert.ToHexString(SimRig.Datagram(2, 8)), Convert.ToHexString(SimRig.Datagram(3, 8)));
        rig.Nodes[1].Received.Select(r => r.Data).Should().BeEquivalentTo([SimRig.Datagram(1, 8), SimRig.Datagram(2, 8), SimRig.Datagram(3, 8)], o => o.WithStrictOrdering());
    }

    [Fact]
    public void The_Identification_Burst_Has_No_Leading_Block_And_Teaches_Nothing()
    {
        var rig = new SimRig(2);
        rig.Stations[0].IdentificationSuspended = false;
        var audio = new List<float[]>();
        rig.Stations[0].TransmitRequest += r => audio.Add(r.Samples.ToArray());
        rig.RunTo(6);
        rig.Send(1, 2, 1).Should().Be(SendResult.Accepted);
        rig.Run(30);

        audio.Should().HaveCount(2, "the datagram and then the identification");
        (BurstHeader header, byte[] payload) = DecodeBurst(audio[1]);
        header.Kind.Should().Be(BurstKind.Identification);
        byte[] expected = new byte[40];
        int n = PayloadCodec.PackIdentification("M0S1"u8, 0, default, expected);
        payload.AsSpan(0, n).ToArray().Should().Equal(expected.AsSpan(0, n).ToArray(), "the callsign's length first, as it always was");
        payload[0].Should().Be(4);
        rig.Stations[1].LearnedAddresses.Count.Should().Be(1, "only the address in the datagram's From: the identification added none");
    }

    // ---- learning ----

    [Fact]
    public void A_Station_Learns_From_Every_Data_Burst_Whoever_It_Is_For()
    {
        using var rig = new ReceiveRig(0);
        LinkAddress forMe = LinkAddress.FromAscii("FOR-ME");
        LinkAddress forAll = LinkAddress.FromAscii("FOR-ALL");
        LinkAddress forOther = LinkAddress.FromAscii("FOR-OTHER");
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 1, 12, [TestBurst.Datagram(1, 30)], new PayloadPrelude(false, null, false, null), symbols: 12, from: [forMe.Key]));
        b.Silence(1.0);
        b.Add(TestBurst.Data(8, 0, 12, [TestBurst.Datagram(2, 30)], null, 1, 12, from: [forAll.Key], forKey: LinkAddress.FromAscii("SOMEONE").Key));
        b.Silence(1.0);
        b.Add(TestBurst.Data(9, 5, 12, [TestBurst.Datagram(3, 30)], new PayloadPrelude(false, null, false, null), 2, 12, from: [forOther.Key]));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();

        rig.Station.LocatedNow(forMe).Should().Be(7, "a burst for this station");
        rig.Station.LocatedNow(forAll).Should().Be(8, "a burst for everyone");
        rig.Station.LocatedNow(forOther).Should().Be(9, "a burst for another station, read just far enough to see its From");
        rig.Datagrams.Select(d => d.Sender).Should().Equal(7, 8);
    }

    [Fact]
    public void The_Latest_Burst_Heard_Wins_And_An_Unknown_Tag_Is_Skipped()
    {
        using var rig = new ReceiveRig(0);
        LinkAddress moving = LinkAddress.FromAscii("MOVING");
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 0, 12, [TestBurst.Datagram(1, 20)], null, 0, 12, from: [moving.Key]));
        b.Silence(1.0);

        // The second burst, from another station, has an item this version does not know in front of its From, and a second unknown one after it.
        b.Add(TestBurst.Data(8, 0, 12, [TestBurst.Datagram(2, 20)], null, 1, 12, from: [moving.Key],
            extra: [new ExtensionItem(0x7F, new byte[] { 1, 2, 3 }), new ExtensionItem(9, default)]));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();

        rig.Station.LocatedNow(moving).Should().Be(8);
        rig.Datagrams.Select(d => d.Sender).Should().Equal(7, 8);
    }

    [Fact]
    public void A_From_Item_Of_The_Wrong_Length_Teaches_Nothing_And_Leaves_The_Rest_Alone()
    {
        using var rig = new ReceiveRig(0);
        LinkAddress good = LinkAddress.FromAscii("GOOD");
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 0, 12, [TestBurst.Datagram(1, 20)], null, 0, 12, extra: [new ExtensionItem(PayloadCodec.FromTag, new byte[5]), new ExtensionItem(PayloadCodec.ForTag, new byte[3])],
            from: [good.Key]));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();

        rig.Station.LearnedAddresses.Count.Should().Be(1, "the key in the well-formed From; the five-byte one and the three-byte For are skipped");
        rig.Station.LocatedNow(good).Should().Be(7);
        rig.Log.Single(l => l.StartsWith("rx ", StringComparison.Ordinal)).Should().Contain("ToEveryone").And.Contain("for -");
    }

    [Fact]
    public void Two_Stations_With_Nothing_Located_Exchange_A_Datagram_And_Its_Reply_And_Then_Address_Each_Other_And_Climb()
    {
        SimRig rig = Nothing(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);

        // The first datagram goes to everyone, for S2, from S1, at first contact, and no answer is waited for.
        rig.Send(1, 2, 100).Should().Be(SendResult.Accepted);
        rig.Run(14);
        SimReceived first = b.Received.Should().ContainSingle().Subject;
        first.Addressing.Should().Be(Addressing.ForAddressNotYetLocated);
        first.ForKey.Should().Be(S(2).Key);
        first.Sender.Should().Be(1);
        a.Transmitted.Single().Event.Address.Should().Be(S(2));
        a.Transmitted.Single().Event.Format.Should().Be(StandardFormats.FirstContact);
        a.LinkStates.Should().ContainSingle().Which.Should().Match<LinkStateEvent>(l => l.Address == S(2) && !l.Located && l.Format == null && l.DatagramRoom == 331);
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out _).Should().BeFalse("nothing was asked of anyone");
        rig.Stations[1].LocatedNow(S(1)).Should().Be(1, "S2's station learned S1 from the From of the burst, though it was not for it");
        rig.Stations[0].LocatedNow(S(2)).Should().Be(0);

        // The reply is addressed: S2's station knows where S1 lives. It carries its own From, so S1's station learns where S2 lives.
        rig.Send(2, 1, 200).Should().Be(SendResult.Accepted);
        rig.Run(14);
        a.Received.Should().ContainSingle().Which.Addressing.Should().Be(Addressing.ToThisStation);
        b.Transmitted.Single().Event.Address.Should().Be(S(1));
        rig.Stations[0].LocatedNow(S(2)).Should().Be(2);
        a.LinkStates.Last().Should().Match<LinkStateEvent>(l => l.Address == S(2) && l.Located);

        // From here on both go addressed, and the rate climbs as it does between stations that have always known each other.
        for (int i = 1; i <= 5; i++)
        {
            rig.Send(1, 2, 100 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
            rig.Send(2, 1, 200 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
        }

        b.Received.Skip(1).Should().OnlyContain(r => r.Addressing == Addressing.ToThisStation);
        a.Received.Should().OnlyContain(r => r.Addressing == Addressing.ToThisStation);
        b.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(100, 6).Select(t => SimRig.Datagram(t, 30)), o => o.WithStrictOrdering());
        a.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(200, 6).Select(t => SimRig.Datagram(t, 30)), o => o.WithStrictOrdering());
        a.Transmitted.Skip(3).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 10 * SimRig.FirstContactRate);
        b.Transmitted.Skip(3).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 10 * SimRig.FirstContactRate);
        a.Dropped.Should().BeEmpty();
        b.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void A_True_Broadcast_Has_No_For_And_Is_Heard_As_Everyone()
    {
        SimRig rig = Nothing(3);
        rig.RunTo(6);
        rig.Send(1, LinkAddress.Everyone, 1, reportWanted: true).Should().Be(SendResult.Accepted);
        rig.Run(14);
        foreach (int listener in new[] { 1, 2 })
        {
            SimReceived got = rig.Nodes[listener].Received.Should().ContainSingle().Subject;
            got.Addressing.Should().Be(Addressing.ToEveryone);
            got.ForKey.Should().BeNull();
            rig.Stations[listener].LocatedNow(S(1)).Should().Be(1, "a broadcast says where its sender's address lives, like any data burst");
        }

        rig.Nodes[0].Transmitted.Single().Event.Address.Should().Be(LinkAddress.Everyone);
        rig.Nodes[0].LinkStates.Should().BeEmpty("everyone is not an address with a link");
    }

    [Fact]
    public void A_Third_Stations_Host_Gets_The_First_Datagram_Marked_Not_Yet_Located_With_The_Key_And_Does_Not_Get_The_Addressed_Ones()
    {
        SimRig rig = Nothing(3);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        SimNode c = rig.Nodes[2];
        rig.RunTo(6);
        rig.Send(1, 2, 100).Should().Be(SendResult.Accepted);
        rig.Run(14);
        SimReceived atC = c.Received.Should().ContainSingle().Subject;
        atC.Addressing.Should().Be(Addressing.ForAddressNotYetLocated, "the modem does not filter: the host is told the burst was for an address, and which");
        atC.ForKey.Should().Be(S(2).Key, "the key of S2, so a host that is not S2 can ignore it");
        atC.Data.Should().Equal(SimRig.Datagram(100, 30));

        rig.Send(2, 1, 200).Should().Be(SendResult.Accepted);
        rig.Run(14);
        rig.Send(1, 2, 101).Should().Be(SendResult.Accepted);
        rig.Run(14);
        rig.Send(2, 1, 201).Should().Be(SendResult.Accepted);
        rig.Run(14);

        c.Received.Should().ContainSingle("the addressed bursts are not for C's station, and are not delivered");
        b.Received.Should().HaveCount(2);
        a.Received.Should().HaveCount(2);
        rig.Stations[2].LocatedNow(S(1)).Should().Be(1, "C learns from bursts it only overhears");
        rig.Stations[2].LocatedNow(S(2)).Should().Be(2);
    }

    // ---- forgetting ----

    [Fact]
    public void An_Address_Forgotten_After_Three_Silences_Is_Sent_To_Everyone_Again_And_Relearned_When_The_Far_Host_Answers()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(2, 1, 1).Should().Be(SendResult.Accepted);
        rig.Run(12);
        rig.Channel.Link(a, b).Gain = 0;
        rig.Channel.Link(b, a).Gain = 0;
        a.LinkStates.Clear();
        for (int k = 0; k < 4; k++)
        {
            rig.Send(1, 2, 10 + k).Should().Be(SendResult.Accepted);
            while (a.Transmitted.Count < k + 1)
            {
                rig.Run(0.5);
            }

            SimTransmission tx = a.Transmissions[^1];
            rig.Channel.Run(tx.Start + tx.Length + Station.AnswerWaitSamples(tx.Length) + 10);
        }

        rig.Stations[0].LocatedNow(S(2)).Should().Be(0, "three bursts running met silence");
        a.LinkStates.Last().Should().Match<LinkStateEvent>(l => l.Address == S(2) && !l.Located && l.Gone, "the host is told");
        a.LinkStates.Count(l => l.Gone).Should().Be(1);
        rig.Stations[0].Snapshot().Single().Should().Match<LinkStateEvent>(l => !l.Located && l.Gone);

        // The path is back. The next datagram goes to everyone, for S2; S2's host answers, and S1's station learns where S2 is.
        rig.Channel.Link(a, b).Gain = 1.0;
        rig.Channel.Link(b, a).Gain = 1.0;
        rig.Send(1, 2, 20).Should().Be(SendResult.Accepted);
        rig.Run(14);
        b.Received.Last().Addressing.Should().Be(Addressing.ForAddressNotYetLocated);
        b.Received.Last().ForKey.Should().Be(S(2).Key);
        rig.Send(2, 1, 21).Should().Be(SendResult.Accepted);
        rig.Run(14);
        rig.Stations[0].LocatedNow(S(2)).Should().Be(2);
        a.LinkStates.Last().Located.Should().BeTrue();
        rig.Send(1, 2, 22).Should().Be(SendResult.Accepted);
        rig.Run(14);
        b.Received.Last().Addressing.Should().Be(Addressing.ToThisStation);
    }

    [Fact]
    public void An_Address_Is_Forgotten_With_The_Station_It_Lives_At_After_Ten_Minutes_And_Learned_Again_From_Its_Next_Burst()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector");
        using var rig = new ReceiveRig(0);
        LinkAddress far = LinkAddress.FromAscii("FAR");
        rig.SendTo(7);
        rig.Station.Locate(far, 7);
        var b = new StreamBuilder().Silence(1.2);
        b.Add(TestBurst.Data(7, 0, 12, [TestBurst.Datagram(1, 20)], null, 0, 12, from: [far.Key]));
        b.Silence(2.5).Noise(0.01, 3);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();
        rig.Station.LocatedNow(far).Should().Be(7);

        rig.Station.Skip(9 * 60 * 48000);
        rig.Station.DeliverResults();
        rig.Station.LearnedAddresses.Count.Should().Be(2, "nine minutes is not ten");

        rig.Station.Skip(2 * 60 * 48000);
        rig.Station.DeliverResults();
        rig.Station.LocatedNow(far).Should().Be(0);
        rig.Station.LearnedAddresses.Count.Should().Be(0, "both the one learned and the one the host sent to went with the station");
    }

    [Fact]
    public void Datagrams_Queued_For_An_Address_When_It_Becomes_Located_Go_Addressed_None_Lost_Or_Duplicated()
    {
        SimRig rig = Nothing(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        for (int i = 0; i < 3; i++)
        {
            rig.Send(1, 2, 10 + i, length: 40).Should().Be(SendResult.Accepted);
        }

        rig.Stations[0].QueuedFor(S(2)).Should().Be(3);
        rig.Stations[0].Locate(S(2), 2);
        rig.RunTo(60);

        b.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(10, 3).Select(t => SimRig.Datagram(t, 40)), o => o.WithStrictOrdering());
        b.Received.Should().OnlyContain(r => r.Addressing == Addressing.ToThisStation);
        a.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(10L, 11L, 12L);
        a.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void Datagrams_Queued_For_An_Address_When_It_Is_Forgotten_Go_To_Everyone_None_Lost_Or_Duplicated()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        for (int i = 0; i < 3; i++)
        {
            rig.Send(1, 2, 10 + i, length: 40).Should().Be(SendResult.Accepted);
        }

        rig.Stations[0].LearnedAddresses.Forget(S(2).Key).Should().BeTrue();
        rig.RunTo(90);

        b.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(10, 3).Select(t => SimRig.Datagram(t, 40)), o => o.WithStrictOrdering());
        b.Received.Should().OnlyContain(r => r.Addressing == Addressing.ForAddressNotYetLocated && r.ForKey == S(2).Key);
        a.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(10L, 11L, 12L);
        a.Transmitted.Should().OnlyContain(t => t.Event.Format == StandardFormats.FirstContact);
        a.Dropped.Should().BeEmpty();
    }

    // ---- capacity ----

    [Fact]
    public void The_Ceiling_Of_A_Burst_Counts_The_Block_With_Each_Distinct_From_Key()
    {
        // The first-contact format's ceiling (2.5 s) carries 57 bytes. Prelude 4, block 3 + 4 for each key, each 8-byte datagram 10: four from one address go together (4 + 7 + 40 = 51), and three from three (4 + 15 + 30 = 49) but not four (4 + 19 + 40 = 63).
        foreach ((string label, int[] groups) in new[] { ("one From", new[] { 4, 2 }), ("distinct Froms", new[] { 3, 3 }) })
        {
            var rig = new SimRig(2);
            rig.RunTo(1);
            for (int i = 0; i < 6; i++)
            {
                LinkAddress from = label == "one From" ? S(1) : LinkAddress.FromAscii($"FROM-{i % 4}");
                rig.Stations[0].Send(SimRig.Datagram(i, 8), S(2), from, LinkProtocol.NoLayer3, new HostTag(i)).Should().Be(SendResult.Accepted);
            }

            rig.Run(60);
            rig.Nodes[0].Transmitted.GroupBy(t => t.Event.Burst).Select(g => g.Count()).Should().Equal(groups, label);
            rig.Nodes[1].Received.Should().HaveCount(6, label);
        }
    }

    [Fact]
    public void The_Queues_Airtime_Counts_The_Block_And_Its_Distinct_From_Keys()
    {
        var rig = new SimRig(2, connect: false);
        Station a = rig.Stations[0];
        double rate = StandardFormats.Rate(StandardFormats.FirstContact) * 1000.0;
        a.Send(new byte[100], S(2), S(1), LinkProtocol.NoLayer3, new HostTag(1));
        a.QueuedAirtimeFor(S(2)).Should().BeApproximately((8 * (102 + 9)) / rate, 1e-9, "one datagram of 100 bytes, its length and a block with one run");
        a.Send(new byte[100], S(2), LinkAddress.FromAscii("OTHER"), LinkProtocol.NoLayer3, new HostTag(2));
        a.QueuedAirtimeFor(S(2)).Should().BeApproximately((8 * ((2 * 102) + 15)) / rate, 1e-9, "two runs");
        a.Send(new byte[100], LinkAddress.FromAscii("UNKNOWN"), S(1), LinkProtocol.NoLayer3, new HostTag(3));
        a.QueuedAirtimeFor(LinkAddress.FromAscii("UNKNOWN")).Should().BeApproximately((8 * (102 + 15)) / rate, 1e-9, "for an address not located the block carries a For key too");
    }

    // ---- events by address ----

    [Fact]
    public void Events_About_A_Station_Are_Raised_Once_For_Each_Address_The_Host_Sent_To_That_Lives_There()
    {
        var rig = new SimRig(2);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        LinkAddress alias = LinkAddress.FromAscii("S2-ALIAS");
        a.Locate(alias, 2);
        rig.RunTo(6);

        rig.Stations[0].Send(SimRig.Datagram(1, 30), S(2), S(1), LinkProtocol.NoLayer3, new HostTag(1), reportWanted: true);
        rig.Stations[0].Send(SimRig.Datagram(2, 30), alias, S(1), LinkProtocol.NoLayer3, new HostTag(2), reportWanted: true);
        node.LinkStates.Select(l => l.Address).Should().Equal(S(2), alias);
        rig.Run(30);

        node.Transmitted.Select(t => (t.Event.Tag.Value, t.Event.Address)).Should().Equal((1L, S(2)), (2L, alias));
        rig.Nodes[1].Received.Should().HaveCount(2).And.OnlyContain(r => r.Addressing == Addressing.ToThisStation);

        // B reports on the path: each of the two addresses at station 2 is told once for each report, in the order they were first sent to.
        node.PathReports.Should().NotBeEmpty();
        node.PathReports.Select(p => p.Address).Distinct().Should().BeEquivalentTo([S(2), alias]);
        node.PathReports.Count(p => p.Address == S(2)).Should().Be(node.PathReports.Count(p => p.Address == alias));

        // What is known of the link is told for each, and the snapshot lists both.
        node.LinkStates.Where(l => l.Address == S(2)).Select(l => (l.Format, l.Knowledge)).Should().Equal(node.LinkStates.Where(l => l.Address == alias).Select(l => (l.Format, l.Knowledge)));
        a.Snapshot().Select(s => s.Address).Should().Equal(S(2), alias);
        a.Snapshot().Should().OnlyContain(s => s.Located);
    }

    [Fact]
    public void Dropped_And_Congestion_Are_By_The_Address_The_Datagram_Was_For()
    {
        var rig = new SimRig(3, connect: false);
        Station a = rig.Stations[0];
        SimNode node = rig.Nodes[0];
        for (int i = 1; i <= 10; i++)
        {
            a.Send(new byte[100], S(2), S(1), LinkProtocol.NoLayer3, new HostTag(i)).Should().Be(SendResult.Accepted);
        }

        a.Send(new byte[100], S(2), S(1), LinkProtocol.NoLayer3, new HostTag(11)).Should().Be(SendResult.QueueFull);
        a.Send(new byte[10], S(3), S(1), LinkProtocol.NoLayer3, new HostTag(12)).Should().Be(SendResult.Accepted, "the bound is for each address's queue");
        node.Congestion.Should().Equal(new CongestionEvent(S(2), false));
        a.Cancel(S(2)).Should().Be(10);
        node.Congestion.Should().Equal(new CongestionEvent(S(2), false), new CongestionEvent(S(2), true));
        node.Dropped.Should().HaveCount(10).And.OnlyContain(d => d.Reason == DropReason.Cancelled);
        a.QueuedFor(S(3)).Should().Be(1);
        a.Cancel(LinkAddress.FromAscii("NOBODY")).Should().Be(0);
    }

    [Fact]
    public void A_Failed_Report_Hints_At_The_Address_The_Burst_Was_Sent_To()
    {
        // Station 1 sends to S2 and to S2-ALIAS (both live at station 2, which is cut off after the first report), then station 2 reports a burst it could not decode: the hint names the address of that burst.
        var rig = new SimRig(2, gain: 0.1);
        Station a = rig.Stations[0];
        SimNode na = rig.Nodes[0];
        SimNode nb = rig.Nodes[1];
        LinkAddress alias = LinkAddress.FromAscii("S2-ALIAS");
        a.Locate(alias, 2);
        rig.RunTo(6);
        for (int i = 0; i < 6; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(6);
        }

        // 12 dB weaker: the burst to the alias is heard and not decoded, and station 2 says so.
        rig.Channel.Link(na, nb).Gain *= SimChannel.Db(-12);
        rig.Send(1, alias, 300, length: 100).Should().Be(SendResult.Accepted);
        rig.Run(40);

        na.NotDecoded.Should().NotBeEmpty();
        na.NotDecoded.Should().OnlyContain(h => h.Address == alias && h.Tags.SequenceEqual(new long[] { 300 }));
        na.PathReports.Where(p => p.Failed).Select(p => p.Address).Distinct().Should().BeEquivalentTo([S(2), alias], "told once for each address that lives at the station that reported");
    }

    // ---- the public surface ----

    [Fact]
    public void No_Public_Member_Of_The_Host_Interface_Mentions_A_Station_Number_But_The_Constructor()
    {
        Type[] hostTypes = [.. typeof(Station).Assembly.GetExportedTypes().Where(t => t == typeof(Station) || t == typeof(LinkAddress) || t == typeof(HostTag) || t.Name.EndsWith("Event", StringComparison.Ordinal)
            || t == typeof(SendResult) || t == typeof(SendPriority) || t == typeof(DropReason) || t == typeof(LinkKnowledge) || t == typeof(Addressing) || t == typeof(IdentityResult) || t == typeof(ModemSentKind))];
        hostTypes.Should().Contain(typeof(ReceivedEvent)).And.Contain(typeof(IdentifiedEvent)).And.Contain(typeof(LinkStateEvent));

        var offending = new List<string>();
        foreach (Type t in hostTypes)
        {
            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m is ConstructorInfo && t == typeof(Station))
                {
                    continue;
                }

                if (m.Name.Contains("Number", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Sender", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Placeholder", StringComparison.OrdinalIgnoreCase))
                {
                    offending.Add($"{t.Name}.{m.Name}");
                }

                IEnumerable<ParameterInfo> parameters = m is MethodBase mb ? mb.GetParameters() : [];
                foreach (ParameterInfo p in parameters)
                {
                    if (p.Name!.Contains("number", StringComparison.OrdinalIgnoreCase) && !(m is ConstructorInfo))
                    {
                        offending.Add($"{t.Name}.{m.Name}({p.Name})");
                    }
                }
            }
        }

        offending.Should().BeEmpty("a station number appears nowhere in what a host sees");
        typeof(Station).GetConstructors().Single().GetParameters().Select(p => p.Name).Should().Contain("stationNumber", "the one exception, until station numbers are made by the stations");
        typeof(Station).GetMember("Bind").Should().BeEmpty();
        typeof(SendResult).GetEnumNames().Should().NotContain("NotBound");
    }
}
