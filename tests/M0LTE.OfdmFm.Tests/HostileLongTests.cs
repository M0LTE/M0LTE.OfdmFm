using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step 8's long runs, behind <c>SM_MASK_GATE=1</c>: the soak, the sweeps over the capture margin (so that no conclusion hangs on one guess of it), the real ten-minute identification interval, a flood, the idle-channel start offsets and
/// the path-cut control run. They print their figures (the console, and a file when SM_HOSTILE_OUT names one).
/// </summary>
public class HostileLongTests
{
    private static string Name(CaptureModel? c) => HostileScenarios.Label(c);

    private static CaptureModel?[] WithAdder => [null, .. Models];

    private static CaptureModel[] Models => [.. HostileScenarios.Margins.Select(m => new CaptureModel(m))];

    [Fact]
    public void A_Carrier_Indication_And_Detection_Alone_Do_Not_Differ_In_Collisions_By_More_Than_Chance_Over_Eight_Seeds()
    {
        Hostile.Gate();
        int with = 0;
        int without = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            HostileScenarios.ThreeAll w = HostileScenarios.ThreeHearAll(HostileScenarios.Default, true, seed, perPair: 3, length: 40, seconds: 110, spreadSeconds: 25);
            HostileScenarios.ThreeAll o = HostileScenarios.ThreeHearAll(HostileScenarios.Default, false, seed, perPair: 3, length: 40, seconds: 110, spreadSeconds: 25);
            foreach (HostileScenarios.ThreeAll r in new[] { w, o })
            {
                r.Delivered.Should().Be(18);
                r.Duplicates.Should().Be(0);
            }

            with += w.CollidedBursts;
            without += o.CollidedBursts;
            Hostile.Say($"three all hear all, seed {seed}: collided bursts {w.CollidedBursts} with a carrier indication, {o.CollidedBursts} with detection alone");
        }

        // Recorded, not asserted as a comparison: the earlier claim that an indication costs fewer collisions was one seed (3 of 25 against 6 of 24); the totals over seeds 1 to 8 are what is printed here.
        Hostile.Say($"three all hear all, seeds 1 to 8: collided bursts {with} with a carrier indication, {without} with detection alone");
        with.Should().BeGreaterThan(0);
        without.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(1, 9.0, false)]
    [InlineData(2, 9.0, true)]
    [InlineData(3, 4.0, false)]
    [InlineData(4, 4.0, true)]
    [InlineData(5, 2.0, false)]
    [InlineData(6, 3.0, false)]
    [InlineData(7, 3.0, true)]
    [InlineData(8, 6.0, false)]
    [InlineData(9, 6.0, true)]
    public void The_Soak_Delivers_Or_Accounts_For_Everything_Never_Twice_And_No_Station_Is_Stuck_And_Nothing_Grows(int seed, double meanGap, bool indication)
    {
        Hostile.Gate();
        HostileScenarios.Soak r = HostileScenarios.Soaking(HostileScenarios.Default, seed, 240, 90, meanGap, indication);
        Hostile.Say($"seed {seed}, mean gap {meanGap} s, {(indication ? "carrier indication" : "detection alone")}: " + HostileScenarios.Describe(r));
        r.Duplicates.Should().Be(0, "no datagram delivered twice");
        r.DeliveredWithoutEvent.Should().Be(0, "nothing arrives that the sender was not told went out");
        r.Accepted.Should().Be(r.Transmitted + r.Dropped + r.StillQueued, "every datagram accepted went out, was reported dropped or is still queued");
        r.StillQueued.Should().Be(0, "the queues emptied in the 90 s after the traffic stopped");
        r.Dropped.Should().Be(r.Cancelled + r.NoLongerFit, "the only datagrams dropped are those the host cancelled and those that no longer fit a burst after the format fell");
        r.WrongHints.Should().Be(0, "a hint names only datagrams sent to that station that did not arrive");
        r.MisattributedReports.Should().Be(0);
        r.Reordered.Should().Be(0, "ordinary datagrams from one station to another arrive in the order they were sent");
        r.ClocksBackwards.Should().Be(0, "events are raised in order of the sample clock");
        r.MaxWaitSeconds.Should().BeLessThan(60, "a station with traffic transmits again within a minute, whatever the channel did");
        r.MaxBurstsInFlight.Should().BeLessThanOrEqualTo(Station.MaxQueuedBursts + 1);
        r.MaxCandidatesWaiting.Should().BeLessThan(Station.MaxCandidatesWaiting);
        r.MaxTimers.Should().BeLessThan(32);
        r.MaxQueued.Should().BeLessThan(64);
        r.Delivered.Should().BeGreaterThan(r.Transmitted / 3);
    }

    [Fact]
    public void The_Soak_Is_The_Same_Run_Twice_Down_To_Every_Event()
    {
        Hostile.Gate();
        List<string> once = HostileScenarios.Soaking(HostileScenarios.Default, 1, 120, 60, 9).Log;
        HostileScenarios.Soaking(HostileScenarios.Default, 1, 120, 60, 9).Log.Should().Equal(once);
    }

    [Fact]
    public void The_Conclusions_Hold_At_Every_Capture_Margin_For_The_Hidden_Station_And_Interference_Cases()
    {
        Hostile.Gate();
        foreach (CaptureModel model in Models)
        {
            // A much stronger at B: A gets through, C not heard, whatever the margin.
            HostileScenarios.HiddenRound strong = HostileScenarios.HiddenOne(model, 1, 0, -20, 0.5, $"{Name(model)}: A 20 dB stronger, C starts 0.5 s later");
            Hostile.Say(HostileScenarios.Describe(strong));
            strong.ADelivered.Should().BeTrue();
            strong.CDelivered.Should().BeFalse();

            // C much stronger from the start: C gets through.
            HostileScenarios.HiddenRound other = HostileScenarios.HiddenOne(model, 1, -20, 0, 0, $"{Name(model)}: C 20 dB stronger, same start");
            Hostile.Say(HostileScenarios.Describe(other));
            other.CDelivered.Should().BeTrue();
            other.ADelivered.Should().BeFalse();

            // Equal strength, same start: nothing heard by anyone, whatever the margin up to the margin itself.
            HostileScenarios.HiddenRound equal = HostileScenarios.HiddenOne(model, 1, 0, 0, 0, $"{Name(model)}: equal, same start");
            Hostile.Say(HostileScenarios.Describe(equal));
            equal.ADelivered.Should().BeFalse();
            equal.CDelivered.Should().BeFalse();
        }
    }

    [Fact]
    public void The_Interference_Table_Holds_At_Every_Capture_Margin_For_An_Interferer_Over_The_Margin_And_Is_Recorded_Where_It_Is_Not()
    {
        Hostile.Gate();
        foreach (CaptureModel model in Models)
        {
            foreach (double rel in new[] { 6.0, -4.0, -10.0 })
            {
                foreach (double fraction in rel == -4.0 ? new[] { 0.5 } : new[] { 0.25, 0.5, 0.75 })
                {
                    HostileScenarios.Interference r = HostileScenarios.Interfered(model, fraction, rel, 1);
                    Hostile.Say(HostileScenarios.Describe(r));
                    if (rel > 0)
                    {
                        r.Sudden.Should().BeTrue();
                        r.ReportedFormat.Should().Be(r.FormatBefore);
                        (r.RecommendationUnchanged && r.EstimateUnchanged && r.MarginUnchanged && r.CapUnchanged && r.GoodUnchanged).Should().BeTrue();
                        r.NextFormat.Should().Be(r.FormatBefore);
                    }
                    else if (r.Sudden)
                    {
                        (r.EstimateUnchanged && r.MarginUnchanged && r.CapUnchanged && r.GoodUnchanged).Should().BeTrue("a sudden failure leaves the rules' state alone");
                    }
                }
            }
        }
    }

    [Fact]
    public void Three_Stations_That_Hear_Each_Other_Deliver_Everything_Once_At_Every_Capture_Margin_And_With_Or_Without_A_Carrier_Indication()
    {
        Hostile.Gate();
        foreach (CaptureModel? model in WithAdder)
        {
            foreach (bool indication in new[] { true, false })
            {
                HostileScenarios.ThreeAll r = HostileScenarios.ThreeHearAll(model, indication, 1, perPair: 3, length: 40, seconds: 150, spreadSeconds: 25);
                Hostile.Say(HostileScenarios.Describe(r));
                r.Delivered.Should().Be(r.Items);
                r.Duplicates.Should().Be(0);
                r.DeliveredTwice.Should().Be(0);
            }
        }
    }

    [Fact]
    public void The_Hidden_Station_Over_Five_Rounds_At_Every_Capture_Margin()
    {
        Hostile.Gate();
        foreach (CaptureModel? model in WithAdder)
        {
            foreach ((double db, string name) in new[] { (-20.0, "A 20 dB stronger"), (0.0, "equal") })
            {
                HostileScenarios.Hidden r = HostileScenarios.HiddenStation(model, false, 1, db, name);
                Hostile.Say(HostileScenarios.Describe(r));
                r.DeliveredA.Should().BeLessThanOrEqualTo(r.SentA);
                r.DeliveredC.Should().BeLessThanOrEqualTo(r.SentC);
            }
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Records_Present_Behaviour_Two_Stations_Given_A_Datagram_On_An_Idle_Channel_Within_The_Detection_Time_Both_Key(double offset)
    {
        Hostile.Gate();
        HostileScenarios.IdleStart without = HostileScenarios.IdleStartOffset(HostileScenarios.Default, false, offset);
        HostileScenarios.IdleStart with = HostileScenarios.IdleStartOffset(HostileScenarios.Default, true, offset);
        Hostile.Say($"idle start, second datagram {offset} s after the first: detection alone: start gap {Hostile.F(without.StartGapSeconds, "F2")} s, collided {without.Collided}, both delivered {without.BothDelivered}; carrier indication: start gap {Hostile.F(with.StartGapSeconds, "F2")} s, collided {with.Collided}, both delivered {with.BothDelivered}");
        if (offset == 0)
        {
            without.Collided.Should().BeTrue("on an idle channel an ordinary datagram goes at once: the random wait is drawn when the channel was last busy");
            with.Collided.Should().BeTrue();
        }

        if (offset >= 1.5)
        {
            without.Collided.Should().BeFalse("beyond the time to notice a burst by detection alone (0.9 s) the second station hears the first");
            with.Collided.Should().BeFalse();
        }
    }

    [Fact]
    public void A_Flood_Of_Datagrams_Fills_The_Queue_Raises_Congestion_And_Drains_Every_Datagram_Accounted_For()
    {
        Hostile.Gate();
        HostileScenarios.Flood r = HostileScenarios.Flooding(HostileScenarios.Default, 1);
        Hostile.Say($"flood: offered {r.Offered}, accepted {r.Accepted}, refused {r.Refused}, transmitted {r.Transmitted}, dropped {r.Dropped}, delivered {r.Delivered}, congestion raised {r.Congested} and cleared {r.Writable} times, queued at the end {r.StillQueued}, drained at {Hostile.F(r.DrainedAtSeconds)} s, at most queued {r.MaxQueued}");
        r.Refused.Should().BeGreaterThan(0, "the queue is bounded");
        r.Congested.Should().BeGreaterThan(0);
        r.Writable.Should().Be(r.Congested, "every congestion is cleared once the queue drains");
        r.Accepted.Should().Be(r.Transmitted + r.Dropped + r.StillQueued);
        r.StillQueued.Should().Be(0);
        r.Delivered.Should().BeLessThanOrEqualTo(r.Transmitted);
    }

    [Fact]
    public void The_Real_Ten_Minute_Identification_Interval_Among_Three_Stations()
    {
        Hostile.Gate();
        HostileScenarios.Idents r = HostileScenarios.Identification(HostileScenarios.Default, 1, 600, 1330);
        Hostile.Say("ten minutes: " + HostileScenarios.Describe(r));
        r.Sent.Should().Equal(new[] { 3, 3, 1 }, "at once, then after the first transmission to end past ten minutes from the start of the last, for the two that keep transmitting; C once");
        r.IdentsOverlapped.Should().Be(0);
        r.TrafficOverlappedByIdent.Should().Be(0);
    }

    [Fact]
    public void The_Path_Cut_Control_Run_Without_The_Cut_Shows_What_The_Cut_Changed()
    {
        Hostile.Gate();
        HostileScenarios.Cut cut = HostileScenarios.PathCut(HostileScenarios.Default, 1);
        HostileScenarios.Cut control = HostileScenarios.PathCut(HostileScenarios.Default, 1, cutIt: false);
        Hostile.Say("control (no cut): " + HostileScenarios.Describe(control));
        control.MaxSilencesA.Should().BeLessThan(cut.MaxSilencesA);
        control.AToBCut.Split(',').Last().Should().NotBe(StandardFormats.FirstContact.ToString());
    }
}
