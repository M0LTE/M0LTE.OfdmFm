using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Two stations with traffic waiting share one simulated channel, each hearing the other's transmission as busy only after the time its detection takes (modelled explicitly, below),
/// and the rate at which both go at once is counted for the design's original single random delay and for the slotted draw. This is the evidence for the correction to section F of the design
/// (packet-net/pdn-soundmodem#892): a delay of up to 0.4 s does not separate two stations unless the later one can hear the earlier one within it, and by detection alone it cannot.
/// </summary>
public class ChannelAccessCollisionTests
{
    // Each transmission: 1.5 s of audio, with the renderer's lead-in in front of its first training sample.
    private const int BurstSamples = 72000;
    private const int Lead = BurstRenderer.DefaultLeadSamples;

    private sealed class SimStation
    {
        private readonly TestClock _clock;
        private readonly List<(long Start, long End)> _windows = [];

        public SimStation(TestClock clock, int seed, int frameBoundaryPhase)
        {
            _clock = clock;
            Phase = frameBoundaryPhase;
            Access = new ChannelAccess(clock, new Random(seed), _ => { }, Grant, _ => { });
        }

        public ChannelAccess Access { get; }

        public int Phase { get; }

        public SimStation Other { get; set; } = null!;

        public bool External { get; set; }

        public Random Noise { get; set; } = null!;

        public long LastStart { get; private set; } = -1;

        public int Transmissions { get; private set; }

        public bool OnAir(long at)
        {
            foreach ((long start, long end) in _windows)
            {
                if (at >= start - BurstRenderer.SymbolSamples && at < end + Station.MuteAfterOwnTransmission)
                {
                    return true;
                }
            }

            return false;
        }

        private void Grant(ChannelAccess.Request request)
        {
            // The audio layer keys it at once; the start is reported at the same sample.
            _clock.ScheduleAt(_clock.Now, () => Begin(request));
        }

        private void Begin(ChannelAccess.Request request)
        {
            if (request.State != ChannelAccess.RequestState.Granted)
            {
                return;
            }

            long start = _clock.Now;
            long end = start + request.Samples.Length;
            LastStart = start;
            Transmissions++;
            _windows.Add((start, end));
            Access.Started(request, start, false);
            _clock.ScheduleAt(end, () => Access.Ended(request, end));
            Other.Hear(start, end);
        }

        /// <summary>The other station's burst on air from <paramref name="start"/> to <paramref name="end"/>: heard once detection has found it, and by the host's indication if there is one.</summary>
        public void Hear(long start, long end)
        {
            // Detection: the opening is one of the three; the burst's first training sample is a lead-in after the start; the header needs the later of the reference plus a symbol and the opening;
            // and it is noticed at the first frame boundary at or after that (frames every Hop samples, this station's own phase).
            FormatOpening opening = (FormatOpening)Noise.Next(3);
            int need = Math.Max((HeaderReader.TrainingSymbolCount(opening) + 1) * BurstRenderer.SymbolSamples, HeaderReader.OpeningSamples(opening));
            long ready = start + Lead + need;
            long boundary = Phase + ((ready - Phase + StreamingBurstDetector.Hop - 1) / StreamingBurstDetector.Hop * StreamingBurstDetector.Hop);
            boundary.Should().BeLessThanOrEqualTo(start + ChannelAccess.DetectionLatency, "the detection latency is the bound on this model");
            _clock.ScheduleAt(boundary, () =>
            {
                if (!OnAir(_clock.Now))
                {
                    Access.HoldUntil(end);
                }
            });

            if (External)
            {
                // A host's indication follows the carrier within 100 ms (the figure the station takes for its own time to notice) and drops with it.
                long rise = start + Noise.Next(ChannelAccess.ExternalNotice + 1);
                _clock.ScheduleAt(rise, () =>
                {
                    if (!OnAir(_clock.Now))
                    {
                        Access.SetExternal(true);
                    }
                });
                _clock.ScheduleAt(end, () => Access.SetExternal(false));
            }
        }
    }

    /// <summary>Runs <paramref name="rounds"/> rounds and returns how many had both stations on air at once.</summary>
    internal static (int Rounds, int Collisions, double AverageExtraWaitSeconds) Run(int rounds, int seed, AccessMode mode, bool external)
    {
        var clock = new TestClock();
        var rng = new Random(seed);
        var a = new SimStation(clock, rng.Next(), rng.Next(StreamingBurstDetector.Hop)) { External = external, Noise = new Random(rng.Next()) };
        var b = new SimStation(clock, rng.Next(), rng.Next(StreamingBurstDetector.Hop)) { External = external, Noise = new Random(rng.Next()) };
        a.Other = b;
        b.Other = a;
        a.Access.Mode = mode;
        b.Access.Mode = mode;
        if (external)
        {
            a.Access.SetExternal(false);
            b.Access.SetExternal(false);
        }

        clock.AdvanceTo(ChannelAccess.StartUpListen + 10000);
        int collisions = 0;
        double extra = 0;
        var burst = new float[BurstSamples];
        for (int round = 0; round < rounds; round++)
        {
            // A third station's burst, heard by both and ending at the same sample: the channel is busy, then clear for both together, with traffic waiting at each.
            long burstEnd = clock.Now + 60000;
            a.Access.HoldUntil(burstEnd);
            b.Access.HoldUntil(burstEnd);
            clock.AdvanceTo(clock.Now + 1000);
            a.Access.Enqueue(burst, TransmitClass.Ordinary);
            b.Access.Enqueue(burst, TransmitClass.Ordinary);

            int before = a.Transmissions + b.Transmissions;
            long limit = burstEnd + 2_000_000;
            while ((a.Access.WaitingCount > 0 || b.Access.WaitingCount > 0 || a.Access.Active is not null || b.Access.Active is not null) && clock.Now < limit)
            {
                clock.AdvanceTo(clock.Now + 1000);
            }

            (a.Transmissions + b.Transmissions - before).Should().Be(2, "each station sends its one datagram");
            long first = Math.Min(a.LastStart, b.LastStart);
            extra += (first - burstEnd - ChannelAccess.OrdinaryClear) / 48000.0;
            if (Math.Abs(a.LastStart - b.LastStart) < BurstSamples)
            {
                collisions++;
            }

            clock.AdvanceTo(clock.Now + 48000);
        }

        return (rounds, collisions, extra / rounds);
    }

    private const int Rounds = 4000;

    [Fact]
    public void With_detection_alone_the_single_delay_collides_nearly_every_time_and_the_slotted_draw_about_half_as_often()
    {
        (_, int single, double singleWait) = Run(Rounds, 11, AccessMode.SingleDelay, external: false);
        (_, int slotted, double slottedWait) = Run(Rounds, 11, AccessMode.Slotted, external: false);
        Report("detection alone", single, singleWait, slotted, slottedWait);

        single.Should().BeGreaterThan((int)(Rounds * 0.80), "the later station has not heard the earlier one yet");
        slotted.Should().BeInRange((int)(Rounds * 0.44), (int)(Rounds * 0.56), "two stations share a slot half the time, and a station one slot behind always hears the other");
    }

    [Fact]
    public void With_a_carrier_indication_the_slotted_draw_collides_a_quarter_of_the_time_and_the_single_delay_about_as_often()
    {
        (_, int single, double singleWait) = Run(Rounds, 12, AccessMode.SingleDelay, external: true);
        (_, int slotted, double slottedWait) = Run(Rounds, 12, AccessMode.Slotted, external: true);
        Report("carrier indication", single, singleWait, slotted, slottedWait);

        slotted.Should().BeInRange((int)(Rounds * 0.20), (int)(Rounds * 0.30));
        single.Should().BeInRange((int)(Rounds * 0.10), (int)(Rounds * 0.40));
    }

    [Fact]
    public void A_long_run_pins_the_rates_down_further()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1 for 40000 rounds of each rule, with and without a carrier indication");
        const int rounds = 40000;
        (_, int singleDetect, _) = Run(rounds, 21, AccessMode.SingleDelay, external: false);
        (_, int slottedDetect, _) = Run(rounds, 21, AccessMode.Slotted, external: false);
        (_, int singleExternal, _) = Run(rounds, 22, AccessMode.SingleDelay, external: true);
        (_, int slottedExternal, _) = Run(rounds, 22, AccessMode.Slotted, external: true);

        singleDetect.Should().BeGreaterThan((int)(rounds * 0.95));
        ((double)slottedDetect / rounds).Should().BeApproximately(0.5, 0.01, "one slot in two, and the later of two different slots always hears the earlier");
        ((double)slottedExternal / rounds).Should().BeApproximately(0.25, 0.015);
        ((double)singleExternal / rounds).Should().BeInRange(0.15, 0.35);
    }

    [Fact]
    public void The_same_seed_gives_the_same_collision_count()
    {
        Run(300, 5, AccessMode.Slotted, false).Should().Be(Run(300, 5, AccessMode.Slotted, false));
        Run(300, 5, AccessMode.Slotted, false).Collisions.Should().NotBe(Run(300, 6, AccessMode.Slotted, false).Collisions);
    }

    private static void Report(string label, int single, double singleWait, int slotted, double slottedWait)
    {
        string line = $"{label}: single delay {single}/{Rounds} collided (extra wait {singleWait:F3} s), slotted {slotted}/{Rounds} (extra wait {slottedWait:F3} s)";
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("SM_ACCESS_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
