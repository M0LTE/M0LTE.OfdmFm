using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Identify first (library 0.3.0): the announced tail, the host telling the station when it last identified, and two cold stations on the simulated channel. The rule's order (identification, then the transmission that made it due;
/// a lone report first, the identification after it) is in <see cref="StationIdentificationClockTests"/>.
/// </summary>
public class StationIdentifyFirstTests
{
    private const long Second = 48000;

    // ---- the item ----

    /// <summary>The identification payload of station 1 (id C0DE0001, callsign M0LTE) as the station builds it, with the tail the host announced.</summary>
    private static string IdentPayload(double? tail)
    {
        var d = new IdentDriver(0);
        if (tail is { } t)
        {
            d.Station.SetIdentificationTail(t);
        }

        byte[]? payload = null;
        d.Station.IdentificationPayloadBuilt = p => payload = p;
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(20 * Second);
        payload.Should().NotBeNull();
        return Convert.ToHexString(payload!);
    }

    [Fact]
    public void With_No_Tail_The_Payload_Is_Byte_For_Byte_What_0_2_0_Sent()
    {
        // Captured from main (0.2.0) before the change: callsign M0LTE, capability word 0, the station id item with C0DE0001.
        const string Vector = "054D304C544500000104C0DE0001";
        IdentPayload(null).Should().Be(Vector);
        IdentPayload(0).Should().Be(Vector, "a tail of zero is no tail");
    }

    [Theory]
    [InlineData(5.3, "35")]
    [InlineData(0.3, "03")]
    [InlineData(0.01, "01")]
    [InlineData(10.0, "64")]
    [InlineData(25.5, "FF")]
    public void A_Tail_Is_Carried_In_Tenths_Of_A_Second_Rounded_Up_In_One_Item_With_Tag_3_After_The_Station_Id(double seconds, string tenths)
    {
        IdentPayload(seconds).Should().Be("054D304C544500000104C0DE0001" + "0301" + tenths);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(25.6)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_Tail_That_Is_Negative_Or_Over_The_Limit_Is_Rejected(double seconds)
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        station.Invoking(s => s.SetIdentificationTail(seconds)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Receiver_That_Reads_Only_The_Station_Id_Skips_The_Tail_Item()
    {
        // What a 0.2.0 receiver does: it looks for the tags it knows (1 and 2) and skips the rest, so the id is read and the callsign heard.
        byte[] id = [0xC0, 0xDE, 0x00, 0x01];
        byte[] tail = [53];
        var packed = new byte[100];
        int n = PayloadCodec.PackIdentification("M0LTE"u8, 0, [new ExtensionItem(PayloadCodec.StationIdTag, id), new ExtensionItem(PayloadCodec.TailTag, tail)], packed);
        PayloadCodec.TryUnpackIdentification(packed.AsSpan(0, n), out ReadOnlySpan<byte> callsign, out int capability, out ExtensionReader items).Should().BeTrue();
        callsign.ToArray().Should().Equal("M0LTE"u8.ToArray());
        capability.Should().Be(0);
        var seen = new List<byte>();
        while (items.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.StationIdTag && value.Length == PayloadCodec.KeyBytes)
            {
                value.ToArray().Should().Equal(id);
            }

            seen.Add(tag);
        }

        seen.Should().Equal(PayloadCodec.StationIdTag, PayloadCodec.TailTag);
    }

    // ---- hearing the tail ----

    /// <summary>A listener (a station with no traffic) hears this identification burst and then silence for 16 s. What it was told of holds (<c>BurstKnown</c>: the sample, and the sample the hold lasts to) and each change of its channel.</summary>
    private static (List<(long Now, long Until)> Holds, List<(long At, bool Busy)> Edges) Hear(TestBurst burst, Action<float[]>? damage = null)
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var edges = new List<(long, bool)>();
        var holds = new List<(long, long)>();
        station.ChannelBusy += e => edges.Add((station.SampleCount, e.Busy));
        station.BurstKnown = (now, until) => holds.Add((now, until));
        if (damage is not null)
        {
            damage(burst.Audio); // a burst made for this call: damaged in place
        }

        var b = new StreamBuilder().Silence(1.2);
        b.Add(burst);
        b.Silence(16);
        float[] stream = b.ToArray();
        for (int at = 0; at < stream.Length; at += 4800)
        {
            station.Push(stream.AsSpan(at, Math.Min(4800, stream.Length - at)));
        }

        return (holds, edges);
    }

    private static TestBurst Ident(params ExtensionItem[] items) => TestBurst.Identification(5, "M0XYZ", 0, items: [new ExtensionItem(PayloadCodec.StationIdTag, new byte[] { 1, 2, 3, 4 }), .. items]);

    [Fact]
    public void A_Station_That_Decodes_An_Identification_With_A_Tail_Holds_Off_For_The_Tail_From_The_End_Of_The_Burst_And_Not_Longer()
    {
        (List<(long Now, long Until)> control, List<(long At, bool Busy)> controlEdges) = Hear(Ident());
        controlEdges.Last().Busy.Should().BeFalse();
        Stretches(controlEdges).Should().Be(1);

        (List<(long Now, long Until)> holds, List<(long At, bool Busy)> edges) = Hear(Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })));
        long tailEnd = holds.Last().Until;
        holds.Select(h => h.Until).Should().Contain(tailEnd - (6 * Second), "the hold is six seconds from a sample the burst's own hold ended at: the end of the burst");
        holds.Max(h => h.Until).Should().Be(tailEnd);
        edges.Last().Should().Be((tailEnd, false), "busy to the last sample of the tail and clear at the next, by the sample clock");
        Stretches(edges).Should().Be(1, "one stretch of busy: the burst and its tail");
    }

    /// <summary>
    /// Stretches of busy that last at least a sample. A candidate under another opening, read and found to be nothing within the block that found it, shows as busy and clear at the same sample (it holds nothing once judged).
    /// </summary>
    private static int Stretches(List<(long At, bool Busy)> edges)
    {
        int count = 0;
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].Busy && (i + 1 == edges.Count || edges[i + 1].At > edges[i].At))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void A_Station_That_Did_Not_Decode_The_Burst_Does_Not_Hold_For_A_Tail_It_Did_Not_Read()
    {
        (List<(long Now, long Until)> control, List<(long At, bool Busy)> controlEdges) = Hear(Ident());
        (List<(long Now, long Until)> holds, List<(long At, bool Busy)> edges) = Hear(
            Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })),
            audio => Array.Clear(audio, (int)(audio.Length * 0.55), (int)(audio.Length * 0.3))); // the middle of the payload is lost
        holds.Should().NotBeEmpty("the header was read: the burst itself holds the channel");
        holds.Max(h => h.Until).Should().BeLessThan(holds.Min(h => h.Until) + (3 * Second), "nothing of the tail was read, so nothing holds for it");
        edges.Last().At.Should().BeLessThan(holds.Min(h => h.Until) + (3 * Second));
        edges.Last().Busy.Should().BeFalse();
        controlEdges.Last().Busy.Should().BeFalse();
    }

    [Fact]
    public void A_Tail_Item_Of_Another_Length_Or_Of_Zero_Is_Skipped()
    {
        (List<(long Now, long Until)> control, List<(long At, bool Busy)> controlEdges) = Hear(Ident());
        foreach (byte[] value in new[] { new byte[] { 0 }, [60, 60], [] })
        {
            (List<(long Now, long Until)> holds, List<(long At, bool Busy)> edges) = Hear(Ident(new ExtensionItem(PayloadCodec.TailTag, value)));
            edges.Last().At.Should().BeLessThan(holds.Min(h => h.Until) + (3 * Second), $"a tail item of {value.Length} bytes holds nothing");
        }
    }

    [Fact]
    public void A_Tail_Is_Honoured_From_One_Station_Number_At_Most_Once_In_A_Minute()
    {
        // Each burst announces 6 s. Three from number 5: the first holds for its tail, the second (30 s later) is treated as carrying none, the third (more than a minute after the first's end) holds again.
        TestBurst[] bursts = [Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })), Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })), Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 }))];
        bursts[1] = TestBurst.Identification(5, "M0XYZ", 0, burstNumber: 1, items: [new ExtensionItem(PayloadCodec.StationIdTag, new byte[] { 1, 2, 3, 4 }), new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })]);
        bursts[2] = TestBurst.Identification(5, "M0XYZ", 0, burstNumber: 2, items: [new ExtensionItem(PayloadCodec.StationIdTag, new byte[] { 1, 2, 3, 4 }), new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })]);
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var holds = new List<long>();
        station.BurstKnown = (now, until) => holds.Add(until);
        var b = new StreamBuilder().Silence(1.2);
        b.Add(bursts[0]);
        b.Silence(30);
        b.Add(bursts[1]);
        b.Silence(70);
        b.Add(bursts[2]);
        b.Silence(12);
        float[] stream = b.ToArray();
        for (int at = 0; at < stream.Length; at += 4800)
        {
            station.Push(stream.AsSpan(at, Math.Min(4800, stream.Length - at)));
        }

        // A tail hold is the one that lasts exactly 6 s beyond another hold of the same burst.
        int tails = holds.Count(h => holds.Contains(h - (6 * Second)));
        tails.Should().Be(2, "the first and the third are honoured; the second, 30 s after the first, is not");
    }

    [Fact]
    public void A_Tail_On_A_Notice_About_A_Station_Number_Is_Not_Honoured()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var holds = new List<long>();
        station.BurstKnown = (now, until) => holds.Add(until);
        var clash = new byte[PayloadCodec.ClashBytes];
        PayloadCodec.WriteClash(9, 0x11111111u, clash);
        TestBurst burst = TestBurst.Identification(5, "M0XYZ", 0, items: [new ExtensionItem(PayloadCodec.StationIdTag, new byte[] { 1, 2, 3, 4 }), new ExtensionItem(PayloadCodec.ClashTag, clash), new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })]);
        var b = new StreamBuilder().Silence(1.2);
        b.Add(burst);
        b.Silence(12);
        float[] stream = b.ToArray();
        for (int at = 0; at < stream.Length; at += 4800)
        {
            station.Push(stream.AsSpan(at, Math.Min(4800, stream.Length - at)));
        }

        holds.Count(h => holds.Contains(h - (6 * Second))).Should().Be(0, "a Clash is the same kind of burst, and carries no tail that anyone has to wait for");
    }

    [Fact]
    public void The_Hold_Is_Said_In_The_Trace_As_The_Cause_Of_The_Busy_Channel()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var tap = new TraceTap(station, TraceLevel.Detail);
        var b = new StreamBuilder().Silence(1.2);
        b.Add(Ident(new ExtensionItem(PayloadCodec.TailTag, new byte[] { 60 })));
        b.Silence(16);
        float[] stream = b.ToArray();
        for (int at = 0; at < stream.Length; at += 4800)
        {
            station.Push(stream.AsSpan(at, Math.Min(4800, stream.Length - at)));
        }

        tap.Texts.Should().Contain(t => t.StartsWith("Holding all traffic until ", StringComparison.Ordinal) && t.EndsWith(": station 5 is sending its callsign after its identification", StringComparison.Ordinal));
    }

    // ---- the identifying station: late TransmitEnded ----

    [Fact]
    public void The_Stations_Own_Data_Waits_For_The_End_Of_A_Late_Ended_Identification_And_Then_Goes_Straight_On()
    {
        var d = new IdentDriver(0) { TailSamples = 8 * Second };
        d.Station.SetIdentificationTail(8);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(40 * Second);

        d.Air.Should().HaveCount(2);
        d.Air[0].End.Should().Be(d.Air[0].Start + d.Air[0].Length + (8 * Second), "the host kept the transmitter keyed for its Morse");
        d.Air[1].Start.Should().Be(d.Air[0].End + 4800 + 2400, "nothing of its own is granted while the transmission is on, and the datagram goes straight on when it has ended");
        d.Log.Where(l => l.What == "request").Select(l => l.Clock).Last().Should().BeGreaterThanOrEqualTo(d.Air[0].End, "the datagram's request is not even made before");
    }

    // ---- telling the station when it last identified ----

    private static IdentDriver Scaled(long interval) => new(interval);

    [Fact]
    public void Told_It_Identified_Part_Of_An_Interval_Ago_Nothing_Is_Due_Until_The_Interval_Has_Run_From_That_Moment()
    {
        // The real figures (3 minutes ago, 7 more minutes) are in the gated test below; this is the same on a 20 s interval, 6 s ago.
        var d = Scaled(20 * Second);
        d.Station.SetLastIdentification(6);
        d.Station.LastIdentificationStart.Should().Be(-6 * Second);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(12 * Second);
        d.Air.Should().ContainSingle("covered: the last identification was 6 s ago, 14 s of the 20 are left");
        d.Clocks("due").Should().BeEmpty();

        d.RunTo(15 * Second);
        d.SendToEveryone(2);
        d.RunTo(30 * Second);
        d.Air.Should().HaveCount(3, "past the 14 s: the identification goes first");
        d.Air[1].Length.Should().Be(IdentLength());
        d.Clocks("due").Should().Equal(d.Requests.Single(r => r.Length == IdentLength()).Clock);
        d.Station.LastIdentificationStart.Should().Be(d.Air[1].Start);
    }

    [Fact]
    public void Told_It_Identified_Longer_Ago_Than_The_Interval_It_Is_Due_At_The_First_Transmission()
    {
        var d = Scaled(20 * Second);
        d.Station.SetLastIdentification(25);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(20 * Second);
        d.Air.Should().HaveCount(2);
        d.Air[0].Length.Should().Be(IdentLength());
        d.Clocks("due").Should().Equal(d.Requests[0].Clock);
    }

    [Fact]
    public void Told_It_Identified_Exactly_One_Interval_Ago_It_Is_Due()
    {
        var d = Scaled(20 * Second);
        d.Station.SetLastIdentification(20);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(20 * Second);
        d.Air.Should().HaveCount(2);
        d.Air[0].Length.Should().Be(IdentLength());
    }

    [Fact]
    public void A_Station_Told_It_Identified_Recently_That_Never_Transmits_Never_Keys()
    {
        var d = Scaled(20 * Second);
        d.Station.SetLastIdentification(1);
        d.RunTo(300 * Second);
        d.Log.Should().BeEmpty();
        d.Station.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void Telling_The_Station_After_It_Has_Transmitted_Is_Refused()
    {
        var d = Scaled(20 * Second);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(8 * Second);
        d.Air.Should().NotBeEmpty();
        d.Station.Invoking(s => s.SetLastIdentification(30)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Telling_The_Station_Can_Be_Repeated_Before_It_Transmits_And_The_Last_Call_Counts()
    {
        var d = Scaled(20 * Second);
        d.Station.SetLastIdentification(30);
        d.Station.SetLastIdentification(2);
        d.Station.LastIdentificationStart.Should().Be(-2 * Second);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e12)]
    [InlineData(31_536_001.0)]
    public void An_Age_That_Is_Negative_Not_A_Number_Or_Absurd_Is_Rejected(double secondsAgo)
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        station.Invoking(s => s.SetLastIdentification(secondsAgo)).Should().Throw<ArgumentOutOfRangeException>();
        station.LastIdentificationStart.Should().BeNull();
    }

    [Fact]
    public void The_Host_Can_Write_Down_When_An_Identification_Went_Out_From_Modem_Sent_At_The_Moment_The_Burst_Goes_On_Air()
    {
        var d = Scaled(20 * Second);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(20 * Second);
        long started = d.Clocks("modem Identification").Single();
        started.Should().Be(d.Air[0].Start);
        d.Station.LastIdentificationStart.Should().Be(started, "the clock of ModemSent with kind Identification is the start of the last identification");
    }

    [Fact]
    public void Real_Figures_Told_3_Minutes_Ago_Nothing_Is_Due_For_7_Minutes_Then_It_Is_And_11_Minutes_Ago_It_Is_Due_At_The_First_Transmission()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: seven minutes of audio goes through the detector");
        var d = Scaled(0);
        d.Station.SetLastIdentification(180);
        d.RunTo(6 * Second);
        d.SendToEveryone(1);
        d.RunTo(400 * Second);
        d.Air.Should().ContainSingle("3 minutes ago: nothing due for 7 more minutes");
        d.SendToEveryone(2);
        d.RunTo(415 * Second);
        d.Air.Should().HaveCount(2);
        d.RunTo(430 * Second);
        d.Clocks("due").Should().BeEmpty("at 400 s the last identification was 580 s ago: still inside the ten minutes");
        d.RunTo(430 * Second);
        d.SendToEveryone(3);
        d.RunTo(450 * Second);
        d.Air.Should().HaveCount(4, "at 430 s it was 610 s ago: due, so the identification goes first");
        d.Air[2].Length.Should().Be(IdentLength());

        var late = Scaled(0);
        late.Station.SetLastIdentification(660);
        late.RunTo(6 * Second);
        late.SendToEveryone(1);
        late.RunTo(20 * Second);
        late.Air.Should().HaveCount(2);
        late.Air[0].Length.Should().Be(IdentLength());
    }

    private static int IdentLength()
    {
        var packed = new byte[100];
        int n = PayloadCodec.PackIdentification("M0LTE"u8, 0, [new ExtensionItem(PayloadCodec.StationIdTag, new byte[PayloadCodec.KeyBytes])], packed);
        return new BurstRenderer().SamplesFor(StandardFormats.FirstContact, PayloadCodec.SymbolsFor(StandardFormats.FirstContact, n)!.Value);
    }

    // ---- two cold stations on the simulated channel ----

    /// <summary>The seeds the existing eight-seed comparisons use.</summary>
    public static IEnumerable<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8];

    private const double TailSeconds = 8;

    /// <summary>
    /// A ("M0AAA") and B ("M0BBB"), both cold, connected. A sends B a datagram that asks for a report. When B's identification has been on the air a second, A's host has another datagram for B. The host's Morse of each station is
    /// 8 s of keyed tone after its identification burst, inside the same transmission. Returns what each did.
    /// </summary>
    internal static (SimChannel Channel, SimNode A, SimNode B) ColdPair(int seed, bool announceTail)
    {
        var channel = new SimChannel(4800, seed);
        var a = new Station(1, new Random((seed * 10) + 1), runJobsInline: true).WithIdentity("M0AAA");
        var b = new Station(2, new Random((seed * 10) + 2), runJobsInline: true).WithIdentity("M0BBB");
        if (announceTail)
        {
            a.SetIdentificationTail(TailSeconds);
            b.SetIdentificationTail(TailSeconds);
        }

        SimNode na = channel.Add(a, "A");
        SimNode nb = channel.Add(b, "B");
        na.IdentificationTailSamples = (int)(TailSeconds * Second);
        nb.IdentificationTailSamples = (int)(TailSeconds * Second);
        channel.Connect(na, nb, 1.0, 0.004);
        LinkAddress hostB = LinkAddress.FromAscii("HOSTB");
        a.Locate(hostB, 2);

        channel.Run(6 * Second);
        a.Send(TestBurst.Datagram(1, 20), hostB, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1), reportWanted: true).Should().Be(SendResult.Accepted);
        for (int i = 0; i < 2000 && nb.Transmissions.Count < 2; i++)
        {
            channel.Run(channel.Clock + 4800);
        }

        channel.Run(channel.Clock + Second);
        a.Send(TestBurst.Datagram(2, 20), hostB, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        channel.Run(channel.Clock + (60 * Second));
        return (channel, na, nb);
    }

    private static List<(string Who, SimTransmission T, ModemSentKind? Kind)> OnAir(SimNode a, SimNode b)
    {
        var all = new List<(string, SimTransmission, ModemSentKind?)>();
        foreach (SimNode n in new[] { a, b })
        {
            // Every transmission of these stations is a burst the station was asked to make; the kinds of the ones it said were its own are in ModemSent, in order, but data bursts have none.
            foreach (SimTransmission t in n.Transmissions)
            {
                all.Add((n.Name, t, null));
            }
        }

        return [.. all.OrderBy(x => x.Item2.Start)];
    }

    [Fact]
    public void Two_Cold_Stations_Go_Identification_Data_Report_Identification_And_The_Next_Burst_Waits_For_The_Tail_On_Every_Seed()
    {
        var table = new List<string>();
        foreach (int seed in Seeds)
        {
            (SimChannel channel, SimNode a, SimNode b) = ColdPair(seed, announceTail: true);
            List<(string Who, SimTransmission T, ModemSentKind? Kind)> air = OnAir(a, b);
            string who = string.Join(" ", air.Select(x => x.Who));
            table.Add($"seed {seed}: {who}");

            // A: identification, datagram, datagram. B: report, identification. In this order on the air for the first four, and A's next after B's identification has ended with its Morse.
            who.Should().Be("A A B B A", $"seed {seed}: A's identification, A's datagram, B's report, B's identification, A's next");
            a.ModemSent.Select(m => m.Kind).Should().Equal(ModemSentKind.Identification);
            b.ModemSent.Select(m => m.Kind).Should().Equal(ModemSentKind.Report, ModemSentKind.Identification);
            for (int i = 1; i < air.Count; i++)
            {
                SimTransmission before = air[i - 1].T;
                air[i].T.Start.Should().BeGreaterThanOrEqualTo(before.Start + before.Length, $"seed {seed}: no two transmissions overlap, including the Morse that follows an identification (transmission {i})");
            }

            air[3].T.Length.Should().BeGreaterThan(8 * (int)Second, "B's identification burst and its Morse");

            // Nothing was lost: B got both datagrams once, each station heard the other's callsign, and A's host was told each of its datagrams went.
            b.Received.Select(r => r.Data).Should().BeEquivalentTo([TestBurst.Datagram(1, 20), TestBurst.Datagram(2, 20)], o => o.WithStrictOrdering(), $"seed {seed}");
            b.Identified.Select(i => i.Event.Callsign).Should().Equal("M0AAA");
            a.Identified.Select(i => i.Event.Callsign).Should().Equal("M0BBB");
            a.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(1L, 2L);
            a.NotDecoded.Should().BeEmpty($"seed {seed}");
            a.PathReports.Should().NotBeEmpty($"seed {seed}: A heard B's report, which arrived into a quiet channel and not while A was keyed");
        }

        Hostile.Say("two cold stations, order on the air by seed: " + string.Join("; ", table));
    }

    /// <summary>What a cold-pair run came to, for the record: the order on the air, how many transmissions overlapped the one before them, how many of A's two datagrams B received (counted once each) and how many path reports A took in.</summary>
    internal static string Summary(int seed, bool announceTail)
    {
        (SimChannel _, SimNode a, SimNode b) = ColdPair(seed, announceTail);
        List<(string Who, SimTransmission T, ModemSentKind? Kind)> air = OnAir(a, b);
        int overlaps = 0;
        for (int i = 1; i < air.Count; i++)
        {
            if (air[i].T.Start < air[i - 1].T.Start + air[i - 1].T.Length)
            {
                overlaps++;
            }
        }

        int delivered = b.Received.Select(r => r.Data).Distinct(new BytesEqual()).Count();
        return $"seed {seed}: {string.Join(" ", air.Select(x => x.Who))}, overlapping {overlaps}, datagrams at B {delivered} of 2, path reports at A {a.PathReports.Count}";
    }

    private sealed class BytesEqual : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }

    [Fact]
    public void The_Same_Run_With_The_Tail_Not_Announced_Is_Printed_For_The_Record()
    {
        // Control, for the record and not for a claim: the same scenario with the tail not announced (the host's Morse is still on the air).
        Hostile.Say("two cold stations, tail not announced: " + string.Join("; ", Seeds.Select(seed => Summary(seed, false))));
    }

    [Fact]
    public void The_Same_Run_With_The_Tail_Announced_Is_Printed_For_The_Record()
    {
        Hostile.Say("two cold stations, tail announced: " + string.Join("; ", Seeds.Select(seed => Summary(seed, true))));
    }

    [Fact]
    public void A_Lone_Report_With_An_Identification_Due_Goes_Before_It_And_The_Identification_Follows_In_The_First_Slot()
    {
        foreach (int seed in Seeds)
        {
            (SimChannel _, SimNode a, SimNode b) = ColdPair(seed, announceTail: true);
            b.ModemSent.Select(m => m.Kind).Should().Equal([ModemSentKind.Report, ModemSentKind.Identification], $"seed {seed}");
            SimTransmission report = b.Transmissions[0];
            SimTransmission ident = b.Transmissions[1];
            ident.Start.Should().Be(report.Start + report.Length + 4800 + 2400, $"seed {seed}: the first slot after the report, the own tail and 50 ms");
            b.IdentificationsDue.Should().ContainSingle().Which.Clock.Should().Be(report.Start + report.Length, "due when the report ends");
        }
    }
}
