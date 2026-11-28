using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The baseline of docs/channel-sharing.md ("Baseline: how the station behaves today"), behind <c>SM_MASK_GATE=1</c>: each scenario run at seeds 1 to 8, every run printed as one line that a script reads back
/// (SM_HOSTILE_OUT names a file to collect them). What is asserted is what must hold on every seed: nothing delivered twice, every datagram accounted for, the causes adding up to the bursts lost, and a control (no impairment) showing none
/// of the impairment's causes. The figures themselves are not asserted: they describe today's station and are about to change on purpose.
/// </summary>
public abstract class LossBaselineBase
{
    private static readonly Dictionary<string, LossSpec> Specs = LossScenarios.All().ToDictionary(s => s.Id);

    /// <summary>Runs the scenario at every seed, prints each run, and asserts what holds on all of them.</summary>
    protected static void Baseline(string id)
    {
        Hostile.Gate();
        LossSpec spec = Specs[id];
        Hostile.Say($"LOSSHEAD id={spec.Id} group={spec.Group} title={spec.Title}");
        foreach (int seed in LossScenarios.Seeds)
        {
            LossRun r = LossScenarios.Run(spec, seed);
            Hostile.Say(r.Line());
            r.Offered.Should().BeGreaterThan(0);
            r.Duplicates.Should().Be(0, $"seed {seed}: no burst's datagram is delivered twice");
            r.DeliveredTwice.Should().Be(0, $"seed {seed}: no datagram arrives twice");
            (r.Delivered + r.Dropped + r.Queued).Should().BeLessThanOrEqualTo(r.Offered, $"seed {seed}: a datagram is delivered, dropped, queued or lost on the air, once");
            r.Causes.Sum().Should().Be(r.NotDecoded, $"seed {seed}: every burst not decoded has one cause");
            r.DataBursts.Should().BeGreaterThan(0);
            r.ImpulseHitNotDecoded.Should().BeLessThanOrEqualTo(r.NotDecoded);
            if (spec.Impair is null)
            {
                r.Causes[(int)LossCause.Fade].Should().Be(0, $"seed {seed}: a control has no fades");
                r.Causes[(int)LossCause.Impulse].Should().Be(0, $"seed {seed}: a control has no impulses");
                r.ImpulseHit.Should().Be(0, $"seed {seed}: a control has no impulses");
            }

            if (spec.Group is "impulses" or "flutter" or "step")
            {
                r.Causes[(int)LossCause.OtherStation].Should().BeLessThanOrEqualTo(r.NotDecoded);
            }
        }
    }
}

/// <summary>A steady good path with impulses at three intensities, and without.</summary>
public class LossBaselineImpulseTests : LossBaselineBase
{
    [Theory]
    [InlineData("impulses-off")]
    [InlineData("impulses-light")]
    [InlineData("impulses-moderate")]
    [InlineData("impulses-heavy")]
    public void Impulses(string id) => Baseline(id);
}

/// <summary>A flutter episode of 30 s at 3 Hz, at 20 Hz, and swept, at two strengths, and without.</summary>
public class LossBaselineFlutterTests : LossBaselineBase
{
    [Theory]
    [InlineData("flutter-off")]
    [InlineData("flutter-3hz-0.5")]
    [InlineData("flutter-3hz-0.9")]
    [InlineData("flutter-20hz-0.5")]
    [InlineData("flutter-20hz-0.9")]
    [InlineData("flutter-sweep-0.5")]
    [InlineData("flutter-sweep-0.9")]
    public void Flutter(string id) => Baseline(id);
}

/// <summary>A 12 dB step down and up again, and the three-station and hidden-station arrangements with moderate impulses, each with its control.</summary>
public class LossBaselineStepAndSharedChannelTests : LossBaselineBase
{
    [Theory]
    [InlineData("step-off")]
    [InlineData("step-12db")]
    [InlineData("three-off")]
    [InlineData("three-impulses")]
    [InlineData("hidden-off")]
    [InlineData("hidden-impulses")]
    public void StepAndSharedChannel(string id) => Baseline(id);
}
