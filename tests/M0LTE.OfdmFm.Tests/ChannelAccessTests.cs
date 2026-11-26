using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Step 4, the rules on their own clock: each busy cause, the waits of each class, withdrawal, order and the hold-off.</summary>
public class ChannelAccessTests
{
    // After the 5 s the station listens for at the start.
    private const long T = 300000;

    private static float[] Burst() => new float[1000];

    [Fact]
    public void A_burst_makes_the_channel_busy_from_the_moment_it_is_known_to_its_last_sample()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.Busy.Should().BeFalse();
        rig.Access.HoldUntil(T + 10000);
        rig.Clock.AdvanceTo(T + 50000);

        rig.BusyLog.Should().Equal([(T, true), (T + 10000, false)]);
    }

    [Fact]
    public void A_burst_that_is_over_when_it_is_known_changes_nothing_but_when_the_channel_was_last_busy()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(T - 700);

        rig.BusyLog.Should().BeEmpty();
        rig.Access.ClearSince.Should().Be(T - 700);
    }

    [Fact]
    public void An_external_indication_is_busy_while_set_and_for_100_ms_after()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.SetExternal(true);
        rig.Access.SetExternal(true);
        rig.Clock.AdvanceTo(T + 10000);
        rig.Access.SetExternal(false);
        rig.Clock.AdvanceTo(T + 10000 + 4799);
        rig.Access.Busy.Should().BeTrue();
        rig.Clock.AdvanceTo(T + 20000);

        rig.BusyLog.Should().Equal([(T, true), (T + 14800, false)]);
    }

    [Fact]
    public void A_candidate_waiting_to_be_judged_is_busy_until_it_is()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.SetPending(true);
        rig.Clock.AdvanceTo(T + 30000);
        rig.Access.SetPending(false);

        rig.BusyLog.Should().Equal([(T, true), (T + 30000, false)]);
    }

    [Fact]
    public void The_stations_own_transmission_is_busy_from_the_grant_to_the_end_and_a_tail_after()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
        rig.GrantedAt(id).Should().Be(T, "the channel has been clear for longer than the first slot");
        rig.Clock.AdvanceTo(T + 700);
        rig.Access.Started(rig.Access.Active!, T + 700, false);
        rig.Clock.AdvanceTo(T + 10000);
        rig.Access.Ended(rig.Access.Active!, T + 10000);
        rig.Clock.AdvanceTo(T + 20000);

        rig.BusyLog.Should().Equal([(T, true), (T + 10000 + ChannelAccess.OwnTail, false)]);
    }

    [Fact]
    public void The_union_changes_only_when_every_cause_has_let_go()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(T + 10000);
        rig.Clock.AdvanceTo(T + 5000);
        rig.Access.SetExternal(true);
        rig.Clock.AdvanceTo(T + 9000);
        rig.Access.SetExternal(false);
        rig.Clock.AdvanceTo(T + 12000);
        rig.Access.SetPending(true);
        rig.Clock.AdvanceTo(T + 13000);
        rig.Access.SetPending(false);
        rig.Clock.AdvanceTo(T + 20000);

        rig.BusyLog.Should().Equal([(T, true), (T + 9000 + 4800, false)]);
    }

    [Fact]
    public void Nothing_is_sent_in_the_first_five_seconds()
    {
        var rig = new AccessRig(1);
        long a = rig.Access.Enqueue(Burst(), TransmitClass.Report).Id;
        rig.Clock.AdvanceTo(T);

        rig.GrantedAt(a).Should().Be(ChannelAccess.StartUpListen + ChannelAccess.FirstSlotClear);
    }

    [Fact]
    public void A_report_and_an_expedited_datagram_go_50_ms_after_a_burst_ends_and_ordinary_traffic_at_the_first_slot_time_plus_its_drawn_slots()
    {
        for (int seed = 1; seed <= 40; seed++)
        {
            foreach (bool external in new[] { false, true })
            {
                long end = T + 100000;
                var rig = new AccessRig(seed, external);
                rig.Clock.AdvanceTo(T);
                rig.Access.HoldUntil(end);
                long report = rig.Access.Enqueue(Burst(), TransmitClass.Report).Id;
                rig.Clock.AdvanceTo(end + 5000);
                rig.GrantedAt(report).Should().Be(end + 2400, $"seed {seed}");

                var rig2 = new AccessRig(seed, external);
                rig2.Clock.AdvanceTo(T);
                rig2.Access.HoldUntil(end);
                long expedited = rig2.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
                rig2.Clock.AdvanceTo(end + 5000);
                rig2.GrantedAt(expedited).Should().Be(end + 2400, $"seed {seed}");

                var rig3 = new AccessRig(seed, external);
                rig3.Clock.AdvanceTo(T);
                rig3.Access.HoldUntil(end);
                long ordinary = rig3.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
                rig3.Clock.AdvanceTo(end + 200000);
                int slots = new Random(seed).Next(external ? 4 : 2);
                int slot = external ? 4800 : ChannelAccess.DetectionLatency;
                rig3.GrantedAt(ordinary).Should().Be(end + 38400 + (slots * slot), $"seed {seed} external {external}");
            }
        }
    }

    [Fact]
    public void The_detection_latency_comes_from_the_detectors_sizes()
    {
        // The 1.5 kHz opening: 4224 of lead, the header read's 19008 (over its reference of 6 symbols and one more, 14784), and a frame's 20097 new values.
        ChannelAccess.DetectionLatency.Should().Be(4224 + 19008 + 20097);
        StreamingBurstDetector.Hop.Should().Be(20097);
        new AccessRig(1).Access.NoticeSamples.Should().Be(43329);
        new AccessRig(1, true).Access.NoticeSamples.Should().Be(4800);
    }

    [Fact]
    public void A_request_made_after_a_long_quiet_goes_at_once_whatever_its_class()
    {
        var rig = new AccessRig(3);
        rig.Clock.AdvanceTo(T);
        long ordinary = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;

        rig.GrantedAt(ordinary).Should().Be(T, "the channel has been clear far longer than the first slot time and the draw");
    }

    [Fact]
    public void A_request_granted_and_not_started_is_withdrawn_when_a_burst_turns_up_and_granted_again_later()
    {
        var rig = new AccessRig(5, true);
        rig.Clock.AdvanceTo(T);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.GrantedAt(id).Should().Be(T);
        rig.Access.Active!.State.Should().Be(ChannelAccess.RequestState.Granted);

        rig.Clock.AdvanceTo(T + 1000);
        rig.Access.HoldUntil(T + 60000);

        rig.WithdrawLog.Should().Equal([(id, T + 1000)]);
        rig.Access.Active.Should().BeNull();
        rig.Access.WaitingCount.Should().Be(1);
        rig.Events.Select(e => e.Stage).Should().Equal(AccessStage.Granted, AccessStage.Withdrawn);

        rig.Clock.AdvanceTo(T + 400000);
        var draws = new Random(5);
        draws.Next(4);
        int slots = draws.Next(4);
        rig.GrantLog.Should().HaveCount(2);
        rig.GrantLog[1].At.Should().Be(T + 60000 + 38400 + (slots * 4800), "a fresh draw: the first draw was made at the start, the second after the burst");
        rig.BusyLog.Select(b => b.Busy).Should().Equal(true, false, true);
    }

    [Fact]
    public void A_request_that_started_after_it_was_withdrawn_is_on_air_and_not_queued()
    {
        var rig = new AccessRig(5, true);
        rig.Clock.AdvanceTo(T);
        ChannelAccess.Request request = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary);
        rig.Clock.AdvanceTo(T + 1000);
        rig.Access.HoldUntil(T + 60000);
        request.State.Should().Be(ChannelAccess.RequestState.Waiting);

        rig.Access.Started(request, T + 900, late: true);

        rig.Access.WaitingCount.Should().Be(0);
        rig.Access.Active.Should().BeSameAs(request);
        request.State.Should().Be(ChannelAccess.RequestState.Started);
    }

    [Fact]
    public void The_stations_own_grant_does_not_withdraw_itself()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.Enqueue(Burst(), TransmitClass.Expedited);
        rig.Clock.AdvanceTo(T + 100000);

        rig.WithdrawLog.Should().BeEmpty();
        rig.Access.Active!.State.Should().Be(ChannelAccess.RequestState.Granted);
    }

    [Fact]
    public void Requests_go_in_order_of_class_then_arrival()
    {
        var rig = new AccessRig(1, true);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(T + 50000);
        long o1 = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        long o2 = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        long e1 = rig.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
        long r1 = rig.Access.Enqueue(Burst(), TransmitClass.Report).Id;
        long e2 = rig.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;

        var order = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            while (rig.Access.Active is null)
            {
                rig.Clock.AdvanceTo(rig.Clock.Now + 100);
            }

            order.Add(rig.Access.Active.Id);
            rig.PlayActive(3000);
        }

        order.Should().Equal(r1, e1, e2, o1, o2);
    }

    [Fact]
    public void The_next_request_waits_from_the_end_of_the_last_one_and_its_tail()
    {
        var rig = new AccessRig(1, true);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(T + 50000);
        long a = rig.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
        long b = rig.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
        rig.Clock.AdvanceTo(T + 50000 + 2400);
        rig.GrantedAt(a).Should().Be(T + 52400);
        rig.PlayActive(3000);
        rig.Clock.AdvanceTo(T + 200000);

        rig.GrantedAt(b).Should().Be(T + 52400 + 3000 + 4800 + 2400);
    }

    [Fact]
    public void After_a_lone_report_ordinary_traffic_leaves_the_channel_clear_for_a_second_and_expedited_traffic_does_not()
    {
        // A report ends at R; with a carrier indication and slot 0 the ordinary wait is 0.8 s, so the second is what holds it.
        int seed = Enumerable.Range(1, 100).First(s => new Random(s).Next(4) == 0);
        long reportEnd = T + 10000;
        var rig = new AccessRig(seed, true);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(reportEnd);
        rig.Access.HoldOffOrdinaryUntil(reportEnd + ChannelAccess.ReportHoldOff);
        long ordinary = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.Clock.AdvanceTo(reportEnd + 100000);
        rig.GrantedAt(ordinary).Should().Be(reportEnd + 48000);

        var rig2 = new AccessRig(seed, true);
        rig2.Clock.AdvanceTo(T);
        rig2.Access.HoldUntil(reportEnd);
        rig2.Access.HoldOffOrdinaryUntil(reportEnd + ChannelAccess.ReportHoldOff);
        long expedited = rig2.Access.Enqueue(Burst(), TransmitClass.Expedited).Id;
        rig2.Clock.AdvanceTo(reportEnd + 100000);
        rig2.GrantedAt(expedited).Should().Be(reportEnd + 2400);
    }

    [Fact]
    public void A_hold_off_that_arrives_after_an_ordinary_grant_takes_the_grant_back()
    {
        var rig = new AccessRig(1, true);
        rig.Clock.AdvanceTo(T);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.GrantedAt(id).Should().Be(T);
        rig.Access.HoldOffOrdinaryUntil(T + 5000);

        rig.WithdrawLog.Should().Equal([(id, T)]);
        rig.Clock.AdvanceTo(T + 20000);
        rig.GrantLog[1].At.Should().Be(T + 5000, "the second grant is at the hold-off's end: the channel has been clear for longer than any wait, and the draw has no effect");
    }

    [Fact]
    public void Switching_to_a_carrier_indication_mid_wait_shortens_the_slot_and_keeps_the_draw()
    {
        // Draws 1 of 2 slots without an indication, so the wait is 0.8 s plus one long slot; the indication appears half way through the long slot.
        int seed = Enumerable.Range(1, 100).First(s => new Random(s).Next(2) == 1);
        long end = T + 10000;
        var rig = new AccessRig(seed);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(end);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.Clock.AdvanceTo(end + 38400 + 1000);
        rig.Access.Draw.Should().Be(1);
        rig.GrantLog.Should().BeEmpty();

        // The indication is supplied (and clear): the slot is now 4800, so the wait is 0.8 s plus 4800 and the request goes at once if that has passed, or then.
        rig.Access.SetExternal(false);
        rig.Clock.AdvanceTo(end + 200000);
        rig.GrantedAt(id).Should().Be(end + 38400 + 4800, "the same draw, the new slot: 38400 + 4800 is later than the call at 38400 + 1000, so the timer was moved and fired exactly there");
    }

    [Fact]
    public void Switching_to_a_carrier_indication_after_the_slot_has_passed_grants_at_once()
    {
        int seed = Enumerable.Range(1, 100).First(s => new Random(s).Next(2) == 1);
        long end = T + 10000;
        var rig = new AccessRig(seed);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(end);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.Clock.AdvanceTo(end + 38400 + 20000);
        rig.GrantLog.Should().BeEmpty();

        rig.Access.SetExternal(false);

        rig.GrantedAt(id).Should().Be(end + 38400 + 20000, "38400 + 4800 has already passed: it goes at the call");
    }

    [Fact]
    public void An_indication_that_goes_busy_mid_wait_starts_the_wait_again_with_a_fresh_draw_after_its_100_ms()
    {
        long end = T + 10000;
        var rig = new AccessRig(9, true);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(end);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        var draws = new Random(9);
        int first = draws.Next(4);
        rig.Clock.AdvanceTo(end + 20000);
        rig.Access.Draw.Should().Be(first);
        rig.Access.SetExternal(true);
        rig.Clock.AdvanceTo(end + 30000);
        rig.Access.SetExternal(false);
        rig.Clock.AdvanceTo(end + 500000);

        int second = draws.Next(4);
        rig.GrantedAt(id).Should().Be(end + 30000 + 4800 + 38400 + (second * 4800));
    }

    [Fact]
    public void One_seed_gives_one_run_and_another_seed_another()
    {
        static List<long> Run(int seed)
        {
            var rig = new AccessRig(seed);
            var grants = new List<long>();
            rig.Clock.AdvanceTo(T);
            for (int i = 0; i < 60; i++)
            {
                rig.Access.HoldUntil(rig.Clock.Now + 20000);
                long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
                while (rig.Access.Active is null)
                {
                    rig.Clock.AdvanceTo(rig.Clock.Now + 1000);
                }

                grants.Add(rig.GrantedAt(id));
                rig.PlayActive(5000);
            }

            return grants;
        }

        Run(7).Should().Equal(Run(7));
        Run(7).Should().NotEqual(Run(8));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 4)]
    public void Over_many_draws_the_slot_counts_are_uniform_over_the_window(bool external, int window)
    {
        var rig = new AccessRig(12345, external);
        var counts = new int[window];
        rig.Clock.AdvanceTo(T);
        int slot = rig.Access.NoticeSamples;
        const int draws = 4000;
        for (int i = 0; i < draws; i++)
        {
            long end = rig.Clock.Now + 20000;
            rig.Access.HoldUntil(end);
            long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
            while (rig.Access.Active is null)
            {
                rig.Clock.AdvanceTo(rig.Clock.Now + 1000);
            }

            long at = rig.GrantedAt(id);
            ((at - end - 38400) % slot).Should().Be(0);
            counts[(int)((at - end - 38400) / slot)]++;
            rig.PlayActive(2000);
        }

        counts.Sum().Should().Be(draws);
        foreach (int count in counts)
        {
            // Expected draws / window; five standard deviations of a binomial is under 150 here.
            count.Should().BeInRange((draws / window) - 150, (draws / window) + 150);
        }
    }

    [Fact]
    public void The_single_delay_variant_is_the_designs_original_wait()
    {
        long end = T + 10000;
        var rig = new AccessRig(4) { Access = { Mode = AccessMode.SingleDelay } };
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(end);
        long id = rig.Access.Enqueue(Burst(), TransmitClass.Ordinary).Id;
        rig.Clock.AdvanceTo(end + 200000);

        rig.GrantedAt(id).Should().Be(end + 38400 + new Random(4).Next(19201));
    }

    [Fact]
    public void Steady_state_timers_are_few()
    {
        var rig = new AccessRig(1);
        rig.Clock.AdvanceTo(T);
        rig.Access.HoldUntil(T + 100000);
        rig.Access.Enqueue(Burst(), TransmitClass.Ordinary);
        for (int i = 0; i < 100; i++)
        {
            rig.Access.HoldUntil(T + 100000 + i);
            rig.Access.SetPending(i % 2 == 0);
        }

        rig.Clock.Timers.Should().BeLessThanOrEqualTo(2);
    }
}
