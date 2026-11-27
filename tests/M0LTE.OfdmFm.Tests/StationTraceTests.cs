using System.Globalization;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The station's trace (docs/trace.md): that each line is said at the right moment with the right names, that nothing is built when nobody is listening or when the level does not ask for it, and that tracing changes nothing the station does.
/// What each sentence says is pinned by <see cref="TraceTextTests"/>. Everything is on the sample clock.
/// </summary>
public class StationTraceTests
{
    private static LinkAddress Name(string text) => LinkAddress.FromAscii(text);

    // ---- the event ----

    [Fact]
    public void A_line_is_written_with_its_clock_in_a_column_and_two_spaces_before_the_text()
    {
        new TraceEvent(12.34, TraceLevel.Flow, "Heard GB7RDG-1: 2 datagrams, signal 31 dB").ToString().Should().Be("  12.34  Heard GB7RDG-1: 2 datagrams, signal 31 dB");
        new TraceEvent(0, TraceLevel.Flow, "Callsign set to M0LTE").ToString().Should().Be("   0.00  Callsign set to M0LTE");
        new TraceEvent(1234.5, TraceLevel.Detail, "Channel clear").ToString().Should().Be("1234.50  Channel clear");
    }

    [Fact]
    public void The_clock_is_written_the_same_way_whatever_the_cultures_decimal_mark()
    {
        CultureInfo? before = CultureInfo.CurrentCulture;
        try
        {
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = comma;
            new TraceEvent(12.34, TraceLevel.Flow, "Heard x").ToString().Should().Be("  12.34  Heard x");
            TraceText.Rate(0.25).Should().Be("0.25");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // ---- how an address and a station are named ----

    [Fact]
    public void An_address_is_shown_as_its_bytes_when_they_are_printable_ascii_else_as_its_key()
    {
        Station.AddressText(Name("G4ABC-1")).Should().Be("G4ABC-1");
        Station.AddressText(LinkAddress.Everyone).Should().Be("everyone");
        Station.AddressText(new LinkAddress([0x01, 0x02, 0x03])).Should().Be("address " + LinkAddress.KeyOf([0x01, 0x02, 0x03]).ToString("x8"));
        Station.AddressText(new LinkAddress([(byte)'A', (byte)' ', (byte)'B'])).Should().StartWith("address ", "a space is not printable here");
        Station.AddressText(new LinkAddress([0xC3, 0xA9])).Should().StartWith("address ");
        Station.AddressText(LinkAddress.FromKey(0x3fa2c1d0)).Should().Be("address 3fa2c1d0");
        Station.AddressText(LinkAddress.FromKey(0xAB)).Should().Be("address 000000ab", "eight lower-case hex digits");
        Station.KeyText(0x3FA2C1D0).Should().Be("address 3fa2c1d0");
    }

    private static Station Listening(out TraceTap tap, TraceLevel level = TraceLevel.Flow)
    {
        Station station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        tap = new TraceTap(station, level);
        return station;
    }

    [Fact]
    public void A_station_with_no_address_known_is_named_by_its_number()
    {
        Station station = Listening(out _);
        station.StationName(17).Should().Be("station 17");
    }

    [Fact]
    public void A_station_known_only_by_an_address_the_host_never_used_is_named_by_its_key_and_by_its_bytes_once_the_host_uses_it()
    {
        Station station = Listening(out _);
        LinkAddress a = Name("G4ABC-1");
        station.LearnedAddresses.Learn(a.Key, 5, 1.0);
        station.StationName(5).Should().Be("address " + a.Key.ToString("x8"));
        station.Send(new byte[] { 1 }, a, Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        station.StationName(5).Should().Be("G4ABC-1");
    }

    [Fact]
    public void A_station_known_by_several_addresses_is_named_by_the_one_the_host_used_most_recently()
    {
        Station station = Listening(out _);
        LinkAddress a = Name("G4ABC-1");
        LinkAddress b = Name("G4ABC-2");
        LinkAddress c = Name("G4ABC-3");
        station.Locate(a, 5);
        station.Locate(b, 5);
        station.Locate(c, 5);
        LinkAddress own = Name("M0LTE-1");

        // Nothing used yet, so each is known only by its key: the one heard last, and the lowest key on a tie, whatever order they were learned in.
        station.StationName(5).Should().Be(Station.KeyText(new[] { a, b, c }.Min(x => x.Key)));

        station.Send(new byte[] { 1 }, b, own, LinkProtocol.NoLayer3, new HostTag(1));
        station.StationName(5).Should().Be("G4ABC-2");
        station.Send(new byte[] { 1 }, a, own, LinkProtocol.NoLayer3, new HostTag(2));
        station.StationName(5).Should().Be("G4ABC-1");
        station.Send(new byte[] { 1 }, b, own, LinkProtocol.NoLayer3, new HostTag(3));
        station.StationName(5).Should().Be("G4ABC-2", "the latest use wins");
        station.Send(new byte[] { 1 }, b, own, LinkProtocol.NoLayer3, new HostTag(4));
        station.StationName(5).Should().Be("G4ABC-2", "and it stays, however often it is used");
    }

    [Fact]
    public void An_address_of_the_hosts_with_unprintable_bytes_is_shown_by_its_key()
    {
        Station station = Listening(out _);
        var raw = new LinkAddress([0x00, 0x01, 0xFF]);
        station.Locate(raw, 8);
        station.Send(new byte[] { 1 }, raw, Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1));
        station.StationName(8).Should().Be("address " + raw.Key.ToString("x8"));
    }

    // ---- when nobody listens ----

    /// <summary>
    /// Two stations on the wire with nothing between them but a block of noise, each pushed by hand, so that the work between the first and last sample measured is the stations' own: no simulated channel, no lists of events. Each station's
    /// audio goes into the other's input a block after the station is told it is on air (the test is its own audio layer).
    /// </summary>
    private sealed class Wire
    {
        private const int Block = 4800;

        private readonly Station[] _stations;
        private readonly float[][] _noise;
        private readonly float[] _input = new float[Block];
        private readonly long[] _requestId = new long[2];
        private readonly ReadOnlyMemory<float>[] _samples = new ReadOnlyMemory<float>[2];
        private readonly bool[] _pending = new bool[2];
        private readonly bool[] _onAir = new bool[2];
        private readonly long[] _startedAt = new long[2];
        private readonly int[] _sent = new int[2];
        private int _noiseAt;

        public Wire(Station a, Station b)
        {
            _stations = [a, b];
            var random = new Random(77);
            _noise = [new float[48000], new float[48000]];
            for (int s = 0; s < 2; s++)
            {
                for (int i = 0; i < _noise[s].Length; i++)
                {
                    _noise[s][i] = (float)(0.004 * ((random.NextDouble() * 2) - 1));
                }
            }

            for (int s = 0; s < 2; s++)
            {
                int k = s;
                _stations[s].TransmitRequest += r =>
                {
                    _requestId[k] = r.Id;
                    _samples[k] = r.Samples;
                    _pending[k] = true;
                };
                _stations[s].WithdrawRequest += w =>
                {
                    if (_pending[k] && _requestId[k] == w.Id)
                    {
                        _pending[k] = false;
                    }
                };
            }
        }

        public long Clock => _stations[0].SampleCount;

        public void Run(double seconds)
        {
            long end = Clock + (long)(seconds * 48000);
            while (Clock < end)
            {
                Step();
            }
        }

        private void Step()
        {
            // The ones that are keyed from now start with this block.
            for (int s = 0; s < 2; s++)
            {
                if (_pending[s] && !_onAir[s])
                {
                    _pending[s] = false;
                    _onAir[s] = true;
                    _startedAt[s] = Clock;
                    _sent[s] = 0;
                    _stations[s].TransmitStarted(_requestId[s], Clock);
                }
            }

            for (int s = 0; s < 2; s++)
            {
                int other = 1 - s;
                Array.Clear(_input);
                if (_onAir[other])
                {
                    ReadOnlySpan<float> audio = _samples[other].Span;
                    int n = Math.Min(Block, audio.Length - _sent[other]);
                    if (n > 0)
                    {
                        audio.Slice(_sent[other], n).CopyTo(_input);
                    }
                }

                float[] noise = _noise[s];
                for (int i = 0; i < Block; i++)
                {
                    _input[i] += noise[(_noiseAt + i) % noise.Length];
                }

                _stations[s].Push(_input);
            }

            _noiseAt = (_noiseAt + Block) % _noise[0].Length;
            for (int s = 0; s < 2; s++)
            {
                if (_onAir[s])
                {
                    _sent[s] += Block;
                    if (_sent[s] >= _samples[s].Length)
                    {
                        _onAir[s] = false;
                        _stations[s].TransmitEnded(_requestId[s], _startedAt[s] + _samples[s].Length);
                    }
                }
            }
        }
    }

    private static (Station A, Station B, Wire Wire) WiredPair()
    {
        Station a = new Station(1, new Random(11), runJobsInline: true).WithIdentity("M0AAA");
        Station b = new Station(2, new Random(12), runJobsInline: true).WithIdentity("M0BBB");
        a.Locate(HostB, 2);
        b.Locate(HostA, 1);
        return (a, b, new Wire(a, b));
    }

    /// <summary>A sends one datagram and B answers with one, each waiting its turn (two stations that sent at once would not hear each other, and the rates would swing: the busy exchange measured is a steady one).</summary>
    private static readonly LinkAddress HostA = Name("HOSTA");
    private static readonly LinkAddress HostB = Name("HOSTB");

    private static void Exchange(Station a, Station b, Wire wire, byte[] one, byte[] two)
    {
        a.Send(one, HostB, HostA, LinkProtocol.NoLayer3, new HostTag(1), reportWanted: true);
        wire.Run(8);
        b.Send(two, HostA, HostB, LinkProtocol.NoLayer3, new HostTag(2), reportWanted: true);
        wire.Run(8);
    }

    [Fact]
    public void A_busy_two_station_exchange_with_no_trace_handler_allocates_nothing_and_builds_no_line()
    {
        (Station a, Station b, Wire wire) = WiredPair();
        int receivedByA = 0;
        int receivedByB = 0;
        a.Received += _ => receivedByA++;
        b.Received += _ => receivedByB++;
        a.PathReport += _ => { };
        b.PathReport += _ => { };
        a.LinkState += _ => { };
        b.LinkState += _ => { };
        a.ChannelBusy += _ => { };
        b.ChannelBusy += _ => { };
        a.Transmitted += _ => { };
        b.Transmitted += _ => { };
        a.ModemSent += _ => { };
        b.ModemSent += _ => { };
        a.BurstNotDecoded += _ => { };
        b.BurstNotDecoded += _ => { };
        byte[] one = TestBurst.Datagram(1, 60);
        byte[] two = TestBurst.Datagram(2, 40);

        // The first exchanges make what the shapes need: the queues, the neighbours' records, the pooled buffers (the rate climbs as the path is measured, and each size of burst is made once), the first identification.
        wire.Run(6);
        for (int i = 0; i < 10; i++)
        {
            Exchange(a, b, wire, one, two);
        }

        int before = receivedByA + receivedByB;
        long allocated = AllocationMeasure.Minimum(() => Exchange(a, b, wire, one, two));
        (receivedByA + receivedByB).Should().BeGreaterThan(before, "the exchange measured was a busy one: datagrams went both ways");
        allocated.Should().Be(0, "a station nobody is tracing allocates nothing in steady state, a whole exchange of datagrams both ways included");
        a.FlowLinesStarted.Should().Be(0);
        a.DetailLinesStarted.Should().Be(0);
        b.FlowLinesStarted.Should().Be(0);
        b.DetailLinesStarted.Should().Be(0);
    }

    [Fact]
    public void With_a_handler_at_flow_no_detail_text_is_built_and_with_the_detail_level_it_is()
    {
        foreach (TraceLevel level in new[] { TraceLevel.Flow, TraceLevel.Detail })
        {
            (Station a, Station b, Wire wire) = WiredPair();
            var ta = new TraceTap(a, level);
            var tb = new TraceTap(b, level);
            wire.Run(6);
            for (int i = 0; i < 3; i++)
            {
                Exchange(a, b, wire, TestBurst.Datagram(1, 60), TestBurst.Datagram(2, 40));
            }

            long detailLines = ta.Events.Count(e => e.Level == TraceLevel.Detail) + tb.Events.Count(e => e.Level == TraceLevel.Detail);
            ta.Events.Should().Contain(e => e.Level == TraceLevel.Flow);
            if (level == TraceLevel.Flow)
            {
                a.DetailLinesStarted.Should().Be(0);
                b.DetailLinesStarted.Should().Be(0);
                detailLines.Should().Be(0);
            }
            else
            {
                (a.DetailLinesStarted + b.DetailLinesStarted).Should().BeGreaterThanOrEqualTo(detailLines);
                detailLines.Should().BeGreaterThan(0);
            }
        }
    }

    [Fact]
    public void The_level_can_be_changed_while_the_station_runs()
    {
        Station station = Listening(out TraceTap tap);
        station.TraceLevel.Should().Be(TraceLevel.Flow, "Flow until set");
        station.Send(new byte[] { 1 }, Name("G4ABC-1"), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(1));
        tap.Events.Should().BeEmpty("a queued datagram is a Detail line");
        station.TraceLevel = TraceLevel.Detail;
        station.Send(new byte[] { 1 }, Name("G4ABC-1"), Name("M0LTE-1"), LinkProtocol.NoLayer3, new HostTag(2));
        tap.Texts.Should().Contain("Queued a 1 byte datagram for G4ABC-1: 2 waiting");
    }

    [Fact]
    public void Tracing_changes_nothing_the_station_does()
    {
        // The same run with no handler, at Flow and at Detail: every event the host sees and every sample sent is the same, so the random source is not drawn from by a line and no line changes a decision.
        string Run(TraceLevel? level)
        {
            var rig = new SimRig(3, locates: (_, _) => false);
            if (level is TraceLevel l)
            {
                foreach (Station s in rig.Stations)
                {
                    _ = new TraceTap(s, l);
                }
            }

            rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 0;
            rig.RunTo(2);
            rig.Send(2, 3, 1);
            rig.RunTo(12);
            rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 1.0;
            rig.Send(3, 2, 2);
            rig.RunTo(22);
            rig.Send(1, 2, 3);
            rig.RunTo(40);
            rig.Send(1, LinkAddress.Everyone, 4);
            rig.RunTo(55);
            return string.Join("\n", rig.Channel.Log);
        }

        string none = Run(null);
        none.Should().NotBeEmpty();
        Run(TraceLevel.Flow).Should().Be(none);
        Run(TraceLevel.Detail).Should().Be(none);
    }
}
