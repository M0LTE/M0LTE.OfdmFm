using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8, scenario 7: identification among three (the test seam that holds identification back is off). A and B send each other a datagram every 20 s and know each other by the host's names; C hears them, sends A one datagram
/// at 60 s and nothing else, and knows A and B by the host's names; A and B know nothing of C. The interval is shortened to 40 s of the sample clock (the real ten minutes is run end to end in the gated tier); the rule is the one
/// in "Step 7 as built" (as changed on 28 November 2026: due when a transmission is about to go and goes first, never from a timer).
/// </summary>
public class HostileIdentificationTests
{
    private static readonly Lazy<HostileScenarios.Idents> Shared = new(() =>
    {
        HostileScenarios.Idents r = HostileScenarios.Identification(HostileScenarios.Default, 1, 40, 150);
        Hostile.Say(HostileScenarios.Describe(r));
        return r;
    });

    [Fact]
    public void Each_Active_Station_Identifies_By_The_Rule_And_A_Station_That_Transmitted_Once_Identifies_Once()
    {
        HostileScenarios.Idents r = Shared.Value;
        // Was {3, 3, 1} when an identification fell due as a transmission ended. Now it falls due as the next one is about to go, one transmission earlier, and B's datagrams (every 20 s, so exactly 40 s apart) meet the 40 s mark exactly.
        r.Sent.Should().Equal(new[] { 3, 4, 1 }, "A and B keep transmitting: one before the first transmission, then one before the first transmission that is about to go 40 s or more after the start of the last; C transmitted once");
        r.Due.Should().Equal(r.Sent);
        for (int station = 0; station < 2; station++)
        {
            double[] starts = r.StartsSeconds[station];
            starts.Should().HaveCount(station == 0 ? 3 : 4);
            for (int i = 1; i < starts.Length; i++)
            {
                // Not on a clock: the next transmission about to go past the 40 s mark makes it due, and each station sends about every 20 s.
                (starts[i] - starts[i - 1]).Should().BeInRange(39.999, 62.0, "after the interval from the start of the previous identification burst, as the next transmission is about to go");
            }
        }

        r.StartsSeconds[0][0].Should().BeLessThan(10, "due at once, as A's first transmission is about to go");
        r.StartsSeconds[2][0].Should().BeInRange(59.0, 62.0, "C identifies right before its one transmission, 60 s in");
    }

    [Fact]
    public void The_Others_Hear_Each_Callsign_And_It_Names_Nothing()
    {
        HostileScenarios.Idents r = Shared.Value;
        r.HeardAt[0].Count(h => h == "M0S2@2").Should().Be(4, "A heard each of B's four");
        r.HeardAt[0].Count(h => h == "M0S3@3").Should().Be(1);
        r.HeardAt[1].Count(h => h == "M0S1@1").Should().Be(3);
        r.HeardAt[1].Count(h => h == "M0S3@3").Should().Be(1);
        r.HeardAt[2].Count(h => h == "M0S1@1").Should().Be(3);
        r.HeardAt[2].Count(h => h == "M0S2@2").Should().Be(4);

        // The event carries the callsign as sent; a callsign names nothing the host can use.
        r.NamesAt[0].Where((_, i) => r.HeardAt[0][i].EndsWith("@2", StringComparison.Ordinal)).Distinct().Should().Equal("M0S2");
        r.NamesAt[0].Where((_, i) => r.HeardAt[0][i].EndsWith("@3", StringComparison.Ordinal)).Distinct().Should().Equal("M0S3");
        r.NamesAt[1].Where((_, i) => r.HeardAt[1][i].EndsWith("@3", StringComparison.Ordinal)).Distinct().Should().Equal("M0S3");
    }

    [Fact]
    public void The_Identification_Bursts_Did_Not_Collide_With_Traffic_In_This_Run()
    {
        // Counted, not designed: an identification goes first, in the first slot before the transmission that made it due, so it precedes its own station's traffic. It can still overlap a burst of a station that has not noticed it.
        HostileScenarios.Idents r = Shared.Value;
        r.IdentBursts.Should().Be(8, "was 7 before identification went first");
        r.IdentsOverlapped.Should().Be(0);
        r.TrafficOverlappedByIdent.Should().Be(0);
        r.TrafficBursts.Should().BeGreaterThan(15);
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Three_Stations_Identifications_Included()
    {
        List<string> once = HostileScenarios.Identification(HostileScenarios.Default, 2, 20, 70).Log;
        once.Should().Contain(l => l.Contains("identified", StringComparison.Ordinal));
        HostileScenarios.Identification(HostileScenarios.Default, 2, 20, 70).Log.Should().Equal(once);
    }
}
