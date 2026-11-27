using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8, scenario 1: three stations that all hear each other and all have traffic for each other, on a channel where overlapping signals behave like FM (the capture model, default parameters, an assumption). The datagrams
/// come at seeded moments; a simple host resends what the station says it could not decode, and anything not delivered 30 to 40 s after it went out. Numbers are printed (and are in the pull request); they are about the station's logic
/// under that model, not predictions for a real channel.
/// </summary>
public class HostileThreeStationTests
{
    // Each run is deterministic, so the tests that read the same one share it.
    private static readonly Lazy<HostileScenarios.ThreeAll> With = new(() => HostileScenarios.ThreeHearAll(HostileScenarios.Default, true, 1, perPair: 3, length: 40, seconds: 110, spreadSeconds: 25));
    private static readonly Lazy<HostileScenarios.ThreeAll> Without = new(() => HostileScenarios.ThreeHearAll(HostileScenarios.Default, false, 1, perPair: 3, length: 40, seconds: 110, spreadSeconds: 25));

    private static HostileScenarios.ThreeAll Run(bool indication) => indication ? With.Value : Without.Value;

    [Fact]
    public void Three_Stations_With_Traffic_For_Each_Other_Deliver_Everything_Exactly_Once_With_A_Carrier_Indication()
    {
        HostileScenarios.ThreeAll r = Run(true);
        Hostile.Say(HostileScenarios.Describe(r));
        Check(r);
        r.CollidedBursts.Should().BeGreaterThan(0, "three stations at first contact with traffic for each other do overlap now and then");
    }

    [Fact]
    public void Three_Stations_With_Traffic_For_Each_Other_Deliver_Everything_Exactly_Once_With_Detection_Alone()
    {
        HostileScenarios.ThreeAll r = Run(false);
        Hostile.Say(HostileScenarios.Describe(r));
        Check(r);
        r.CollidedBursts.Should().BeGreaterThan(0);
        r.Recovered.Should().BeGreaterThan(0, "without a carrier indication a station can take 0.9 s to notice another, so bursts are lost to overlap and recovered by the host's resend");
    }

    private static void Check(HostileScenarios.ThreeAll r)
    {
        r.Items.Should().Be(18);
        r.Delivered.Should().Be(18, "every datagram arrives, the lost ones after the host's resend");
        r.Duplicates.Should().Be(0, "no burst's datagram is delivered twice");
        r.DeliveredTwice.Should().Be(0, "no datagram arrives twice, even by two attempts");
        r.Dropped.Should().Be(0);
        r.BytesBySender.Should().Equal(new[] { 240, 240, 240 }, "each station got all of its 6 datagrams of 40 bytes through");
        r.AirSeconds.Should().OnlyContain(a => a > 0, "every station got some airtime");
    }

    [Fact]
    public void Both_Ways_Of_Noticing_A_Carrier_Deliver_Everything_Once_Whatever_The_Seed()
    {
        // What holds on every seed tried (1 to 4 here, 1 to 8 in the gated test below): every datagram arrives, none twice. How many bursts collided is a seeded figure, kept for seed 1 above, and is not compared between the two:
        // over seeds 1 to 8 the totals do not differ by more than chance (see HostileLongTests).
        foreach (int seed in new[] { 2, 3, 4 })
        {
            foreach (bool indication in new[] { true, false })
            {
                HostileScenarios.ThreeAll r = HostileScenarios.ThreeHearAll(HostileScenarios.Default, indication, seed, perPair: 3, length: 40, seconds: 110, spreadSeconds: 25);
                Check(r);
            }
        }
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Three_Stations()
    {
        List<string> once = HostileScenarios.ThreeHearAll(HostileScenarios.Default, true, 3, perPair: 1, length: 40, seconds: 45, spreadSeconds: 10).Log;
        once.Should().Contain(l => l.Contains(" rx from ", StringComparison.Ordinal));
        HostileScenarios.ThreeHearAll(HostileScenarios.Default, true, 3, perPair: 1, length: 40, seconds: 45, spreadSeconds: 10).Log.Should().Equal(once);
        HostileScenarios.ThreeHearAll(HostileScenarios.Default, true, 4, perPair: 1, length: 40, seconds: 45, spreadSeconds: 10).Log.Should().NotEqual(once, "the seeds matter");
    }
}
