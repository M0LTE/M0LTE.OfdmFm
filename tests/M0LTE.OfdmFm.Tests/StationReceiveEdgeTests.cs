using System.Diagnostics.CodeAnalysis;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Step 3 at its edges: failures, bursts cut off or lost, floods of candidates, a full queue, headers the audio cannot show, and what allocates.</summary>
public class StationReceiveEdgeTests
{
    private const int Format = 12;

    private static float[] OneBurst(TestBurst burst, double leadSeconds = 1.5, double tailSeconds = 2.5, double noise = 0.01, Func<Placed, StreamBuilder, StreamBuilder>? damage = null, int seed = 3)
    {
        var b = new StreamBuilder().Silence(leadSeconds);
        Placed p = b.Add(burst);
        b.Silence(tailSeconds).Noise(noise, seed);
        damage?.Invoke(p, b);
        return b.ToArray();
    }

    [Fact]
    public void A_Burst_Whose_Payload_Fails_Delivers_Nothing_Measures_Its_Opening_And_Leaves_A_Report_Owed()
    {
        TestBurst burst = TestBurst.Data(4, 1, Format, [TestBurst.Datagram(1, 40)], new PayloadPrelude(true, null, false, null), symbols: 12);
        float[] samples = OneBurst(burst, damage: (p, b) => b.Noise(1.5, 99, p.Start + (2 * BurstRenderer.SymbolSamples), p.End));
        using var rig = new ReceiveRig(0);
        rig.Push(samples, () => 480);
        rig.Finish();

        rig.Datagrams.Should().BeEmpty();
        Station.AdaptationCall call = rig.Calls.Should().ContainSingle().Subject;
        call.Decoded.Should().BeFalse();
        call.Sender.Should().Be(4);
        call.Dest.Should().Be(1);
        call.FootingDb.Should().Be(LinkAdaptation.Footing(Format, opening: true));
        call.Quality.Should().HaveCount(333);
        call.Quality.Count(double.IsFinite).Should().Be(333, "the opening of a full-band burst measures every carrier");
        call.TheirReport.Should().BeNull("a payload that failed gave no prelude to read");

        // The rules owe the sender a report that says "failed"; the station keeps it where step 6 will pick it up.
        call.Returned.Should().NotBeNull();
        call.Returned!.Value.Failed.Should().BeTrue();
        Station.OwedReport owed = rig.Station.OwedReports.Should().ContainSingle().Subject;
        owed.Station.Should().Be(4);
        owed.Report.Failed.Should().BeTrue();
        owed.SinceSample.Should().BeCloseTo((long)Math.Round(call.Now * 48000), 1);
        rig.Station.Adaptation.TryGetNeighbour(4, out LinkNeighbour? nb).Should().BeTrue();
        nb!.Good.Should().Be(0);
        nb.Cap.Should().NotBeNull("a failure at a format sets the cap");
        rig.Log.Should().NotContain(l => l.StartsWith("rx ", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_Bursts_For_Everyone_Or_For_Another_Station_Are_Measured_And_Owe_Nothing()
    {
        var b = new StreamBuilder().Silence(1.2);
        foreach (int dest in new[] { 0, 9 })
        {
            Placed p = b.Add(TestBurst.Data(4, dest, Format, [TestBurst.Datagram(2, 40)], symbols: 12));
            b.Silence(0.5);
            b.Noise(1.5, 90 + dest, p.Start + (2 * BurstRenderer.SymbolSamples), p.End);
        }

        b.Silence(2).Noise(0.01, 5);
        using var rig = new ReceiveRig(0);
        rig.Push(b.ToArray(), () => 480);
        rig.Finish();

        rig.Calls.Select(c => (c.Dest, c.Decoded)).Should().Equal((0, false), (9, false));
        rig.Calls.Should().OnlyContain(c => c.Returned == null && c.Quality.Length == 333);
        rig.Station.OwedReports.Should().BeEmpty();
        rig.Datagrams.Should().BeEmpty();
    }

    [Fact]
    public void A_Newer_Failure_From_The_Same_Station_Replaces_The_Report_Owed()
    {
        var b = new StreamBuilder().Silence(1.2);
        for (int i = 0; i < 2; i++)
        {
            Placed p = b.Add(TestBurst.Data(4, 1, Format, [TestBurst.Datagram(3, 40)], symbols: 12));
            b.Silence(0.8);
            b.Noise(1.5, 70 + i, p.Start + (2 * BurstRenderer.SymbolSamples), p.End);
        }

        b.Silence(2).Noise(0.01, 6);
        using var rig = new ReceiveRig(0);
        rig.Push(b.ToArray(), () => 480);
        rig.Finish();

        rig.Calls.Should().HaveCount(2).And.OnlyContain(c => !c.Decoded);
        Station.OwedReport owed = rig.Station.OwedReports.Should().ContainSingle().Subject;
        owed.SinceSample.Should().BeCloseTo((long)Math.Round(rig.Calls[1].Now * 48000), 1, "the newest verdict's end");
    }

    [Fact]
    public void A_Burst_Cut_Off_By_The_End_Of_The_Stream_Delivers_Nothing_And_Is_Delivered_Once_When_The_Rest_Comes()
    {
        TestBurst burst = TestBurst.Data(5, 1, Format, [TestBurst.Datagram(4, 40)], symbols: 20);
        float[] all = OneBurst(burst, noise: 0.005);
        int cut = (int)(1.5 * 48000) + burst.Lead + (burst.Length / 2);
        using var rig = new ReceiveRig(0);
        rig.Push(all.AsSpan(0, cut).ToArray(), () => 480);
        rig.Station.DeliverResults();

        rig.Datagrams.Should().BeEmpty();
        rig.Calls.Should().BeEmpty();
        rig.Station.BurstsInFlight.Should().Be(1, "the header read, so the burst is known, and waiting for its last sample");

        rig.Push(all.AsSpan(cut).ToArray(), () => 480);
        rig.Finish();
        rig.Datagrams.Should().HaveCount(1);
        rig.Calls.Should().HaveCount(1);
        rig.Station.BurstsInFlight.Should().Be(0);
    }

    [Fact]
    public void A_Burst_The_Ring_Overwrote_Before_Its_Header_Was_Read_Is_Dropped_Cleanly_And_The_Next_One_Is_Received()
    {
        var b = new StreamBuilder().Silence(1.5);
        b.Add(TestBurst.Data(5, 1, Format, [TestBurst.Datagram(5, 40)], symbols: 12));
        b.Silence(Station.RingSamples / 48000.0 + 4.0);
        Placed second = b.Add(TestBurst.Data(5, 1, Format, [TestBurst.Datagram(6, 40)], symbols: 12));
        b.Silence(2.5).Noise(0.01, 8);

        // A queued station that is pushed far faster than anything runs the jobs: no worker runs until the first burst has been pushed out of the ring.
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var datagrams = new List<byte[]>();
        station.Received += e => datagrams.Add(e.Datagram.ToArray());
        float[] samples = b.ToArray();
        int firstLeg = (int)(second.Start - second.Burst.Lead - 48000);
        for (int at = 0; at < firstLeg; at += 4800)
        {
            station.Push(samples.AsSpan(at, Math.Min(4800, firstLeg - at)));
        }

        station.RunPending();
        station.DeliverResults();
        station.HeadersLost.Should().BeGreaterThan(0, "the burst's candidates were read after the ring had moved past them");
        datagrams.Should().BeEmpty();

        for (int at = firstLeg; at < samples.Length; at += 4800)
        {
            station.Push(samples.AsSpan(at, Math.Min(4800, samples.Length - at)));
            while (station.HasWork)
            {
                station.RunOne();
                station.DeliverResults();
            }
        }

        for (int i = 0; i < 20; i++)
        {
            station.RunPending();
            station.DeliverResults();
        }

        datagrams.Should().HaveCount(1);
        datagrams[0].Should().Equal(TestBurst.Datagram(6, 40));
        station.BurstsDropped.Should().Be(0);
        station.BurstsInFlight.Should().Be(0);
    }

    [Fact]
    public void A_Burst_Whose_Header_Was_Read_Too_Late_For_Its_Start_To_Be_Copied_Is_Dropped_Cleanly()
    {
        // A defence that real audio cannot reach (a header is read from the ring, so its start was in it, and the next piece of audio waits for the result to be taken in): a burst whose start is older than the
        // ring is counted, resolved without a result, and does not hold up what comes after it.
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var datagrams = new List<byte[]>();
        station.Received += e => datagrams.Add(e.Datagram.ToArray());
        station.Push(new float[Station.RingSamples + 100_000]);
        byte[] bits = new byte[BurstHeaderCodec.HeaderBits];
        station.TakeInHeader(1000, FormatOpening.Full, BurstHeader.Data(3, 1, 12, 12, 0), bits);
        station.BurstsInFlight.Should().Be(1);
        station.Push(new float[4 * 48000]);
        station.DeliverResults();

        station.BurstsLost.Should().Be(1);
        station.BurstsInFlight.Should().Be(0);
        datagrams.Should().BeEmpty();

        // And the station goes on to receive a burst after it.
        station.Push(OneBurst(TestBurst.Data(3, 1, Format, [TestBurst.Datagram(60, 40)], symbols: 12)));
        station.DeliverResults();
        datagrams.Should().HaveCount(1);
    }

    [Fact]
    public void Hundreds_Of_Candidates_Inside_A_Strong_Bursts_Payload_Cause_No_Header_Reads()
    {
        // 252 symbols, the longest burst, at full level on the full band: the payload correlates with the opening's reference all the way along.
        TestBurst burst = TestBurst.Data(6, 1, 96, [TestBurst.Datagram(8, 100)], symbols: 252);
        float[] samples = OneBurst(burst, noise: 0.005);
        using var rig = new ReceiveRig(0);
        rig.Push(samples, () => 4800);
        rig.Finish();

        Station s = rig.Station;
        s.CandidatesSeen.Should().BeGreaterThan(150, "a strong burst throws a candidate for many of its symbols");
        s.CandidatesIgnored.Should().BeGreaterThan(150);
        s.HeaderReads.Should().Be(s.CandidatesSeen - s.CandidatesIgnored).And.BeLessThanOrEqualTo(6, "only the candidates at the burst's start, before it was known, were read");
        rig.Datagrams.Should().HaveCount(1);
        rig.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void A_Station_Whose_Workers_Never_Run_Does_Not_Grow_Without_Bound()
    {
        // The longest burst throws a hundred candidates or more; with no worker to read any of them, the station keeps only the newest it is allowed (here 30, for the test).
        TestBurst burst = TestBurst.Data(6, 1, 96, [TestBurst.Datagram(8, 100)], symbols: 252);
        var station = (new Station(1, new Random(1)) { CandidateLimit = 30 }).WithIdentity("M0LTE");
        float[] samples = OneBurst(burst, noise: 0.005);
        for (int at = 0; at < samples.Length; at += 4800)
        {
            station.Push(samples.AsSpan(at, Math.Min(4800, samples.Length - at)));
        }

        station.CandidatesSeen.Should().BeGreaterThan(30);
        station.CandidatesWaiting.Should().BeLessThanOrEqualTo(30);
        station.CandidatesDropped.Should().Be(station.CandidatesSeen - station.CandidatesWaiting - 1, "the one in flight is neither waiting nor dropped");
        station.HasWork.Should().BeTrue("one header read is out, and nobody takes it");
    }

    [Fact]
    public void The_Estimate_Is_Paused_Over_Each_Burst_Known_From_Its_First_Sample_To_Its_Last()
    {
        (float[] samples, List<Placed> bursts) = MixedStream.Build(8);
        using var rig = new ReceiveRig(0);
        rig.Push(samples, () => 4800);
        rig.Finish();
        rig.Station.PausedSamples.Should().Be(bursts.Sum(p => (long)p.Burst.Length), "the bursts do not overlap, so each is paused for exactly its length");
    }

    [Fact]
    public void A_Header_Of_A_Version_Or_Type_This_Step_Does_Not_Know_Is_Noted_And_Nothing_More_Is_Done_With_It()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        byte[] bits = new byte[BurstHeaderCodec.HeaderBits];
        station.TakeInHeader(1000, FormatOpening.Full, new BurstHeader(1, BurstKind.Data, 0, 0, 0, 0, 0), bits);
        station.TakeInHeader(2000, FormatOpening.UpTo3000Hz, new BurstHeader(0, (BurstKind)6, 3, 0, 192, 12, 0), bits);
        station.TakeInHeader(3000, FormatOpening.Full, new BurstHeader(0, (BurstKind)5, 3, 0, 12, 12, 0), bits);
        station.TakeInHeader(4000, FormatOpening.Full, BurstHeader.Data(0, 1, 12, 12, 0), bits);
        station.TakeInHeader(5000, FormatOpening.UpTo1500Hz, BurstHeader.Data(3, 1, 12, 12, 0), bits);

        station.NotedHeaders.Select(n => (n.Index, n.Reason)).Should().Equal(
            (1000, Station.NoteReason.UnknownVersion),
            (2000, Station.NoteReason.UnsupportedKind),
            (3000, Station.NoteReason.UnsupportedKind),
            (4000, Station.NoteReason.Invalid),
            (5000, Station.NoteReason.Invalid));
        station.BurstsInFlight.Should().Be(0);
        station.PausedSamples.Should().Be(0);
        station.OwedReports.Should().BeEmpty();
    }

    [Fact]
    public void A_Report_For_This_Station_Is_Heard_And_One_For_Another_Is_Read_And_Left_Alone()
    {
        var b = new StreamBuilder().Silence(1.2);
        Placed mine = b.Add(TestBurst.Report(6, 1, 140, true, FormatOpening.Full));
        b.Silence(1.0);
        Placed other = b.Add(TestBurst.Report(6, 8, 99, false, FormatOpening.UpTo3000Hz));
        b.Silence(2.5).Noise(0.01, 12);
        using var rig = new ReceiveRig(0);
        rig.SendTo(6);
        rig.Push(b.ToArray(), () => 480);
        rig.Finish();

        rig.Calls.Should().ContainSingle().Which.Should().Match<Station.AdaptationCall>(c => c.IsReport && c.Sender == 6 && c.TheirReport!.Value.Format == 140 && c.TheirReport.Value.Failed);
        rig.Calls[0].Now.Should().BeApproximately(mine.End / 48000.0, 4.0 / 48000);
        rig.Log.Where(l => l.StartsWith("path ", StringComparison.Ordinal)).Should().Equal("path #6 fmt 140 failed True");
        rig.Station.Adaptation.TryGetNeighbour(6, out LinkNeighbour? nb).Should().BeTrue();
        nb!.ReportFormat.Should().Be(140);
        rig.Station.BurstsApplied.Should().Be(2, "both reports were read");
        rig.Station.OwedReports.Should().BeEmpty();
        other.Burst.Header.Destination.Should().Be(8);
    }

    [Fact]
    public void Link_State_Is_Raised_When_What_Is_Known_Of_A_Neighbour_Changes_And_Not_Otherwise()
    {
        (float[] samples, List<Placed> _) = MixedStream.Build();
        using var rig = new ReceiveRig(0);
        rig.SendTo(2, 3);
        rig.Push(samples, () => 4800);
        rig.Finish();

        var links = rig.Log.Where(l => l.StartsWith("link ", StringComparison.Ordinal)).ToList();
        links.Should().NotBeEmpty();
        links.Should().OnlyContain(l => l.StartsWith("link #2 ", StringComparison.Ordinal) || l.StartsWith("link #3 ", StringComparison.Ordinal));
        links.Count.Should().BeLessThan(rig.Calls.Count(c => !(c.IsReport && c.Dest != 1)), "a heard burst that changes nothing raises nothing");
        foreach (string who in new[] { "#2", "#3" })
        {
            var mine = links.Where(l => l.StartsWith("link " + who + " ", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(" age ", StringComparison.Ordinal)]).ToList();
            mine.Zip(mine.Skip(1)).Should().OnlyContain(pair => pair.First != pair.Second, "each event differs from the one before it");
        }
    }

    [Fact]
    public void Events_Name_The_Address_The_Host_Sent_To_And_A_Station_It_Sent_Nothing_To_Raises_None()
    {
        TestBurst burst = TestBurst.Data(7, 1, Format, [TestBurst.Datagram(9, 30)], new PayloadPrelude(false, 77, false, null), symbols: 12);
        foreach (bool sentTo in new[] { false, true })
        {
            var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
            station.SetExternalBusy(true);
            var names = new List<LinkAddress>();
            var received = new List<ReceivedEvent>();
            station.PathReport += e => names.Add(e.Address);
            station.LinkState += e => names.Add(e.Address);
            station.Received += received.Add;
            if (sentTo)
            {
                station.Locate(ReceiveRig.Address(7), 7);
                station.Send(new byte[] { 1 }, ReceiveRig.Address(7), ReceiveRig.Own, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
                names.Clear();
            }

            station.Push(OneBurst(burst));
            station.DeliverResults();

            if (sentTo)
            {
                names.Should().NotBeEmpty().And.OnlyContain(n => n == ReceiveRig.Address(7));
            }
            else
            {
                names.Should().BeEmpty("the host has sent to no address, so there is none to tell it about");
            }

            received.Should().ContainSingle().Which.Should().Match<ReceivedEvent>(e => e.Addressing == Addressing.ToThisStation && e.ForKey == null);
        }
    }

    [Fact]
    public void More_Than_Four_Bursts_Waiting_Drops_The_Oldest_And_Raises_Nothing_For_It()
    {
        var b = new StreamBuilder().Silence(1.0);
        const int count = 8;
        for (int i = 0; i < count; i++)
        {
            b.Add(TestBurst.Data(5, 1, Format, [TestBurst.Datagram(20 + i, 30)], symbols: 8));
            b.Silence(0.6);
        }

        b.Silence(1.5).Noise(0.01, 31);

        // Every worker is busy for the whole stream: the test runs the header reads and nothing else, so each burst's receive waits in the queue.
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        station.SetExternalBusy(true);
        station.Locate(ReceiveRig.Address(5), 5);
        station.Send(new byte[] { 1 }, ReceiveRig.Address(5), ReceiveRig.Own, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        var datagrams = new List<byte[]>();
        var events = 0;
        station.Received += e => datagrams.Add(e.Datagram.ToArray());
        station.PathReport += _ => events++;
        station.LinkState += _ => events++;
        float[] samples = b.ToArray();
        for (int at = 0; at < samples.Length; at += 4800)
        {
            station.Push(samples.AsSpan(at, Math.Min(4800, samples.Length - at)));
            station.DeliverResults();
            while (station.HeaderJobPending)
            {
                station.RunOne();
                station.DeliverResults();
            }
        }

        station.BurstsQueued.Should().Be(4);
        station.BurstsDropped.Should().Be(count - 4);
        datagrams.Should().BeEmpty("nothing has been received yet");

        station.RunPending();
        station.DeliverResults();

        // The last four waiting are received; the four before them were dropped, and nothing says so but the count.
        datagrams.Should().BeEquivalentTo(new[] { 4, 5, 6, 7 }.Select(i => TestBurst.Datagram(20 + i, 30)), o => o.WithStrictOrdering());
        station.BurstsApplied.Should().Be(count, "a dropped burst is still a burst the station knew of, and is passed in its place");
        station.BurstsInFlight.Should().Be(0);
        events.Should().Be(1, "one neighbour's first link state, and nothing for the four dropped");
    }

    [Fact]
    public void A_Queued_Station_Delivers_Nothing_Until_Its_Jobs_Are_Run_And_Then_On_The_Next_Delivery()
    {
        TestBurst burst = TestBurst.Data(3, 1, Format, [TestBurst.Datagram(30, 40)], symbols: 12);
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var datagrams = new List<byte[]>();
        int signals = 0;
        station.Received += e => datagrams.Add(e.Datagram.ToArray());
        station.WorkAvailable += () => signals++;
        float[] samples = OneBurst(burst);

        for (int at = 0; at < samples.Length; at += 480)
        {
            station.Push(samples.AsSpan(at, Math.Min(480, samples.Length - at)));
            if (station.HasWork)
            {
                // A worker's turn: one job at a time, on this thread, since the test is its own worker.
                station.RunOne().Should().BeTrue();
            }
        }

        signals.Should().BeGreaterThan(0);
        station.HasWork.Should().BeFalse();
        station.RunOne().Should().BeFalse();
        station.RunPending().Should().Be(0);
        datagrams.Should().HaveCount(1);
        datagrams[0].Should().Equal(burst.Datagrams[0]);
    }

    [Fact]
    public void An_Inline_Station_Has_No_Work_For_Workers()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        station.Push(OneBurst(TestBurst.Data(3, 1, Format, [TestBurst.Datagram(31, 40)], symbols: 12)));
        station.HasWork.Should().BeFalse();
        station.RunOne().Should().BeFalse();
    }

    [Fact]
    public void Events_Cannot_Call_Back_Into_The_Station()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        Exception? caught = null;
        station.Received += _ =>
        {
            try
            {
                station.Push(new float[10]);
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        };
        station.Push(OneBurst(TestBurst.Data(3, 1, Format, [TestBurst.Datagram(32, 40)], symbols: 12)));
        caught.Should().NotBeNull();
    }

    [Fact]
    public void Push_Allocates_Nothing_In_Steady_State_With_Worker_Threads_And_Quiet_Audio()
    {
        using var rig = new ReceiveRig(2);

        // A steady tone, level 0.05, nowhere near a burst.
        var quiet = new float[480];
        for (int i = 0; i < quiet.Length; i++)
        {
            quiet[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 1100.0 * i / 48000));
        }

        for (int i = 0; i < 2000; i++)
        {
            rig.Station.Push(quiet);
        }

        int pushes = 0;
        AllocationMeasure.Minimum(() =>
        {
            for (int i = 0; i < 6000; i++)
            {
                rig.Station.Push(quiet);
            }

            pushes += 6000;
        }).Should().Be(0, "60 s of audio pushed");
        rig.Station.CandidatesSeen.Should().Be(0);
        rig.Station.SampleCount.Should().Be((2000L + pushes) * 480);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receiving_A_Burst_Allocates_Nothing_In_Steady_State_On_Either_Side_Of_The_Job_Queue(bool toEveryone)
    {
        // The test is its own worker on one thread, so one count covers both sides. The first burst of a shape builds what the shape needs (codes, pooled buffers, a neighbour's record); each later one reuses it.
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        int received = 0;
        station.Received += _ => received++;
        station.PathReport += _ => { };
        station.LinkState += _ => { };
        // The first burst, then further bursts of the same shape, all made before any is measured; a retry after a stray reading takes the next one.
        float[][] bursts = new float[1 + AllocationMeasure.Attempts][];
        for (int round = 0; round < bursts.Length; round++)
        {
            // Each burst carries its From keys (and a For to everyone): the learning table is written to on every one, and once it has the addresses that costs nothing.
            bursts[round] = OneBurst(TestBurst.Data(3, toEveryone ? 0 : 1, Format, [TestBurst.Datagram(40 + round, 40)], toEveryone ? null : new PayloadPrelude(false, 70, false, null), burstNumber: round, symbols: 12,
                from: [LinkAddress.FromAscii("X1").Key, LinkAddress.FromAscii("X2").Key], forKey: toEveryone ? LinkAddress.FromAscii("X3").Key : null), noise: 0.005, seed: round);
        }

        int next = 0;
        void Receive()
        {
            float[] samples = bursts[next++];
            for (int at = 0; at < samples.Length; at += 480)
            {
                station.Push(samples.AsSpan(at, Math.Min(480, samples.Length - at)));
                station.RunPending();
            }
        }

        long first = AllocationMeasure.Once(Receive);
        long steady = AllocationMeasure.Minimum(Receive);

        received.Should().Be(next);
        Console.WriteLine($"bytes allocated receiving a 12-symbol burst: first {first}, then (best of {next - 1}) {steady}");
        first.Should().BeGreaterThan(0, "the first burst builds what it needs");
        steady.Should().Be(0, "after the first burst of a shape everything is pooled");
    }

    [Fact]
    public void Gone_Is_Raised_Once_A_Neighbour_Has_Been_Silent_For_Ten_Minutes()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector");
        TestBurst burst = TestBurst.Data(3, 1, Format, [TestBurst.Datagram(50, 40)], symbols: 12);
        using var rig = new ReceiveRig(0);
        rig.SendTo(3);
        rig.Push(OneBurst(burst), () => 4800);
        rig.Finish();
        rig.Log.Count(l => l.StartsWith("link ", StringComparison.Ordinal)).Should().Be(2, "at the send, and when heard");

        rig.Station.Skip(9 * 60 * 48000);
        rig.Station.DeliverResults();
        rig.Log.Count(l => l.StartsWith("link ", StringComparison.Ordinal)).Should().Be(2, "nine minutes is not ten");

        rig.Station.Skip(2 * 60 * 48000);
        rig.Station.DeliverResults();
        rig.Log.Where(l => l.StartsWith("link ", StringComparison.Ordinal)).Last().Should().Contain("located False").And.EndWith("gone True");
        rig.Log.Count(l => l.StartsWith("link ", StringComparison.Ordinal)).Should().Be(3);
        rig.Station.LocatedNow(ReceiveRig.Address(3)).Should().Be(0, "forgotten with the station it lived at");

        rig.Push(OneBurst(TestBurst.Data(3, 1, Format, [TestBurst.Datagram(51, 40)], symbols: 12, from: [ReceiveRig.Address(3).Key])), () => 4800);
        rig.Finish();
        rig.Log.Count(l => l.StartsWith("link ", StringComparison.Ordinal)).Should().Be(4, "heard again, it is a new neighbour, and its address is learned afresh");
        rig.Station.LocatedNow(ReceiveRig.Address(3)).Should().Be(3);
        rig.Datagrams.Should().HaveCount(2);
    }
}
