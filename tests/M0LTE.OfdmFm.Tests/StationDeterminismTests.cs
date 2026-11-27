using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>What a run of a stream through a station gave: every event and call as text in order, and the rules' state.</summary>
internal sealed record RunOutcome(List<string> Log, string Neighbours, List<long> ReleasedOrder);

internal static class Runs
{
    private static readonly Lazy<(float[] Samples, List<Placed> Bursts)> Full = new(() => MixedStream.Build());
    private static readonly Lazy<(float[] Samples, List<Placed> Bursts)> Short = new(() => MixedStream.Build(3));
    private static readonly Lazy<RunOutcome> FullBaseline = new(() => Run(Full.Value.Samples, 0, false, ReleaseOrder.Natural, () => 480));
    private static readonly Lazy<RunOutcome> ShortBaseline = new(() => Run(Short.Value.Samples, 0, false, ReleaseOrder.Natural, () => 480));

    public static (float[] Samples, List<Placed> Bursts) Stream(bool shortStream) => shortStream ? Short.Value : Full.Value;

    public static RunOutcome Baseline(bool shortStream) => shortStream ? ShortBaseline.Value : FullBaseline.Value;

    /// <summary>Block sizes drawn log-uniformly from 1 to 40000 by a seeded generator.</summary>
    public static Func<int> RandomBlocks(int seed)
    {
        var rng = new Random(seed);
        return () => Math.Max(1, (int)Math.Exp(rng.NextDouble() * Math.Log(40000)));
    }

    public static RunOutcome Run(float[] samples, int workers, bool park, ReleaseOrder order, Func<int> block, int shuffleSeed = 1)
    {
        using var rig = new ReceiveRig(workers, park);
        rig.SetOrder(order, shuffleSeed);
        rig.SendTo(2, 3); // the host has sent to the addresses at the two stations that talk to this one, so what is known of them is told
        rig.Push(samples, block, settleHeaders: workers > 0);
        rig.Finish();
        return new RunOutcome(rig.Log, ReceiveRig.Neighbours(rig.Station.Adaptation), rig.ReleasedOrder);
    }

    /// <summary>Runs the full stream <paramref name="times"/> times with this many workers and this release order, each compared with the baseline; a run that lets jobs go out of order must show that it did.</summary>
    public static void Repeat(int workers, ReleaseOrder order, int times)
    {
        float[] samples = Stream(false).Samples;
        for (int run = 0; run < times; run++)
        {
            RunOutcome outcome = Run(samples, workers, true, order, () => 480, shuffleSeed: 10 + run);
            ShouldMatch(outcome, Baseline(false));
            if (order != ReleaseOrder.Natural)
            {
                outcome.ReleasedOrder.Should().NotBeInAscendingOrder("the jobs finished out of the order the bursts began in");
            }
        }
    }

    public static void ShouldMatch(RunOutcome actual, RunOutcome baseline)
    {
        actual.Log.Should().Equal(baseline.Log);
        actual.Neighbours.Should().Be(baseline.Neighbours);
    }
}

/// <summary>
/// The property that makes step 3 safe to run on a slow processor: the same audio gives the same events and the same adaptation state however it is cut into blocks and however fast, and in what order,
/// the jobs finish. Every run is compared with one inline run in blocks of 480, event by event with every number in full.
/// </summary>
public class StationDeterminismInlineTests
{
    [Fact]
    public void The_Baseline_Run_Has_Something_To_Compare()
    {
        RunOutcome b = Runs.Baseline(false);
        b.Log.Count(l => l.StartsWith("rx ", StringComparison.Ordinal)).Should().Be(MixedStream.Expected(Runs.Stream(false).Bursts).Count);
        b.Log.Count(l => l.StartsWith("adapt ", StringComparison.Ordinal)).Should().Be(MixedStream.ExpectedCalls(Runs.Stream(false).Bursts).Count);
        b.Log.Should().Contain(l => l.StartsWith("link ", StringComparison.Ordinal)).And.Contain(l => l.StartsWith("path ", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocks_Of_One_Sample_Give_The_Same_Events_On_A_Short_Stream()
    {
        Runs.ShouldMatch(Runs.Run(Runs.Stream(true).Samples, 0, false, ReleaseOrder.Natural, () => 1), Runs.Baseline(true));
    }

    [Fact]
    public void Blocks_Of_4096_Give_The_Same_Events()
    {
        Runs.ShouldMatch(Runs.Run(Runs.Stream(false).Samples, 0, false, ReleaseOrder.Natural, () => 4096), Runs.Baseline(false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Seeded_Random_Block_Sizes_Give_The_Same_Events(int seed)
    {
        Runs.ShouldMatch(Runs.Run(Runs.Stream(false).Samples, 0, false, ReleaseOrder.Natural, Runs.RandomBlocks(seed)), Runs.Baseline(false));
    }

    [Fact]
    public void Random_Block_Sizes_Give_The_Same_Events_On_A_Short_Stream()
    {
        Runs.ShouldMatch(Runs.Run(Runs.Stream(true).Samples, 0, false, ReleaseOrder.Natural, Runs.RandomBlocks(9)), Runs.Baseline(true));
    }
}

/// <summary>
/// The same property with worker threads. Each burst's receive job is held at its start and let go in an order the test picks, so that jobs finish out of the order the bursts began in. Header reads
/// are let through at once (a host whose workers keep up with the sample clock), and the audio does not run so far ahead that a burst is dropped for want of a worker; each case has its own class so that
/// they run side by side.
/// </summary>
public class StationDeterminismOneWorkerTests
{
    [Fact]
    public void One_Worker_Gives_The_Same_Events()
    {
        Runs.ShouldMatch(Runs.Run(Runs.Stream(false).Samples, 1, true, ReleaseOrder.Natural, () => 480), Runs.Baseline(false));
    }
}

/// <summary>Two workers, jobs let go in the order the bursts began.</summary>
public class StationDeterminismTwoWorkersInOrderTests
{
    [Fact]
    public void Workers_That_Finish_In_Order_Give_The_Same_Events() => Runs.Repeat(2, ReleaseOrder.Natural, 1);
}

/// <summary>Two workers, jobs let go newest first.</summary>
public class StationDeterminismTwoWorkersReversedTests
{
    [Fact]
    public void Workers_That_Finish_Newest_First_Give_The_Same_Events_Every_Time() => Runs.Repeat(2, ReleaseOrder.Reversed, 3);
}

/// <summary>Two workers, jobs let go in a seeded shuffle.</summary>
public class StationDeterminismTwoWorkersShuffledTests
{
    [Fact]
    public void Workers_That_Finish_In_A_Shuffled_Order_Give_The_Same_Events_Every_Time() => Runs.Repeat(2, ReleaseOrder.Shuffled, 3);
}

/// <summary>Four workers, so that four jobs can be out at once and a reversal is a whole reversal.</summary>
public class StationDeterminismFourWorkersTests
{
    [Fact]
    public void Four_Workers_Finishing_Newest_First_Or_Shuffled_Give_The_Same_Events()
    {
        Runs.Repeat(4, ReleaseOrder.Reversed, 2);
        Runs.Repeat(4, ReleaseOrder.Shuffled, 2);
    }
}

/// <summary>Two workers with nothing held back, at other block sizes.</summary>
public class StationDeterminismFreeRunningTests
{
    [Fact]
    public void Two_Free_Running_Workers_Give_The_Same_Events_At_Other_Block_Sizes()
    {
        float[] samples = Runs.Stream(false).Samples;
        for (int run = 0; run < 3; run++)
        {
            Runs.ShouldMatch(Runs.Run(samples, 2, false, ReleaseOrder.Natural, run == 0 ? () => 4096 : Runs.RandomBlocks(30 + run)), Runs.Baseline(false));
        }
    }
}
