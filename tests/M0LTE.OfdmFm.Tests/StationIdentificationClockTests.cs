using System.Globalization;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// One station and a stand-in for its audio layer: it answers each transmit request the moment its earliest sample comes, plays it to its end and says so, and everything the station raises is logged with the clock it was raised at.
/// The station hears silence, so only its own transmissions matter. Nothing waits: the clock is stepped.
/// </summary>
internal sealed class IdentDriver
{
    private const int Block = 4800;
    private readonly float[] _zeros = new float[Block];
    private (long Id, int Length, long Earliest)? _plan;
    private long _id;
    private long _airEnd;

    public IdentDriver(long intervalSamples, string callsign = "M0LTE", int number = 1, int seed = 1)
    {
        Station = new Station(callsign, number, new Random(seed), runJobsInline: true);
        if (intervalSamples > 0)
        {
            Station.IdentificationInterval = intervalSamples;
        }

        Station.TransmitRequest += e =>
        {
            Log.Add((Station.SampleCount, "request", e.Samples.Length));
            Requests.Add((Station.SampleCount, e.Samples.Length));
            _plan = (e.Id, e.Samples.Length, e.EarliestSample);
        };
        Station.WithdrawRequest += e =>
        {
            Log.Add((Station.SampleCount, "withdraw", 0));
            if (_plan is { } p && p.Id == e.Id)
            {
                _plan = null;
            }
        };
        Station.IdentificationDue += e => Log.Add((Station.SampleCount, "due", (long)Math.Round(e.Seconds * Station.SampleRate)));
        Station.ModemSent += e => Log.Add((Station.SampleCount, "modem " + e.Kind, 0));
        Station.Transmitted += e => Log.Add((Station.SampleCount, "transmitted " + e.Tag.Value, 0));
    }

    public Station Station { get; }

    public List<(long Clock, string What, long Value)> Log { get; } = [];

    /// <summary>Each transmit request: the clock it was raised at and its length.</summary>
    public List<(long Clock, int Length)> Requests { get; } = [];

    /// <summary>Each transmission: where it started and ended.</summary>
    public List<(long Start, long End, int Length)> Air { get; } = [];

    private readonly List<(long At, Action Do)> _actions = [];

    public void At(long sample, Action action) => _actions.Add((sample, action));

    /// <summary>Steps the clock to <paramref name="target"/>, answering requests as they come.</summary>
    public void RunTo(long target)
    {
        while (Station.SampleCount < target)
        {
            long stop = Math.Min(target, Station.SampleCount + Block);
            if (Station.NextTimerDue is { } due && due > Station.SampleCount)
            {
                stop = Math.Min(stop, due);
            }

            if (_airEnd > Station.SampleCount)
            {
                stop = Math.Min(stop, _airEnd);
            }

            if (_plan is { } p && p.Earliest > Station.SampleCount)
            {
                stop = Math.Min(stop, p.Earliest);
            }

            foreach ((long at, _) in _actions)
            {
                if (at > Station.SampleCount)
                {
                    stop = Math.Min(stop, at);
                }
            }

            stop = Math.Min(stop, Station.SampleCount + Station.SamplesToNextFrame);
            Station.Push(_zeros.AsSpan(0, (int)(stop - Station.SampleCount)));
            Settle();
        }
    }

    private void Settle()
    {
        bool again;
        do
        {
            again = false;
            if (_airEnd != 0 && Station.SampleCount >= _airEnd && Station.IsTransmitting)
            {
                Station.TransmitEnded(_id, _airEnd);
                Log.Add((_airEnd, "end", 0));
                again = true;
            }

            if (!Station.IsTransmitting && _plan is { } p && Station.SampleCount >= p.Earliest)
            {
                _id = p.Id;
                Station.TransmitStarted(p.Id, Station.SampleCount);
                _airEnd = Station.SampleCount + p.Length;
                Air.Add((Station.SampleCount, _airEnd, p.Length));
                Log.Add((Station.SampleCount, "start", p.Length));
                _plan = null;
                again = true;
            }

            for (int i = 0; i < _actions.Count; i++)
            {
                if (_actions[i].At <= Station.SampleCount)
                {
                    Action a = _actions[i].Do;
                    _actions.RemoveAt(i);
                    a();
                    again = true;
                    break;
                }
            }
        }
        while (again);
    }

    public void SendToEveryone(int tag, int length = 30, SendPriority priority = SendPriority.Ordinary) =>
        Station.Send(TestBurst.Datagram(tag, length), StationName.Everyone, new HostTag(tag), false, priority).Should().Be(SendResult.Accepted);

    public IEnumerable<long> Clocks(string what) => Log.Where(l => l.What == what).Select(l => l.Clock);

    public static string Text(IEnumerable<(long Clock, string What, long Value)> log) =>
        string.Join("\n", log.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Clock} {l.What} {l.Value}")));
}

/// <summary>
/// The identification clock (step 7). It runs on the sample clock, only while the station transmits, and these tests step it with a shorter interval (a seam: <c>Station.IdentificationInterval</c>) so that a run is seconds and not ten minutes of audio through the detector.
/// The seam is sound because the interval is read once, when a transmission starts the count, and goes into the due sample of one timer; the real interval is pinned by a test of its own and run end to end behind <c>SM_MASK_GATE=1</c>.
/// </summary>
public class StationIdentificationClockTests
{
    /// <summary>Twelve seconds: longer than the station's own start-up listen (5 s) and a burst, short enough to step.</summary>
    private const long Interval = 12L * 48000;

    [Fact]
    public void The_Interval_Is_Ten_Minutes_Of_The_Sample_Clock_And_The_Count_Starts_At_The_First_Transmission()
    {
        Station.IdentificationIntervalSamples.Should().Be(28_800_000);
        var d = new IdentDriver(0);
        d.Station.IdentificationInterval.Should().Be(28_800_000);
        d.Station.IdentificationResting.Should().BeTrue();
        d.Station.IdentificationDueAt.Should().BeNull();
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(10 * 48000);
        d.Air.Should().ContainSingle();
        d.Station.IdentificationDueAt.Should().Be(d.Air[0].Start + 28_800_000);
        d.Station.NextTimerDue.Should().Be(d.Station.IdentificationDueAt, "one timer, at the due sample");
    }

    [Fact]
    public void A_Station_That_Never_Transmits_Never_Identifies()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(4 * Interval);
        d.Log.Should().BeEmpty();
        d.Station.IdentificationResting.Should().BeTrue();
        d.Station.PendingTimers.Should().Be(0, "the clock is not even counting");
    }

    [Fact]
    public void A_Station_That_Transmits_Once_Identifies_Once_An_Interval_After_That_First_Transmission_And_Then_Rests()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(5 * Interval);

        d.Air.Should().HaveCount(2, "the datagram, then the identification");
        long first = d.Air[0].Start;
        d.Clocks("due").Should().Equal(first + Interval);
        d.Log.Single(l => l.What == "due").Value.Should().Be(first + Interval, "the event's time is the due sample");
        d.Clocks("modem Identification").Should().ContainSingle();
        d.Station.IdentificationsSent.Should().Be(1);
        d.Station.IdentificationResting.Should().BeTrue("the identification burst did not start the count again");
        d.Station.PendingTimers.Should().Be(0);
        d.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void Each_Identification_Raises_Due_Once_Then_Asks_Then_Starts_Then_Says_Modem_Sent_Once_In_That_Order()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(3 * Interval);

        string[] order = [.. d.Log.Select(l => l.What).SkipWhile(w => w != "due")];
        order.Should().Equal("due", "request", "modem Identification", "start", "end"); // ModemSent is raised inside the audio layer's call to TransmitStarted, which the driver logs after it returns
        d.Station.IdentificationsDue.Should().Be(1);
    }

    [Fact]
    public void A_Station_That_Keeps_Transmitting_Identifies_Every_Interval_From_The_First_Transmission_After_The_Last_Identification()
    {
        const long interval = Interval;
        var d = new IdentDriver(interval);
        d.RunTo(6 * 48000);
        for (int i = 0; i < 10; i++)
        {
            d.SendToEveryone(i);
            d.RunTo((6 + (8 * (i + 1))) * 48000L);
        }

        d.RunTo(90 * 48000L);
        List<long> due = [.. d.Clocks("due")];
        List<long> sent = [.. d.Log.Where(l => l.What == "modem Identification").Select(l => l.Clock)];
        due.Count.Should().BeGreaterThanOrEqualTo(4);
        sent.Should().HaveCount(due.Count, "one burst for each time it fell due");

        // The first is an interval after the first transmission. Each later one is an interval after the first transmission that started after the previous identification burst began.
        due[0].Should().Be(d.Air[0].Start + interval);
        int identLength = IdentLength("M0LTE");
        for (int i = 1; i < due.Count; i++)
        {
            long next = d.Air.First(a => a.Start > sent[i - 1] && a.Length != identLength).Start;
            due[i].Should().Be(next + interval);
            (due[i] - sent[i - 1]).Should().BeGreaterThan(interval).And.BeLessThan(interval + (15L * 48000), "a datagram every 8 s follows each identification closely");
        }
    }

    [Fact]
    public void The_Identification_Burst_Does_Not_Restart_The_Clock_But_A_Later_Datagram_Does()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(2 * Interval);
        long identStart = d.Air[^1].Start;
        d.Station.IdentificationResting.Should().BeTrue();
        d.RunTo(identStart + (2 * Interval));
        d.Air.Should().HaveCount(2, "an idle station does not identify again and again");
        d.SendToEveryone(2);
        d.RunTo(identStart + (4 * Interval));
        d.Air.Should().HaveCount(4, "a new datagram started the count again");
        d.Clocks("due").Last().Should().Be(d.Air[2].Start + Interval);
    }

    [Fact]
    public void An_Identification_Owed_While_The_Channel_Is_Busy_For_A_Long_Time_Waits_Is_Not_Raised_Twice_And_Goes_When_The_Channel_Clears()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(10 * 48000);
        long due = d.Air[0].Start + Interval;
        long freed = due + (400L * 48000);
        d.At(due - 1000, () => d.Station.SetExternalBusy(true));
        d.At(freed, () => d.Station.SetExternalBusy(false));
        d.RunTo(freed + 12 * 48000);

        d.Clocks("due").Should().Equal(due);
        d.Requests.Should().HaveCount(2);
        d.Air.Should().HaveCount(2);
        d.Air[1].Start.Should().Be(freed + 4800 + 2400, "100 ms after the indication drops, then the first slot's 50 ms");
        d.Station.IdentificationsDue.Should().Be(1);
        d.Station.IdentificationOwed.Should().BeFalse();
        d.Station.IdentificationResting.Should().BeTrue();
    }

    [Fact]
    public void The_Identification_Goes_Ahead_Of_Queued_Datagrams_By_Class_And_Waits_Its_Turn_Behind_A_Burst_In_Progress()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(12 * 48000);
        long due = d.Air[0].Start + Interval;
        long burstEnd = due + (30L * 48000);
        d.At(due - 100, () => d.Station.NoteBurst(burstEnd));
        d.At(due + 200, () => d.SendToEveryone(2));
        d.At(due + 300, () => d.SendToEveryone(3, priority: SendPriority.Expedited));
        d.RunTo(burstEnd + (11L * 48000));

        d.Air.Should().HaveCount(4, "the first datagram, the identification, then the expedited datagram and the ordinary one queued while it was owed");
        d.Air[2].Start.Should().BeGreaterThan(d.Air[1].End);
        d.Air[1].Start.Should().Be(burstEnd + 2400, "not while another station's burst is on the air, and the channel clear 50 ms after it");
        d.Air[1].Length.Should().Be(IdentLength("M0LTE"), "the identification is first, ahead of the datagrams queued after it fell due");
        string[] after = [.. d.Log.Select(l => l.What).SkipWhile(w => w != "due")];
        after.Where(w => w.StartsWith("modem", StringComparison.Ordinal) || w.StartsWith("transmitted", StringComparison.Ordinal)).Should().Equal("modem Identification", "transmitted 3", "transmitted 2");
    }

    [Fact]
    public void An_Identification_That_Falls_Due_Inside_The_Stations_Own_Burst_Goes_After_It_Ends_Plus_The_Tail_And_The_First_Slot()
    {
        var d = new IdentDriver(24000);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1, length: 200);
        d.RunTo(60 * 48000);

        d.Air.Should().HaveCount(2);
        long start = d.Air[0].Start;
        d.Clocks("due").Should().Equal(start + 24000);
        d.Air[0].End.Should().BeGreaterThan(start + 24000, "it fell due while the datagram was on the air");
        d.Air[1].Start.Should().Be(d.Air[0].End + 4800 + 2400);
    }

    [Fact]
    public void Identical_Runs_Give_Identical_Logs()
    {
        string Run()
        {
            var d = new IdentDriver(Interval, seed: 7);
            d.RunTo(6 * 48000);
            for (int i = 0; i < 6; i++)
            {
                d.SendToEveryone(i);
                d.RunTo((6 + (15 * (i + 1))) * 48000L);
            }

            return IdentDriver.Text(d.Log);
        }

        string once = Run();
        once.Should().Contain("modem Identification");
        Run().Should().Be(once);
    }

    [Fact]
    public void The_Whole_Ten_Minutes_Run_Through_The_Detector_Give_One_Identification_Exactly_Ten_Minutes_After_The_First_Transmission()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector, twice");
        var d = new IdentDriver(0);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(9 * 60 * 48000L);
        d.Clocks("due").Should().BeEmpty("nine minutes after the first transmission is not ten");
        d.RunTo(11 * 60 * 48000L);
        long first = d.Air[0].Start;
        d.Clocks("due").Should().Equal(first + (600L * 48000));
        d.Air.Should().HaveCount(2);
        d.Station.IdentificationsSent.Should().Be(1);
        d.RunTo(16 * 60 * 48000L);
        d.Air.Should().HaveCount(2, "rested: nothing sent for fifteen idle minutes");
    }

    private static int IdentLength(string callsign)
    {
        var packed = new byte[100];
        int n = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(callsign), 0, default, packed);
        return new BurstRenderer().SamplesFor(StandardFormats.FirstContact, PayloadCodec.SymbolsFor(StandardFormats.FirstContact, n)!.Value);
    }
}
