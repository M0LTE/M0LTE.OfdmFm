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
    private long _extra;

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
        Station.ModemSent += e =>
        {
            Log.Add((Station.SampleCount, "modem " + e.Kind, 0));
            if (e.Kind == ModemSentKind.Identification)
            {
                _extra = TailSamples; // the host's Morse: the transmission goes on for the tail after the burst
            }
        };
        Station.Transmitted += e => Log.Add((Station.SampleCount, "transmitted " + e.Tag.Value, 0));
    }

    public Station Station { get; }

    /// <summary>How long the stand-in keeps the transmitter on after an identification burst, in samples: the host's Morse. The station is told with <see cref="Station.SetIdentificationTail"/>; this only plays it.</summary>
    public long TailSamples { get; set; }

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
                _airEnd = Station.SampleCount + p.Length + _extra;
                _extra = 0;
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
        Station.Send(TestBurst.Datagram(tag, length), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(tag), false, priority).Should().Be(SendResult.Accepted);

    public IEnumerable<long> Clocks(string what) => Log.Where(l => l.What == what).Select(l => l.Clock);

    public static string Text(IEnumerable<(long Clock, string What, long Value)> log) =>
        string.Join("\n", log.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Clock} {l.What} {l.Value}")));
}

/// <summary>
/// The identification rule (step 7, as changed on 28 November 2026): an identification is due when the station is about to transmit of its own accord (data, an announcement, a notice) and either it has never identified or ten minutes have
/// passed since its last identification started. The identification goes first and the transmission that made it due follows it without a fresh wait. A lone report goes first and the identification follows it. No timer, so an idle station
/// never keys to identify. These tests step it with a shorter interval (a seam: <c>Station.IdentificationInterval</c>) so that a run is seconds and not ten minutes of audio through the detector. The seam is sound because the interval is
/// read in one place (when a transmission is about to go) and goes into one comparison; the real interval is pinned by a test of its own and run end to end behind <c>SM_MASK_GATE=1</c>.
/// </summary>
public class StationIdentificationClockTests
{
    /// <summary>Twelve seconds: longer than the station's own start-up listen (5 s) and a burst, short enough to step.</summary>
    private const long Interval = 12L * 48000;

    /// <summary>The clocks the identification's transmit requests were raised at: when each was granted (the stand-in keys it a little later, at its next step, when the request was made inside a host call).</summary>
    private static List<long> IdentRequests(IdentDriver d) => [.. d.Requests.Where(r => r.Length == IdentLength("M0LTE")).Select(r => r.Clock)];

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
        d.Station.LastIdentificationStart.Should().Be(d.Air[0].Start, "the identification went first, and it is its start that counts");
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
    public void Only_The_Request_For_The_Identification_Burst_Says_It_Is_The_Identification()
    {
        // A host that follows the burst with Morse must know which request to append it to, and cannot go by order: a report can be asked for between the identification falling due and its burst.
        var d = new IdentDriver(Interval);
        var flags = new List<bool>();
        d.Station.TransmitRequest += e => flags.Add(e.Identification);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(5 * Interval);

        flags.Should().Equal([true, false], "the identification, then the datagram");
        d.Air[0].Length.Should().Be(IdentLength("M0LTE"), "the request that said so is the identification burst");
    }

    [Fact]
    public void A_Cold_Station_About_To_Transmit_Identifies_First_And_Its_Datagram_Goes_Straight_On_After_It_Without_A_Fresh_Wait_And_Only_One_Is_Sent()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(5 * Interval);

        d.Air.Should().HaveCount(2, "the identification, then the datagram");
        d.Air[0].Length.Should().Be(IdentLength("M0LTE"));
        d.Air[1].Length.Should().NotBe(IdentLength("M0LTE"));
        d.Clocks("due").Should().ContainSingle().Which.Should().Be(d.Requests[0].Clock, "due when the station was about to transmit, and handed to the audio layer at once");
        d.Air[1].Start.Should().Be(d.Air[0].End + 4800 + 2400, "straight on: the own tail and the first slot, no 0.8 s and no random wait");
        d.Clocks("modem Identification").Should().ContainSingle();
        d.Station.IdentificationsSent.Should().Be(1);
        d.Station.IdentificationOwed.Should().BeFalse("the identification burst is not a transmission that owes another");
        d.Station.PendingTimers.Should().Be(0);
    }

    [Fact]
    public void Each_Identification_Raises_Due_Once_Then_Asks_Then_Says_Modem_Sent_Once_In_That_Order_And_Due_Is_Raised_When_The_Transmission_Is_About_To_Go()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.RunTo(3 * Interval);

        d.Log.Where(l => l.What is "due" or "request" or "modem Identification").Select(l => l.What).SkipWhile(w => w != "due").Take(3)
            .Should().Equal("due", "request", "modem Identification");
        d.Station.IdentificationsDue.Should().Be(1);
        d.Log.Single(l => l.What == "due").Clock.Should().Be(d.Requests[0].Clock, "when the datagram's turn came, before anything of the station's was keyed");
        d.Log.Where(l => l.What == "request").Select(l => l.Value).Should().Equal([d.Air[0].Length, d.Air[1].Length], "the identification's request, then the datagram's");
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
        IdentRequests(d).Should().Equal(due, "each is handed over the moment it falls due: the channel is clear, because the datagram it precedes had waited for it");
        for (int i = 1; i < starts.Count; i++)
        {
            (starts[i] - starts[i - 1]).Should().BeGreaterThanOrEqualTo(Interval).And.BeLessThan(Interval + (10L * 48000), "an interval from the last one, then the next datagram's turn");
            d.Air.Should().NotContain(a => a.Start < due[i] && due[i] < a.End, "it is raised when the station is not keyed, never in the middle of a transmission");
        }
    }

    [Fact]
    public void A_Station_Quiet_For_More_Than_The_Interval_Since_Its_Last_Identification_Identifies_First_When_It_Transmits_Again()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[0].Start;
        d.RunTo(identStart + (3 * Interval));
        d.Air.Should().HaveCount(2, "nothing sent since: nothing owed, nothing keyed");
        d.SendToEveryone(2);
        d.RunTo(identStart + (4 * Interval));
        d.Air.Should().HaveCount(4);
        d.Air[2].Length.Should().Be(IdentLength("M0LTE"));
        d.Clocks("due").Last().Should().Be(IdentRequests(d).Last(), "when the datagram is about to go, and it goes after");
        d.Air[3].Start.Should().Be(d.Air[2].End + 4800 + 2400);
    }

    [Fact]
    public void A_Station_That_Identified_Transmitted_Again_Within_The_Interval_And_Went_Quiet_Never_Keys_Again_However_Long_The_Clock_Runs()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[0].Start;
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
        d.Air.Should().HaveCount(3, "the identification, the datagram, the datagram");
        d.Station.IdentificationsSent.Should().Be(1);
        d.Station.PendingTimers.Should().Be(0, "there is no identification timer");
    }

    [Fact]
    public void A_Transmission_Granted_Inside_The_Interval_Owes_Nothing_Even_If_It_Ends_Past_It_And_The_First_Granted_After_It_Is_Preceded_By_An_Identification()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[0].Start;
        d.RunTo(d.Air[1].End + 48000);
        d.SendToEveryone(2);
        d.RunTo(identStart + Interval - 48000);
        d.Air.Should().HaveCount(3);
        d.Clocks("due").Should().HaveCount(1);

        // A transmission that begins inside the interval and ends past it is covered: what counts is when it is about to go.
        d.RunTo(identStart + Interval - 2000);
        d.SendToEveryone(3, length: 200);
        d.RunTo(identStart + Interval + (4L * 48000));
        d.Air.Should().HaveCount(4);
        d.Requests[3].Clock.Should().BeLessThan(identStart + Interval, "handed over inside the interval");
        d.Air[3].End.Should().BeGreaterThanOrEqualTo(identStart + Interval);
        d.Clocks("due").Should().HaveCount(1, "it was about to go inside the interval");

        // The next one is about to go past it: the identification first.
        d.RunTo(d.Air[3].End + 48000);
        d.SendToEveryone(4);
        d.RunTo(identStart + (3 * Interval));
        d.Air.Should().HaveCount(6);
        d.Air[4].Length.Should().Be(IdentLength("M0LTE"));
        d.Clocks("due").Last().Should().Be(IdentRequests(d).Last());
        d.Air[5].Start.Should().Be(d.Air[4].End + 4800 + 2400);
    }

    [Fact]
    public void An_Identification_Is_Not_Raised_In_The_Middle_Of_The_Stations_Own_Burst_Whatever_The_Interval_Does_And_The_Next_Transmission_Gets_It_First()
    {
        var d = new IdentDriver(6 * 48000L);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[0].Start;
        d.SendToEveryone(2, length: 200);
        RunUntilAir(d, 3);
        d.RunTo(identStart + (14L * 48000));

        d.Air.Should().HaveCount(3);
        d.Air[2].Start.Should().BeLessThan(identStart + (6 * 48000L), "the second datagram began before the interval ran out");
        d.Air[2].End.Should().BeGreaterThan(identStart + (6 * 48000L), "and was still on the air when it did");
        d.Clocks("due").Should().ContainSingle("nothing was raised in the middle of it");

        d.SendToEveryone(3);
        d.RunTo(identStart + (40L * 48000));
        d.Air.Should().HaveCount(5);
        d.Air[3].Length.Should().Be(IdentLength("M0LTE"));
        d.Clocks("due").Last().Should().Be(IdentRequests(d).Last());
        d.Air[4].Start.Should().Be(d.Air[3].End + 4800 + 2400);
    }

    [Fact]
    public void An_Identification_Falls_Due_When_The_Datagrams_Wait_Ends_Not_Before_Is_Not_Raised_Twice_However_Long_The_Channel_Was_Busy()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000 - 100);
        long freed = 66L * 48000;
        d.Station.SetExternalBusy(true);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        d.At(freed, () => d.Station.SetExternalBusy(false));
        d.RunTo(freed + 12 * 48000);

        d.Air.Should().HaveCount(2);
        d.Clocks("due").Should().Equal(IdentRequests(d));
        d.Air[0].Start.Should().BeGreaterThan(freed + 4800 + 38400 - 1, "the indication drops, 100 ms, the 0.8 s of ordinary traffic and its slots: the identification waits for the datagram's turn, it does not go before");
        d.Station.IdentificationsDue.Should().Be(1);
        d.Station.IdentificationOwed.Should().BeFalse();
        d.Station.LastIdentificationStart.Should().Be(d.Air[0].Start, "counted from when it went on the air");
    }

    [Fact]
    public void The_Identification_Goes_Ahead_Of_Queued_Datagrams_Whatever_Their_Class_And_Waits_Its_Turn_Behind_A_Burst_In_Progress()
    {
        var d = new IdentDriver(Interval);
        d.RunTo(6 * 48000);
        long burstEnd = 6L * 48000 + (30L * 48000);
        d.Station.NoteBurst(burstEnd);
        d.SendToEveryone(2);
        d.SendToEveryone(3, priority: SendPriority.Expedited);
        d.RunTo(burstEnd + (14L * 48000));

        d.Air.Should().HaveCount(3, "the identification, then the expedited datagram and the ordinary one queued before it fell due");
        d.Air[0].Start.Should().Be(burstEnd + 2400, "not while another station's burst is on the air, and the channel clear 50 ms after it: the expedited datagram's slot");
        d.Air[0].Length.Should().Be(IdentLength("M0LTE"), "the identification is first, ahead of the datagrams");
        d.Air[1].Start.Should().Be(d.Air[0].End + 4800 + 2400);
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
    public void The_Whole_Ten_Minutes_Run_Through_The_Detector_An_Idle_Station_Never_Keys_And_The_First_Transmission_About_To_Go_Past_Ten_Minutes_Is_Preceded_By_An_Identification()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector");
        var d = new IdentDriver(0);
        d.RunTo(6 * 48000);
        d.SendToEveryone(1);
        RunUntilAir(d, 2);
        long identStart = d.Air[0].Start;
        d.RunTo(d.Air[1].End + 48000);
        d.SendToEveryone(2);
        d.RunTo(identStart + 28_800_000 + (60L * 48000));
        d.Air.Should().HaveCount(3, "identification, datagram, datagram: an idle station never keys, not at ten minutes and not after");
        d.Clocks("due").Should().HaveCount(1);

        d.SendToEveryone(3);
        d.RunTo(d.Station.SampleCount + (30L * 48000));
        d.Air.Should().HaveCount(5);
        d.Clocks("due").Last().Should().Be(IdentRequests(d).Last(), "when the first transmission past the ten minutes is about to go");
        d.Air[3].Start.Should().BeGreaterThanOrEqualTo(identStart + 28_800_000);
        d.Air[3].Length.Should().Be(IdentLength("M0LTE"));
    }

    private static int IdentLength(string callsign)
    {
        var packed = new byte[100];
        int n = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(callsign), 0, [new ExtensionItem(PayloadCodec.StationIdTag, new byte[PayloadCodec.KeyBytes])], packed);
        return new BurstRenderer().SamplesFor(StandardFormats.FirstContact, PayloadCodec.SymbolsFor(StandardFormats.FirstContact, n)!.Value);
    }
}
