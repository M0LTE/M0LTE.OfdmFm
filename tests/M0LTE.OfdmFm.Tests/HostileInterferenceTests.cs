using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8, scenario 3: interference part-way through a burst (the protocol's own example, "Under a link layer": a station that B cannot hear keys over one of A's bursts). A and B exchange traffic until A is at a settled format,
/// then A sends B a datagram long enough to make a burst of 24 payload symbols; a third station's burst, at the same format and so the same drive, is put on the channel a quarter, a half or three quarters of the way through it,
/// and stops when A's burst does. The capture model has default parameters (assumed). What the protocol's table says B does: judge it sudden, report failed with its recommendation unchanged, and leave its estimate, margin and cap alone.
/// </summary>
public class HostileInterferenceTests
{
    private static HostileScenarios.Interference Run(double fraction, double relativeDb)
    {
        HostileScenarios.Interference r = HostileScenarios.Interfered(HostileScenarios.Default, fraction, relativeDb, 1);
        Hostile.Say(HostileScenarios.Describe(r));
        return r;
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void A_Stronger_Station_Keying_Over_A_Burst_Is_Judged_Sudden_Reported_Failed_With_The_Recommendation_Unchanged_And_The_Rules_State_Untouched(double fraction)
    {
        HostileScenarios.Interference r = Run(fraction, 6);
        r.PayloadSymbols.Should().BeGreaterThanOrEqualTo(24);
        r.BurstFound.Should().BeTrue();
        r.Decoded.Should().BeFalse();
        r.Sudden.Should().BeTrue("good, then collapsing");
        r.ReportedFailed.Should().BeTrue("a failed report reached A");
        r.ReportedFormat.Should().Be(r.FormatBefore, "failed, format unchanged");
        r.RecommendationUnchanged.Should().BeTrue();
        r.EstimateUnchanged.Should().BeTrue("no measurement was taken");
        r.MarginUnchanged.Should().BeTrue();
        r.CapUnchanged.Should().BeTrue();
        r.GoodUnchanged.Should().BeTrue("the count of bursts decoded running did not move");
        r.BigArrived.Should().BeFalse();
        r.NextFormat.Should().Be(r.FormatBefore, "A sends again at the same format");
        r.NextDelivered.Should().BeTrue("and it gets through, the other station having stopped");
    }

    [Fact]
    public void A_Weaker_Station_Keying_Over_A_Burst_Is_Left_As_Noise_And_The_Burst_Still_Arrives()
    {
        HostileScenarios.Interference r = Run(0.5, -10);
        r.BurstFound.Should().BeTrue();
        r.Decoded.Should().BeTrue("10 dB weaker is over the margin: the capture takes A, the other is noise 27 dB under it, which this format rides over");
        r.Sudden.Should().BeFalse();
        r.ReportedFailed.Should().BeFalse();
        r.BigArrived.Should().BeTrue();
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Three_Stations()
    {
        List<string> once = HostileScenarios.Interfered(HostileScenarios.Default, 0.5, 6, 2).Log;
        once.Should().Contain(l => l.Contains("notdecoded", StringComparison.Ordinal));
        HostileScenarios.Interfered(HostileScenarios.Default, 0.5, 6, 2).Log.Should().Equal(once);
    }
}
