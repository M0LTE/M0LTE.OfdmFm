using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The lines of the station's trace as runs produce them: the two runs read as a story, in order, and a run for each family of lines. The runs are the existing rigs on the sample clock. Wording is pinned by <see cref="TraceTextTests"/>;
/// here it is the moment and the names. A station whose host has not sent to or from an address knows it only by its key, so the first lines of a run name some stations <c>address</c> and a hex number.
/// </summary>
public class StationTraceScenarioTests
{
    private static LinkAddress Name(string text) => LinkAddress.FromAscii(text);

    // ---- the two runs ----

    [Fact]
    public void Two_stations_read_as_a_story_at_the_flow_level()
    {
        (TraceTap s1, TraceTap s2) = TraceScenarios.TwoStations();

        // S1 sends two datagrams; at first contact a burst carries one, so two bursts go. S2 tells it what the path supports, S1 hears S2's own datagram and tells S2 in turn, and its next datagram goes at the new rate.
        s1.Lines.Should().Equal(
            "   5.80  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for",
            "   7.97  Channel clear: waiting to send 1 datagram to S2",
            "   8.77  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for",
            "  11.57  Told by S2: we can send at 70.4 kbit/s",
            "  12.82  Told by S2: we can send at 70.4 kbit/s",
            "  20.78  Heard S2: 1 datagram, signal 52 dB",
            "  20.78  Path from S2 is good for 70.4 kbit/s",
            "  21.85  Told S2 it can send at 70.4 kbit/s",
            "  40.00  Sent 1 datagram to S2 at 70.4 kbit/s, 0.35 s on air");

        // S2 has not sent to S1's address until 20 s, so it knows S1 by its key until then.
        s2.Lines.Should().Equal(
            "   8.22  Heard address f3b8bcd6: 1 datagram, signal 75 dB",
            "   8.22  Path from address f3b8bcd6 is good for 70.4 kbit/s",
            "  10.89  Told address f3b8bcd6 it can send at 70.4 kbit/s",
            "  11.15  Heard address f3b8bcd6: 1 datagram, signal 74 dB",
            "  12.34  Told address f3b8bcd6 it can send at 70.4 kbit/s",
            "  20.00  Sent 1 datagram to S1 at 54.9 kbit/s, 0.35 s on air, report asked for",
            "  22.45  Told by S1: we can send at 70.4 kbit/s",
            "  40.46  Heard S1: 1 datagram, signal 51 dB");
    }

    [Fact]
    public void Three_stations_read_as_a_story_at_the_flow_level()
    {
        (TraceTap s1, TraceTap s2, TraceTap s3) = TraceScenarios.ThreeStations();

        // S1 is out of hearing of S2 until 12 s. It hears S3 talk to S2 (a station it knows nothing of), then asks for S2 by sending to everyone for it, is answered, and sends to everyone.
        s1.Lines.Should().Equal(
            "  12.41  Heard address fa3a87b8: 1 datagram for station 2, signal 49 dB",
            "  12.41  Path from address fa3a87b8 is good for 70.4 kbit/s",
            "  22.00  Sent 1 datagram to everyone for S2 at 0.25 kbit/s, 2.24 s on air",
            "  26.22  Found S2",
            "  26.22  Heard S2: no datagrams, signal 49 dB",
            "  26.22  Path from S2 is good for 70.4 kbit/s",
            "  26.22  Told by S2: we can send at 70.4 kbit/s",
            "  40.00  Sent 1 datagram to everyone at 0.25 kbit/s, 2.07 s on air");

        // S2 asks for S3 by sending to everyone for it; S3 answers by sending it a datagram. Later S1's burst for S2 reaches it, and S2, which has sent from the address S1 asked for, tells S1 that it is here.
        s2.Lines.Should().Equal(
            "   5.80  Sent 1 datagram to everyone for S3 at 0.25 kbit/s, 2.24 s on air",
            "  12.41  Found S3",
            "  12.41  Heard S3: 1 datagram, signal 49 dB",
            "  12.41  Path from S3 is good for 70.4 kbit/s",
            "  12.41  Told by S3: we can send at 70.4 kbit/s",
            "  13.85  Told S3 it can send at 70.4 kbit/s",
            "  24.55  Heard address f3b8bcd6: 1 datagram to everyone for S2, signal 72 dB",
            "  24.55  Path from address f3b8bcd6 is good for 70.4 kbit/s",
            "  25.74  Told address f3b8bcd6 that S2 is here",
            "  42.13  Heard address f3b8bcd6: 1 datagram to everyone, signal 72 dB");

        // S3 overhears all of it: it is asked for (not yet a station that has sent from the address, so nothing is owed), it talks to S2, and it hears the answer S2 gives S1.
        s3.Lines.Should().Equal(
            "   8.22  Heard address fefb9a0f: 1 datagram to everyone for address fa3a87b8, signal 72 dB",
            "   8.22  Path from address fefb9a0f is good for 70.4 kbit/s",
            "  12.00  Told S2 it can send at 70.4 kbit/s",
            "  12.00  Sent 1 datagram to S2 at 54.9 kbit/s, 0.35 s on air, report asked for",
            "  14.50  Told by S2: we can send at 70.4 kbit/s",
            "  24.55  Heard address f3b8bcd6: 1 datagram to everyone for S2, signal 72 dB",
            "  24.55  Path from address f3b8bcd6 is good for 70.4 kbit/s",
            "  26.22  Heard S2: no datagrams for address f3b8bcd6, signal 49 dB",
            "  42.13  Heard address f3b8bcd6: 1 datagram to everyone, signal 72 dB");
    }

    // ---- identity ----

    [Fact]
    public void The_callsign_and_the_number_are_the_first_two_lines_and_giving_the_same_callsign_again_says_nothing()
    {
        var station = new Station(23, new Random(1));
        var tap = new TraceTap(station);
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.SetIdentity("M0XYZ").Should().Be(IdentityResult.Different);
        station.SetIdentity("not a callsign").Should().Be(IdentityResult.Invalid);
        tap.Lines.Should().Equal("   0.00  Callsign set to M0LTE", "   0.00  Took station number 23");
    }

    [Fact]
    public void A_station_that_picks_its_own_number_says_which_it_took()
    {
        var station = new Station(new Random(3));
        var tap = new TraceTap(station);
        station.SetIdentity("M0LTE");
        tap.Texts.Should().HaveCount(2);
        tap.Texts[1].Should().Be($"Took station number {station.StationNumber}");
    }

    [Fact]
    public void A_station_told_its_number_is_in_use_moves_and_says_so()
    {
        var station = new Station(23, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station);
        station.HearHeaderFrom(23);
        station.StationNumber.Should().NotBe(23);
        tap.Texts.Should().Equal($"Changed station number from 23 to {station.StationNumber}: 23 is in use by another station");
    }

    // ---- sending: what is refused and dropped ----

    [Fact]
    public void A_datagram_is_refused_with_the_reason_and_dropped_with_the_reason()
    {
        var station = new Station(1, new Random(1));
        var tap = new TraceTap(station);
        LinkAddress to = Name("G4ABC-1");
        LinkAddress own = Name("M0LTE-1");

        station.Send(new byte[10], to, own, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.NoIdentity);
        station.SetIdentity("M0LTE");
        tap.Events.Clear();

        // Not located: a burst for it goes to everyone with a For, and carries a little less.
        station.Send(new byte[400], to, own, LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.TooLarge);
        station.Locate(Name("G4XYZ-1"), 2);
        station.Send(new byte[400], Name("G4XYZ-1"), own, LinkProtocol.NoLayer3, new HostTag(3)).Should().Be(SendResult.TooLarge);
        for (int i = 0; i < 10; i++)
        {
            station.Send(new byte[100], to, own, LinkProtocol.NoLayer3, new HostTag(10 + i)).Should().Be(SendResult.Accepted);
        }

        station.Send(new byte[100], to, own, LinkProtocol.NoLayer3, new HostTag(30)).Should().Be(SendResult.QueueFull);
        station.Cancel(new HostTag(10)).Should().Be(1);
        station.Cancel(to).Should().Be(9);

        tap.Texts.Should().Equal(
            "Refused a 400 byte datagram for G4ABC-1: one burst carries 331 bytes at 0.25 kbit/s",
            "Refused a 400 byte datagram for G4XYZ-1: one burst carries 333 bytes at 0.25 kbit/s",
            "Refused a datagram for G4ABC-1: queue full, 32.53 s on air waiting",
            "Dropped 1 datagram for G4ABC-1: cancelled by the host",
            "Dropped 9 datagrams for G4ABC-1: cancelled by the host");
    }

    [Fact]
    public void The_refusal_names_the_address_by_the_bytes_the_host_gave_even_when_it_asks_by_key()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station);
        LinkAddress to = Name("G4ABC-1");
        station.Send(new byte[10], to, Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1));
        station.Send(new byte[400], LinkAddress.FromKey(to.Key), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.TooLarge);
        tap.Texts.Should().Equal("Refused a 400 byte datagram for G4ABC-1: one burst carries 331 bytes at 0.25 kbit/s");
    }

    [Fact]
    public void A_queue_filling_and_draining_is_a_detail_line_with_each_datagram_queued()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station, TraceLevel.Detail);
        LinkAddress to = Name("G4ABC-1");
        for (int i = 0; i < 10; i++)
        {
            station.Send(new byte[100], to, Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(i));
        }

        station.Cancel(to);
        string[] detail = [.. tap.Events.Where(e => e.Level == TraceLevel.Detail).Select(e => e.Text)];
        detail.Should().ContainInOrder(
            "Queued a 100 byte datagram for G4ABC-1: 1 waiting",
            "Queued a 100 byte datagram for G4ABC-1: 9 waiting",
            "Queued a 100 byte datagram for G4ABC-1: 10 waiting",
            "Queue for G4ABC-1 is full: 32.53 s on air waiting",
            "Queue for G4ABC-1 can take datagrams again");
        tap.Events.Where(e => e.Text.StartsWith("Queued", StringComparison.Ordinal)).Should().OnlyContain(e => e.Level == TraceLevel.Detail);
    }

    // ---- waiting for an answer, and losing track of an address ----

    [Fact]
    public void Three_silences_in_a_row_lose_the_address_and_the_next_datagram_goes_to_everyone_for_it()
    {
        (SimRig rig, TraceTap s1, TraceTap s2) = TraceScenarios.Silence();
        s1.LinesAt(TraceLevel.Flow).Should().Equal(
            "   5.80  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for",
            "  14.83  No answer from S2 (1 of 3)",
            "  27.00  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for",
            "  36.03  No answer from S2 (2 of 3)",
            "  52.00  Sent 1 datagram to S2 at 0.25 kbit/s, 2.07 s on air, report asked for",
            "  61.03  No answer from S2 (3 of 3)",
            "  61.03  Lost track of S2: sending to everyone until it is heard again",
            "  77.00  Sent 1 datagram to everyone for S2 at 0.25 kbit/s, 2.24 s on air");
        s2.Events.Should().BeEmpty("it heard nothing and sent nothing");

        // The wait, in detail: how long is waited, what is chosen and why, and the answer that did not come.
        s1.LinesAt(TraceLevel.Detail).Take(14).Should().Equal(
            "   2.00  Queued a 30 byte datagram for S2: 1 waiting",
            "   2.00  Asked to send datagrams: needs the channel clear for 0.8 s plus a random wait",
            "   2.00  Drew a random wait of 0 s: slot 1 of 2, 0.9 s each",
            "   2.00  Can send at 5.80 if the channel stays clear",
            "   5.80  Chose 0.25 kbit/s for S2 [format 192]: nothing known of them yet, asking for a report",
            "   5.80  Clear to send datagrams after waiting 3.8 s",
            "   5.80  Channel busy: this station is sending",
            "   7.87  Waiting up to 6.96 s for an answer from S2",
            "   7.97  Channel clear",
            "  27.00  Queued a 30 byte datagram for S2: 1 waiting",
            "  27.00  Asked to send datagrams: needs the channel clear for 0.8 s plus a random wait",
            "  27.00  Drew a random wait of 0 s: slot 1 of 2, 0.9 s each",
            "  27.00  Chose 0.25 kbit/s for S2 [format 192]: nothing known of them yet, asking for a report",
            "  27.00  Clear to send datagrams after waiting 0 s");
        s1.Texts.Should().Contain("Chose 0.25 kbit/s for S2 [format 192]: two silences running, two steps slower, asking for a report");
        s1.Texts.Should().Contain("Chose 0.25 kbit/s for S2 [format 192]: address not located, first contact format");

        // The path comes back: S2 sends to S1, and S1 finds the address again.
        rig.Channel.Connect(rig.Nodes[0], rig.Nodes[1], 1.0, 0.004);
        rig.Send(2, 1, 99);
        rig.Run(25);
        s1.Texts.Should().Contain("Found S2 again");
    }

    // ---- a burst that did not decode ----

    [Fact]
    public void A_burst_that_another_station_keyed_over_is_reported_as_not_decoded_and_the_two_stations_say_so()
    {
        (TraceTap s1, TraceTap s2, TraceTap s3) = TraceScenarios.Interference();

        // The burst from S1 (a long one at 37.9 kbit/s) was keyed over by S3 half way through: S2 heard it, could not decode it, knew something else had landed on it, and told S1.
        s2.LinesAt(TraceLevel.Flow).SkipWhile(l => !l.StartsWith("  63.49  Heard S1 but could not decode", StringComparison.Ordinal)).Take(3).Should().Equal(
            "  63.49  Heard S1 but could not decode it",
            "  63.49  Something else landed on the burst from S1: not used to judge the path",
            "  63.49  Told S1 its burst was not decoded: it can send at 37.9 kbit/s");
        s2.Texts.Should().Contain("Owing S1 a report: its burst was not decoded");
        s2.LinesAt(TraceLevel.Flow).Should().Contain("  34.60  Path from S1 is good for 37.9 kbit/s (was 42.9)", "an earlier burst the station could not decode brought the rate down");
        s1.LinesAt(TraceLevel.Flow).Should().Contain("  63.90  Told by S2: our burst was not decoded, we can send at 37.9 kbit/s");
        s1.Texts.Should().Contain("Told the host that its burst to S2 was not decoded: 1 datagram");
        s1.Texts.Should().Contain("Holding ordinary traffic until 64.66: report from S2");

        // S2's lines are not the only ones in the run that mention the datagram: S3 did the keying, and it said nothing of what it was keying over.
        s3.Texts.Should().NotContain(t => t.StartsWith("Heard", StringComparison.Ordinal) && t.Contains("could not decode", StringComparison.Ordinal));
    }

    // ---- identification ----

    [Fact]
    public void Identification_is_due_after_the_first_transmission_is_sent_and_heard_by_the_other_station()
    {
        var rig = new SimRig(2);
        foreach (Station s in rig.Stations)
        {
            s.IdentificationSuspended = false;
            s.IdentificationInterval = 20L * 48000;
        }

        var s1 = new TraceTap(rig.Stations[0], TraceLevel.Detail);
        var s2 = new TraceTap(rig.Stations[1]);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(30);

        s1.LinesAt(TraceLevel.Detail).Should().Contain("   7.87  Identification due: first transmission");
        s1.Lines.Should().Contain("   8.02  Sent identification as M0S1, 1.19 s on air");
        s1.Lines.Should().Contain("  11.15  Heard S2: identified as M0S2, signal 74 dB");
        s1.Texts.Should().Contain("Heard from S2 within the wait: not counted as silence");
        s2.Lines.Should().Contain("   9.48  Heard address f3b8bcd6: identified as M0S1, signal 73 dB");
        s2.Lines.Should().Contain("   9.82  Sent identification as M0S2, 1.19 s on air");
    }

    // ---- numbers ----

    [Fact]
    public void Two_stations_holding_one_number_are_told_by_a_third_and_one_moves()
    {
        ScenarioRun run = TraceScenarios.ClashRun(TraceSetting.Flow);
        TraceTap[] taps = [run.Taps[0]!, run.Taps[1]!, run.Taps[2]!];
        taps[2].Lines.Should().Contain("  17.85  Station number 7 is in use by two stations: will tell the later one to move");
        taps[2].Lines.Should().Contain("  26.19  Told the station using number 7 to move");
        taps[0].Lines.Should().Contain("  27.90  Heard a notice that the station using number 7 must move");
        taps[1].Lines.Should().Contain("  27.90  Heard a notice that the station using number 7 must move");
        taps[1].Lines.Should().Contain("  27.90  Changed station number from 7 to 48: told to move by another station");
        taps[0].Texts.Should().NotContain(t => t.StartsWith("Changed", StringComparison.Ordinal), "the notice named the later station, and the first stays");

        TraceTap detail = TraceScenarios.ClashRun(TraceSetting.Detail).Taps[2]!;
        detail.Lines.Should().Contain("  17.85  Forgot station 7: its number is in use by two stations", "nothing the host sent to lived there, so the detail says what Flow had no address to say");
    }

    [Fact]
    public void A_station_with_no_callsign_hears_two_stations_hold_a_number_and_says_it_cannot_tell_them()
    {
        TraceTap listener = TraceScenarios.ClashRun(TraceSetting.Flow, listener: true).Taps[2]!;
        listener.Texts.Should().Contain("Station number 7 is in use by two stations: not telling, no callsign set");
        listener.Texts.Should().NotContain(t => t.StartsWith("Told", StringComparison.Ordinal));
    }

    // ---- addresses ----

    [Fact]
    public void An_address_heard_from_another_station_is_found_there_and_the_old_station_is_remembered()
    {
        var rig = new SimRig(3);
        var s1 = new TraceTap(rig.Stations[0]);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(14);

        // Station 3's host sends from the address S2: S1 has S2 at station 2, and finds it at station 3.
        rig.Stations[2].Send(SimRig.Datagram(9, 20), SimRig.Address(1), SimRig.Address(2), LinkProtocol.NoLayer3, new HostTag(9)).Should().Be(SendResult.Accepted);
        rig.RunTo(40);
        s1.Lines.Should().Contain("  14.50  Found S2 at station 3, was at station 2");
    }

    // ---- the audio layer's side ----

    [Fact]
    public void A_header_the_station_does_nothing_with_is_a_detail_line_with_the_reason()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var tap = new TraceTap(station, TraceLevel.Detail);
        byte[] bits = new byte[BurstHeaderCodec.HeaderBits];
        station.TakeInHeader(1000, FormatOpening.Full, new BurstHeader(1, BurstKind.Data, 0, 0, 0, 0, 0), bits);
        station.TakeInHeader(2000, FormatOpening.UpTo3000Hz, new BurstHeader(0, (BurstKind)6, 3, 0, 192, 12, 0), bits);
        station.TakeInHeader(4000, FormatOpening.Full, BurstHeader.Data(0, 1, 12, 12, 0), bits);
        tap.Events.Where(e => e.Text.StartsWith("Header", StringComparison.Ordinal)).Select(e => e.Text).Should().Equal(
            "Header read but not used: a version this station does not know",
            "Header read but not used: a kind of burst this station does not receive",
            "Header read but not used: it describes no burst");
    }

    [Fact]
    public void A_burst_whose_start_has_left_the_buffer_is_lost_and_the_line_says_whose()
    {
        var station = new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var tap = new TraceTap(station);
        station.Push(new float[Station.RingSamples + 100_000]);
        byte[] bits = new byte[BurstHeaderCodec.HeaderBits];
        station.TakeInHeader(1000, FormatOpening.Full, BurstHeader.Data(3, 1, 12, 12, 0), bits);
        station.Push(new float[4 * 48000]);
        station.DeliverResults();
        tap.Texts.Should().Equal("Lost a burst from station 3: its start had left the buffer");
    }

    [Fact]
    public void The_carrier_from_the_radio_and_what_the_station_does_about_it_are_detail_lines_in_order()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station, TraceLevel.Detail);
        long handshake = 0;
        station.TransmitRequest += r => handshake = r.Id;
        station.Push(new float[5 * 48000]);
        tap.Events.Clear();

        station.Enqueue(new float[1000], TransmitClass.Ordinary);
        station.Push(new float[48000]);
        handshake.Should().NotBe(0, "granted within the second");
        station.TransmitRefused(handshake);
        station.Push(new float[3 * 48000]);
        station.SetExternalBusy(true);
        station.Push(new float[48000]);
        station.SetExternalBusy(false);
        station.SetExternalBusy(null);

        // The request is made, the random wait drawn and the time worked out; it is granted; the audio layer will not key it, so it is held back and goes through the same again; the radio says there is a carrier, which holds it back
        // once more, and later none (and that it can no longer say).
        tap.Lines.Should().Equal(
            "   5.00  Asked to send audio: needs the channel clear for 0.8 s plus a random wait",
            "   5.00  Drew a random wait of 0 s: slot 1 of 2, 0.9 s each",
            "   5.00  Can send at 5.80 if the channel stays clear",
            "   5.80  Clear to send audio after waiting 0.8 s",
            "   6.00  Held back audio: the audio layer would not key it",
            "   6.00  Channel clear",
            "   6.00  Drew a random wait of 0 s: slot 1 of 2, 0.9 s each",
            "   6.00  Can send at 6.80 if the channel stays clear",
            "   6.80  Clear to send audio after waiting 0.8 s",
            "   9.00  Carrier from the radio: present",
            "   9.00  Held back audio: the radio reports a carrier",
            "  10.00  Carrier from the radio: gone",
            "  10.00  Carrier from the radio: not available",
            "  10.00  Channel clear",
            "  10.00  Drew a random wait of 0 s: slot 1 of 2, 0.9 s each",
            "  10.00  Can send at 10.80 if the channel stays clear");
    }

    [Fact]
    public void More_than_four_bursts_waiting_for_a_worker_drops_the_oldest_and_the_line_says_so()
    {
        var b = new StreamBuilder().Silence(1.0);
        const int count = 8;
        for (int i = 0; i < count; i++)
        {
            b.Add(TestBurst.Data(5, 1, 12, [TestBurst.Datagram(20 + i, 30)], symbols: 8));
            b.Silence(0.6);
        }

        b.Silence(1.5).Noise(0.01, 31);

        // The test runs the header reads and nothing else, so each burst's receive waits in the queue, as in the existing test of the drop.
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station);
        station.SetExternalBusy(true);
        station.Locate(ReceiveRig.Address(5), 5);
        station.Send(new byte[] { 1 }, ReceiveRig.Address(5), ReceiveRig.Own, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
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

        station.BurstsDropped.Should().Be(count - 4);
        string name = Station.AddressText(ReceiveRig.Address(5));
        tap.Texts.Where(t => t.StartsWith("Dropped", StringComparison.Ordinal)).Should().Equal(Enumerable.Repeat($"Dropped a burst from {name}: decoding could not keep up", count - 4));
    }

    [Fact]
    public void A_busy_channel_with_datagrams_waiting_is_a_flow_line_and_a_busy_channel_with_nothing_waiting_is_not()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var both = new TraceTap(station, TraceLevel.Detail);

        // Nothing waiting: the carrier comes and goes, and only a Detail line says so.
        station.Push(new float[48000]);
        station.SetExternalBusy(true);
        station.Push(new float[48000]);
        station.SetExternalBusy(false);
        station.Push(new float[48000]);
        both.Lines.Should().Equal(
            "   1.00  Carrier from the radio: present",
            "   1.00  Channel busy: the radio reports a carrier",
            "   2.00  Carrier from the radio: gone",
            "   2.10  Channel clear");

        // Two addresses with a datagram each, and then one with three: it is said as it is.
        both.Events.Clear();
        station.Send(new byte[10], Name("G4ABC-1"), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1));
        station.SetExternalBusy(true);
        station.SetExternalBusy(false);
        station.Push(new float[48000]);
        station.Send(new byte[10], Name("G4ABC-2"), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(2));
        station.SetExternalBusy(true);
        both.Events.Where(e => e.Level == TraceLevel.Flow).Select(e => e.Text).Should().Equal(
            "Channel busy: waiting to send 1 datagram to G4ABC-1",
            "Channel clear: waiting to send 1 datagram to G4ABC-1",
            "Channel busy: waiting to send 2 datagrams for 2 addresses");
    }

    [Fact]
    public void A_datagram_accepted_at_a_fast_rate_and_too_big_once_the_rate_falls_is_dropped_with_the_reason()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station);
        LinkAddress to = Name("G4ABC-1");
        station.Locate(to, 2);
        station.Adaptation.HeardReport(2, 31, failed: false, now: 0);
        station.Send(new byte[1000], to, Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted, "at 70 kbit/s a burst carries thousands of bytes");
        for (int i = 0; i < 3; i++)
        {
            station.Adaptation.NoAnswer(2);
        }

        station.Push(new float[7 * 48000]);
        tap.Texts.Should().Equal("Dropped a datagram for G4ABC-1: no longer fits one burst at 0.25 kbit/s");
    }

    [Fact]
    public void A_burst_built_and_not_yet_keyed_is_built_again_when_the_station_moves_to_another_number()
    {
        var station = new Station(23, new Random(1)).WithIdentity("M0LTE");
        var tap = new TraceTap(station, TraceLevel.Detail);
        station.Locate(Name("G4ABC-1"), 2);
        station.Send(new byte[10], Name("G4ABC-1"), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1));
        station.Push(new float[6 * 48000]);
        tap.Events.Clear();

        station.HearHeaderFrom(23);
        tap.Texts.Take(3).Should().Equal(
            $"Changed station number from 23 to {station.StationNumber}: 23 is in use by another station",
            "Built burst discarded: it carries the old station number, building it again",
            "Held back datagrams: the station number changed, so it is built again");
    }

    [Fact]
    public void Ordinary_traffic_held_off_after_a_report_is_held_back_with_that_cause()
    {
        var rig = new AccessRig(1);
        var notes = new List<AccessNote>();
        rig.Access.Note = notes.Add;
        rig.Clock.AdvanceTo(ChannelAccess.StartUpListen);
        rig.Access.Enqueue(new float[100], TransmitClass.Ordinary);
        rig.Clock.AdvanceTo(ChannelAccess.StartUpListen + 100_000);
        notes.Select(n => n.Kind).Should().Contain(AccessNoteKind.Granted);
        rig.Access.HoldOffOrdinaryUntil(rig.Clock.Now + 48000);
        AccessNote held = notes.Last(n => n.Kind == AccessNoteKind.Withdrawn);
        held.Cause.Should().Be(AccessCause.ReportHoldOff);
        TraceText.HeldBack("datagrams", TraceText.CauseText(held.Cause)).Should().Be("Held back datagrams: a report just came in, ordinary traffic waits a second");
    }

    [Fact]
    public void A_report_owed_is_dropped_when_data_going_back_carries_the_news_and_the_detail_says_so()
    {
        var rig = new SimRig(2);
        var s1 = new TraceTap(rig.Stations[0], TraceLevel.Detail);
        var s2 = new TraceTap(rig.Stations[1], TraceLevel.Detail);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(8.5);
        rig.Send(2, 1, 2);
        rig.RunTo(20);
        s2.Texts.Should().Contain("Owing address f3b8bcd6 a report: it asked for one");
        s2.Texts.Should().Contain("Dropped the report owed to address f3b8bcd6: the datagrams going to it carry the news", "named as it was when it fell owed, though the host has since sent to S1");
        s2.Texts.Count(t => t.StartsWith("Told S1 it can send", StringComparison.Ordinal)).Should().Be(1, "the datagram goes with the news in its prelude, and that is said once, as the datagram is built");
        s2.Texts.Should().NotContain(t => t.StartsWith("Asked to send a report", StringComparison.Ordinal), "so no report goes alone");
        s1.Texts.Should().Contain("Waiting up to 6.96 s for an answer from S2");
    }

    [Fact]
    public void An_address_not_heard_for_ten_minutes_is_lost_and_found_again()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: ten minutes of audio goes through the detector");
        TestBurst burst = TestBurst.Data(3, 1, 12, [TestBurst.Datagram(50, 40)], symbols: 12);
        using var rig = new ReceiveRig(0);
        var tap = new TraceTap(rig.Station);
        rig.SendTo(3);
        var stream = new StreamBuilder().Silence(1.5);
        stream.Add(burst);
        stream.Silence(2.5).Noise(0.01, 3);
        rig.Push(stream.ToArray(), () => 4800);
        rig.Finish();
        rig.Station.Skip(11 * 60 * 48000);
        rig.Station.DeliverResults();
        string address = Station.AddressText(ReceiveRig.Address(3));
        tap.Texts.Should().Contain($"Lost track of {address}: not heard for 10 minutes, sending to everyone until it is heard again");
    }

    // ---- a line is true when it is printed ----

    [Fact]
    public void A_report_the_audio_layer_refused_says_nothing_until_it_is_really_on_air_and_then_says_it_once()
    {
        var rig = new SimRig(2);
        rig.Nodes[1].RefuseRequest = n => n == 1;
        var s1 = new TraceTap(rig.Stations[0], TraceLevel.Detail);
        var s2 = new TraceTap(rig.Stations[1], TraceLevel.Detail);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(30);

        rig.Nodes[1].Refused.Should().ContainSingle("S2's first request, a report, was refused");
        s2.Texts.Should().Contain("Held back a report: the audio layer would not key it");
        int reportsOnAir = rig.Nodes[1].ModemSent.Count(m => m.Kind == ModemSentKind.Report);
        reportsOnAir.Should().BeGreaterThan(0);
        s2.Texts.Count(t => t.StartsWith("Told address", StringComparison.Ordinal) && t.Contains("it can send at", StringComparison.Ordinal)).Should().Be(reportsOnAir, "one line for each report that went on air, none for the one that was refused");
        s2.Texts.Count(t => t == "Channel busy: this station is sending").Should().Be(rig.Nodes[1].Transmissions.Count, "said when keyed: the refused request was never on air");
        s1.Texts.Count(t => t == "Channel busy: this station is sending").Should().Be(rig.Nodes[0].Transmissions.Count);
    }

    [Fact]
    public void A_told_line_comes_after_the_grant_with_the_channel_busy_line_and_with_the_sent_line()
    {
        string[] lines = TraceScenarios.TwoStationsRun(TraceSetting.Detail).Taps[0]!.Texts;
        int told = Array.FindIndex(lines, t => t.StartsWith("Told S2 it can send", StringComparison.Ordinal));
        told.Should().BeGreaterThan(Array.FindIndex(lines, 0, told, t => t.StartsWith("Clear to send a report", StringComparison.Ordinal)));
        lines[told - 1].Should().Be("Channel busy: this station is sending");
    }

    [Fact]
    public void The_second_silence_says_sending_slower_when_it_will_and_a_fourth_is_still_silent_never_four_of_three()
    {
        // Two stations settle at a fast rate, then S2 goes out of hearing: the next bursts really do fall back.
        var rig = new SimRig(2);
        var s1 = new TraceTap(rig.Stations[0]);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(20);
        rig.Send(2, 1, 3);
        rig.RunTo(40);
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Gain = 0;
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 0;
        for (int i = 0; i < 4; i++)
        {
            rig.Send(1, 2, 20 + i);
            rig.Run(25);
        }

        // Once the address is lost S1 is told where it is by the test seam, as a station heard again would be, and sends once more: a silence past the third.
        rig.Stations[0].Locate(SimRig.Address(2), 2);
        rig.Send(1, 2, 30);
        rig.Run(25);
        string[] answers = [.. s1.Texts.Where(t => t.StartsWith("No answer", StringComparison.Ordinal))];
        answers.Should().Contain("No answer from S2 (2 of 3): sending slower");
        answers.Should().Contain("No answer from S2 (3 of 3): back to the slowest rate");
        answers.Should().Contain("No answer from S2 (still silent)");
        answers.Should().NotContain(t => t.Contains("of 3)", StringComparison.Ordinal) && !(t.Contains("(1 of 3)", StringComparison.Ordinal) || t.Contains("(2 of 3)", StringComparison.Ordinal) || t.Contains("(3 of 3)", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_announcement_is_asked_for_as_an_announcement_and_a_report_as_a_report()
    {
        var rig = new SimRig(3, locates: (_, _) => false);
        var s2 = new TraceTap(rig.Stations[1], TraceLevel.Detail);
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 0;
        rig.RunTo(2);
        rig.Send(2, 3, 1);
        rig.RunTo(12);
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 1.0;
        rig.Send(3, 2, 2);
        rig.RunTo(22);
        rig.Send(1, 2, 3);
        rig.RunTo(40);
        s2.Texts.Should().Contain("Asked to send an announcement: needs the channel clear for 0.05 s");
        s2.Texts.Should().Contain("Clear to send an announcement after waiting 0 s");
    }

    [Fact]
    public void A_report_owed_keeps_one_name_in_every_line_about_it()
    {
        var rig = new SimRig(2);
        var s2 = new TraceTap(rig.Stations[1], TraceLevel.Detail);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.RunTo(8.5);
        rig.Send(2, 1, 2);
        rig.RunTo(20);
        // Owed to a station known by its key, then the host sends to it and it is known as S1: the line that drops the report still says the name it was owed under.
        s2.Texts.Should().Contain("Owing address f3b8bcd6 a report: it asked for one");
        s2.Texts.Should().Contain("Dropped the report owed to address f3b8bcd6: the datagrams going to it carry the news");
    }
}
