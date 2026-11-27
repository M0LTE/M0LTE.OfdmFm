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
            "  20.78  Told by S2: we can send at 70.4 kbit/s",
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
            "  40.46  Heard S1: 1 datagram, signal 51 dB",
            "  40.46  Told by S1: we can send at 70.4 kbit/s");
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
}
