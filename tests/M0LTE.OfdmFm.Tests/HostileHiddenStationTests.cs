using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8, scenario 2: a hidden station. A and C cannot hear each other; both hear and are heard by B; both send B a first-contact datagram. Their bursts overlap at B, which behaves as an FM receiver does under the capture model
/// (default parameters, assumed). Each case is one round with the exact start of each burst known (the channel has been idle, so a datagram goes at once): A's at 20 s, C's at the offset in the name.
/// </summary>
public class HostileHiddenStationTests
{
    private static HostileScenarios.HiddenRound Round(double aDb, double cDb, double offset, string name)
    {
        HostileScenarios.HiddenRound r = HostileScenarios.HiddenOne(HostileScenarios.Default, 1, aDb, cDb, offset, name);
        Hostile.Say(HostileScenarios.Describe(r));
        return r;
    }

    [Fact]
    public void When_A_Is_Much_Stronger_At_B_A_Gets_Through_And_C_Is_Not_Heard_At_All_Whether_It_Starts_Together_Or_Later()
    {
        foreach (double offset in new[] { 0.0, 0.5 })
        {
            HostileScenarios.HiddenRound r = Round(0, -20, offset, $"A 20 dB stronger, C starts {offset} s later");
            r.ADelivered.Should().BeTrue();
            r.CDelivered.Should().BeFalse();
            r.BHeard.Should().StartWith("from 1 ").And.NotContain("from 3");
            r.ReportsToA.Should().Be(1, "A asked for a report and its burst decoded");
            r.ReportsToC.Should().Be(0, "B never saw C's burst, so it cannot report on it");
            (r.HintsAtA, r.HintsAtC).Should().Be((0, 0));
            r.MarginForA.Should().BeLessThan(3.0, "a decoded burst lowers the margin; nothing was held against A");
            r.MarginForC.Should().BeNull("B has never heard C");
        }
    }

    [Fact]
    public void When_C_Is_Much_Stronger_And_Starts_Together_C_Gets_Through_And_A_Is_Not_Heard_At_All()
    {
        HostileScenarios.HiddenRound r = Round(-20, 0, 0, "C 20 dB stronger, same start");
        r.CDelivered.Should().BeTrue();
        r.ADelivered.Should().BeFalse();
        r.BHeard.Should().StartWith("from 3 ").And.NotContain("from 1");
        r.ReportsToC.Should().Be(1);
        r.ReportsToA.Should().Be(0);
        (r.HintsAtA, r.HintsAtC).Should().Be((0, 0));
    }

    [Fact]
    public void A_Stronger_Station_Starting_Inside_A_Burst_B_Is_Already_Receiving_Spoils_That_Burst_And_Is_Not_Itself_Received()
    {
        // A starts, B reads its header; C, 20 dB stronger, starts 0.43 s later, so that its signal (after its lead-in) arrives under one symbol into A's payload. B's audio becomes C's. A's payload fails, and C's burst,
        // which began inside a burst B knows of, is ignored unread: neither datagram arrives. B reports "failed" to A (and the host is hinted at which datagram), and reads the failure as the path's: the margin goes up and the cap is set, though the path is fine.
        // #898: 0.43 s, not the original 0.5 s, and not to dodge that fault. At 0.5 s C's signal arrives two symbols into the payload, the receiver now rightly judges the collision sudden, and a sudden failure from a station
        // B has no estimate for yet owes no report: A is told nothing and counts a silence (A_Collision_Two_Symbols_Into_A_First_Contact_Payload_Is_Judged_Sudden...). This test is about the report and the hint, so it keeps the
        // collision where the payload begins.
        HostileScenarios.HiddenRound r = Round(-20, 0, 0.43, "C 20 dB stronger, starts 0.43 s later");
        r.ADelivered.Should().BeFalse();
        r.CDelivered.Should().BeFalse("it is ignored: a candidate inside a burst already known is not read");
        r.BHeard.Should().StartWith("from 1 fmt 192 decoded False").And.NotContain("from 3");
        r.ReportsToA.Should().Be(1);
        r.FailedReportAtA.Should().BeTrue();
        r.HintsAtA.Should().Be(1, "A is told which datagram was in the burst that was heard and not decoded");
        r.ReportsToC.Should().Be(0);
        r.FailedReportAtC.Should().BeFalse();
        r.SilencesC.Should().Be(0, "C heard B's report to A, and any header from B counts as an answer, whoever it was for");
    }

    [Fact]
    public void Records_Present_Behaviour_A_Collision_Where_The_Payload_Begins_Is_Read_As_The_Path_Failing()
    {
        // Poor behaviour, recorded, not tuned. The collision begins as the payload does, so there is no clean early part and the failure is not judged sudden (the test needs two clean symbols first): by the rules it is a failure of
        // the path, so the margin goes up by half a dB and the cap is set. At the most robust format the cap changes nothing, the margin is what is left of it; at any other format the recommendation would have fallen.
        // #898: C starts 0.43 s after A, not the original 0.5 s, and not to dodge that fault: C's signal then arrives under one symbol into A's payload. At 0.5 s it arrives two symbols in, which the receiver now rightly judges
        // sudden (the next test), so 0.5 s no longer puts the collision where the payload begins.
        HostileScenarios.HiddenRound r = Round(0, 0, 0.43, "equal strength, C starts 0.43 s later");
        r.BHeard.Should().Contain("decoded False sudden False");
        r.MarginForA.Should().BeApproximately(3.5, 1e-9);
        r.CapForA.Should().Be(StandardFormats.FirstContact);
        r.ADelivered.Should().BeFalse();
        r.CDelivered.Should().BeFalse();
        r.ReportsToA.Should().Be(1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-20.0)]
    public void A_Collision_Two_Symbols_Into_A_First_Contact_Payload_Is_Judged_Sudden_And_The_Rules_State_Is_Left_Alone(double cDb)
    {
        // C starts 0.5 s after A: after its lead-in its signal arrives two symbols into A's payload. Until packet-net/pdn-soundmodem#898 was fixed the receiver judged this a failure of the path (poor behaviour 2 of step 8).
        // It is judged sudden: B's margin for A stays at 3.00 and no cap is set. B has no estimate for A yet (first contact), so it has no recommendation to repeat and sends no report: A is told nothing and counts a silence.
        HostileScenarios.HiddenRound r = Round(cDb, 0, 0.5, cDb == 0 ? "equal strength, C starts 0.5 s later" : "C 20 dB stronger, starts 0.5 s later");
        r.ADelivered.Should().BeFalse();
        r.CDelivered.Should().BeFalse();
        r.BHeard.Should().StartWith("from 1 fmt 192 decoded False sudden True").And.NotContain("from 3");
        r.MarginForA.Should().BeApproximately(3.0, 1e-9, "a sudden failure leaves the margin alone");
        r.CapForA.Should().BeNull("and sets no cap");
        (r.ReportsToA, r.HintsAtA).Should().Be((0, 0));
        r.SilencesA.Should().Be(1, "the datagram is lost with a count, not without a trace");
    }

    [Fact]
    public void Equal_Strength_Starting_Together_Nobody_Is_Heard_Nothing_Is_Reported_And_Both_Count_A_Silence()
    {
        HostileScenarios.HiddenRound r = Round(0, 0, 0, "equal strength, same start");
        r.ADelivered.Should().BeFalse();
        r.CDelivered.Should().BeFalse();
        r.BHeard.Should().Be("nothing", "B's receiver found no burst it could read");
        (r.ReportsToA, r.ReportsToC).Should().Be((0, 0));
        (r.HintsAtA, r.HintsAtC).Should().Be((0, 0));
        (r.SilencesA, r.SilencesC).Should().Be((1, 1), "each asked for a report and nothing came back: one silence each");
        (r.MarginForA, r.MarginForC).Should().Be((null, null));
    }

    [Fact]
    public void Records_Present_Behaviour_A_Silence_Is_Not_Counted_While_The_Common_Neighbour_Talks_To_The_Other_Station()
    {
        // Poor behaviour, recorded, not tuned. C's burst is lost without a trace (A is 20 dB stronger at B). C asked for a report; B sends A a lone report in the same 40 s, and C hears it. The answer wait counts any header read from B, so C
        // counts no silence: it believes it is being heard and stays at its format. In the case where B says nothing at all (both lost), C counts one.
        HostileScenarios.HiddenRound heard = Round(0, -20, 0.5, "A 20 dB stronger, C starts 0.5 s later");
        heard.CDelivered.Should().BeFalse();
        heard.SilencesC.Should().Be(0);
        HostileScenarios.HiddenRound quiet = Round(0, 0, 0, "equal strength, same start");
        quiet.SilencesC.Should().Be(1);
    }

    [Fact]
    public void The_Old_Additive_Channel_Is_Still_There_And_Gives_The_Same_Answer_For_A_Weak_Interferer()
    {
        HostileScenarios.HiddenRound adder = HostileScenarios.HiddenOne(null, 1, 0, -20, 0.5, "additive: A 20 dB stronger, C starts 0.5 s later");
        Hostile.Say(HostileScenarios.Describe(adder));
        adder.ADelivered.Should().BeTrue();
        adder.CDelivered.Should().BeFalse();
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Three_Stations()
    {
        // #898: C starts 0.43 s after A, not the original 0.5 s, and not to dodge that fault: the log must hold a hint, and at 0.5 s the collision is judged sudden and owes no report (see above).
        List<string> once = HostileScenarios.HiddenOne(HostileScenarios.Default, 2, 0, 0, 0.43, "x").Log;
        once.Should().Contain(l => l.Contains("notdecoded", StringComparison.Ordinal));
        HostileScenarios.HiddenOne(HostileScenarios.Default, 2, 0, 0, 0.43, "x").Log.Should().Equal(once);
    }
}
