using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 6 of docs/station-design.md, "Reports": the stations tell each other what the path supports, by the protocol's rules, and the host is told when a burst it sent was heard and not decoded.
/// Every test steps the simulated channel (SimChannel, from step 5); none waits. Datagrams are 30 bytes so that a burst is a few symbols at any format. The numbers each test prints are the ones quoted in the pull request.
/// </summary>
public class StationReportTests
{
    private static long Count(SimNode n, ModemSentKind kind = ModemSentKind.Report) => n.ModemSent.Count(m => m.Kind == kind);

    private static double Kbit(double bitsPerSecond) => bitsPerSecond / 1000.0;

    private static string Seconds(long samples) => (samples / 48000.0).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Runs until <paramref name="done"/> holds, in steps of a quarter second, at most <paramref name="limit"/> seconds.</summary>
    private static void RunUntil(SimRig rig, Func<bool> done, double limit = 30)
    {
        for (double t = 0; t < limit && !done(); t += 0.25)
        {
            rig.Run(0.25);
        }

        done().Should().BeTrue("the condition was met within the time allowed");
    }

    /// <summary>The first sample after the last burst station <paramref name="n"/> put on air, from the audio layer's own reports.</summary>
    private static long EndOfLast(SimNode n) => n.Transmissions[^1].Start + n.Transmissions[^1].Length;

    [Fact]
    public void The_Mute_After_Own_Transmission_Is_Shorter_Than_The_First_Slot_Plus_The_Lead_In_And_Is_The_Own_Tail()
    {
        // The first slot: a reply starts 50 ms after the channel clears and its first training sample is a lead-in later. The mute must end before that, with a margin, or a station is deaf to the replies the slot exists for.
        int firstTrainingSample = ChannelAccess.FirstSlotClear + BurstRenderer.DefaultLeadSamples;
        firstTrainingSample.Should().Be(6624, "50 ms and 88 ms");
        Station.MuteAfterOwnTransmission.Should().Be(ChannelAccess.OwnTail, "one figure for the tail the channel stays busy and the audio is ignored");
        Station.MuteAfterOwnTransmission.Should().Be(4800);
        (firstTrainingSample - Station.MuteAfterOwnTransmission).Should().BeGreaterThan(1500, "a margin of 38 ms");
    }

    [Fact]
    public void One_Way_Traffic_Climbs_Because_The_Receiver_Reports_When_Asked()
    {
        // A sends one datagram every 10 s to B and B sends nothing: the climb is all lone reports. A asks on every burst that has no fresh report (the protocol's own rule), so B reports about every 30 s.
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        for (int i = 0; i < 12; i++)
        {
            rig.Send(1, 2, 100 + i).Should().Be(SendResult.Accepted);
            rig.Run(10);
        }

        // Every datagram, once, in order.
        b.Received.Select(r => r.Data).Should().BeEquivalentTo(Enumerable.Range(100, 12).Select(i => SimRig.Datagram(i, 30)), o => o.WithStrictOrdering());

        // The first burst is at first contact; the sender is at a format within a rung or so of the receiver's own recommendation, at over 200 times the rate, by the 11th burst.
        a.Transmitted[0].Event.RateBitsPerSecond.Should().Be(SimRig.FirstContactRate);
        rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? atB).Should().BeTrue();
        double supported = StandardFormats.Rate(atB!.Recommended!.Value) * 1000.0;
        double reached = a.Transmitted[^1].Event.RateBitsPerSecond;
        int firstNear = a.Transmitted.Select(t => t.Event.RateBitsPerSecond).ToList().FindIndex(r => r >= 0.9 * supported) + 1;
        Console.WriteLine($"one way: first contact {Kbit(SimRig.FirstContactRate):F2} kbit/s, receiver supports {Kbit(supported):F1} kbit/s (format {atB.Recommended}), sender at {Kbit(reached):F1} kbit/s after 12 bursts, within 90 percent from burst {firstNear}, with {Count(b)} lone reports");
        reached.Should().BeGreaterThan(0.9 * supported);
        firstNear.Should().BeLessThanOrEqualTo(11);
        reached.Should().BeGreaterThan(200 * SimRig.FirstContactRate);

        // Four lone reports did it, all from B, none failed, none from A; each was heard and made a path report and a link state at A.
        Count(b).Should().Be(4);
        Count(a).Should().Be(0);
        a.PathReports.Should().HaveCount(4).And.OnlyContain(p => !p.Failed && p.Station == SimRig.Name(2));
        a.PathReports.Select(p => p.RecommendedFormat!.Value).Should().BeInAscendingOrder(Comparer<int>.Create((x, y) => StandardFormats.Rate(x).CompareTo(StandardFormats.Rate(y))));
        int? lastReported = a.PathReports[^1].RecommendedFormat;
        a.LinkStates.Should().Contain(l => l.Knowledge == LinkKnowledge.Report && l.Format == lastReported, "the link state shows where the format came from, and follows the reports");
        a.LinkStates[^1].Knowledge.Should().Be(LinkKnowledge.Report);
        a.LinkStates[^1].Format.Should().Be(a.Transmitted[^1].Event.Format);

        // The first one goes alone 1.5 s after the end of the burst it answers, to the sample: nothing went back to ride on.
        SimTransmission first = b.Transmissions[0];
        first.Start.Should().Be(a.Transmissions[0].Start + a.Transmissions[0].Length + Station.AskedReportWaitSamples);
        b.ModemSent[0].Station.Should().Be(SimRig.Name(1));
    }

    [Fact]
    public void Two_Way_Traffic_Settles_Each_Direction_With_The_Recommendations_Riding_In_Preludes()
    {
        // Each datagram is answered at once by an expedited one, as an acknowledgement would be. The recommendation rides in the preludes: the first burst each way is at first contact and from the fourth the rate is at the top.
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        for (int i = 0; i < 8; i++)
        {
            rig.Send(1, 2, 100 + i).Should().Be(SendResult.Accepted);
            RunUntil(rig, () => b.Received.Count == i + 1);
            rig.Send(2, 1, 200 + i, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
            RunUntil(rig, () => a.Received.Count == i + 1);
            rig.Run(0.5);
        }

        b.Received.Should().HaveCount(8);
        a.Received.Should().HaveCount(8);
        string Rates(SimNode n) => string.Join(" ", n.Transmitted.Select(t => Kbit(t.Event.RateBitsPerSecond).ToString("F1", CultureInfo.InvariantCulture)));
        Console.WriteLine($"two way: A kbit/s {Rates(a)}; B kbit/s {Rates(b)}; lone reports A {Count(a)} B {Count(b)}; covered by data A {rig.Stations[0].ReportsCovered} B {rig.Stations[1].ReportsCovered}");
        a.Transmitted[0].Event.RateBitsPerSecond.Should().Be(SimRig.FirstContactRate);
        b.Transmitted[0].Event.RateBitsPerSecond.Should().BeGreaterThan(1000, "B's first reply already carries and is sent at B's own estimate");
        a.Transmitted.Skip(3).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 60000);
        b.Transmitted.Skip(3).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 60000);
        (Count(a) + Count(b)).Should().BeLessThanOrEqualTo(1, "the reports rode on data going back; A's host answered with ordinary data, which waits longer than the 1.5 s that a requested report does, so one went alone");
        a.PathReports.Should().NotBeEmpty();
        b.PathReports.Should().NotBeEmpty();
        rig.Stations[1].ReportsCovered.Should().BeGreaterThan(0, "B's expedited reply took the slot with the recommendation in its prelude");
    }

    /// <summary>A path that carries about 30 kbit/s: settled quickly by asking on every burst, ready for a change.</summary>
    private static SimRig Settled(double gain = 0.1, int bursts = 8)
    {
        var rig = new SimRig(2, gain: gain);
        rig.RunTo(6);
        for (int i = 0; i < bursts; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(6);
        }

        return rig;
    }

    [Fact]
    public void A_10_dB_Drop_Costs_One_Burst_The_Failure_Is_Reported_Alone_And_The_Hint_Names_Exactly_That_Burst()
    {
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        int before = a.Transmitted.Count;
        int received = b.Received.Count;
        int atSettled = a.Transmitted[^1].Event.Format;
        long reportsBefore = Count(b);
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-10);
        var lost = new List<long>();
        for (int i = 0; i < 4; i++)
        {
            long tag = 300 + i;
            int got = b.Received.Count;
            rig.Send(1, 2, (int)tag).Should().Be(SendResult.Accepted);
            rig.Run(8);
            if (b.Received.Count == got)
            {
                lost.Add(tag);
            }
        }

        string formats = string.Join(" ", a.Transmitted.Skip(before).Select(t => $"{t.Event.Tag.Value}:f{t.Event.Format}"));
        Console.WriteLine($"10 dB drop: was at format {atSettled} ({Kbit(StandardFormats.Rate(atSettled) * 1000):F1} kbit/s), then {formats}; lost {string.Join(",", lost)}; hints {string.Join(";", a.NotDecoded.Select(h => $"{Seconds(h.Clock)} s tags {string.Join(",", h.Tags)}"))}");
        lost.Should().Equal(300L);
        b.Received.Count.Should().Be(received + 3);

        // B reported "failed" alone, once, and A heard it: a path report marked failed, and the hint names exactly the lost burst's datagram. (A second lone report followed, a requested one: two bursts had gone since A had
        // heard anything, so the sender's rules set "report wanted" again. The kinds are counted apart.)
        Console.WriteLine($"10 dB drop: lone reports after the drop {Count(b) - reportsBefore}, owed {rig.Stations[1].ReportsOwedCount}");
        rig.Stations[1].ReportsOwedCount.Failed.Should().Be(1);
        a.PathReports.Count(p => p.Failed).Should().Be(1);
        a.PathReports[^1].Failed.Should().BeFalse("the next report, if any, says nothing failed");
        (long Clock, LinkAddress Station, long[] Tags) hint = a.NotDecoded.Should().ContainSingle().Subject;
        hint.Tags.Should().Equal(300L);
        hint.Station.Should().Be(SimRig.Name(2));
        hint.Clock.Should().BeGreaterThan(EndOfLast(a) - 8 * 48000 * 3, "raised soon after the burst");

        // The report was a response to that burst and no other: the hint came after the report was heard.
        List<string> log = rig.Channel.Log;
        int pathAt = log.FindIndex(l => l.Contains(" S1 path ", StringComparison.Ordinal) && l.EndsWith("failed True", StringComparison.Ordinal));
        int hintAt = log.FindIndex(l => l.Contains(" S1 notdecoded ", StringComparison.Ordinal));
        pathAt.Should().BeGreaterThan(0);
        hintAt.Should().Be(pathAt + 1);
    }

    [Fact]
    public void A_Failure_Whose_Verdict_Lands_After_The_First_Slot_Is_Still_Reported_At_The_First_Clear_Moment_And_The_Hint_Names_The_Burst()
    {
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-10);

        // B's receive jobs are parked: its verdict on the burst arrives when the test says, no real time involved.
        rig.Stations[1].ReceivesHeld = true;
        int requestsBefore = b.Requests.Count;
        long reportsBefore = Count(b);
        rig.Send(1, 2, 500).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => a.Transmitted.Count == 9);
        long burstEnd = EndOfLast(a);
        long firstSlot = burstEnd + ChannelAccess.FirstSlotClear;
        rig.Channel.Run(burstEnd + (6 * 48000));
        b.Requests.Count.Should().Be(requestsBefore, "no verdict yet, so nothing owed: the first slot has gone by with nothing sent");
        a.NotDecoded.Should().BeEmpty();
        rig.Stations[1].ReportsOwedCount.Failed.Should().Be(0);

        // The verdict lands 6 s after the burst: the failed report goes at the first clear moment, at the next frame's end, which is when results are applied.
        long release = rig.Channel.Clock;
        rig.Stations[1].ReceivesHeld = false;
        rig.Run(1.0);
        b.Requests.Count.Should().Be(requestsBefore + 1);
        SimRequest request = b.Requests[^1];
        Console.WriteLine($"late verdict: burst ended {Seconds(burstEnd)} s, first slot {Seconds(firstSlot)} s, verdict let go {Seconds(release)} s, report requested {Seconds(request.Clock)} s ({(request.Clock - release) / 48000.0:F2} s after), hint at {Seconds(a.NotDecoded[0].Clock)} s");
        request.Clock.Should().BeGreaterThanOrEqualTo(release);
        request.Clock.Should().BeLessThan(release + StreamingBurstDetector.Hop + 1, "within a frame of the verdict being let go");
        request.Clock.Should().BeGreaterThan(firstSlot + (5 * 48000), "long after the first slot");
        Count(b).Should().Be(reportsBefore + 1);
        rig.Stations[1].ReportsOwedCount.Failed.Should().Be(1);
        rig.Run(2.0);
        a.PathReports.Count(p => p.Failed).Should().Be(1);
        a.NotDecoded.Should().ContainSingle().Which.Tags.Should().Equal(500L);
    }

    [Fact]
    public void A_Newer_Verdict_For_The_Same_Station_Replaces_A_Failed_Report_That_Has_Not_Gone()
    {
        // Two bursts fail while the channel is not free for B (its carrier indication is held): B owes one failed report, the newer, and sends one.
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-10);
        rig.Stations[1].SetExternalBusy(true);
        long reportsBefore = Count(b);
        rig.Send(1, 2, 600).Should().Be(SendResult.Accepted);
        rig.Run(5);
        rig.Send(1, 2, 601).Should().Be(SendResult.Accepted);
        rig.Run(5);
        rig.Stations[1].ReportsOwedCount.Failed.Should().BeGreaterThanOrEqualTo(2);
        rig.Stations[1].OwedReports.Should().ContainSingle("one owed for A, the newest verdict's");
        Count(b).Should().Be(reportsBefore);
        rig.Stations[1].SetExternalBusy(false);
        rig.Run(3);
        Count(b).Should().Be(reportsBefore + 1, "one report, not two");
        rig.Stations[1].OwedReports.Should().BeEmpty();
    }

    [Fact]
    public void A_Drop_Report_Goes_Alone_In_The_First_Slot_When_Nothing_Is_Going_Back()
    {
        // A's report from B has aged (100 s, three rungs slower), so a burst at 6 dB less decodes, and B's recommendation falls below what B last told A: B reports at once, alone, with nothing marked failed.
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Run(100);
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-6);
        long reportsBefore = Count(b);
        var owedBefore = rig.Stations[1].ReportsOwedCount;
        int got = b.Received.Count;
        rig.Send(1, 2, 700).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => a.Transmitted.Count == 9);
        long burstEnd = EndOfLast(a);
        rig.Run(4);
        b.Received.Count.Should().Be(got + 1, "it decoded");
        var owed = rig.Stations[1].ReportsOwedCount;
        (owed.Drop - owedBefore.Drop).Should().Be(1);
        (owed.Failed - owedBefore.Failed).Should().Be(0);
        Count(b).Should().Be(reportsBefore + 1);
        SimTransmission report = b.Transmissions[^1];
        Console.WriteLine($"drop alone: burst ended {Seconds(burstEnd)} s, report on air from {Seconds(report.Start)} s ({(report.Start - burstEnd) / 48000.0:F2} s after, the verdict taking its frame)");
        report.Start.Should().BeLessThan(burstEnd + (2 * 48000), "well inside the 1.5 s a requested report would wait for data");
        a.PathReports[^1].Failed.Should().BeFalse();
        a.NotDecoded.Should().BeEmpty();
    }

    [Fact]
    public void A_Drop_Report_Is_Not_Sent_When_Data_Going_Back_To_That_Station_Goes_First_And_Carries_The_Recommendation()
    {
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Run(100);
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-6);

        // B's carrier indication holds the channel while A's burst arrives, and B's host has an acknowledgement queued for A (expedited).
        rig.Stations[1].SetExternalBusy(true);
        rig.Send(2, 1, 800, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        long reportsBefore = Count(b);
        var owedBefore = rig.Stations[1].ReportsOwedCount;
        long coveredBefore = rig.Stations[1].ReportsCovered;
        int transmissionsBefore = b.Transmissions.Count;
        rig.Send(1, 2, 700).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => a.Transmitted.Count == 9);
        rig.Run(4);
        (rig.Stations[1].ReportsOwedCount.Drop - owedBefore.Drop).Should().Be(1, "the verdict came in while the channel was not free");
        b.Transmissions.Count.Should().Be(transmissionsBefore, "nothing has gone yet");
        a.Received.Should().BeEmpty();
        int pathReports = a.PathReports.Count;

        rig.Stations[1].SetExternalBusy(false);
        rig.Run(4);
        rig.Stations[1].ReportsCovered.Should().Be(coveredBefore + 1);
        Count(b).Should().Be(reportsBefore, "no lone report: the data went first");
        a.Received.Should().ContainSingle().Which.Data.Should().Equal(SimRig.Datagram(800, 30));
        a.PathReports.Count.Should().Be(pathReports + 1, "the recommendation rode in the prelude");
        a.PathReports[^1].Failed.Should().BeFalse();
        rig.Stations[1].OwedReports.Should().BeEmpty();
    }

    [Fact]
    public void A_Requested_Report_Rides_On_Data_Going_Back_In_The_First_Slot_And_No_Lone_Report_Follows()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(1, 2, 101, reportWanted: true).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => b.Received.Count == 1);
        rig.Stations[1].ReportsOwedCount.Should().Be((0L, 0L, 1L));
        rig.Send(2, 1, 102, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(6);
        a.Received.Should().ContainSingle();
        a.PathReports.Should().ContainSingle("the recommendation came in the reply's prelude");
        Count(b).Should().Be(0);
        rig.Stations[1].ReportsCovered.Should().Be(1);
        rig.Stations[1].AskedReportsOwed.Should().Be(0);

        // And the 1.5 s timer found nothing to send.
        rig.Run(4);
        Count(b).Should().Be(0);
    }

    [Fact]
    public void A_Requested_Report_Goes_Alone_Once_The_Wait_Has_Passed_With_Nothing_Going_Back_And_Then_The_Reporter_Goes_Straight_On_With_Its_Own_Data()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        rig.Send(1, 2, 101, reportWanted: true).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => b.Received.Count == 1);
        long end = EndOfLast(a);
        long due = end + Station.AskedReportWaitSamples;
        rig.Stations[1].AskedReportsOwed.Should().Be(1);
        rig.Channel.Run(due - 1);
        Count(b).Should().Be(0, "still waiting for data to ride on");
        b.Requests.Should().BeEmpty();
        rig.Channel.Run(due + 1000);
        Count(b).Should().Be(1);
        b.Transmissions[0].Start.Should().Be(due, "alone, the moment the wait ran out");
        SimTransmission report = b.Transmissions[0];
        long reportEnd = report.Start + report.Length;
        rig.Stations[1].AskedReportsOwed.Should().Be(0);

        // While it is on the air B queues an ordinary datagram, and half a second after it is over, A (which has applied it by then) does. B just sent the report and is not waiting for an answer: it goes straight on, a first slot after its own tail. A heard the report and
        // leaves the channel clear for a second.
        rig.Send(2, 1, 102).Should().Be(SendResult.Accepted);
        rig.Channel.Run(reportEnd + 24000);
        rig.Send(1, 2, 103).Should().Be(SendResult.Accepted);
        rig.Run(8);
        a.PathReports.Should().HaveCountGreaterThanOrEqualTo(1);
        SimTransmission backFromB = b.Transmissions[1];
        SimTransmission fromA = a.Transmissions[1];
        Console.WriteLine($"after a lone report: report ended {Seconds(reportEnd)} s, B's own data {(backFromB.Start - reportEnd) / 48000.0:F3} s later, A's {(fromA.Start - reportEnd) / 48000.0:F3} s later");
        backFromB.Start.Should().Be(reportEnd + ChannelAccess.OwnTail + ChannelAccess.FirstSlotClear, "straight on, no 0.8 s wait and no draw");
        fromA.Start.Should().BeGreaterThanOrEqualTo(reportEnd + ChannelAccess.ReportHoldOff, "the station that heard it leaves the channel clear for about a second");
        a.Received.Should().ContainSingle();
        b.Received.Should().HaveCount(2);
    }

    [Fact]
    public void A_Reporter_Still_Waiting_For_An_Answer_To_A_Burst_Of_Its_Own_Does_Not_Go_Straight_On()
    {
        // A's burst fails at B, whose receive is parked. After A's burst has been read (its header) B sends A a burst that asks for a report, then the verdict lands and B sends its failed report while A has not answered: B is still
        // waiting for an answer, so the datagram it has queued behind the report waits the ordinary 0.8 s and a draw, and does not go straight on.
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.Channel.Link(a, b).Gain *= SimChannel.Db(-10);
        rig.Stations[1].ReceivesHeld = true;
        rig.Send(1, 2, 900).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => a.Transmitted.Count == 9);
        rig.Run(0.6);
        rig.Send(2, 1, 901, priority: SendPriority.Expedited, reportWanted: true).Should().Be(SendResult.Accepted);
        rig.Send(2, 1, 902).Should().Be(SendResult.Accepted);
        RunUntil(rig, () => b.Transmitted.Count == 1);
        rig.Stations[1].AwaitingAnswerFrom(1).Should().BeTrue("B asked and has heard nothing from A since");
        long reports = Count(b);
        rig.Stations[1].ReceivesHeld = false;
        RunUntil(rig, () => Count(b) == reports + 1);
        rig.Run(0.5);
        SimTransmission report = b.Transmissions.Last(t => t.Length < 100000 && b.ModemSent.Count > 0);
        RunUntil(rig, () => b.Transmitted.Count == 2, 40);
        SimTransmission data = b.Transmissions[^1];
        long reportEnd = report.Start + report.Length;
        Console.WriteLine($"waiting for an answer: report ended {Seconds(reportEnd)} s, the queued datagram {(data.Start - reportEnd) / 48000.0:F2} s later");
        data.Start.Should().BeGreaterThanOrEqualTo(reportEnd + ChannelAccess.OwnTail + ChannelAccess.OrdinaryClear);
    }

    [Fact]
    public void Nothing_Is_Reported_For_A_Burst_To_Everyone_Or_To_A_Third_Station()
    {
        // Three stations. A sends to C and to everyone; B hears all of it (A is weak at B, so B fails some of what A sends to C at the faster formats) and reports nothing: it is neither the destination nor, for a broadcast, owed anything.
        var rig = new SimRig(3, connect: false);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        SimNode c = rig.Nodes[2];
        rig.Channel.Connect(a, c, 1.0, 0.004);
        rig.Channel.Connect(b, c, 1.0, 0.004);
        rig.Channel.Connect(a, b, 0.02, 0.004);
        rig.RunTo(6);
        for (int i = 0; i < 8; i++)
        {
            rig.Send(1, 3, 100 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
            rig.Send(1, LinkAddress.Everyone, 200 + i).Should().Be(SendResult.Accepted);
            rig.Run(8);
        }

        rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? atB).Should().BeTrue();
        atB!.Measures.Should().BeGreaterThan(0, "B heard A and measured it");
        Console.WriteLine($"third station: B measured A {atB.Measures} times, owed {rig.Stations[1].ReportsOwedCount}, sent {Count(b)} reports; C sent {Count(c)}; A sent {a.Transmitted.Count} bursts at formats {string.Join(",", a.Transmitted.Where(t => t.Event.Station != LinkAddress.Everyone).Select(t => t.Event.Format))}");
        rig.Stations[1].ReportsOwedCount.Should().Be((0L, 0L, 0L));
        Count(b).Should().Be(0);
        b.Transmissions.Should().BeEmpty();
        b.PathReports.Should().BeEmpty();
        b.Received.Should().OnlyContain(r => r.Everyone, "only the broadcasts were for B");

        // C, the destination, reported, and A climbed.
        Count(c).Should().BeGreaterThan(0);
        a.Transmitted.Where(t => t.Event.Station == SimRig.Name(3)).Last().Event.RateBitsPerSecond.Should().BeGreaterThan(20 * SimRig.FirstContactRate);
    }

    [Fact]
    public void A_Path_Cut_And_Restored_Falls_To_First_Contact_Then_Climbs_Straight_Back()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        rig.RunTo(6);
        for (int i = 0; i < 8; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(6);
        }

        int settled = a.Transmitted[^1].Event.Format;
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? far).Should().BeTrue();
        rig.Channel.Link(a, b).Gain = 0;
        rig.Channel.Link(b, a).Gain = 0;
        int cutStart = a.Transmitted.Count;
        var silences = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            rig.Send(1, 2, 300 + i).Should().Be(SendResult.Accepted);
            RunUntil(rig, () => a.Transmitted.Count == cutStart + i + 1);
            SimTransmission tx = a.Transmissions[^1];
            rig.Channel.Run(tx.Start + tx.Length + Station.AnswerWaitSamples(tx.Length) + 10);
            silences.Add(far!.Silences);
        }

        int[] duringCut = [.. a.Transmitted.Skip(cutStart).Select(t => t.Event.Format)];
        rig.Channel.Link(a, b).Gain = 1.0;
        rig.Channel.Link(b, a).Gain = 1.0;
        int restoreAt = a.Transmitted.Count;
        for (int i = 0; i < 8; i++)
        {
            rig.Send(1, 2, 400 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(6);
        }

        int[] afterRestore = [.. a.Transmitted.Skip(restoreAt).Select(t => t.Event.Format)];
        Console.WriteLine($"cut and restored: settled at format {settled} ({Kbit(StandardFormats.Rate(settled) * 1000):F1} kbit/s); silences after each burst into the cut {string.Join(",", silences)}; formats during the cut {string.Join(",", duringCut)}; after restoring {string.Join(",", afterRestore)} ({string.Join(" ", a.Transmitted.Skip(restoreAt).Select(t => Kbit(t.Event.RateBitsPerSecond).ToString("F1", CultureInfo.InvariantCulture)))} kbit/s)");

        // The silences count up one a burst; the format steps down as the protocol says (the same after one silence, two rungs slower after two, first contact from three) and stays there.
        // A report less than 30 s old means "report wanted" is clear, so the first bursts into the cut do not ask and count no silence; once the report is old enough, or two bursts have gone since anything was heard, each burst asks and each
        // is a silence. The format each burst goes at follows the silences before it: the same after none or one, two rungs slower after two, first contact from three.
        silences.Should().Equal(0, 0, 1, 2, 3, 4);
        for (int k = 0; k < duringCut.Length; k++)
        {
            int before = k == 0 ? 0 : silences[k - 1];
            int expected = before >= 3 ? StandardFormats.FirstContact : before == 2 ? StandardFormats.Slower(settled, 2) : settled;
            duringCut[k].Should().Be(expected, $"burst {k} into the cut, after {before} silences");
        }

        // Restored: the first burst goes at first contact and asks, B reports at once (it kept its estimate), and the very next burst is back at the rate it had.
        afterRestore[0].Should().Be(StandardFormats.FirstContact);
        far!.Silences.Should().Be(0, "a report that gives a recommendation clears them");
        StandardFormats.Rate(afterRestore[^1]).Should().BeGreaterThan(StandardFormats.Rate(settled) * 0.9);
        b.Received.Count.Should().Be(8 + 8, "the six sent into the cut were never heard");
    }

    private static List<string> Scenario(int block, int seed)
    {
        var rig = new SimRig(2, block, seed, gain: 0.1);
        rig.RunTo(6);
        for (int i = 0; i < 5; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true);
            rig.Run(6);
        }

        rig.Send(2, 1, 150, priority: SendPriority.Expedited);
        rig.Run(4);
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Gain *= SimChannel.Db(-10);
        for (int i = 0; i < 3; i++)
        {
            rig.Send(1, 2, 300 + i);
            rig.Run(8);
        }

        return rig.Channel.Log;
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_Both_Stations_Run_Twice_And_At_Other_Lockstep_Block_Sizes()
    {
        List<string> reference = Scenario(4800, 1);
        reference.Should().Contain(l => l.Contains(" modem sent Report ", StringComparison.Ordinal));
        reference.Should().Contain(l => l.Contains(" notdecoded ", StringComparison.Ordinal));
        reference.Count(l => l.Contains(" path ", StringComparison.Ordinal)).Should().BeGreaterThan(4);

        // Run twice: line for line, clocks included.
        Scenario(4800, 1).Should().Equal(reference);

        // Another block size cuts the steps differently. A station applies the results at a frame's end, whatever the block, so every clock is the same as well: the logs are identical.
        foreach (int block in new[] { 1000, 100000 })
        {
            Scenario(block, 1).Should().Equal(reference, $"block {block}");
        }

        // And the seed matters.
        Scenario(4800, 2).Should().NotEqual(reference);
    }

    // ---- the hint, driven by hand: the station's own transmissions and a stream of reports ----

    private static Station Make(StreamDriver? _ = null)
    {
        var station = (new Station(1, new Random(1), runJobsInline: true) { IdentificationSuspended = true }).WithIdentity("M0LTE");
        station.Bind(LinkAddress.FromAscii("S4"), 4);
        return station;
    }

    /// <summary>Silence up to the sample where the report's first training sample goes, then the report.</summary>
    private static void PlaceAt(StreamBuilder builder, TestBurst burst, long firstSample)
    {
        long silence = firstSample - burst.Lead - builder.Count;
        silence.Should().BeGreaterThanOrEqualTo(0);
        builder.Silence((silence + 0.5) / 48000.0);
        builder.Add(burst).Start.Should().Be(firstSample);
    }

    [Fact]
    public void A_Failed_Report_Raises_The_Hint_For_The_Latest_Burst_That_Ended_Before_It_Began_Once()
    {
        long txStart = 6 * 48000L;
        const int playLength = 3000;
        long txEnd = txStart + playLength;
        TestBurst report = TestBurst.Report(4, 1, 80, true, FormatOpening.Full);
        var builder = new StreamBuilder();
        long first = txEnd + ChannelAccess.OwnTail + 6000;
        PlaceAt(builder, report, first);
        PlaceAt(builder, report, first + (3 * 48000));
        builder.Silence(4.0);
        Station station = Make();
        var driver = new StreamDriver(station, builder.ToArray(), autoPlay: true, playLength: playLength);
        var events = new List<string>();
        station.PathReport += e => events.Add("path " + e.Failed);
        station.BurstNotDecoded += e => events.Add("hint " + string.Join(",", e.Tags.Select(t => t.Value)) + " " + (e.Station == LinkAddress.FromAscii("S4")));
        driver.At(txStart, () => station.Send(SimRig.Datagram(9, 30), LinkAddress.FromAscii("S4"), new HostTag(9), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted));
        driver.PushTo(builder.Count, () => 4800);
        driver.Requests.Should().ContainSingle();
        driver.Requests[0].Request.EarliestSample.Should().Be(txStart);

        // Two failed reports, one hint: the burst has been attributed.
        events.Should().Equal("path True", "hint 9 True", "path True");
    }

    [Fact]
    public void A_Report_That_Began_Before_The_Latest_Burst_Ended_Cannot_Be_About_It_And_Raises_Nothing()
    {
        // The station sends tag 9, then, a moment into a failed report from S4 (read from its first training sample, which comes before the station keys), sends tag 10 over it. "Cannot": the burst's last sample is later than the report's first training sample.
        long txStart = 6 * 48000L;
        const int playLength = 3000;
        long txEnd = txStart + playLength;
        TestBurst report = TestBurst.Report(4, 1, 80, true, FormatOpening.Full);
        var builder = new StreamBuilder();
        long r1 = txEnd + ChannelAccess.OwnTail + 6000;
        long second = r1 + (6 * 48000);
        PlaceAt(builder, report, r1);
        PlaceAt(builder, report, second);
        builder.Silence(4.0);
        Station station = Make();
        var driver = new StreamDriver(station, builder.ToArray(), autoPlay: true, playLength: playLength);
        var hints = new List<(long Clock, string Tags)>();
        station.BurstNotDecoded += e => hints.Add((station.SampleCount, string.Join(",", e.Tags.Select(t => t.Value))));
        driver.At(txStart, () => station.Send(SimRig.Datagram(9, 30), LinkAddress.FromAscii("S4"), new HostTag(9), priority: SendPriority.Expedited));
        driver.At(r1 + 3000, () => station.Send(SimRig.Datagram(10, 30), LinkAddress.FromAscii("S4"), new HostTag(10), priority: SendPriority.Expedited));
        driver.PushTo(builder.Count, () => 4800);

        // Tag 10's burst ended at r1 + 6000, after the first report began: that report is not about it, and the station does not say it was. The second report began after it: it is.
        driver.Requests.Should().HaveCount(2);
        hints.Select(h => h.Tags).Should().Equal("10");
        hints[0].Clock.Should().BeGreaterThan(second);
    }
}
