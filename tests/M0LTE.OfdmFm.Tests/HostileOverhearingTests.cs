using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Step 8, scenarios 4 and 5: a station that overhears, and a burst for everyone with listeners at different levels.</summary>
public class HostileOverhearingTests
{
    [Fact]
    public void A_Station_That_Has_Overheard_Two_Others_Starts_Its_First_Burst_To_One_Of_Them_Two_Rungs_Under_Its_Own_Recommendation()
    {
        HostileScenarios.Overheard r = HostileScenarios.Overhearing(HostileScenarios.Default, 1);
        Hostile.Say(HostileScenarios.Describe(r));
        r.HeardMeasures.Should().BeGreaterThanOrEqualTo(4, "C measured every burst of A's that it heard, whoever it was for");
        r.Recommended.Should().NotBeNull();
        r.FirstFormat.Should().Be(r.ExpectedFormat, "no report, but heard: its own recommendation for the opposite direction, two rungs slower");
        r.FirstFormat.Should().NotBe(StandardFormats.FirstContact);
        r.FirstRate.Should().BeGreaterThan(100 * SimRig.FirstContactRate, "not the first-contact format");
        r.FirstRate.Should().BeLessThan(r.RecommendedRate, "two rungs under what the path supports");
        r.FirstDecoded.Should().BeTrue();
        r.SupportedRate.Should().BeGreaterThanOrEqualTo(r.FirstRate, "what A then says the path supports is at least where C began");
    }

    [Fact]
    public void A_Burst_For_Everyone_Reaches_Every_Listener_That_Can_Hear_It_And_Nobody_Reports_Anything()
    {
        double[] gains = [0, -40, -60, -68];
        HostileScenarios.Broadcast r = HostileScenarios.BroadcastTo(HostileScenarios.Default, 1, gains);
        Hostile.Say(HostileScenarios.Describe(r));
        r.Format.Should().Be(StandardFormats.FirstContact, "a burst for everyone goes at the first-contact format");
        r.Decoded.Should().Equal(true, true, true, false);
        r.Copies.Should().Equal(1, 1, 1, 0);
        r.PathReportsAt.Should().OnlyContain(c => c == 0, "nothing is owed for a burst for everyone, whoever heard it and however well");
        r.ModemSentAt.Should().OnlyContain(c => c == 0, "no lone report, from the listener that could not decode either");
        r.Hints.Should().Be(0);
        r.Transmitted.Should().Be(1);
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Stations()
    {
        List<string> once = HostileScenarios.BroadcastTo(HostileScenarios.Default, 2, [0, -40, -68]).Log;
        once.Should().Contain(l => l.Contains(" rx from 1 ", StringComparison.Ordinal));
        HostileScenarios.BroadcastTo(HostileScenarios.Default, 2, [0, -40, -68]).Log.Should().Equal(once);
        List<string> overheard = HostileScenarios.Overhearing(HostileScenarios.Default, 2, pairs: 2).Log;
        HostileScenarios.Overhearing(HostileScenarios.Default, 2, pairs: 2).Log.Should().Equal(overheard);
    }
}
