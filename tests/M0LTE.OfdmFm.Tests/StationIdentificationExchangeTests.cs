using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Stations identify by themselves and learn each other's callsigns (step 7), through the simulated channel and from recorded audio.</summary>
public class StationIdentificationExchangeTests
{
    private const long Interval = 15L * 48000;
    private static readonly LinkAddress HostB = LinkAddress.FromAscii("HOSTB");

    private static LinkAddress Name(string text) => LinkAddress.FromAscii(text);

    /// <summary>A ("M0AAA", number 1) and B ("M0BBB", number 2) on one channel, connected, with the identification interval shortened. A knows B by the host's name "HOSTB"; B knows nobody.</summary>
    private static (SimChannel Channel, SimNode A, SimNode B) Pair(int seed)
    {
        var channel = new SimChannel(4800, seed);
        var a = (new Station(1, new Random(seed * 10 + 1), runJobsInline: true) { IdentificationInterval = Interval }).WithIdentity("M0AAA");
        var b = (new Station(2, new Random(seed * 10 + 2), runJobsInline: true) { IdentificationInterval = Interval }).WithIdentity("M0BBB");
        SimNode na = channel.Add(a, "A");
        SimNode nb = channel.Add(b, "B");
        channel.Connect(na, nb, 1.0, 0.004);
        a.Bind(HostB, 2);
        return (channel, na, nb);
    }

    /// <summary>A sends B one datagram that asks for a report, so that B sends a lone report and both stations have transmitted; then both run past their interval.</summary>
    private static void Scenario(SimChannel channel, SimNode a)
    {
        channel.Run(6 * 48000);
        a.Station.Send(TestBurst.Datagram(1, 20), HostB, new HostTag(1), reportWanted: true).Should().Be(SendResult.Accepted);
        channel.Run(34 * 48000);
    }

    [Fact]
    public void Two_Stations_That_Have_Transmitted_Identify_Themselves_And_Each_Learns_The_Others_Callsign()
    {
        (SimChannel channel, SimNode a, SimNode b) = Pair(3);
        var callsB = new List<Station.AdaptationCall>();
        b.Station.AdaptationCalled = callsB.Add;
        Scenario(channel, a);

        // Each sent exactly what it should: A the datagram and its identification, B a lone report and its identification.
        a.Transmissions.Should().HaveCount(2);
        b.Transmissions.Should().HaveCount(2);
        a.ModemSent.Select(m => m.Kind).Should().Equal(ModemSentKind.Identification);
        b.ModemSent.Select(m => m.Kind).Should().Equal(ModemSentKind.Report, ModemSentKind.Identification);
        a.ModemSent[0].Station.Should().BeNull("an identification is for everyone");
        a.ModemSent[0].Format.Should().Be(StandardFormats.FirstContact);
        a.IdentificationsDue.Should().ContainSingle();
        b.IdentificationsDue.Should().ContainSingle();

        // B has no host name for A, so the callsign becomes the name; the capability word is the station's own (revision 0, no flags).
        IdentifiedEvent atB = b.Identified.Should().ContainSingle().Subject.Event;
        atB.Should().Be(new IdentifiedEvent(1, "M0AAA", 0, Name("M0AAA")));
        b.Station.Snapshot().Select(s => s.Station).Should().Contain(Name("M0AAA"));
        b.LinkStates.Select(l => l.Station).Should().Contain(Name("M0AAA"), "a station heard before it had a name is announced again under it");
        b.Station.Send(TestBurst.Datagram(2, 10), Name("M0AAA"), new HostTag(2)).Should().Be(SendResult.Accepted, "the learned name can be sent to");

        // A has a host name for B, which stands. The event still says what B sent.
        IdentifiedEvent atA = a.Identified.Should().ContainSingle().Subject.Event;
        atA.Should().Be(new IdentifiedEvent(2, "M0BBB", 0, HostB));
        a.Station.Snapshot().Select(s => s.Station).Should().Equal(HostB);
        a.LinkStates.Select(l => l.Station).Should().NotContain(Name("M0BBB"));

        // An identification is not a datagram, and nothing is owed for it: B heard the datagram and A's identification, measured both, and owes A nothing for the second.
        b.Received.Should().ContainSingle().Which.Everyone.Should().BeFalse();
        a.Received.Should().BeEmpty();
        Station.AdaptationCall heard = callsB.Should().ContainSingle(c => c.Dest == 0).Subject;
        (heard.Sender, heard.Decoded, heard.Capability, heard.Returned, heard.IsReport).Should().Be((1, true, 0, null, false));
        heard.Quality.Should().NotBeEmpty("it is measured like any other burst from that station");
        b.Station.AskedReportsOwed.Should().Be(0);
        b.Station.OwedReports.Should().BeEmpty();
    }

    [Fact]
    public void An_Identification_Is_Sent_In_Both_Directions_Once_And_Each_Station_Sees_It_Only_After_It_Has_Gone_Out()
    {
        (SimChannel channel, SimNode a, SimNode b) = Pair(5);
        Scenario(channel, a);
        long aSent = channel.Log.Where(l => l.Contains(" A modem sent Identification", StringComparison.Ordinal)).Select(l => long.Parse(l.Split(' ')[0])).Single();
        long aHeardAtB = b.Identified.Single().Clock;
        aHeardAtB.Should().BeGreaterThan(aSent, "heard when it has been on the air and received");
        a.IdentificationsDue.Single().Clock.Should().BeLessThan(aSent);
        a.IdentificationsDue.Single().Clock.Should().Be(a.Transmissions[0].Start + a.Transmissions[0].Length, "due at once, when its first transmission ends");
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_Both_Stations_Identifications_Included()
    {
        string Run(int seed)
        {
            (SimChannel channel, SimNode a, _) = Pair(seed);
            Scenario(channel, a);
            return string.Join("\n", channel.Log);
        }

        string once = Run(7);
        once.Should().Contain("identification due").And.Contain("identified 1 M0AAA").And.Contain("identified 2 M0BBB");
        Run(7).Should().Be(once);
    }

    // ---- recorded audio: what a listener does with one identification burst ----

    private static (ReceiveRig Rig, List<IdentifiedEvent> Identified, List<LinkStateEvent> Links) Listen(Action<Station>? setup, params TestBurst[] bursts)
    {
        var rig = new ReceiveRig(0);
        var identified = new List<IdentifiedEvent>();
        var links = new List<LinkStateEvent>();
        rig.Station.Identified += identified.Add;
        rig.Station.LinkState += links.Add;
        setup?.Invoke(rig.Station);
        var b = new StreamBuilder().Silence(1.2);
        foreach (TestBurst burst in bursts)
        {
            b.Add(burst);
            b.Silence(1.0);
        }

        b.Silence(2.5).Noise(0.01, 12);
        rig.Push(b.ToArray(), () => 4800);
        rig.Finish();
        return (rig, identified, links);
    }

    [Fact]
    public void An_Identification_Binds_The_Number_To_The_Callsign_Shows_The_Capability_Word_Is_Measured_And_Owes_Nothing()
    {
        (ReceiveRig rig, List<IdentifiedEvent> identified, List<LinkStateEvent> links) = Listen(null, TestBurst.Identification(5, "M0XYZ-1", 0x1234, burstNumber: 9));
        using (rig)
        {
            identified.Should().Equal(new IdentifiedEvent(5, "M0XYZ-1", 0x1234, Name("M0XYZ-1")));
            rig.Station.Adaptation.TryGetNeighbour(5, out LinkNeighbour? nb).Should().BeTrue();
            nb!.Capability.Should().Be(0x1234);
            nb.Measures.Should().Be(1, "measured like any burst from that station");
            nb.Margin.Should().BeLessThan(LinkAdaptation.MarginStart, "it decoded");
            rig.Calls.Should().ContainSingle().Which.Should().Match<Station.AdaptationCall>(c => c.Sender == 5 && c.Dest == 0 && c.Decoded && c.Capability == 0x1234 && c.Returned == null && !c.IsReport);
            rig.Datagrams.Should().BeEmpty("it is not a datagram");
            rig.Station.OwedReports.Should().BeEmpty();
            rig.Station.AskedReportsOwed.Should().Be(0);
            links.Should().ContainSingle().Which.Station.Should().Be(Name("M0XYZ-1"), "it is announced once, under the callsign");
            rig.Station.Snapshot().Should().ContainSingle().Which.Station.Should().Be(Name("M0XYZ-1"));
        }
    }

    [Fact]
    public void A_Name_The_Host_Bound_Wins_Over_A_Callsign_And_Nothing_Misleading_Is_Raised()
    {
        (ReceiveRig rig, List<IdentifiedEvent> identified, List<LinkStateEvent> links) = Listen(s => s.Bind(Name("HOST"), 5), TestBurst.Identification(5, "M0XYZ", 0x0001));
        using (rig)
        {
            identified.Should().Equal(new IdentifiedEvent(5, "M0XYZ", 1, Name("HOST")));
            links.Select(l => l.Station).Should().OnlyContain(n => n == Name("HOST"), "the burst was heard under the host's name, and no other name appears");
            rig.Station.Snapshot().Single().Station.Should().Be(Name("HOST"));
            rig.Station.Adaptation.TryGetNeighbour(5, out LinkNeighbour? nb).Should().BeTrue();
            nb!.Capability.Should().Be(1, "the capability word is recorded whatever the name");
        }
    }

    [Fact]
    public void A_Callsign_The_Host_Bound_To_Another_Number_Is_Not_Taken_From_It_And_A_Learned_One_Moves_To_The_Latest_Number()
    {
        (ReceiveRig rig, List<IdentifiedEvent> identified, _) = Listen(s => s.Bind(Name("M0XYZ"), 3), TestBurst.Identification(6, "M0XYZ", 0));
        using (rig)
        {
            identified.Should().ContainSingle().Which.Name.Should().Be(Name("#6"), "the host's M0XYZ is number 3; number 6 stays a placeholder");
            rig.Station.Snapshot().Select(s => s.Station).Should().Equal(Name("M0XYZ"), Name("#6"));
        }

        (ReceiveRig rig2, List<IdentifiedEvent> two, _) = Listen(null, TestBurst.Identification(5, "M0XYZ", 0), TestBurst.Identification(6, "M0XYZ", 0, burstNumber: 1), TestBurst.Identification(6, "M0ABC", 0, burstNumber: 2));
        using (rig2)
        {
            two.Select(e => (e.StationNumber, e.Name)).Should().Equal((5, Name("M0XYZ")), (6, Name("M0XYZ")), (6, Name("M0ABC")));
            rig2.Station.Snapshot().Select(s => s.Station).Should().Equal(Name("#5"), Name("M0ABC")); // the callsign moved from 5 to 6, and then 6 took another
        }
    }

    [Fact]
    public void A_Callsign_That_Is_The_Stations_Own_Is_Reported_But_Never_Bound()
    {
        (ReceiveRig rig, List<IdentifiedEvent> identified, _) = Listen(null, TestBurst.Identification(7, "M0LTE", 0));
        using (rig)
        {
            identified.Should().ContainSingle().Which.Name.Should().Be(Name("#7"));
        }
    }

    [Fact]
    public void A_Payload_That_Is_Not_An_Identification_Is_Measured_But_Raises_Nothing_And_Learns_No_Capability()
    {
        // A burst of the identification type whose payload (checked and decoded fine) is not a callsign: a length of 5 and nothing valid behind it.
        TestBurst burst = TestBurst.IdentificationWithPayload(5, new byte[] { 5, 1, 2, 3, 4, 5, 0, 0 });
        (ReceiveRig rig, List<IdentifiedEvent> identified, _) = Listen(null, burst);
        using (rig)
        {
            identified.Should().BeEmpty();
            rig.Calls.Should().ContainSingle().Which.Should().Match<Station.AdaptationCall>(c => c.Decoded && c.Dest == 0 && c.Capability == null);
            rig.Station.Adaptation.TryGetNeighbour(5, out LinkNeighbour? nb).Should().BeTrue();
            nb!.Capability.Should().BeNull();
            rig.Datagrams.Should().BeEmpty();
        }
    }

    [Fact]
    public void An_Identification_Header_Not_Addressed_To_Everyone_Is_Noted_And_Not_Received()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        byte[] bits = new byte[BurstHeaderCodec.HeaderBits];
        station.TakeInHeader(2000, FormatOpening.UpTo1500Hz, new BurstHeader(0, BurstKind.Identification, 3, 5, StandardFormats.FirstContact, 8, 0), bits);
        station.NotedHeaders.Should().ContainSingle().Which.Reason.Should().Be(Station.NoteReason.Invalid);
        station.BurstsInFlight.Should().Be(0);

        station.TakeInHeader(9000, FormatOpening.UpTo1500Hz, new BurstHeader(0, BurstKind.Identification, 3, 0, StandardFormats.FirstContact, 8, 0), bits);
        station.NotedHeaders.Should().ContainSingle("an identification to everyone is a burst, as a data burst to everyone is");
        station.BurstsInFlight.Should().Be(1);
    }

}
