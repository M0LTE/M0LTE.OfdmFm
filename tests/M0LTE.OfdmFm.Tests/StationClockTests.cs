using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Step 1 of the station: the sample clock, the timers on it, and one seed giving one run.</summary>
public class StationClockTests
{
    private static Station Make(int seed = 1) => new("M0LTE", 1, new Random(seed));

    private static void PushInBlocks(Station station, long total, int block)
    {
        float[] zeros = new float[block];
        long left = total;
        while (left > 0)
        {
            int n = (int)Math.Min(block, left);
            station.Push(zeros.AsSpan(0, n));
            left -= n;
        }
    }

    [Fact]
    public void A_new_station_is_at_sample_zero()
    {
        Station station = Make();
        station.SampleCount.Should().Be(0);
        station.Seconds.Should().Be(0);
    }

    [Fact]
    public void Push_and_Skip_advance_the_clock_and_the_derived_time()
    {
        Station station = Make();
        station.Push(new float[48000]);
        station.Skip(24000);
        station.SampleCount.Should().Be(72000);
        station.Seconds.Should().Be(1.5);
    }

    [Fact]
    public void Skip_refuses_a_negative_count_and_accepts_zero()
    {
        Station station = Make();
        station.Skip(0);
        Action act = () => station.Skip(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
        station.SampleCount.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    [InlineData(100000)]
    public void Timers_fire_at_exact_sample_indexes_whatever_the_block_size(int block)
    {
        Station station = Make();
        long[] due = [1, 2, 6, 7, 8, 479, 480, 481, 47999, 48000, 99999, 100000, 100001, 150000];
        var seen = new List<(long Due, long Clock)>();
        foreach (long d in due)
        {
            long captured = d;
            station.ScheduleAt(d, () => seen.Add((captured, station.SampleCount)));
        }

        PushInBlocks(station, 150000, block);

        seen.Should().HaveCount(due.Length);
        seen.Should().OnlyContain(s => s.Clock == s.Due);
        seen.Select(s => s.Due).Should().Equal(due);
        station.SampleCount.Should().Be(150000);
    }

    [Fact]
    public void A_block_that_spans_several_deadlines_runs_each_at_its_own_sample_in_order()
    {
        Station station = Make();
        var seen = new List<(string Name, long Clock)>();
        station.ScheduleAt(30, () => seen.Add(("c", station.SampleCount)));
        station.ScheduleAt(10, () => seen.Add(("a", station.SampleCount)));
        station.ScheduleAt(20, () => seen.Add(("b", station.SampleCount)));

        station.Push(new float[100]);

        seen.Should().Equal(("a", 10L), ("b", 20L), ("c", 30L));
        station.SampleCount.Should().Be(100);
    }

    [Fact]
    public void Timers_due_at_the_same_sample_run_in_the_order_they_were_scheduled()
    {
        Station station = Make();
        var seen = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            int n = i;
            station.ScheduleAt(50, () => seen.Add(n));
        }

        station.Push(new float[50]);
        seen.Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void A_timer_is_not_early_and_fires_when_the_last_sample_before_it_arrives()
    {
        Station station = Make();
        int fired = 0;
        station.ScheduleAt(100, () => fired++);
        station.Push(new float[99]);
        fired.Should().Be(0);
        station.Push(new float[1]);
        fired.Should().Be(1);
    }

    [Fact]
    public void A_delay_counts_from_the_clock_when_it_is_scheduled()
    {
        Station station = Make();
        station.Push(new float[1000]);
        long seen = -1;
        station.ScheduleIn(250, () => seen = station.SampleCount);
        station.Push(new float[1000]);
        seen.Should().Be(1250);
    }

    [Fact]
    public void A_timer_scheduled_from_an_action_is_relative_to_that_actions_sample_and_runs_in_the_same_block()
    {
        Station station = Make();
        var seen = new List<long>();
        station.ScheduleAt(100, () =>
        {
            seen.Add(station.SampleCount);
            station.ScheduleIn(50, () => seen.Add(station.SampleCount));
        });

        station.Push(new float[1000]);
        seen.Should().Equal(100L, 150L);
    }

    [Fact]
    public void A_timer_in_the_past_or_now_runs_at_the_start_of_the_next_advance_with_the_clock_unchanged_and_never_inside_the_call()
    {
        Station station = Make();
        station.Push(new float[500]);
        long seen = -1;
        station.ScheduleAt(100, () => seen = station.SampleCount);
        station.ScheduleIn(0, () => { });
        seen.Should().Be(-1);
        station.Push(ReadOnlySpan<float>.Empty);
        seen.Should().Be(500);
        station.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void Cancelling_stops_a_timer_and_reports_whether_it_was_still_waiting()
    {
        Station station = Make();
        int fired = 0;
        TimerHandle handle = station.ScheduleAt(100, () => fired++);
        station.CancelTimer(handle).Should().BeTrue();
        station.CancelTimer(handle).Should().BeFalse();
        station.Push(new float[200]);
        fired.Should().Be(0);

        TimerHandle ran = station.ScheduleIn(10, () => fired++);
        station.Push(new float[10]);
        fired.Should().Be(1);
        station.CancelTimer(ran).Should().BeFalse();
    }

    [Fact]
    public void A_timer_can_cancel_a_later_one_in_the_same_block()
    {
        Station station = Make();
        int fired = 0;
        TimerHandle later = default;
        station.ScheduleAt(10, () => station.CancelTimer(later).Should().BeTrue());
        later = station.ScheduleAt(20, () => fired++);
        station.Push(new float[100]);
        fired.Should().Be(0);
    }

    [Fact]
    public void Skip_advances_the_clock_and_runs_what_it_passes_at_their_own_samples()
    {
        Station station = Make();
        var seen = new List<long>();
        station.ScheduleAt(100, () => seen.Add(station.SampleCount));
        station.ScheduleAt(900, () => seen.Add(station.SampleCount));
        station.ScheduleAt(5000, () => seen.Add(station.SampleCount));

        station.Skip(1000);

        seen.Should().Equal(100L, 900L);
        station.SampleCount.Should().Be(1000);
        station.PendingTimers.Should().Be(1);
    }

    [Fact]
    public void The_station_refuses_to_be_advanced_from_inside_a_timer_action()
    {
        Station station = Make();
        Exception? caught = null;
        station.ScheduleAt(5, () =>
        {
            try
            {
                station.Push(new float[1]);
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        });

        station.Push(new float[10]);
        caught.Should().NotBeNull();
        station.SampleCount.Should().Be(10);
    }

    [Fact]
    public void Push_allocates_nothing_in_steady_state()
    {
        Station station = Make();
        float[] block = new float[480];
        station.ScheduleAt(long.MaxValue, () => { });

        for (int i = 0; i < 5; i++)
        {
            station.Push(block);
        }

        AllocationMeasure.Minimum(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                station.Push(block);
            }
        }).Should().Be(0);
    }

    /// <summary>One run: jittered timers that each ask for a transmission, pushed in blocks; everything observable goes in the log.</summary>
    private static List<string> Run(int seed, int block)
    {
        Station station = Make(seed);
        var log = new List<string>();
        station.TransmitRequest += r => log.Add($"request {r.Id} earliest {r.EarliestSample} at {station.SampleCount}");
        for (int i = 0; i < 20; i++)
        {
            int n = i;
            station.ScheduleJittered(1000L * (n + 1), 5000, () =>
            {
                log.Add($"timer {n} at {station.SampleCount}");
                station.RequestTransmit(new float[4], station.SampleCount + 10);
            });
        }

        PushInBlocks(station, 40000, block);
        log.Add($"end {station.SampleCount}");
        return log;
    }

    [Fact]
    public void One_seed_gives_one_run_whatever_the_block_size()
    {
        List<string> first = Run(42, 480);
        Run(42, 480).Should().Equal(first);
        Run(42, 7).Should().Equal(first);
        Run(42, 40000).Should().Equal(first);
        first.Should().HaveCount(41);
    }

    [Fact]
    public void A_different_seed_gives_a_different_run()
    {
        Run(1, 480).Should().NotEqual(Run(2, 480));
    }

    [Fact]
    public void Jitter_stays_within_its_bounds()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            Station station = Make(seed);
            long seen = -1;
            station.ScheduleJittered(100, 50, () => seen = station.SampleCount);
            station.Push(new float[200]);
            seen.Should().BeInRange(100, 150);
        }
    }

    [Fact]
    public void Nothing_in_the_library_reads_a_wall_clock_or_waits()
    {
        string src = Path.Combine(RepoRoot(), "src", "M0LTE.OfdmFm");
        string[] banned = ["DateTime", "Stopwatch", "Task.Delay", "Thread.Sleep", "TimeProvider", "System.Threading.Timer", "Environment.TickCount"];
        foreach (string file in Directory.EnumerateFiles(src, "Station*.cs").Concat(Directory.EnumerateFiles(src, "SampleTimers.cs")).Concat(Directory.EnumerateFiles(src, "ChannelAccess.cs")))
        {
            string text = File.ReadAllText(file);
            foreach (string word in banned)
            {
                text.Should().NotContain(word, $"{Path.GetFileName(file)} must not use real time");
            }
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
