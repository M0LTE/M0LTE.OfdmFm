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
        Station = new Station(number, new Random(seed), runJobsInline: true).WithIdentity(callsign);
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
/// The identification rule (step 7, as changed on 27 November 2026): due when a transmission of the station's ends and either it has never identified or ten minutes have passed since its last identification started. No timer, so an idle
/// station never keys to identify. These tests step it with a shorter interval (a seam: <c>Station.IdentificationInterval</c>) so that a run is seconds and not ten minutes of audio through the detector. The seam is sound because the interval is
/// read in one place (when a transmission ends) and goes into one comparison; the real interval is pinned by a test of its own and run end to end behind <c>SM_MASK_GATE=1</c>.
/// </summary>
public class StationIdentificationClockTests
{
    /// <summary>Twelve seconds: longer than the station's own start-up listen (5 s) and a burst, short enough to step.</summary>
    private const long Interval = 12L * 48000;

    private static void RunUntilAir(IdentDriver d, int count)
    {
        while (d.Air.Count < count)
        {
            d.RunTo(d.Station.SampleCount + 4800);
        }
    }

    [Fact]
    public void The_Interval_Is_Ten_Minutes_Of_The_Sample_Clock_Counted_From_When_The_Last_Identification_Started()
    {
        Station.IdentificationIntervalSamples.Should().Be(28_800_000);
        var d = new IdentDriver(0);
        d.Station.IdentificationInterval.Should().Be(28_800_000);
        d.Station.LastIdentificationStart.Should().BeNull();
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        d.Station.LastIdentificationStart.Should().Be(d.Air[1].Start);
    }

    [Fact]
    public void A_Station_That_Never_Transmits_Never_Identifies()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(4 * Interval);
        d.Log.Should().BeEmpty();
        d.Station.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void A_Station_That_Has_Been_Quiet_And_Transmits_Is_Due_At_Once_At_The_End_Of_That_Transmission_And_Identifies_Right_After_It_And_Only_Once()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(5 * Interval);

        d.Air.Should().HaveCount(2, "the datagram, then the identification");
        d.Clocks("due").Should().Equal(d.Air[0].End);
        d.Air[1].Start.Should().Be(d.Air[0].End + 4800 + 2400, "right after, by the ordinary rules: the own tail and the first slot");
        d.Clocks("modem Identification").Should().ContainSingle();
        d.Station.IdentificationsSent.Should().Be(1);
        d.Station.IdentificationOwed.Should().BeFalse("the identification burst is not a transmission that owes another");
        d.Station.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void Each_Identification_Raises_Due_Once_Then_Asks_Then_Says_Modem_Sent_Once_In_That_Order_And_Due_Is_Raised_When_The_Transmission_Ends()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(3 * Interval);

        d.Log.Where(l => l.What is "due" or "request" or "modem Identification").Select(l => l.What).SkipWhile(w => w != "due")
            .Should().Equal("due", "request", "modem Identification");
        d.Station.IdentificationsDue.Should().Be(1);
        d.Log.Single(l => l.What == "due").Clock.Should().Be(d.Air[0].End, "not in the middle of the transmission");
    }

    [Fact]
    public void A_Station_That_Keeps_Transmitting_Identifies_Every_Interval_Each_Counted_From_The_Start_Of_The_Previous_Identification()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        for (int i = 0; i < 10; i++)
        {
            d.SendToEveryone(i);
            d.RunTo((6 + (8 * (i + 1))) * 48000L);
        }

        d.RunTo(90 * 48000L);
        List<long> due = [.. d.Clocks("due")];
        List<long> starts = [.. d.Air.Where(a => a.Length == IdentLength("M0LTE")).Select(a => a.Start)];
        starts.Count.Should().BeGreaterThanOrEqualTo(5);
        starts.Should().HaveCount(due.Count, "one burst for each time it fell due");
        due[0].Should().Be(d.Air[0].End);
        for (int i = 1; i < starts.Count; i++)
        {
            (starts[i] - starts[i - 1]).Should().BeGreaterThanOrEqualTo(Interval).And.BeLessThan(Interval + (10L * 48000), "an interval from the last one, then the end of the transmission in progress and the wait for the channel");
            d.Air.Should().NotContain(a => a.Start < due[i] && due[i] < a.End, "it is raised when the station is not keyed, never in the middle of a transmission");
        }
    }

    [Fact]
    public void A_Station_Quiet_For_More_Than_The_Interval_Since_Its_Last_Identification_Is_Due_At_Once_When_It_Transmits_Again()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[1].Start;
        d.RunTo(identStart + (3 * Interval));
        d.Air.Should().HaveCount(2, "nothing sent since: nothing owed, nothing keyed");
        d.SendToEveryone(2);
        d.RunTo(identStart + (4 * Interval));
        d.Air.Should().HaveCount(4);
        d.Clocks("due").Last().Should().Be(d.Air[2].End, "at once, at the end of that transmission");
        d.Air[3].Start.Should().Be(d.Air[2].End + 4800 + 2400);
    }

    [Fact]
    public void A_Station_That_Identified_Transmitted_Again_Within_The_Interval_And_Went_Quiet_Never_Keys_Again_However_Long_The_Clock_Runs()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[1].Start;
        d.RunTo(d.Air[1].End + 48000);
        d.SendToEveryone(2);
        RunUntilAir(d, 3);
        d.RunTo(d.Air[2].End + 48000);
        d.Air[2].End.Should().BeLessThan(identStart + Interval, "sent inside the interval: covered by the identification before it");
        int requests = d.Requests.Count;
        int due = d.Clocks("due").Count();

        // The clock runs on: many intervals, in steps.
        for (int i = 0; i < 10; i++)
        {
            d.Station.Skip((int)Interval);
        }

        d.Station.SampleCount.Should().BeGreaterThan(identStart + (10 * Interval));
        d.Requests.Count.Should().Be(requests, "no request: an idle station never keys to identify");
        d.Clocks("due").Count().Should().Be(due, "and no identification fell due");
        d.Air.Should().HaveCount(3, "the datagram, the identification, the datagram");
        d.Station.IdentificationsSent.Should().Be(1);
        d.Station.PendingTimers.Should().Be(0, "there is no identification timer");
    }

    [Fact]
    public void A_Transmission_That_Ends_Inside_The_Interval_Owes_Nothing_And_The_First_To_End_Past_It_Is_Followed_By_An_Identification()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[1].Start;
        d.RunTo(d.Air[1].End + 48000);
        d.SendToEveryone(2);
        d.RunTo(identStart + Interval - 48000);
        d.Air.Should().HaveCount(3);
        d.Clocks("due").Should().HaveCount(1);

        // A transmission that begins inside the interval and ends past it is the one that makes it due.
        d.RunTo(identStart + Interval - 2000);
        d.SendToEveryone(3, length: 200);
        d.RunTo(identStart + (3 * Interval));
        d.Air.Should().HaveCount(5);
        d.Air[3].End.Should().BeGreaterThanOrEqualTo(identStart + Interval);
        d.Clocks("due").Last().Should().Be(d.Air[3].End);
        d.Air[4].Length.Should().Be(IdentLength("M0LTE"));
    }

    [Fact]
    public void An_Identification_Due_Inside_The_Stations_Own_Burst_Waits_For_The_Burst_To_End_Before_It_Is_Raised()
    {
        var d = new IdentDriver(4 * 48000L);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[1].Start;
        d.SendToEveryone(2, length: 200);
        RunUntilAir(d, 3);
        d.RunTo(identStart + (12L * 48000));

        d.Air.Should().HaveCount(4);
        d.Air[2].Start.Should().BeLessThan(identStart + (4 * 48000L), "the second datagram began before the interval ran out");
        d.Air[2].End.Should().BeGreaterThan(identStart + (4 * 48000L), "and was still on the air when it did");
        d.Clocks("due").Last().Should().Be(d.Air[2].End);
        d.Air[3].Start.Should().Be(d.Air[2].End + 4800 + 2400);
    }

    [Fact]
    public void An_Identification_Owed_While_The_Channel_Is_Busy_For_A_Long_Time_Waits_Is_Not_Raised_Twice_And_Goes_When_The_Channel_Clears()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(7 * 48000);
        d.Air.Should().ContainSingle();
        long due = d.Air[0].End;
        long freed = due + (60L * 48000);
        d.At(due - 1000, () => d.Station.SetExternalBusy(true));
        d.At(freed, () => d.Station.SetExternalBusy(false));
        d.RunTo(freed + 12 * 48000);

        d.Clocks("due").Should().Equal(due);
        d.Air.Should().HaveCount(2);
        d.Air[1].Start.Should().Be(freed + 4800 + 2400, "100 ms after the indication drops, then the first slot's 50 ms");
        d.Station.IdentificationsDue.Should().Be(1);
        d.Station.IdentificationOwed.Should().BeFalse();
        d.Station.LastIdentificationStart.Should().Be(d.Air[1].Start, "counted from when it went on the air, not from when it fell due");
    }

    [Fact]
    public void The_Identification_Goes_Ahead_Of_Queued_Datagrams_By_Class_And_Waits_Its_Turn_Behind_A_Burst_In_Progress()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(7 * 48000);
        d.Air.Should().ContainSingle();
        long due = d.Air[0].End;
        long burstEnd = due + (30L * 48000);
        d.At(due - 100, () => d.Station.NoteBurst(burstEnd));
        d.At(due + 200, () => d.SendToEveryone(2));
        d.At(due + 300, () => d.SendToEveryone(3, priority: SendPriority.Expedited));
        d.RunTo(burstEnd + (11L * 48000));

        d.Air.Should().HaveCount(4, "the first datagram, the identification, then the expedited datagram and the ordinary one queued while it was owed");
        d.Air[1].Start.Should().Be(burstEnd + 2400, "not while another station's burst is on the air, and the channel clear 50 ms after it");
        d.Air[1].Length.Should().Be(IdentLength("M0LTE"), "the identification is first, ahead of the datagrams queued after it fell due");
        d.Air[2].Start.Should().BeGreaterThan(d.Air[1].End);
        string[] after = [.. d.Log.Select(l => l.What).SkipWhile(w => w != "due")];
        after.Where(w => w.StartsWith("modem", StringComparison.Ordinal) || w.StartsWith("transmitted", StringComparison.Ordinal)).Should().Equal("modem Identification", "transmitted 3", "transmitted 2");
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
    public void The_Whole_Ten_Minutes_Run_Through_The_Detector_An_Idle_Station_Never_Keys_And_The_First_Transmission_To_End_Past_Ten_Minutes_Is_Followed_By_An_Identification()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector");
        var d = new IdentDriver(0);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[1].Start;
        d.RunTo(d.Air[1].End + 48000);
        d.SendToEveryone(2);
        d.RunTo(identStart + 28_800_000 + (60L * 48000));
        d.Air.Should().HaveCount(3, "datagram, identification, datagram: an idle station never keys, not at ten minutes and not after");
        d.Clocks("due").Should().HaveCount(1);

        d.SendToEveryone(3);
        d.RunTo(d.Station.SampleCount + (30L * 48000));
        d.Air.Should().HaveCount(5);
        d.Clocks("due").Last().Should().Be(d.Air[3].End, "at the end of the first transmission past the ten minutes");
        d.Air[3].End.Should().BeGreaterThanOrEqualTo(identStart + 28_800_000);
    }

    private static int IdentLength(string callsign)
    {
        var packed = new byte[100];
        int n = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(callsign), 0, default, packed);
        return new BurstRenderer().SamplesFor(StandardFormats.FirstContact, PayloadCodec.SymbolsFor(StandardFormats.FirstContact, n)!.Value);
    }
}
