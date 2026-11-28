using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

// The simulated channel of step 5 (docs/station-design.md K): two or more stations in one process on one sample clock, in lockstep, every station inline, so that a run is exactly reproducible.
// Steps 6 and 8 reuse it, and packet-net/pdn-soundmodem#885 and #886 (unlike radios) are asked of it.
//
// How it stays exact whatever the block size. The sim cuts every step at the next thing that can happen to any station: the end of the block, a station's next timer, the end of the detector's frame, and the
// sim's own scheduled actions (a start, an end, a carrier indication, the end of a deaf spell). So a timer's action (a grant, a withdrawal) is raised at the last sample of a Push, the clock still reads that sample
// when the sim answers it, and TransmitStarted and TransmitEnded are called at exactly the clock the audio layer would report. Nothing the station does depends on the block size, only the noise and mixing, which are streams.

/// <summary>One station in the simulation, with the settings of its radio. Everything but the station is off by default.</summary>
internal sealed class SimNode
{
    internal SimNode(Station station, string name, int index)
    {
        Station = station;
        Name = name;
        Index = index;
    }

    /// <summary>The station.</summary>
    public Station Station { get; }

    /// <summary>The name in logs.</summary>
    public string Name { get; }

    /// <summary>Order in the channel; stations are stepped in this order.</summary>
    public int Index { get; }

    /// <summary>A low-pass on what this station's receiver passes to the modem, 4th order Butterworth at this frequency (a narrow radio passes about 3 kHz). Null: none.</summary>
    public double? ReceiveLowPassHz { get; set; }

    /// <summary>A low-pass on what this station's transmitter radiates. Null: none.</summary>
    public double? TransmitLowPassHz { get; set; }

    /// <summary>Linear gain of the receiver's audio (after its noise and filter).</summary>
    public double ReceiveGain { get; set; } = 1.0;

    /// <summary>The transmitter's key-up delay, samples: the first this many samples of what the audio layer plays are not radiated (a slow transmitter cuts off the start of the lead-in).</summary>
    public int KeyUpSamples { get; set; }

    /// <summary>The host's Morse: after an identification burst the transmitter stays keyed for this many samples (the other stations' modems hear silence: they cannot hear Morse) and the transmission ends then. Zero: it ends with the burst.</summary>
    public int IdentificationTailSamples { get; set; }

    /// <summary>After the station's own transmission ends the receiver hears nothing for this many samples.</summary>
    public int DeafSamples { get; set; }

    /// <summary>Impulsive noise on this station's receiver input (see <see cref="ImpulseModel"/>). Null (the default): none. Set before <see cref="SimChannel.Run"/>.</summary>
    public ImpulseModel? Impulses { get; set; }

    /// <summary>The carrier indication: null means the host supplies none and <see cref="Station.SetExternalBusy"/> is never called. Otherwise it follows any carrier heard at the receiver, this many samples late.</summary>
    public int? CarrierDelaySamples { get; set; }

    /// <summary>The audio layer's own delay between a request and playing it, samples: the request starts at its earliest sample plus this. During it the station may withdraw the request.</summary>
    public int StartLatencySamples { get; set; }

    /// <summary>The audio layer cannot key: for each request handed over, given its number (1 for the first the station ever asked for), true refuses it (nothing is radiated). Null: none refused.</summary>
    public Func<int, bool>? RefuseRequest { get; set; }

    /// <summary>True (the default): a refusal is reported with <see cref="Station.TransmitRefused"/>. False: the layer refuses in silence, as a layer without the call would.</summary>
    public bool ReportRefusals { get; set; } = true;

    /// <summary>Every request the audio layer refused: its id and the clock.</summary>
    public List<(long Id, long Clock)> Refused { get; } = [];

    // ---- what the tests read ----

    /// <summary>Every datagram received: the clock, the sender's number (read from the station by the test seam: no host sees it), whether it was for everyone (with or without a For), how it was addressed, the For key, the bytes and the quality.</summary>
    public List<SimReceived> Received { get; } = [];

    /// <summary>Every data burst that ended on air: the sample it ended at, the station number it went to (0 for everyone), whether it asked for a report and the address its datagrams were for. Read from the station by a test seam.</summary>
    public List<(long End, int Dest, bool Asked, LinkAddress Address)> BurstsSent { get; } = [];

    /// <summary>Every silence counted: the clock, the station number and the count after it.</summary>
    public List<(long Clock, int Dest, int Count)> SilenceCounts { get; } = [];

    /// <summary>Every <see cref="Station.Transmitted"/> event, in order, with the clock.</summary>
    public List<(long Clock, TransmittedEvent Event)> Transmitted { get; } = [];

    /// <summary>Every <see cref="Station.Dropped"/> event.</summary>
    public List<DroppedEvent> Dropped { get; } = [];

    /// <summary>Every <see cref="Station.Congestion"/> event.</summary>
    public List<CongestionEvent> Congestion { get; } = [];

    /// <summary>Every <see cref="Station.ModemSent"/> event.</summary>
    public List<ModemSentEvent> ModemSent { get; } = [];

    /// <summary>The number of the station each <see cref="ModemSent"/> burst was for (0 for everyone), in step with it: read from the station by the test seam, as no host sees it.</summary>
    public List<int> ModemSentTo { get; } = [];

    /// <summary>Every <see cref="Station.PathReport"/> event.</summary>
    public List<PathReportEvent> PathReports { get; } = [];

    /// <summary>Every <see cref="Station.BurstNotDecoded"/> event: the clock, the station and the tags.</summary>
    public List<(long Clock, LinkAddress Address, long[] Tags)> NotDecoded { get; } = [];

    /// <summary>Every <see cref="Station.LinkState"/> event.</summary>
    public List<LinkStateEvent> LinkStates { get; } = [];

    /// <summary>Each time the station's identification fell due, with the clock it was raised at.</summary>
    public List<(long Clock, IdentificationDueEvent Event)> IdentificationsDue { get; } = [];

    /// <summary>Each identification heard from another station, with the clock it was raised at.</summary>
    public List<(long Clock, IdentifiedEvent Event, int Sender)> Identified { get; } = [];

    /// <summary>The bursts this station put on air: the samples-start clock, the length, and the request id.</summary>
    public List<SimTransmission> Transmissions { get; } = [];

    /// <summary>Every request the station made: its id, the clock it was raised at, its length and a checksum of its samples (a withdrawn burst handed over again must be the same audio).</summary>
    public List<SimRequest> Requests { get; } = [];

    /// <summary>The ids of requests the sim honoured a withdrawal of before they started.</summary>
    public List<long> WithdrawnBeforeStart { get; } = [];

    /// <summary>Withdrawals that arrived too late: the request had started (the tolerated race).</summary>
    public List<long> WithdrawnTooLate { get; } = [];

    // ---- the sim's own state ----

    internal readonly Queue<NodeEvent> Events = new();
    internal readonly Dictionary<long, Plan> Plans = [];
    internal readonly List<Incoming> Heard = [];
    internal long DeafUntil;
    internal bool Transmitting;
    internal int Carriers;
    internal Filter? ReceiveFilter;
    internal Random? CaptureRandom;
    internal ImpulseSource? ImpulseStream;

    /// <summary>The impulses this receiver has been given so far, in order, when <see cref="Impulses"/> is set (ground truth for a test).</summary>
    public IReadOnlyList<ImpulsePulse> ImpulsePulses => (IReadOnlyList<ImpulsePulse>?)ImpulseStream?.Pulses ?? [];

    /// <summary>Whether an impulse overlapped the samples from <paramref name="from"/> up to <paramref name="to"/> at this receiver.</summary>
    public bool ImpulseOverlaps(long from, long to) => ImpulseStream?.Overlaps(from, to) ?? false;

    /// <summary>Every burst that reached this receiver, with what the path did to it, when <see cref="SimChannel.RecordDeliveries"/> is set.</summary>
    public List<SimDelivery> Deliveries { get; } = [];

    /// <summary>Set to keep everything this station's receiver is given in <see cref="Input"/> (for a test that wants to look at the audio itself).</summary>
    public bool RecordInput { get; set; }

    /// <summary>The audio the receiver was given, from the clock at which <see cref="RecordInput"/> was set.</summary>
    public List<float> Input { get; } = [];
}

/// <summary>A datagram a station received.</summary>
internal sealed record SimReceived(long Clock, int Sender, bool Everyone, Addressing Addressing, uint? ForKey, byte[] Data, double QualityDb, uint FromKey = 0, byte Protocol = 0);

/// <summary>A transmit request as the audio layer saw it.</summary>
internal sealed record SimRequest(long Id, long Clock, int Length, long Checksum);

/// <summary>A burst on air.</summary>
internal sealed record SimTransmission(long Id, long Start, int Length);

/// <summary>A request or withdrawal the station raised, stamped with the clock it was raised at.</summary>
internal readonly record struct NodeEvent(bool IsWithdrawal, long Id, long Stamp, ReadOnlyMemory<float> Samples, long Earliest);

internal sealed class Plan
{
    public long Id;
    public ReadOnlyMemory<float> Samples;
    public long StartAt;
    public bool Cancelled;
    public bool Started;
}

internal sealed class Incoming
{
    public long Start;
    public float[] Data = [];

    /// <summary>The signal's level at each sample as the receiver sees it (the trailing root mean square over <see cref="CaptureModel.LevelWindowSamples"/>), made only when the channel has a capture model.</summary>
    public float[]? Level;
}

/// <summary>
/// What an FM receiver does when two or more signals overlap (step 8). NOTHING HERE HAS BEEN MEASURED ON A RADIO: every number is an assumption, there so that the station's logic can be run against something that behaves like FM
/// and not like an adder (packet-net/pdn-soundmodem#835 is where it will be measured). The model is per receiver and per instant, from the received levels (each signal's root mean square over the last <see cref="LevelWindowSamples"/>,
/// after the path's gain). Silence is not a signal: a burst's lead-in does not capture anything.
/// <list type="bullet">
/// <item>The strongest signal is compared with the power sum of the others. When it is stronger by at least <see cref="MarginDb"/>, the receiver's audio is that signal's audio, and the others are reduced to noise
/// whose level, relative to their own level at the receiver, is <c>-(SuppressionBaseDb + SuppressionSlope * (difference - MarginDb))</c> dB: at the margin the others are 10 dB down (the defaults), and each further dB of difference takes
/// another dB off them. The noise is white and Gaussian, from a stream seeded per receiver.</item>
/// <item>When the difference is less than the margin, the audio is unusable: Gaussian noise at the level of all the signals together.</item>
/// </list>
/// The receiver's own noise (<see cref="SimLink.NoiseRms"/>) is added after, as before. With no model the channel is the adder of steps 5 to 7.
/// </summary>
/// <param name="MarginDb">The capture margin: how much stronger the strongest must be for the receiver to be captured by it. Assumed 3 dB.</param>
/// <param name="SuppressionBaseDb">How far the weaker signals are reduced at exactly the margin. Assumed 10 dB.</param>
/// <param name="SuppressionSlope">Further dB of reduction for each dB of difference above the margin. Assumed 1.</param>
internal sealed record CaptureModel(double MarginDb = 3.0, double SuppressionBaseDb = 10.0, double SuppressionSlope = 1.0)
{
    /// <summary>The window of the level estimate, samples (20 ms: under half an OFDM symbol, long enough that a signal's own amplitude swings do not flip the decision).</summary>
    public const int LevelWindowSamples = 960;

    /// <summary>A signal below this level (linear, after the path) is silence and takes no part.</summary>
    public const double ActiveFloor = 1e-5;

    /// <summary>The linear factor the weaker signals are multiplied by, for a strongest-to-others difference of <paramref name="differenceDb"/> at or above the margin.</summary>
    public double Residual(double differenceDb) => Math.Pow(10, -(SuppressionBaseDb + (SuppressionSlope * (differenceDb - MarginDb))) / 20);

    /// <summary>The levels of a signal: the trailing root mean square of <paramref name="data"/> over the window.</summary>
    public static float[] Levels(float[] data)
    {
        var level = new float[data.Length];
        double sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            sum += (double)data[i] * data[i];
            if (i >= LevelWindowSamples)
            {
                sum -= (double)data[i - LevelWindowSamples] * data[i - LevelWindowSamples];
            }

            int n = Math.Min(i + 1, LevelWindowSamples);
            level[i] = (float)Math.Sqrt(Math.Max(0, sum) / n);
        }

        return level;
    }
}

/// <summary>What one direction of a pair does to the audio. A new link carries nothing: set <see cref="Gain"/>.</summary>
internal sealed class SimLink
{
    /// <summary>Linear gain of the path from the transmitter to the receiver's input.</summary>
    public double Gain { get; set; }

    /// <summary>Standard deviation of the receiver's noise on this path, with an FM receiver's shape; zero for none. Added whether or not anything is transmitted; the noise of every path into a receiver adds.</summary>
    public double NoiseRms { get; set; }

    /// <summary>Whole samples of propagation and audio delay.</summary>
    public int DelaySamples { get; set; }

    /// <summary>The receiver's sample clock relative to the transmitter's, in parts per million (positive: the receiver's clock runs fast, as in <see cref="StreamBuilder"/>): the audio is resampled for it.</summary>
    public double Ppm { get; set; }

    /// <summary>Whether this transmitter's carrier counts for the receiver's carrier indication.</summary>
    public bool CarrierHeard { get; set; } = true;

    /// <summary>A reflection that fades the signal on this one direction of the path for an episode (see <see cref="FlutterModel"/>). Null (the default): none.</summary>
    public FlutterModel? Flutter { get; set; }

    internal FmNoise? Noise;
}

/// <summary>A 4th-order Butterworth low-pass, as two biquads in cascade, one sample at a time.</summary>
internal sealed class Filter
{
    private readonly (double B0, double B1, double B2, double A1, double A2)[] _sections;
    private readonly double[] _state;

    public Filter(double hz)
    {
        _sections = [Section(hz, 0.5411961), Section(hz, 1.3065630)];
        _state = new double[4];
    }

    private static (double, double, double, double, double) Section(double hz, double q)
    {
        double w = 2 * Math.PI * hz / 48000;
        double alpha = Math.Sin(w) / (2 * q);
        double cos = Math.Cos(w);
        double a0 = 1 + alpha;
        return ((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double y = samples[i];
            for (int k = 0; k < _sections.Length; k++)
            {
                (double b0, double b1, double b2, double a1, double a2) = _sections[k];
                double o = (b0 * y) + _state[k * 2];
                _state[k * 2] = (b1 * y) - (a1 * o) + _state[(k * 2) + 1];
                _state[(k * 2) + 1] = (b2 * y) - (a2 * o);
                y = o;
            }

            samples[i] = (float)y;
        }
    }
}

/// <summary>White Gaussian noise shaped like an FM receiver's audio noise (flat to about 300 Hz, rising 20 dB a decade above), from a seeded generator, drawn a sample at a time so that any block size gives the same noise.</summary>
internal sealed class FmNoise
{
    private const double Alpha = 48000.0 / (2 * Math.PI * 300);

    private readonly Random _rng;
    private readonly double _scale;
    private double _previous;
    private bool _haveSpare;
    private double _spare;

    public FmNoise(int seed, double rms)
    {
        _rng = new Random(seed);
        _scale = rms / Math.Sqrt(((1 + Alpha) * (1 + Alpha)) + (Alpha * Alpha));
    }

    private double Gauss()
    {
        if (_haveSpare)
        {
            _haveSpare = false;
            return _spare;
        }

        double u1 = 1 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        double r = Math.Sqrt(-2 * Math.Log(u1));
        _spare = r * Math.Sin(2 * Math.PI * u2);
        _haveSpare = true;
        return r * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Adds the next samples of noise to <paramref name="block"/>.</summary>
    public void AddTo(Span<float> block)
    {
        for (int i = 0; i < block.Length; i++)
        {
            double x = Gauss();
            double y = ((1 + Alpha) * x) - (Alpha * _previous);
            _previous = x;
            block[i] += (float)(y * _scale);
        }
    }
}

/// <summary>The simulated channel. See the comments at the top of the file.</summary>
internal sealed class SimChannel
{
    private readonly record struct SimAction(long Time, long Order, Action Do);

    private readonly int _block;
    private readonly int _seed;
    private readonly List<SimNode> _nodes = [];
    private readonly Dictionary<(int From, int To), SimLink> _links = [];
    private readonly List<SimAction> _actions = [];
    private readonly float[] _mix;
    private long _order;

    /// <summary>Makes a channel whose steps are at most <paramref name="blockSamples"/> long. <paramref name="seed"/> seeds every noise source.</summary>
    public SimChannel(int blockSamples = 4800, int seed = 1)
    {
        _block = blockSamples;
        _seed = seed;
        _mix = new float[blockSamples];
    }

    /// <summary>The clock every station is at, samples.</summary>
    public long Clock { get; private set; }

    /// <summary>The root mean square of a burst's audio at path gain 1 (0.44 for most formats, 0.23 for the dense ones, which are sent 6 dB lower): the level impulses (<see cref="ImpulseModel.LevelDb"/>) are given against. Pinned by a test.</summary>
    public const double ReferenceRms = 0.44;

    /// <summary>Set to keep, for every burst that reaches a receiver, what the path did to it (<see cref="SimNode.Deliveries"/>). Off by default.</summary>
    public bool RecordDeliveries { get; set; }

    /// <summary>
    /// Steps the loss of one direction of a path by <paramref name="db"/> (negative: weaker) when the clock reaches <paramref name="at"/>. A burst takes the path's gain from the moment it goes on the air, so a step takes effect from the next
    /// burst, as the path-cut scenarios always have. For the other direction too, call it again.
    /// </summary>
    public void StepPath(SimNode from, SimNode to, long at, double db) => Schedule(at, () => Link(from, to).Gain *= Db(db));

    /// <summary>What a receiver does when signals overlap. Null (the default): they add, as in steps 5 to 7. Set before <see cref="Run"/>.</summary>
    public CaptureModel? Capture { get; set; }

    /// <summary>Raised when a station's burst goes on air (after the station was told), with the clock at the start. A test uses it to put something on the channel relative to a burst.</summary>
    public event Action<SimNode, SimTransmission>? TransmissionStarted;

    /// <summary>Runs <paramref name="action"/> when the clock reaches <paramref name="time"/>, in order with the channel's own actions (a transmission start, a carrier indication).</summary>
    public void ScheduleAt(long time, Action action) => Schedule(time, action);

    /// <summary>
    /// Puts <paramref name="samples"/> on the air from <paramref name="from"/>'s antenna at <paramref name="startAt"/> without the station's knowledge (the station is not told and does not mute): another station keying, or
    /// whatever else is on the channel. It is heard where <paramref name="from"/>'s links reach and raises the receivers' carrier indications there. Its rolling noise and mixing are those of any transmission.
    /// </summary>
    public void Interfere(SimNode from, float[] samples, long startAt) => Schedule(startAt, () => Radiate(from, samples, Clock, 0));

    /// <summary>The stations, in the order added.</summary>
    public IReadOnlyList<SimNode> Nodes => _nodes;

    /// <summary>Every event of every station as text, with the clock it was raised at, in the order raised.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Adds a station. Its events are logged and recorded.</summary>
    public SimNode Add(Station station, string name)
    {
        var node = new SimNode(station, name, _nodes.Count);
        _nodes.Add(node);
        Hook(node);
        return node;
    }

    /// <summary>The one direction of a path, made on first use with no gain: nothing passes until <see cref="SimLink.Gain"/> is set.</summary>
    public SimLink Link(SimNode from, SimNode to)
    {
        if (!_links.TryGetValue((from.Index, to.Index), out SimLink? link))
        {
            link = new SimLink();
            _links[(from.Index, to.Index)] = link;
        }

        return link;
    }

    /// <summary>Connects two stations both ways with the same gain, noise and delay.</summary>
    public void Connect(SimNode a, SimNode b, double gain = 1.0, double noiseRms = 0.0, int delaySamples = 0)
    {
        foreach ((SimNode from, SimNode to) in new[] { (a, b), (b, a) })
        {
            SimLink l = Link(from, to);
            l.Gain = gain;
            l.NoiseRms = noiseRms;
            l.DelaySamples = delaySamples;
        }
    }

    /// <summary>Gain for a decibel figure.</summary>
    public static double Db(double db) => Math.Pow(10, db / 20);

    /// <summary>Runs every station until the clock reaches <paramref name="until"/>.</summary>
    public void Run(long until)
    {
        foreach (SimNode n in _nodes)
        {
            Prepare(n);
        }

        Settle();
        while (Clock < until)
        {
            long u = Math.Min(Clock + _block, until);
            foreach (SimNode n in _nodes)
            {
                if (n.Station.NextTimerDue is { } due)
                {
                    u = Math.Min(u, due);
                }

                u = Math.Min(u, Clock + Math.Max(1, n.Station.SamplesToNextFrame));
                if (n.DeafUntil > Clock)
                {
                    u = Math.Min(u, n.DeafUntil);
                }
            }

            foreach (SimAction a in _actions)
            {
                if (a.Time > Clock)
                {
                    u = Math.Min(u, a.Time);
                }
            }

            int count = (int)(u - Clock);
            foreach (SimNode n in _nodes)
            {
                Push(n, count);
            }

            Clock = u;
            Settle();
        }
    }

    private void Prepare(SimNode n)
    {
        if (n.ReceiveFilter is null && n.ReceiveLowPassHz is { } hz)
        {
            n.ReceiveFilter = new Filter(hz);
        }

        if (n.ImpulseStream is null && n.Impulses is { } impulses)
        {
            n.ImpulseStream = new ImpulseSource(impulses, unchecked((_seed * 15485863) + (n.Index * 2038) + 29));
        }

        foreach (SimNode from in _nodes)
        {
            if (from != n && _links.TryGetValue((from.Index, n.Index), out SimLink? l) && l.Noise is null && l.NoiseRms > 0)
            {
                l.Noise = new FmNoise(unchecked((_seed * 7919) + (from.Index * 131) + (n.Index * 17) + 3), l.NoiseRms);
            }
        }
    }

    /// <summary>Feeds one station its audio for the next <paramref name="count"/> samples: what it hears from the others, noise, muted while it transmits or is deaf, through its receive filter and gain.</summary>
    private void Push(SimNode n, int count)
    {
        Span<float> block = _mix.AsSpan(0, count);
        block.Clear();
        long from = Clock;
        long to = Clock + count;
        if (Capture is { } capture && n.Heard.Count >= 2)
        {
            n.CaptureRandom ??= new Random(unchecked((_seed * 104729) + (n.Index * 7907) + 11));
            MixWithCapture(n, block, from, to, capture);
        }
        else
        {
            for (int i = n.Heard.Count - 1; i >= 0; i--)
            {
                Incoming h = n.Heard[i];
                long a = Math.Max(h.Start, from);
                long b = Math.Min(h.Start + h.Data.Length, to);
                for (long k = a; k < b; k++)
                {
                    block[(int)(k - from)] += h.Data[(int)(k - h.Start)];
                }
            }
        }

        for (int i = n.Heard.Count - 1; i >= 0; i--)
        {
            if (n.Heard[i].Start + n.Heard[i].Data.Length <= to)
            {
                n.Heard.RemoveAt(i);
            }
        }

        foreach (SimNode other in _nodes)
        {
            if (other != n && _links.TryGetValue((other.Index, n.Index), out SimLink? l) && l.Noise is not null)
            {
                l.Noise.AddTo(block);
            }
        }

        n.ImpulseStream?.AddTo(block, from);

        bool muted = n.Transmitting || Clock < n.DeafUntil;
        if (muted)
        {
            block.Clear();
        }

        n.ReceiveFilter?.Process(block);
        if (n.ReceiveGain != 1.0)
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = (float)(block[i] * n.ReceiveGain);
            }
        }

        if (n.RecordInput)
        {
            n.Input.AddRange(block.ToArray());
        }

        n.Station.Push(block);
    }

    /// <summary>
    /// The audio a receiver hears over <paramref name="from"/> to <paramref name="to"/> when signals may overlap (see <see cref="CaptureModel"/>). A sample where one signal or none is above the floor is the sum, as before;
    /// where two or more are, the strongest is compared with the power sum of the rest.
    /// </summary>
    internal static void MixWithCapture(SimNode n, Span<float> block, long from, long to, CaptureModel capture)
    {
        List<Incoming> heard = n.Heard;
        Random rng = n.CaptureRandom!;
        for (long k = from; k < to; k++)
        {
            int active = 0;
            int strongest = -1;
            double strongestLevel = 0;
            double power = 0;
            double sum = 0;
            for (int i = 0; i < heard.Count; i++)
            {
                Incoming h = heard[i];
                long idx = k - h.Start;
                if (idx < 0 || idx >= h.Data.Length)
                {
                    continue;
                }

                sum += h.Data[(int)idx];
                double level = h.Level![(int)idx];
                if (level < CaptureModel.ActiveFloor)
                {
                    continue;
                }

                active++;
                power += level * level;
                if (level > strongestLevel)
                {
                    strongestLevel = level;
                    strongest = i;
                }
            }

            if (active < 2)
            {
                block[(int)(k - from)] = (float)sum;
                continue;
            }

            double others = power - (strongestLevel * strongestLevel);
            double difference = 20 * Math.Log10(strongestLevel / Math.Sqrt(others));
            double noise = Gauss(rng);
            if (difference >= capture.MarginDb)
            {
                Incoming s = heard[strongest];
                block[(int)(k - from)] = (float)(s.Data[(int)(k - s.Start)] + (noise * Math.Sqrt(others) * capture.Residual(difference)));
            }
            else
            {
                block[(int)(k - from)] = (float)(noise * Math.Sqrt(power));
            }
        }
    }

    private static double Gauss(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>
    /// At the clock: runs what is due (timers, results), answers the stations' requests and withdrawals, and runs the sim's own actions due now, until nothing more happens at this sample.
    /// </summary>
    private void Settle()
    {
        bool progress;
        do
        {
            progress = false;
            foreach (SimNode n in _nodes)
            {
                if (n.Station.NextTimerDue is { } due && due <= Clock)
                {
                    n.Station.Push(default);
                    progress = true;
                }

                n.Station.DeliverResults();
                if (n.Station.NextTimerDue is { } due2 && due2 <= Clock)
                {
                    progress = true;
                }
            }

            foreach (SimNode n in _nodes)
            {
                while (n.Events.Count > 0)
                {
                    Answer(n, n.Events.Dequeue());
                    progress = true;
                }
            }

            for (;;)
            {
                int pick = -1;
                for (int i = 0; i < _actions.Count; i++)
                {
                    if (_actions[i].Time <= Clock && (pick < 0 || (_actions[i].Time, _actions[i].Order).CompareTo((_actions[pick].Time, _actions[pick].Order)) < 0))
                    {
                        pick = i;
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                SimAction a = _actions[pick];
                _actions.RemoveAt(pick);
                a.Do();
                progress = true;
            }
        }
        while (progress);
    }

    private void Schedule(long time, Action action) => _actions.Add(new SimAction(time, _order++, action));

    /// <summary>The audio layer's answer to a request or a withdrawal.</summary>
    private void Answer(SimNode n, NodeEvent e)
    {
        if (!e.IsWithdrawal)
        {
            var plan = new Plan { Id = e.Id, Samples = e.Samples, StartAt = e.Earliest + n.StartLatencySamples };
            n.Plans[e.Id] = plan;
            Schedule(plan.StartAt, () => Start(n, plan));
            return;
        }

        // A withdrawal raised before the audio layer would have keyed the request takes it back; at or after that sample the request has started anyway (the race the handshake tolerates).
        if (n.Plans.TryGetValue(e.Id, out Plan? p) && !p.Started && e.Stamp < p.StartAt)
        {
            p.Cancelled = true;
            n.WithdrawnBeforeStart.Add(e.Id);
        }
        else
        {
            n.WithdrawnTooLate.Add(e.Id);
        }
    }

    private void Start(SimNode n, Plan plan)
    {
        if (plan.Cancelled)
        {
            return;
        }

        if (n.RefuseRequest is { } refuse && refuse(n.Requests.FindIndex(r => r.Id == plan.Id) + 1))
        {
            n.Refused.Add((plan.Id, Clock));
            n.Plans.Remove(plan.Id);
            if (n.ReportRefusals)
            {
                n.Station.TransmitRefused(plan.Id);
            }

            return;
        }

        plan.Started = true;
        int modemSentBefore = n.ModemSent.Count;
        n.Station.TransmitStarted(plan.Id, Clock);
        n.Transmitting = true;
        float[] tx = plan.Samples.ToArray();
        if (n.IdentificationTailSamples > 0 && n.ModemSent.Count > modemSentBefore && n.ModemSent[^1].Kind == ModemSentKind.Identification)
        {
            tx = AddMorse(tx, n.IdentificationTailSamples);
        }

        if (n.TransmitLowPassHz is { } hz)
        {
            new Filter(hz).Process(tx);
        }

        int swallowed = Math.Min(n.KeyUpSamples, tx.Length);
        tx.AsSpan(0, swallowed).Clear();
        long start = Clock;
        int length = tx.Length;
        var record = new SimTransmission(plan.Id, start, length);
        n.Transmissions.Add(record);
        Radiate(n, tx, start, swallowed);
        TransmissionStarted?.Invoke(n, record);

        Schedule(start + length, () =>
        {
            n.Station.TransmitEnded(plan.Id, Clock);
            n.Transmitting = false;
            n.DeafUntil = Math.Max(n.DeafUntil, Clock + n.DeafSamples);
            n.Plans.Remove(plan.Id);
        });
    }

    /// <summary>The host's Morse after an identification burst, as the modem on another station sees it: nothing. The transmitter is keyed for <paramref name="tail"/> samples more and the others' receivers hear silence (the premise of the announced tail: Morse is not a signal the modem finds).</summary>
    private static float[] AddMorse(float[] tx, int tail)
    {
        float[] all = new float[tx.Length + tail];
        tx.CopyTo(all, 0);
        return all;
    }

    /// <summary>Sends <paramref name="tx"/> (already filtered and cut for key-up) to every receiver the antenna reaches.</summary>
    private void Radiate(SimNode n, float[] tx, long start, int swallowed)
    {
        int length = tx.Length;
        foreach (SimNode rx in _nodes)
        {
            if (rx == n || !_links.TryGetValue((n.Index, rx.Index), out SimLink? link) || link.Gain == 0)
            {
                continue;
            }

            float[] heard = Resample(tx, link.Ppm, link.Gain);
            long at = start + link.DelaySamples;
            double flutterMin = 1.0;
            double flutterSum = 0;
            if (link.Flutter is { } flutter)
            {
                flutterMin = double.MaxValue;
                for (int i = 0; i < heard.Length; i++)
                {
                    double g = flutter.GainAt(at + i);
                    heard[i] = (float)(heard[i] * g);
                    flutterMin = Math.Min(flutterMin, g);
                    flutterSum += g;
                }
            }

            if (RecordDeliveries)
            {
                rx.Deliveries.Add(new SimDelivery(n.Index, start, at, length, link.Gain, flutterMin, link.Flutter is null ? 1.0 : flutterSum / Math.Max(1, heard.Length)));
            }

            rx.Heard.Add(new Incoming { Start = at, Data = heard, Level = Capture is null ? null : CaptureModel.Levels(heard) });
            if (rx.CarrierDelaySamples is { } delay && link.CarrierHeard)
            {
                SimNode r = rx;
                Schedule(at + swallowed + delay, () => Carrier(r, +1));
                Schedule(at + length + delay, () => Carrier(r, -1));
            }
        }
    }

    private void Carrier(SimNode rx, int change)
    {
        bool was = rx.Carriers > 0;
        rx.Carriers += change;
        bool now = rx.Carriers > 0;
        if (was != now)
        {
            rx.Station.SetExternalBusy(now);
        }
    }

    private static float[] Resample(float[] audio, double ppm, double gain)
    {
        var heard = new float[audio.Length];
        if (ppm == 0)
        {
            for (int i = 0; i < audio.Length; i++)
            {
                heard[i] = (float)(audio[i] * gain);
            }

            return heard;
        }

        var padded = new float[audio.Length + 300];
        audio.CopyTo(padded, 0);
        double seconds = audio.Length / 48000.0;
        var output = new float[padded.Length];
        DelayResampler.Resample(padded, 0, padded.Length, [0.0, seconds], [0.0, ppm * 1e-6 * seconds], BurstRenderer.DefaultLeadSamples, output);
        for (int i = 0; i < audio.Length; i++)
        {
            heard[i] = (float)(output[i] * gain);
        }

        return heard;
    }

    // ---- events ----

    private void Hook(SimNode n)
    {
        Station s = n.Station;
        string name = n.Name;
        void Say(string text) => Log.Add(string.Create(CultureInfo.InvariantCulture, $"{s.SampleCount} {name} {text}"));

        s.Received += e =>
        {
            n.Received.Add(new SimReceived(s.SampleCount, s.HearingSender, e.Addressing == Addressing.ToEveryone || e.Addressing == Addressing.ForAddressNotYetLocated, e.Addressing, e.ForKey, e.Datagram.ToArray(), e.QualityDb, e.FromKey, e.Protocol));
            Say(string.Create(CultureInfo.InvariantCulture, $"rx from {s.HearingSender} key {e.FromKey:X8} proto {e.Protocol:X2} {e.Addressing} for {(e.ForKey is { } k ? k.ToString("X8", CultureInfo.InvariantCulture) : "-")} q {e.QualityDb:R} data {Convert.ToHexString(e.Datagram.Span)}"));
        };
        s.DataBurstSent += (dest, asked, end, address) => n.BurstsSent.Add((end, dest, asked, address));
        s.SilenceCounted += (dest, count) => n.SilenceCounts.Add((s.SampleCount, dest, count));
        s.PathReport += e =>
        {
            n.PathReports.Add(e);
            Say(string.Create(CultureInfo.InvariantCulture, $"path {e.Address} fmt {e.RecommendedFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"} failed {e.Failed}"));
        };
        s.LinkState += e =>
        {
            n.LinkStates.Add(e);
            Say(string.Create(CultureInfo.InvariantCulture, $"link {e.Address} located {e.Located} fmt {e.Format?.ToString(CultureInfo.InvariantCulture) ?? "-"} rate {e.RateBitsPerSecond?.ToString("R", CultureInfo.InvariantCulture) ?? "-"} told {e.RecommendedRateBitsPerSecond?.ToString("R", CultureInfo.InvariantCulture) ?? "-"} room {e.DatagramRoom} {e.Knowledge} age {e.AgeSeconds:R} gone {e.Gone}"));
        };
        s.Transmitted += e =>
        {
            n.Transmitted.Add((s.SampleCount, e));
            Say(string.Create(CultureInfo.InvariantCulture, $"transmitted tag {e.Tag.Value} to {e.Address} burst {e.Burst} fmt {e.Format} rate {e.RateBitsPerSecond:R} airtime {e.AirtimeSeconds:R} out {e.TimeOutSeconds:R}"));
        };
        s.Dropped += e =>
        {
            n.Dropped.Add(e);
            Say($"dropped tag {e.Tag.Value} {e.Reason}");
        };
        s.Congestion += e =>
        {
            n.Congestion.Add(e);
            Say($"congestion {e.Address} writable {e.Writable}");
        };
        s.ModemSent += e =>
        {
            n.ModemSent.Add(e);
            n.ModemSentTo.Add(s.ModemSentTo);
            Say($"modem sent {e.Kind} fmt {e.Format}");
        };
        s.IdentificationDue += e =>
        {
            n.IdentificationsDue.Add((s.SampleCount, e));
            Say(string.Create(CultureInfo.InvariantCulture, $"identification due {e.Seconds:R}"));
        };
        s.Identified += e =>
        {
            n.Identified.Add((s.SampleCount, e, s.HearingSender));
            Say(string.Create(CultureInfo.InvariantCulture, $"identified {s.HearingSender} {e.Callsign} cap {e.Capability}"));
        };
        s.BurstNotDecoded += e =>
        {
            long[] tags = [.. e.Tags.Select(t => t.Value)];
            n.NotDecoded.Add((s.SampleCount, e.Address, tags));
            Say($"notdecoded {e.Address} tags {string.Join(",", tags)}");
        };
        s.ChannelBusy += e => Say($"busy {e.Busy}");
        s.TransmitRequest += e =>
        {
            n.Events.Enqueue(new NodeEvent(false, e.Id, s.SampleCount, e.Samples, e.EarliestSample));
            long sum = 0;
            foreach (float f in e.Samples.Span)
            {
                sum = unchecked((sum * 31) + BitConverter.SingleToInt32Bits(f));
            }

            n.Requests.Add(new SimRequest(e.Id, s.SampleCount, e.Samples.Length, sum));
            Say($"request {e.Id} samples {e.Samples.Length} earliest {e.EarliestSample}");
        };
        s.WithdrawRequest += e =>
        {
            n.Events.Enqueue(new NodeEvent(true, e.Id, s.SampleCount, default, 0));
            Say($"withdraw {e.Id}");
        };
    }
}
