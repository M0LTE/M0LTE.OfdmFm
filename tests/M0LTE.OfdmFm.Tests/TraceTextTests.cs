using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The wording of the station's trace, one test for each sentence shape, so that it cannot drift by accident (docs/trace.md lists them). Each builds the text for a constructed case and compares it with the exact words.
/// What a shape says is pinned here; that the station says it at the right moment is pinned by <see cref="StationTraceTests"/>.
/// </summary>
public class TraceTextTests
{
    // ---- how numbers are written ----

    [Theory]
    [InlineData(0.2545, "0.25")]
    [InlineData(1.4, "1.4")]
    [InlineData(9.99, "9.99")]
    [InlineData(12.5, "12.5")]
    [InlineData(54.94, "54.9")]
    [InlineData(70.4, "70.4")]
    [InlineData(70.0, "70")]
    [InlineData(123.4, "123")]
    public void A_rate_is_written_to_about_three_figures_without_trailing_zeros(double kbit, string text) => TraceText.Rate(kbit).Should().Be(text);

    [Theory]
    [InlineData(0.35, "0.35")]
    [InlineData(2.07, "2.07")]
    [InlineData(1.4, "1.4")]
    [InlineData(11.5, "11.5")]
    [InlineData(3.0, "3")]
    [InlineData(-0.001, "0")]
    public void A_length_of_time_is_written_to_two_places_without_trailing_zeros(double seconds, string text) => TraceText.Seconds(seconds).Should().Be(text);

    [Theory]
    [InlineData(30.6, "31")]
    [InlineData(-0.4, "0")]
    [InlineData(-12.6, "-13")]
    public void Decibels_are_whole_and_never_negative_zero(double db, string text) => TraceText.Db(db).Should().Be(text);

    [Fact]
    public void A_moment_is_written_as_the_lines_write_the_clock() => TraceText.Moment(18.4).Should().Be("18.40");

    [Fact]
    public void Datagrams_are_counted_in_words()
    {
        TraceText.Datagrams(0).Should().Be("no datagrams");
        TraceText.Datagrams(1).Should().Be("1 datagram");
        TraceText.Datagrams(3).Should().Be("3 datagrams");
    }

    // ---- Flow: receiving ----

    [Fact]
    public void Heard_for_this_station() => TraceText.HeardForUs("GB7RDG-1", 2, 31.2).Should().Be("Heard GB7RDG-1: 2 datagrams, signal 31 dB");

    [Fact]
    public void Heard_for_this_station_with_no_signal_figure() => TraceText.HeardForUs("GB7RDG-1", 1, double.NaN).Should().Be("Heard GB7RDG-1: 1 datagram");

    [Fact]
    public void Heard_for_this_station_with_nothing_in_it() => TraceText.HeardForUs("GB7RDG-1", 0, 31).Should().Be("Heard GB7RDG-1: no datagrams, signal 31 dB");

    [Fact]
    public void Heard_for_everyone() => TraceText.HeardForEveryone("GB7RDG-1", 1, 28).Should().Be("Heard GB7RDG-1: 1 datagram to everyone, signal 28 dB");

    [Fact]
    public void Heard_for_everyone_asking_for_an_address() => TraceText.HeardForEveryoneAsking("station 9", 1, "G4ABC-1", 28).Should().Be("Heard station 9: 1 datagram to everyone for G4ABC-1, signal 28 dB");

    [Fact]
    public void Heard_for_another_station() => TraceText.HeardForOther("GB7RDG-1", 3, "station 17", 40).Should().Be("Heard GB7RDG-1: 3 datagrams for station 17, signal 40 dB");

    [Fact]
    public void Heard_and_not_decoded() => TraceText.HeardNotDecoded("station 9").Should().Be("Heard station 9 but could not decode it");

    [Fact]
    public void Heard_and_not_readable() => TraceText.HeardUnreadable("station 9").Should().Be("Heard station 9: could not read what was in it");

    [Fact]
    public void Heard_an_identification() => TraceText.HeardIdentified("address 3fa2c1d0", "M0LTE", 33).Should().Be("Heard address 3fa2c1d0: identified as M0LTE, signal 33 dB");

    [Fact]
    public void Something_else_landed_on_a_burst() => TraceText.LandedOn("GB7RDG-1").Should().Be("Something else landed on the burst from GB7RDG-1: not used to judge the path");

    [Fact]
    public void The_path_is_good_for_a_rate_and_was_another() => TraceText.PathGood("GB7RDG-1", 70.4, 54.9).Should().Be("Path from GB7RDG-1 is good for 70.4 kbit/s (was 54.9)");

    [Fact]
    public void The_path_is_good_for_a_rate_first_time() => TraceText.PathGood("GB7RDG-1", 70.4, null).Should().Be("Path from GB7RDG-1 is good for 70.4 kbit/s");

    [Fact]
    public void Told_by_a_station_what_we_can_send_at() => TraceText.ToldBy("GB7RDG-1", 70.4, false).Should().Be("Told by GB7RDG-1: we can send at 70.4 kbit/s");

    [Fact]
    public void Told_by_a_station_our_burst_was_not_decoded() => TraceText.ToldBy("GB7RDG-1", 12.5, true).Should().Be("Told by GB7RDG-1: our burst was not decoded, we can send at 12.5 kbit/s");

    // ---- Flow: telling ----

    [Fact]
    public void Told_a_station_what_it_can_send_at() => TraceText.Told("GB7RDG-1", 70.4, false).Should().Be("Told GB7RDG-1 it can send at 70.4 kbit/s");

    [Fact]
    public void Told_a_station_its_burst_was_not_decoded() => TraceText.Told("station 9", 12.5, true).Should().Be("Told station 9 its burst was not decoded: it can send at 12.5 kbit/s");

    [Fact]
    public void Told_a_station_that_an_address_is_here() => TraceText.ToldHere("GB7RDG-1", "G4ABC-1").Should().Be("Told GB7RDG-1 that G4ABC-1 is here");

    // ---- Flow: sending ----

    [Fact]
    public void Refused_a_datagram_that_is_too_large() => TraceText.RefusedTooLarge("G4ABC-1", 400, 333, 0.25).Should().Be("Refused a 400 byte datagram for G4ABC-1: one burst carries 333 bytes at 0.25 kbit/s");

    [Fact]
    public void Refused_a_datagram_because_the_queue_is_full() => TraceText.RefusedFull("G4ABC-1", 30.4).Should().Be("Refused a datagram for G4ABC-1: queue full, 30.4 s on air waiting");

    [Fact]
    public void Refused_a_datagram_because_there_is_no_callsign() => TraceText.RefusedNoCallsign("G4ABC-1").Should().Be("Refused a datagram for G4ABC-1: no callsign set");

    [Fact]
    public void Dropped_datagrams_the_host_cancelled() => TraceText.DroppedCancelled("G4ABC-1", 2).Should().Be("Dropped 2 datagrams for G4ABC-1: cancelled by the host");

    [Fact]
    public void Dropped_a_datagram_that_no_longer_fits() => TraceText.DroppedNoLongerFits("G4ABC-1", 0.25).Should().Be("Dropped a datagram for G4ABC-1: no longer fits one burst at 0.25 kbit/s");

    [Fact]
    public void Sent_to_a_station_with_a_report_asked_for() => TraceText.Sent(3, "GB7RDG-1", 70.4, 0.35, true).Should().Be("Sent 3 datagrams to GB7RDG-1 at 70.4 kbit/s, 0.35 s on air, report asked for");

    [Fact]
    public void Sent_to_a_station_without_asking() => TraceText.Sent(1, "GB7RDG-1", 70.4, 0.35, false).Should().Be("Sent 1 datagram to GB7RDG-1 at 70.4 kbit/s, 0.35 s on air");

    [Fact]
    public void Sent_to_everyone_for_an_address_not_yet_located() => TraceText.Sent(1, TraceText.EveryoneFor("GB7RDG-1"), 0.25, 1.4, false).Should().Be("Sent 1 datagram to everyone for GB7RDG-1 at 0.25 kbit/s, 1.4 s on air");

    [Fact]
    public void Sent_to_everyone() => TraceText.Sent(2, "everyone", 0.25, 2.07, false).Should().Be("Sent 2 datagrams to everyone at 0.25 kbit/s, 2.07 s on air");

    [Fact]
    public void No_answer_the_first_time() => TraceText.NoAnswer("GB7RDG-1", 1, false).Should().Be("No answer from GB7RDG-1 (1 of 3)");

    [Fact]
    public void No_answer_the_second_time_when_the_next_burst_goes_slower() => TraceText.NoAnswer("GB7RDG-1", 2, true).Should().Be("No answer from GB7RDG-1 (2 of 3): sending slower");

    [Fact]
    public void No_answer_the_second_time_at_the_slowest_rate_already() => TraceText.NoAnswer("GB7RDG-1", 2, false).Should().Be("No answer from GB7RDG-1 (2 of 3)");

    [Fact]
    public void No_answer_the_third_time_when_the_rate_falls() => TraceText.NoAnswer("GB7RDG-1", 3, true).Should().Be("No answer from GB7RDG-1 (3 of 3): back to the slowest rate");

    [Fact]
    public void No_answer_the_third_time_at_the_slowest_rate_already() => TraceText.NoAnswer("GB7RDG-1", 3, false).Should().Be("No answer from GB7RDG-1 (3 of 3)");

    [Fact]
    public void No_answer_past_the_third_is_never_a_count_above_the_total()
    {
        TraceText.NoAnswer("GB7RDG-1", 4, true).Should().Be("No answer from GB7RDG-1 (still silent)");
        TraceText.NoAnswer("GB7RDG-1", 9, false).Should().Be("No answer from GB7RDG-1 (still silent)");
    }

    // ---- Flow: addresses ----

    [Fact]
    public void Found_an_address() => TraceText.Found("GB7RDG-1").Should().Be("Found GB7RDG-1");

    [Fact]
    public void Found_an_address_again() => TraceText.FoundAgain("GB7RDG-1").Should().Be("Found GB7RDG-1 again");

    [Fact]
    public void Found_an_address_at_another_station() => TraceText.FoundMoved("GB7RDG-1", 9, 5).Should().Be("Found GB7RDG-1 at station 9, was at station 5");

    [Fact]
    public void Lost_track_of_an_address_after_three_silences() => TraceText.LostAfterSilence("GB7RDG-1").Should().Be("Lost track of GB7RDG-1: sending to everyone until it is heard again");

    [Fact]
    public void Lost_track_of_an_address_not_heard_for_ten_minutes() => TraceText.LostUnheard("GB7RDG-1").Should().Be("Lost track of GB7RDG-1: not heard for 10 minutes, sending to everyone until it is heard again");

    [Fact]
    public void Lost_track_of_an_address_whose_station_number_changed_hands() => TraceText.LostChangedHands("GB7RDG-1", 17).Should().Be("Lost track of GB7RDG-1: station number 17 is now someone else, sending to everyone until it is heard again");

    [Fact]
    public void Lost_track_of_an_address_whose_station_number_is_in_use_by_two_stations() => TraceText.LostClash("GB7RDG-1", 17).Should().Be("Lost track of GB7RDG-1: station number 17 is in use by two stations, sending to everyone until it is heard again");

    // ---- Flow: the channel ----

    [Fact]
    public void The_channel_turned_busy_with_something_waiting() => TraceText.ChannelBusyWaiting(TraceText.Waiting(2, "GB7RDG-1")).Should().Be("Channel busy: waiting to send 2 datagrams to GB7RDG-1");

    [Fact]
    public void The_channel_turned_clear_with_something_waiting() => TraceText.ChannelClearWaiting(TraceText.Waiting(1, "GB7RDG-1")).Should().Be("Channel clear: waiting to send 1 datagram to GB7RDG-1");

    [Fact]
    public void The_channel_turned_busy_with_datagrams_waiting_for_several_addresses() => TraceText.ChannelBusyWaiting(TraceText.WaitingMany(5, 3)).Should().Be("Channel busy: waiting to send 5 datagrams for 3 addresses");

    // ---- Flow: identity ----

    [Fact]
    public void The_callsign_was_set() => TraceText.CallsignSet("M0LTE").Should().Be("Callsign set to M0LTE");

    [Fact]
    public void The_station_took_its_number() => TraceText.TookNumber(23).Should().Be("Took station number 23");

    [Fact]
    public void An_identification_was_sent() => TraceText.SentIdentification("M0LTE", 2.07).Should().Be("Sent identification as M0LTE, 2.07 s on air");

    [Fact]
    public void The_station_changed_number_because_another_held_it() => TraceText.NumberChangedInUse(23, 41).Should().Be("Changed station number from 23 to 41: 23 is in use by another station");

    [Fact]
    public void The_station_changed_number_because_it_was_told_to() => TraceText.NumberChangedTold(23, 41).Should().Be("Changed station number from 23 to 41: told to move by another station");

    [Fact]
    public void A_number_is_in_use_by_two_stations() => TraceText.NumberClash(23).Should().Be("Station number 23 is in use by two stations: will tell the later one to move");

    [Fact]
    public void A_number_is_in_use_by_two_stations_and_this_station_cannot_say_so() => TraceText.NumberClashListening(23).Should().Be("Station number 23 is in use by two stations: not telling, no callsign set");

    [Fact]
    public void A_number_changed_hands() => TraceText.NumberChangedHands(23).Should().Be("Station number 23 changed hands: forgetting what was known of it");

    [Fact]
    public void A_station_was_told_to_move() => TraceText.ToldToMove(23).Should().Be("Told the station using number 23 to move");

    [Fact]
    public void A_notice_to_move_was_heard() => TraceText.HeardMoveNotice(23).Should().Be("Heard a notice that the station using number 23 must move");

    // ---- Flow: decoding could not keep up ----

    [Fact]
    public void A_burst_was_dropped_before_decoding() => TraceText.DroppedBurst("GB7RDG-1").Should().Be("Dropped a burst from GB7RDG-1: decoding could not keep up");

    [Fact]
    public void A_burst_was_lost_before_it_could_be_copied() => TraceText.LostBurst("GB7RDG-1").Should().Be("Lost a burst from GB7RDG-1: its start had left the buffer");

    // ---- Detail ----

    [Fact]
    public void A_measurement_was_folded_in() =>
        TraceText.Measured("GB7RDG-1", 31.2, 29.84, 3.0, 70.4, 31, 5, false, null, null)
            .Should().Be("Measured GB7RDG-1: signal 31 dB, estimate now 29.8 dB, margin 3 dB, path good for 70.4 kbit/s [format 31, measurement 5]");

    [Fact]
    public void A_measurement_from_the_opening_with_a_faster_format_waiting_and_a_cap() =>
        TraceText.Measured("GB7RDG-1", 20, 18.04, 3.5, 12.5, 20, 6, true, 24, 18)
            .Should().Be("Measured GB7RDG-1 from the opening only: signal 20 dB, estimate now 18 dB, margin 3.5 dB, path good for 12.5 kbit/s [format 20, measurement 6, faster format 24 waits for a second burst, held to format 18 after a failure]");

    [Fact]
    public void The_channel_turned_busy_and_why()
    {
        TraceText.ChannelBusy(TraceText.CauseText(AccessCause.OwnTransmission)).Should().Be("Channel busy: this station is sending");
        TraceText.ChannelBusy(TraceText.CauseText(AccessCause.Carrier)).Should().Be("Channel busy: the radio reports a carrier");
        TraceText.ChannelBusy(TraceText.CauseText(AccessCause.HeardBurst)).Should().Be("Channel busy: another station is sending");
        TraceText.ChannelBusy(TraceText.CauseText(AccessCause.Judging)).Should().Be("Channel busy: a signal is being checked");
    }

    [Fact]
    public void The_channel_turned_clear() => TraceText.ChannelClear().Should().Be("Channel clear");

    [Fact]
    public void The_carrier_from_the_radio_changed()
    {
        TraceText.CarrierFromRadio(true).Should().Be("Carrier from the radio: present");
        TraceText.CarrierFromRadio(false).Should().Be("Carrier from the radio: gone");
        TraceText.CarrierFromRadio(null).Should().Be("Carrier from the radio: not available");
    }

    [Fact]
    public void A_request_to_send_was_made()
    {
        TraceText.AskedToSend("datagrams", TraceText.WaitOrdinary(0.8)).Should().Be("Asked to send datagrams: needs the channel clear for 0.8 s plus a random wait");
        TraceText.AskedToSend("a report", TraceText.WaitFirstSlot(0.05)).Should().Be("Asked to send a report: needs the channel clear for 0.05 s");
    }

    [Fact]
    public void A_random_wait_was_drawn() => TraceText.DrewWaitSlots(0.9, 2, 2, 0.9).Should().Be("Drew a random wait of 0.9 s: slot 2 of 2, 0.9 s each");

    [Fact]
    public void The_time_the_station_can_send_was_worked_out() => TraceText.CanSendAt(18.4).Should().Be("Can send at 18.40 if the channel stays clear");

    [Fact]
    public void A_request_was_handed_to_the_audio_layer() => TraceText.ClearToSend("datagrams", 1.2).Should().Be("Clear to send datagrams after waiting 1.2 s");

    [Fact]
    public void A_request_was_held_back()
    {
        TraceText.HeldBack("datagrams", TraceText.CauseText(AccessCause.HeardBurst)).Should().Be("Held back datagrams: another station is sending");
        TraceText.HeldBack("datagrams", TraceText.CauseText(AccessCause.ReportHoldOff)).Should().Be("Held back datagrams: a report just came in, ordinary traffic waits a second");
        TraceText.HeldBack("a report", TraceText.CauseText(AccessCause.NumberChanged)).Should().Be("Held back a report: the station number changed, so it is built again");
        TraceText.HeldBack("datagrams", TraceText.CauseText(AccessCause.Refused)).Should().Be("Held back datagrams: the audio layer would not key it");
    }

    [Fact]
    public void A_datagram_was_queued() => TraceText.Queued(30, "G4ABC-1", 2).Should().Be("Queued a 30 byte datagram for G4ABC-1: 2 waiting");

    [Fact]
    public void A_rate_was_chosen() => TraceText.Chose(54.9, "G4ABC-1", 29, "report said 70.4 kbit/s, 11.5 s ago").Should().Be("Chose 54.9 kbit/s for G4ABC-1 [format 29]: report said 70.4 kbit/s, 11.5 s ago");

    [Fact]
    public void A_report_is_owed() => TraceText.OwingReport("GB7RDG-1", "it asked for one").Should().Be("Owing GB7RDG-1 a report: it asked for one");

    [Fact]
    public void An_owed_report_was_dropped_because_data_carries_it() => TraceText.ReportCovered("GB7RDG-1").Should().Be("Dropped the report owed to GB7RDG-1: the datagrams going to it carry the news");

    [Fact]
    public void An_announcement_was_not_needed() => TraceText.AnnouncementCovered("G4ABC-1", "GB7RDG-1").Should().Be("No separate announcement of G4ABC-1 to GB7RDG-1: the datagrams going to it carry it");

    [Fact]
    public void An_answer_is_waited_for() => TraceText.AnswerWait("GB7RDG-1", 6.96).Should().Be("Waiting up to 6.96 s for an answer from GB7RDG-1");

    [Fact]
    public void An_answer_came_in_time() => TraceText.AnswerInTime("GB7RDG-1").Should().Be("Heard from GB7RDG-1 within the wait: not counted as silence");

    [Fact]
    public void An_identification_is_due()
    {
        TraceText.IdentificationDue(TraceWords.DueFirst).Should().Be("Identification due: none sent yet");
        TraceText.IdentificationDue(TraceWords.DueInterval).Should().Be("Identification due: 10 minutes since the last");
    }

    [Fact]
    public void An_identification_goes_first() => TraceText.IdentifyingFirst("datagrams").Should().Be("Sending identification first, before datagrams");

    [Fact]
    public void The_station_goes_straight_on_after_its_identification() => TraceText.GoingStraightOnAfterIdentification().Should().Be("Going straight on after the identification: no random wait");

    [Fact]
    public void All_traffic_is_held_off_for_another_stations_callsign() =>
        TraceText.HoldingForIdentification("GB7RDG-1", 14.62).Should().Be("Holding all traffic until 14.62: GB7RDG-1 is sending its callsign after its identification");

    [Fact]
    public void The_host_said_when_the_station_last_identified() => TraceText.LastIdentificationTold(180).Should().Be("Told by the host that the last identification was 180 s ago");

    [Fact]
    public void A_header_was_read_and_not_used() => TraceText.HeaderNotUsed("a version this station does not know").Should().Be("Header read but not used: a version this station does not know");

    [Fact]
    public void A_queue_became_full() => TraceText.QueueFull("G4ABC-1", 30.2).Should().Be("Queue for G4ABC-1 is full: 30.2 s on air waiting");

    [Fact]
    public void A_queue_can_take_datagrams_again() => TraceText.QueueWritable("G4ABC-1").Should().Be("Queue for G4ABC-1 can take datagrams again");

    [Fact]
    public void A_station_was_forgotten_with_no_address_of_the_host_in_it() => TraceText.ForgotStation(9, "not heard for 10 minutes").Should().Be("Forgot station 9: not heard for 10 minutes");

    [Fact]
    public void A_built_burst_was_discarded() => TraceText.BuiltDiscarded("burst").Should().Be("Built burst discarded: it carries the old station number, building it again");

    [Fact]
    public void The_host_was_told_a_burst_was_not_decoded() => TraceText.HostToldNotDecoded("G4ABC-1", 2).Should().Be("Told the host that its burst to G4ABC-1 was not decoded: 2 datagrams");

    [Fact]
    public void Ordinary_traffic_is_held_off_after_a_report() => TraceText.HoldingOrdinary("GB7RDG-1", 12.26).Should().Be("Holding ordinary traffic until 12.26: report from GB7RDG-1");

    [Fact]
    public void The_station_goes_straight_on_after_a_report() => TraceText.GoingStraightOn("GB7RDG-1").Should().Be("Going straight on to GB7RDG-1 after the report: no random wait");

    [Fact]
    public void A_notice_was_not_needed() => TraceText.NoticeCancelled(23).Should().Be("Not sending a notice for number 23: another station sent it first");

    // ---- what every line is ----

    [Fact]
    public void Every_shape_is_plain_ascii_starts_with_a_capital_and_ends_without_a_full_stop()
    {
        string[] all =
        [
            TraceText.HeardForUs("a", 1, 3), TraceText.HeardForEveryone("a", 1, 3), TraceText.HeardForEveryoneAsking("a", 1, "b", 3), TraceText.HeardForOther("a", 1, "b", 3), TraceText.HeardNotDecoded("a"), TraceText.HeardUnreadable("a"),
            TraceText.HeardIdentified("a", "M0LTE", 3), TraceText.LandedOn("a"), TraceText.PathGood("a", 1, 2), TraceText.ToldBy("a", 1, true), TraceText.Told("a", 1, true), TraceText.ToldHere("a", "b"),
            TraceText.RefusedTooLarge("a", 1, 2, 3), TraceText.RefusedFull("a", 1), TraceText.RefusedNoCallsign("a"), TraceText.DroppedCancelled("a", 1), TraceText.DroppedNoLongerFits("a", 1), TraceText.Sent(1, "a", 1, 1, true),
            TraceText.NoAnswer("a", 3, true), TraceText.Found("a"), TraceText.FoundAgain("a"), TraceText.FoundMoved("a", 1, 2), TraceText.LostAfterSilence("a"), TraceText.LostUnheard("a"), TraceText.LostChangedHands("a", 1), TraceText.LostClash("a", 1),
            TraceText.ChannelBusyWaiting("a"), TraceText.ChannelClearWaiting("a"), TraceText.CallsignSet("a"), TraceText.TookNumber(1), TraceText.SentIdentification("a", 1), TraceText.NumberChangedInUse(1, 2), TraceText.NumberChangedTold(1, 2),
            TraceText.NumberClash(1), TraceText.NumberClashListening(1), TraceText.NumberChangedHands(1), TraceText.ToldToMove(1), TraceText.HeardMoveNotice(1), TraceText.DroppedBurst("a"), TraceText.LostBurst("a"),
            TraceText.Measured("a", 1, 2, 3, 4, 5, 6, false, null, null), TraceText.ChannelBusy("a"), TraceText.ChannelClear(), TraceText.CarrierFromRadio(true), TraceText.AskedToSend("a", "b"), TraceText.DrewWaitSlots(1, 1, 2, 1),
            TraceText.CanSendAt(1), TraceText.ClearToSend("a", 1), TraceText.HeldBack("a", "b"), TraceText.Queued(1, "a", 1), TraceText.Chose(1, "a", 1, "b"), TraceText.OwingReport("a", "b"),
            TraceText.ReportCovered("a"), TraceText.AnnouncementCovered("a", "b"), TraceText.AnswerWait("a", 1), TraceText.AnswerInTime("a"), TraceText.IdentificationDue(TraceWords.DueFirst), TraceText.HeaderNotUsed("a"), TraceText.QueueFull("a", 1),
            TraceText.QueueWritable("a"), TraceText.ForgotStation(1, "a"), TraceText.BuiltDiscarded("a"), TraceText.HostToldNotDecoded("a", 1), TraceText.HoldingOrdinary("a", 1), TraceText.GoingStraightOn("a"), TraceText.GoingStraightOnAfterIdentification(), TraceText.HoldingForIdentification("a", 1), TraceText.IdentifyingFirst("a"), TraceText.LastIdentificationTold(1), TraceText.NoticeCancelled(1),
        ];
        foreach (string line in all)
        {
            line.Should().MatchRegex("^[A-Z][\\x20-\\x7E]*[^.\\x20]$", line);
            line.Should().NotContain("!");
        }
    }
}
