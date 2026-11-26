using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Pushes a stream into a station in blocks, stopping at the samples a script names so that the audio layer's reports arrive with the clock exactly there,
/// and records what the station says: every change of the channel, every request and withdrawal, every access event, and the bursts and holds it learned of.
/// </summary>
internal sealed class StreamDriver
{
    private readonly float[] _stream;
    private readonly List<(long Index, long Order, Action Action)> _script = [];
    private long _order;

    public StreamDriver(Station station, float[]? stream = null, bool autoPlay = false, int playLength = 3000)
    {
        Station = station;
        _stream = stream ?? [];
        station.ChannelBusy += e => Busy.Add((station.SampleCount, e.Busy));
        station.BurstKnown = (now, end) => Known.Add((now, end));
        station.AccessChanged += e => Access.Add((station.SampleCount, e));
        station.TransmitRequest += r =>
        {
            Requests.Add((station.SampleCount, r));
            if (autoPlay)
            {
                long start = r.EarliestSample;
                At(start, () => station.TransmitStarted(r.Id, start));
                At(start + playLength, () => station.TransmitEnded(r.Id, start + playLength));
            }
        };
        station.WithdrawRequest += w => Withdrawals.Add((station.SampleCount, w.Id));
    }

    public Station Station { get; }

    /// <summary>The id of the last request a test made through a script, for it to read back.</summary>
    public long Enqueued { get; set; }

    /// <summary>Each change of the channel, with the clock when it was raised.</summary>
    public List<(long At, bool Busy)> Busy { get; } = [];

    /// <summary>Each burst or hold the station learned of: the clock when it did and the sample the channel is busy until.</summary>
    public List<(long Now, long Until)> Known { get; } = [];

    public List<(long At, AccessEvent Event)> Access { get; } = [];

    public List<(long At, TransmitRequestEvent Request)> Requests { get; } = [];

    public List<(long At, long Id)> Withdrawals { get; } = [];

    /// <summary>The clock at which the request with this access id was granted.</summary>
    public long GrantedAt(long accessId) => Access.Last(a => a.Event.Id == accessId && a.Event.Stage == AccessStage.Granted).At;

    public void At(long index, Action action) => _script.Add((index, _order++, action));

    /// <summary>Pushes until the clock reads <paramref name="target"/>, in blocks the function gives, cut at each scripted index.</summary>
    public void PushTo(long target, Func<int> block)
    {
        for (;;)
        {
            foreach (var due in _script.Where(s => s.Index <= Station.SampleCount).OrderBy(s => s.Index).ThenBy(s => s.Order).ToList())
            {
                _script.Remove(due);
                due.Action();
            }

            if (Station.SampleCount >= target)
            {
                return;
            }

            long stop = target;
            if (_script.Count > 0)
            {
                stop = Math.Min(stop, _script.Min(s => s.Index));
            }

            int take = (int)Math.Min(Math.Min(block(), stop - Station.SampleCount), int.MaxValue);
            take = Math.Max(1, take);
            long at = Station.SampleCount;
            var slice = new float[take];
            for (int i = 0; i < take; i++)
            {
                long k = at + i;
                slice[i] = k < _stream.Length ? _stream[k] : 0f;
            }

            Station.Push(slice);
        }
    }
}

/// <summary>Step 4 at the station: the busy causes at their exact samples, whatever the block size, and the waits and withdrawals of channel access, driven by pushing audio.</summary>
public class StationAccessTests
{
    private static readonly long Start = 6 * 48000L;

    private static Func<int> Fixed(int n) => () => n;

    private static Func<int> Seeded(int seed)
    {
        var rng = new Random(seed);
        return () => Math.Max(1, (int)Math.Exp(rng.NextDouble() * Math.Log(30000)));
    }

    /// <summary>A positive size is a fixed block; zero is seeded random sizes.</summary>
    private static Func<int> Blocks(int block) => block > 0 ? Fixed(block) : Seeded(4);

    /// <summary>As <see cref="Blocks"/>, with seeded sizes under 1500: the audio layer's report of the end of one transmission must reach the station before the next one's turn comes, 7200 samples on.</summary>
    private static Func<int> SmallBlocks(int block)
    {
        if (block > 0)
        {
            return Fixed(block);
        }

        var rng = new Random(4);
        return () => rng.Next(1, 1500);
    }

    private static Station Make(int seed = 1) => new("M0LTE", 1, new Random(seed), runJobsInline: true);

    private static (float[] Stream, Placed Placed) OneDataBurst()
    {
        var builder = new StreamBuilder().Silence(6.0);
        Placed placed = builder.Add(TestBurst.Data(7, 1, 0, [TestBurst.Datagram(3, 200)]));
        builder.Silence(3.0);
        return (builder.ToArray(), placed);
    }

    // ---- busy causes ----

    [Theory]
    [InlineData(480, 0)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    public void A_burst_in_progress_is_busy_from_the_frame_that_finds_it_to_its_last_sample(int block, int seed)
    {
        (float[] stream, Placed placed) = OneDataBurst();
        Station station = Make();
        var driver = new StreamDriver(station, stream);
        driver.PushTo(stream.Length, block > 0 ? Fixed(block) : Seeded(seed));

        (long now, long end) = driver.Known.Should().ContainSingle().Subject;
        end.Should().BeCloseTo(placed.End, 4, "the burst's length comes from its header");
        now.Should().BeInRange(placed.Start + 4224, placed.Start + 4224 + StreamingBurstDetector.Hop + 2112, "a frame boundary at or after the opening is in");
        now.Should().BeLessThan(end);
        driver.Busy.Should().Equal([(now, true), (end, false)]);
        station.IsChannelBusy.Should().BeFalse();
    }

    [Fact]
    public void The_same_burst_gives_the_same_busy_log_at_every_block_size()
    {
        (float[] stream, _) = OneDataBurst();
        var logs = new List<(long, bool)[]>();
        foreach (Func<int> block in new[] { Fixed(480), Fixed(4096), Seeded(21) })
        {
            var driver = new StreamDriver(Make(), stream);
            driver.PushTo(stream.Length, block);
            logs.Add([.. driver.Busy]);
        }

        logs[1].Should().Equal(logs[0]);
        logs[2].Should().Equal(logs[0]);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void A_candidate_whose_header_will_not_read_is_busy_for_its_opening_and_a_second_more_then_judged_again(int block)
    {
        var builder = new StreamBuilder().Silence(6.0);
        Placed placed = builder.Add(TestBurst.Report(7, 1, 80, false, FormatOpening.Full));
        builder.Silence(4.0);
        float[] stream = builder.ToArray();

        // Wreck the header symbol (the one after the training symbol) with noise of its own strength; the training symbol, which is what the detector correlates, is left alone.
        long from = placed.Start + 2112;
        double power = 0;
        for (long i = from; i < from + 2112; i++)
        {
            power += stream[i] * stream[i];
        }

        double rms = Math.Sqrt(power / 2112);
        var rng = new Random(77);
        for (long i = from; i < from + 2112; i++)
        {
            stream[i] = (float)(rms * 1.7 * (rng.NextDouble() - 0.5) * 2);
        }

        Station station = Make();
        var driver = new StreamDriver(station, stream);
        driver.PushTo(stream.Length, Blocks(block));

        station.NotedHeaders.Should().BeEmpty("no header read");
        driver.Known.Should().NotBeEmpty();
        (long now, long until) = driver.Known[0];
        (until - 48000 - 4224).Should().BeCloseTo(placed.Start, 8, "the hold counts from the candidate's first sample: its opening (4224) and a second");
        driver.Busy.Should().HaveCount(2);
        driver.Busy[0].Should().Be((now, true));
        driver.Busy[1].Should().Be((driver.Known.Max(k => k.Until), false));
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void A_header_of_an_unknown_version_holds_the_channel_a_second_at_a_time_while_candidates_keep_arriving(int block)
    {
        Station station = Make();
        var driver = new StreamDriver(station);
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        var unknown = new BurstHeader(3, BurstKind.Data, 0, 0, 0, 0, 0);
        driver.PushTo(Start, Blocks(block));
        driver.At(Start + 20000, () => station.TakeInHeader(Start + 20000 - 9000, FormatOpening.Full, unknown, bits));
        driver.At(Start + 50000, () => station.TakeInHeader(Start + 50000 - 9000, FormatOpening.Full, unknown, bits));
        driver.At(Start + 60000, () => station.TakeInHeader(Start + 60000 - 9000, FormatOpening.Full, unknown, bits));
        driver.PushTo(Start + 400000, Blocks(block));

        // 20000 + 48000 = 68000 would have ended it; the third at 60000 carries it to 108000.
        driver.Busy.Should().Equal([(Start + 20000, true), (Start + 108000, false)]);
        station.NotedHeaders.Should().HaveCount(3).And.OnlyContain(n => n.Reason == Station.NoteReason.UnknownVersion);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void An_unknown_burst_type_under_a_known_version_holds_the_channel_for_the_length_it_carries(int block)
    {
        Station station = Make();
        var driver = new StreamDriver(station);
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        var ident = new BurstHeader(0, BurstKind.Identification, 5, 0, 12, 40, 0);
        driver.PushTo(Start, Blocks(block));
        long index = Start - 3000;
        driver.At(Start + 100, () => station.TakeInHeader(index, FormatOpening.Full, ident, bits));
        driver.PushTo(Start + 200000, Blocks(block));

        driver.Busy.Should().Equal([(Start + 100, true), (index + 4224 + (40 * 2112), false)]);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(37)]
    public void An_external_indication_is_busy_while_set_and_for_100_ms_after_whatever_the_block_size(int block)
    {
        Station station = Make();
        var driver = new StreamDriver(station);
        driver.At(Start, () => station.SetExternalBusy(true));
        driver.At(Start + 100, () => station.SetExternalBusy(true));
        driver.At(Start + 10000, () => station.SetExternalBusy(false));
        driver.At(Start + 14000, () => station.SetExternalBusy(true));
        driver.At(Start + 15000, () => station.SetExternalBusy(false));
        driver.PushTo(Start + 40000, Fixed(block));

        driver.Busy.Should().Equal([(Start, true), (Start + 15000 + 4800, false)]);
    }

    [Fact]
    public void The_external_indication_and_a_burst_together_are_one_busy_period()
    {
        Station station = Make();
        var driver = new StreamDriver(station);
        driver.PushTo(Start, Fixed(480));
        driver.At(Start + 1000, () => station.NoteBurst(Start + 20000));
        driver.At(Start + 5000, () => station.SetExternalBusy(true));
        driver.At(Start + 18000, () => station.SetExternalBusy(false));
        driver.PushTo(Start + 80000, Seeded(9));

        driver.Busy.Should().Equal([(Start + 1000, true), (Start + 22800, false)]);
        driver.Known.Should().Equal([(Start + 1000, Start + 20000)]);
    }

    [Fact]
    public void A_handler_cannot_call_back_into_the_station()
    {
        Station station = Make();
        station.ChannelBusy += _ => station.SetExternalBusy(true);
        station.Push(new float[100]);

        ((Action)(() => station.NoteBurst(1_000_000))).Should().Throw<InvalidOperationException>();
    }

    // ---- the station's own transmission ----

    /// <summary>Silence up to the sample where the burst's first training sample goes, then the burst.</summary>
    private static Placed PlaceAt(StreamBuilder builder, TestBurst burst, long firstSample)
    {
        long silence = firstSample - burst.Lead - builder.Count;
        silence.Should().BeGreaterThanOrEqualTo(0);
        builder.Silence((silence + 0.5) / 48000.0);
        Placed placed = builder.Add(burst);
        placed.Start.Should().Be(firstSample);
        return placed;
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void Audio_in_the_window_of_the_stations_own_transmission_is_ignored_and_audio_after_it_is_not(int block)
    {
        // The station sends from 6.0 s for 60000 samples. Bursts begin 1000 before its start, inside it, 300 after its end (inside the 4800 of mute), and 30000 after its end.
        long txStart = Start;
        long txEnd = txStart + 60000;
        var builder = new StreamBuilder();
        TestBurst report = TestBurst.Report(7, 1, 80, false, FormatOpening.Full);
        PlaceAt(builder, report, txStart - 1000);
        PlaceAt(builder, report, txStart + 15000);
        PlaceAt(builder, report, txEnd + 300);
        Placed late = PlaceAt(builder, report, txEnd + 30000);
        builder.Silence(4.0);
        float[] stream = builder.ToArray();

        Station station = Make();
        var driver = new StreamDriver(station, stream, autoPlay: true, playLength: 60000);
        driver.PushTo(Start, Blocks(block));
        long id = station.Enqueue(new float[60000], TransmitClass.Expedited);
        driver.PushTo(stream.Length, Blocks(block));

        driver.GrantedAt(id).Should().Be(txStart);
        station.CandidatesMuted.Should().BeGreaterThanOrEqualTo(3);
        driver.Known.Should().ContainSingle("only the burst after the window is learned of");
        driver.Known[0].Until.Should().BeCloseTo(late.End, 4);
        long noticed = driver.Known[0].Now;
        driver.Busy.Should().Equal([(txStart, true), (txEnd + 4800, false), (noticed, true), (noticed, false)], "the report was over before it was found: busy while it was judged");
        station.PausedSamples.Should().BeGreaterThanOrEqualTo(60000 + Station.MuteAfterOwnTransmission);
    }

    // ---- waits ----

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void Requests_made_while_a_burst_is_received_wait_for_its_last_sample_and_go_in_order_of_class_each_at_its_exact_sample(int block)
    {
        const int seed = 2;
        (float[] stream, _) = OneDataBurst();
        Station station = Make(seed);
        var driver = new StreamDriver(station, stream, autoPlay: true, playLength: 3000);
        long ordinary = 0, expedited = 0, report = 0;
        bool queued = false;
        station.BurstKnown = (now, end) =>
        {
            driver.Known.Add((now, end));
            if (!queued)
            {
                queued = true;
                driver.At(now + 1, () =>
                {
                    ordinary = station.Enqueue(new float[3000], TransmitClass.Ordinary);
                    expedited = station.Enqueue(new float[3000], TransmitClass.Expedited);
                    report = station.Enqueue(new float[3000], TransmitClass.Report);
                });
            }
        };

        driver.PushTo(stream.Length, SmallBlocks(block));

        long end = driver.Known[0].Until;
        driver.Access.Where(a => a.Event.Stage == AccessStage.Granted).Select(a => a.Event.Id).Should().Equal(report, expedited, ordinary);
        long reportStart = end + 2400;
        driver.GrantedAt(report).Should().Be(reportStart, "a report goes 50 ms after the burst's last sample");
        long expeditedStart = reportStart + 3000 + 4800 + 2400;
        driver.GrantedAt(expedited).Should().Be(expeditedStart, "50 ms after the channel was last busy: the end of the last, and its tail");
        int slots = new Random(seed).Next(2);
        long ordinaryStart = expeditedStart + 3000 + 4800 + 38400 + (slots * Station.DetectionLatencySamples);
        driver.GrantedAt(ordinary).Should().Be(ordinaryStart, "the first-slot time and the slots the seed draws, each the detection latency");
        station.AccessWaiting.Should().Be(0);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void An_ordinary_request_with_an_external_indication_supplied_uses_the_short_slot(int block)
    {
        const int seed = 6;
        Station station = Make(seed);
        var driver = new StreamDriver(station, null, autoPlay: true);
        driver.PushTo(Start, Blocks(block));
        station.SetExternalBusy(false);
        driver.At(Start + 100, () => station.NoteBurst(Start + 90000));
        driver.At(Start + 200, () => driver.Enqueued = station.Enqueue(new float[2000], TransmitClass.Ordinary));
        driver.PushTo(Start + 400000, Blocks(block));

        int slots = new Random(seed).Next(4);
        driver.GrantedAt(driver.Enqueued).Should().Be(Start + 90000 + 38400 + (slots * 4800));
        station.NoticeSamples.Should().Be(4800);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void A_request_handed_over_and_not_started_is_withdrawn_when_a_burst_turns_up_and_granted_again_when_it_is_over(int block)
    {
        Station station = Make(3);
        var driver = new StreamDriver(station);
        driver.PushTo(Start, Blocks(block));
        long id = station.Enqueue(new float[2000], TransmitClass.Expedited);
        driver.GrantedAt(id).Should().Be(Start);
        driver.PushTo(Start + 5000, Blocks(block));
        driver.Withdrawals.Should().BeEmpty("the station's own request makes the channel busy, but does not take itself back");

        driver.At(Start + 7000, () => station.NoteBurst(Start + 60000));
        driver.PushTo(Start + 300000, Blocks(block));

        driver.Withdrawals.Should().Equal([(Start + 7000, driver.Requests[0].Request.Id)]);
        driver.Requests.Should().HaveCount(2);
        driver.Requests[1].At.Should().Be(Start + 60000 + 2400);
        driver.Requests[1].Request.Id.Should().NotBe(driver.Requests[0].Request.Id);
        driver.Access.Select(a => a.Event.Stage).Should().Equal(AccessStage.Granted, AccessStage.Withdrawn, AccessStage.Granted);
        driver.Busy.Select(b => b.Busy).Should().Equal(true, false, true);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void A_burst_that_turns_up_while_a_request_is_handed_over_withdraws_it_and_the_burst_is_real_audio(int block)
    {
        (float[] stream, _) = OneDataBurst();
        Station station = Make(3);
        var driver = new StreamDriver(station, stream);
        driver.PushTo(Start + 8000, Blocks(block));
        long id = station.Enqueue(new float[2000], TransmitClass.Expedited);
        driver.PushTo(stream.Length, Blocks(block));

        // Granted at once, as the channel looked clear; the burst begun at 6.0 s is found and the request goes back; it goes again 50 ms after the burst's end.
        driver.Withdrawals.Should().ContainSingle();
        driver.Withdrawals[0].At.Should().Be(driver.Known[0].Now);
        driver.Requests.Should().HaveCount(2);
        driver.Requests[1].At.Should().Be(driver.Known[0].Until + 2400);
        driver.GrantedAt(id).Should().Be(driver.Known[0].Until + 2400);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void A_request_that_starts_after_it_was_withdrawn_is_on_air_and_is_not_asked_for_again(int block)
    {
        Station station = Make(3);
        var driver = new StreamDriver(station);
        driver.PushTo(Start, Blocks(block));
        long id = station.Enqueue(new float[2000], TransmitClass.Expedited);
        driver.PushTo(Start + 1000, Blocks(block));
        driver.At(Start + 2000, () => station.NoteBurst(Start + 50000));
        driver.PushTo(Start + 2100, Blocks(block));
        driver.Withdrawals.Should().HaveCount(1);
        long firstHandshake = driver.Requests[0].Request.Id;

        // The audio layer had keyed it before it saw the withdrawal.
        station.TransmitStarted(firstHandshake, Start + 1500);
        driver.PushTo(Start + 200000, Blocks(block));

        station.AccessWaiting.Should().Be(0);
        driver.Requests.Should().HaveCount(1, "it is on air; it is not asked for again");
        driver.Access.Select(a => a.Event.Stage).Should().Equal(AccessStage.Granted, AccessStage.Withdrawn, AccessStage.Started);
        station.StartedAfterWithdrawal(firstHandshake).Should().BeTrue();
        id.Should().BeGreaterThan(0);
    }

    // ---- after a lone report ----

    [Fact]
    public void Expedited_traffic_after_a_lone_report_for_the_station_is_not_held()
    {
        var builder = new StreamBuilder().Silence(6.0);
        builder.Add(TestBurst.Report(7, 1, 80, false, FormatOpening.Full));
        builder.Silence(5.0);
        float[] stream = builder.ToArray();
        Station station = Make();
        var driver = new StreamDriver(station, stream);
        station.BurstKnown = (now, end) =>
        {
            driver.Known.Add((now, end));
            driver.At(end + 30000, () => driver.Enqueued = station.Enqueue(new float[2000], TransmitClass.Expedited));
        };
        driver.PushTo(stream.Length, Fixed(480));

        long end = driver.Known.Should().ContainSingle().Subject.Until;
        driver.GrantedAt(driver.Enqueued).Should().BeInRange(end + 30000, end + 30000 + 480, "it goes when it is asked for: the channel has been clear for longer than 50 ms");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Ordinary_traffic_after_a_lone_report_goes_at_a_second_or_at_its_own_wait_if_that_is_later(int slots)
    {
        int seed = Enumerable.Range(1, 200).First(s => new Random(s).Next(2) == slots);
        var builder = new StreamBuilder().Silence(6.0);
        Placed placed = builder.Add(TestBurst.Report(7, 1, 80, false, FormatOpening.Full));
        builder.Silence(5.0);
        float[] stream = builder.ToArray();

        Station station = Make(seed);
        var driver = new StreamDriver(station, stream);
        long ordinary = 0;
        station.BurstKnown = (now, end) =>
        {
            driver.Known.Add((now, end));
            driver.At(end + 30000, () => ordinary = station.Enqueue(new float[2000], TransmitClass.Ordinary));
        };
        driver.PushTo(stream.Length, Fixed(480));

        long end = driver.Known.Should().ContainSingle().Subject.Until;
        end.Should().BeCloseTo(placed.End, 4);
        long expected = Math.Max(end + 48000, end + 38400 + (slots * Station.DetectionLatencySamples));
        driver.GrantedAt(ordinary).Should().Be(expected);
        if (slots == 0)
        {
            expected.Should().Be(end + 48000, "the hold-off of a second is later than 0.8 s");
        }
        else
        {
            expected.Should().Be(end + 38400 + Station.DetectionLatencySamples);
        }
    }

    [Fact]
    public void A_report_for_another_station_does_not_hold_ordinary_traffic_off()
    {
        var builder = new StreamBuilder().Silence(6.0);
        builder.Add(TestBurst.Report(7, 9, 80, false, FormatOpening.Full));
        builder.Silence(5.0);
        float[] stream = builder.ToArray();
        int seed = Enumerable.Range(1, 200).First(s => new Random(s).Next(2) == 0);
        Station station = Make(seed);
        var driver = new StreamDriver(station, stream);
        long ordinary = 0;
        station.BurstKnown = (now, end) =>
        {
            driver.Known.Add((now, end));
            driver.At(end + 30000, () => ordinary = station.Enqueue(new float[2000], TransmitClass.Ordinary));
        };
        driver.PushTo(stream.Length, Fixed(480));

        driver.GrantedAt(ordinary).Should().Be(driver.Known[0].Until + 38400);
    }

    // ---- start-up and allocation ----

    [Fact]
    public void The_station_sends_nothing_in_its_first_five_seconds()
    {
        Station station = Make();
        var driver = new StreamDriver(station);
        long id = station.Enqueue(new float[100], TransmitClass.Report);
        driver.PushTo(Start, Fixed(480));

        driver.GrantedAt(id).Should().Be((5 * 48000) + 2400);
    }

    [Fact]
    public void Pushing_quiet_audio_allocates_nothing_with_requests_waiting_and_a_hold_in_force()
    {
        Station station = Make();
        var block = new float[480];
        station.Push(new float[(int)Start]);
        station.SetExternalBusy(true);
        station.Enqueue(new float[100], TransmitClass.Ordinary);
        station.NoteBurst(Start + 60_000_000);
        for (int i = 0; i < 30; i++)
        {
            station.Push(block);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            station.Push(block);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

}
