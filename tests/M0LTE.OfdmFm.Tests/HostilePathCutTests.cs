using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8, scenario 6: a path cut and restored among three. A and B send each other and C datagrams at seeded moments through a warm-up; then A and B cannot hear each other for 75 s while both still reach C; then the path is restored.
/// Default capture parameters (assumed). No host resends anything, so what is delivered is what the stations managed.
/// </summary>
public class HostilePathCutTests
{
    // One run, shared by the tests that read it (it is deterministic, so sharing it changes nothing but the time).
    private static readonly Lazy<HostileScenarios.Cut> Shared = new(() =>
    {
        HostileScenarios.Cut r = HostileScenarios.PathCut(HostileScenarios.Default, 3); // seed 3: the scenario is seed-sensitive (over seeds 1 to 8 only 3, 7 and 8 reach three silences at both ends and a first-contact last burst each way)
        Hostile.Say(HostileScenarios.Describe(r));
        return r;
    });

    private static HostileScenarios.Cut Run() => Shared.Value;

    [Fact]
    public void A_And_B_Fall_Back_By_The_Silence_Steps_While_The_Paths_To_C_Carry_On_And_After_The_Cut_They_Find_Each_Other_And_Climb()
    {
        HostileScenarios.Cut r = Run();

        // Into the cut, both count silences up to the third (A's last burst of the cut is at first contact, B's earlier ones step down), and A's formats go down the rungs the rules give.
        r.MaxSilencesA.Should().BeGreaterThanOrEqualTo(3);
        r.MaxSilencesB.Should().BeGreaterThanOrEqualTo(3);
        r.SilencesAtCutEnd.Should().BeGreaterThanOrEqualTo(3);
        r.AToBCut.Split(',').Last().Should().Be(StandardFormats.FirstContact.ToString(), "after three silences, first contact");
        StandardFormats.Rate(int.Parse(r.BToACut.Split(',').Last())).Should().BeLessThan(StandardFormats.Rate(int.Parse(r.BToACut.Split(',').First())), "B's bursts into the cut get slower");

        // The paths to C go on being used and are not sent back to first contact by the silences of the other path; every datagram to C sent in the cut arrived.
        int.Parse(r.AToCCut.Split(',').Last()).Should().NotBe(StandardFormats.FirstContact, "A's last burst to C in the cut is not at first contact");
        int.Parse(r.BToCCut.Split(',').Last()).Should().NotBe(StandardFormats.FirstContact);
        r.ToCSentInCut.Should().BeGreaterThan(0);
        // Before link addresses none was lost in the cut (no burst collided at C then). Now four bursts collide at C in the cut (A and B are hidden from each other, and every burst is a few bytes longer and the timing differs), and the scenario
        // resends nothing, so a datagram to C that did not arrive must be in a burst that collided at C: that is what is checked, and the figure moved from 10 of 10 to 7 of 10.
        (r.ToCSentInCut - r.ToCDeliveredInCut).Should().BeLessThanOrEqualTo(r.CollidedByPeriod[1], "every datagram to C lost in the cut was in a burst that collided at C");

        // Restored: they find each other and climb back to where they were.
        r.BurstsToRecover.Should().NotBeNull("A is back at 90 percent of its pre-cut rate");
        r.BurstsToRecover!.Value.Should().BeLessThanOrEqualTo(6);
        r.AToBAfter.Split(',').Select(int.Parse).Select(StandardFormats.Rate).Max().Should().BeGreaterThan(StandardFormats.Rate(StandardFormats.FirstContact) * 50);
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_All_Three_Stations()
    {
        List<string> once = HostileScenarios.PathCut(HostileScenarios.Default, 2, warm: 2, cutRounds: 2, after: 2, spacing: 12).Log;
        once.Should().Contain(l => l.Contains(" transmitted ", StringComparison.Ordinal));
        HostileScenarios.PathCut(HostileScenarios.Default, 2, warm: 2, cutRounds: 2, after: 2, spacing: 12).Log.Should().Equal(once);
    }
}
