using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The transmitter identifier callsign is given by the host (<see cref="Station.SetIdentity"/>), and the station does not transmit until it has one: it is a listener, and does everything on the receive side.
/// Everything here runs on the sample clock.
/// </summary>
public class StationIdentityTests
{
    // ---- SetIdentity ----

    [Fact]
    public void A_New_Station_Has_No_Callsign_And_The_First_Valid_One_Is_Accepted()
    {
        var station = new Station(1, new Random(1));
        station.Callsign.Should().BeNull();
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.Callsign.Should().Be("M0LTE");
    }

    [Fact]
    public void The_Same_Callsign_Again_Is_Accepted_And_A_Different_One_Is_Refused_And_Changes_Nothing()
    {
        var station = new Station(1, new Random(1));
        station.SetIdentity("M0LTE-1").Should().Be(IdentityResult.Accepted);
        station.SetIdentity("M0LTE-1").Should().Be(IdentityResult.Accepted);
        station.SetIdentity("M0LTE-2").Should().Be(IdentityResult.Different);
        station.SetIdentity("G4XYZ").Should().Be(IdentityResult.Different);
        station.Callsign.Should().Be("M0LTE-1");
    }

    [Fact]
    public void A_Refused_Callsign_Leaves_The_Station_Without_One_And_A_Valid_One_Still_Goes_In()
    {
        var station = new Station(1, new Random(1));
        station.SetIdentity("m0lte").Should().Be(IdentityResult.Invalid);
        station.Callsign.Should().BeNull();
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.SetIdentity("not valid").Should().Be(IdentityResult.Invalid, "an invalid one is Invalid whether or not the station has one");
        station.Callsign.Should().Be("M0LTE");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("m0lte")]
    [InlineData("M0 LTE")]
    [InlineData("M0LTE_1")]
    [InlineData("M0LTE.1")]
    [InlineData("M0LTEé")]
    [InlineData("12345678901234567")]
    public void Each_Invalid_Form_Is_Refused(string callsign)
    {
        new Station(1, new Random(1)).SetIdentity(callsign).Should().Be(IdentityResult.Invalid);
    }

    [Fact]
    public void A_Null_Callsign_Is_Refused()
    {
        new Station(1, new Random(1)).SetIdentity(null!).Should().Be(IdentityResult.Invalid);
    }

    [Fact]
    public void Sixteen_Characters_Are_Accepted_And_Seventeen_Are_Refused()
    {
        new Station(1, new Random(1)).SetIdentity("VK2/M0LTE/QRP-15").Should().Be(IdentityResult.Accepted);
        new Station(1, new Random(1)).SetIdentity("VK2/M0LTE/QRP-155").Should().Be(IdentityResult.Invalid);
    }

    // ---- no identity: no transmit ----

    [Fact]
    public void Send_Before_An_Identity_Is_NoIdentity_Queues_Nothing_And_Raises_Neither_Dropped_Nor_Transmitted()
    {
        var station = new Station(1, new Random(1), runJobsInline: true);
        var peer = LinkAddress.FromAscii("G9XYZ");
        station.Bind(peer, 2);
        int requests = 0;
        int dropped = 0;
        int transmitted = 0;
        station.TransmitRequest += _ => requests++;
        station.Dropped += _ => dropped++;
        station.Transmitted += _ => transmitted++;

        station.Send(new byte[20], peer, new HostTag(1)).Should().Be(SendResult.NoIdentity);
        station.Send(new byte[20], LinkAddress.Everyone, new HostTag(2)).Should().Be(SendResult.NoIdentity);
        station.Send(new byte[20], LinkAddress.FromAscii("NOBODY"), new HostTag(3)).Should().Be(SendResult.NoIdentity, "checked before the name is looked up");
        station.Send(new byte[20], peer, new HostTag(4), reportWanted: true, priority: SendPriority.Expedited).Should().Be(SendResult.NoIdentity);
        station.QueuedFor(2).Should().Be(0);
        station.QueuedFor(0).Should().Be(0);

        float[] silence = new float[4800];
        for (int i = 0; i < 300; i++)
        {
            station.Push(silence); // 30 s: long enough for the first slot and a random delay to have passed
        }

        (requests, dropped, transmitted).Should().Be((0, 0, 0));
        station.BurstBuilt.Should().BeFalse();
    }

    [Fact]
    public void Send_Goes_Through_Once_The_Identity_Is_Set()
    {
        var station = new Station(1, new Random(1), runJobsInline: true);
        var peer = LinkAddress.FromAscii("G9XYZ");
        station.Bind(peer, 2);
        int requests = 0;
        station.TransmitRequest += _ => requests++;
        station.Send(new byte[20], peer, new HostTag(1)).Should().Be(SendResult.NoIdentity);
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.Send(new byte[20], peer, new HostTag(1)).Should().Be(SendResult.Accepted);
        float[] silence = new float[4800];
        for (int i = 0; i < 300 && requests == 0; i++)
        {
            station.Push(silence);
        }

        requests.Should().Be(1);
    }

    // ---- a listener in a rig ----

    private sealed record Outcome(
        int Received, int ReceivedAfterDrop, int ReceivedAfterFail, int Requests, int Transmissions, long Owed, int AUnanswered, int ASilences, int ReturnedByRules, int Lost, SimRig Rig);

    private static int _settledFormat = -1;

    /// <summary>The format a settled 0.1-gain path carries (what two identified stations climb to), so that the listener's sender can be started there without any report from it.</summary>
    private static int SettledFormat()
    {
        if (_settledFormat < 0)
        {
            var rig = new SimRig(2, gain: 0.1);
            rig.RunTo(6);
            for (int i = 0; i < 8; i++)
            {
                rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
                rig.Run(6);
            }

            _settledFormat = rig.Nodes[0].Transmitted[^1].Event.Format;
        }

        return _settledFormat;
    }

    /// <summary>
    /// Station 1 sends to station 2 on a path with some margin: three bursts that ask for a report, a drop of 6 dB after 100 s (the burst decodes and the recommendation falls below what it last told, which the test marks), and, with the sender told the settled format afresh, a further 10 dB (the burst is heard and not decoded). Station 1 starts as if it had been told the settled format, so that it sends fast without any report from station 2. Station 2 has an identity if <paramref name="bIdentified"/>.
    /// </summary>
    private static Outcome Scenario(bool bIdentified)
    {
        int settled = SettledFormat();
        var rig = new SimRig(2, gain: 0.1, identified: i => i == 1 || bIdentified);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        int returned = 0;
        rig.Stations[1].AdaptationCalled = c => returned += c.Returned is null ? 0 : 1;
        rig.RunTo(6);
        rig.Stations[0].Adaptation.HeardReport(2, settled, false, rig.Stations[0].Seconds);

        for (int i = 0; i < 3; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(8);
        }

        int received = b.Received.Count;
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? nb);
        int silences = nb!.Silences;
        int unanswered = nb.Unanswered;
        rig.Stations[1].Adaptation.ReportFor(1); // what B last told A

        rig.Run(100);
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-6);
        rig.Send(1, 2, 200).Should().Be(SendResult.Accepted);
        rig.Run(8);
        int afterDrop = b.Received.Count;

        // Told the settled format afresh (the silences so far have walked it down), then 10 dB weaker: the burst is heard and not decoded.
        rig.Stations[0].Adaptation.HeardReport(2, settled, false, rig.Stations[0].Seconds);
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-10);
        int before = b.Received.Count;
        rig.Send(1, 2, 300).Should().Be(SendResult.Accepted);
        rig.Run(8);
        int lost = b.Received.Count == before ? 1 : 0;

        int afterFail = b.Received.Count;

        var owed = rig.Stations[1].ReportsOwedCount;
        return new Outcome(received, afterDrop, afterFail, b.Requests.Count, b.Transmissions.Count, owed.Failed + owed.Drop + owed.Asked, unanswered, silences, returned, lost, rig);
    }

    [Fact]
    public void A_Listener_Delivers_Measures_And_Runs_The_Rules_As_Usual_And_Never_Asks_To_Transmit()
    {
        Outcome twin = Scenario(bIdentified: true);
        Outcome listener = Scenario(bIdentified: false);
        Console.WriteLine($"identified: received {twin.Received}/{twin.ReceivedAfterDrop}/{twin.ReceivedAfterFail}, requests {twin.Requests}, owed {twin.Owed}, silences {twin.ASilences}, rules returned {twin.ReturnedByRules}");
        Console.WriteLine($"listener:   received {listener.Received}/{listener.ReceivedAfterDrop}/{listener.ReceivedAfterFail}, requests {listener.Requests}, owed {listener.Owed}, silences {listener.ASilences}, rules returned {listener.ReturnedByRules}");

        // The scenario is a real one: with an identity, B reports (failed, drop and requested reports go on the air).
        twin.Requests.Should().BeGreaterThan(0);
        twin.Owed.Should().BeGreaterThan(0);
        twin.ASilences.Should().Be(0);

        // Without one, B delivers what it decodes, and the rules are called and return reports (they are called as now), but nothing is owed, asked for or put on the air.
        listener.Received.Should().Be(3, "all three bursts that asked for a report were delivered");
        listener.ReceivedAfterDrop.Should().Be(4, "the weaker burst decoded");
        listener.Lost.Should().Be(1, "a burst weak enough was heard and not decoded");
        listener.ReturnedByRules.Should().BeGreaterThan(0, "the adaptation rules are still called and still return what is owed");
        listener.Owed.Should().Be(0);
        listener.Requests.Should().Be(0);
        listener.Transmissions.Should().Be(0);
        listener.Rig.Nodes[1].ModemSent.Should().BeEmpty();
        listener.Rig.Nodes[1].Dropped.Should().BeEmpty();
        listener.Rig.Stations[1].OwedReports.Should().BeEmpty();

        // The sender, hearing nothing back after a burst that asked, counts a silence where it would have had a report.
        listener.ASilences.Should().BeGreaterThan(0);
        listener.AUnanswered.Should().BeGreaterThan(0);
    }

    [Fact]
    public void After_The_Identity_Is_Set_The_Same_Station_Sends_Reports_And_Identifies_After_Its_First_Transmission()
    {
        var rig = new SimRig(2, gain: 0.1, identified: i => i == 1);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(1, 2, 100, reportWanted: true).Should().Be(SendResult.Accepted);
        rig.Run(12);
        b.Received.Should().ContainSingle();
        b.Requests.Should().BeEmpty();
        rig.Send(2, 1, 101).Should().Be(SendResult.NoIdentity);

        rig.Stations[1].SetIdentity("M0S2").Should().Be(IdentityResult.Accepted);
        rig.Stations[1].IdentificationSuspended = false;
        b.IdentificationsDue.Should().BeEmpty();
        rig.Send(2, 1, 102).Should().Be(SendResult.Accepted);
        rig.Run(30);

        b.Transmitted.Should().ContainSingle().Which.Event.Tag.Should().Be(new HostTag(102));
        a.Received.Should().ContainSingle().Which.Data.Should().Equal(SimRig.Datagram(102, 30));
        b.IdentificationsDue.Should().ContainSingle("the first transmission after the identity was set made an identification owed");
        b.ModemSent.Should().Contain(m => m.Kind == ModemSentKind.Identification);
        a.Identified.Should().ContainSingle().Which.Event.Callsign.Should().Be("M0S2");
    }

    [Fact]
    public void A_Station_Given_Its_Identity_Only_After_Hearing_Bursts_Sends_No_Stale_Report_For_Them()
    {
        Outcome listener = Scenario(bIdentified: false);
        SimRig rig = listener.Rig;
        SimNode b = rig.Nodes[1];
        rig.Stations[1].SetIdentity("M0S2").Should().Be(IdentityResult.Accepted);
        rig.Stations[1].OwedReports.Should().BeEmpty();
        rig.Stations[1].AskedReportsOwed.Should().Be(0);
        rig.Stations[1].ReportsOwedCount.Should().Be((0L, 0L, 0L));
        rig.Run(30);
        b.Requests.Should().BeEmpty("what B heard before it had an identity owes nothing");
        b.ModemSent.Should().BeEmpty();
        b.Transmissions.Should().BeEmpty();
    }
}
