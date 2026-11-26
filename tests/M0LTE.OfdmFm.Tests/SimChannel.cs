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

    /// <summary>After the station's own transmission ends the receiver hears nothing for this many samples.</summary>
    public int DeafSamples { get; set; }

    /// <summary>The carrier indication: null means the host supplies none and <see cref="Station.SetExternalBusy"/> is never called. Otherwise it follows any carrier heard at the receiver, this many samples late.</summary>
    public int? CarrierDelaySamples { get; set; }

    /// <summary>The audio layer's own delay between a request and playing it, samples: the request starts at its earliest sample plus this. During it the station may withdraw the request.</summary>
    public int StartLatencySamples { get; set; }

    // ---- what the tests read ----

    /// <summary>Every datagram received: the clock, the sender's number, whether it was for everyone, the bytes and the quality.</summary>
    public List<SimReceived> Received { get; } = [];

    /// <summary>Every <see cref="Station.Transmitted"/> event, in order, with the clock.</summary>
    public List<(long Clock, TransmittedEvent Event)> Transmitted { get; } = [];

    /// <summary>Every <see cref="Station.Dropped"/> event.</summary>
    public List<DroppedEvent> Dropped { get; } = [];

    /// <summary>Every <see cref="Station.Congestion"/> event.</summary>
    public List<CongestionEvent> Congestion { get; } = [];

    /// <summary>Every <see cref="Station.ModemSent"/> event.</summary>
    public List<ModemSentEvent> ModemSent { get; } = [];

    /// <summary>Every <see cref="Station.PathReport"/> event.</summary>
    public List<PathReportEvent> PathReports { get; } = [];

    /// <summary>Every <see cref="Station.BurstNotDecoded"/> event: the clock, the station and the tags.</summary>
    public List<(long Clock, StationName Station, long[] Tags)> NotDecoded { get; } = [];

    /// <summary>Every <see cref="Station.LinkState"/> event.</summary>
    public List<LinkStateEvent> LinkStates { get; } = [];

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
}

/// <summary>A datagram a station received.</summary>
internal sealed record SimReceived(long Clock, int Sender, bool Everyone, byte[] Data, double QualityDb);

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
        for (int i = n.Heard.Count - 1; i >= 0; i--)
        {
            Incoming h = n.Heard[i];
            long end = h.Start + h.Data.Length;
            long a = Math.Max(h.Start, from);
            long b = Math.Min(end, to);
            for (long k = a; k < b; k++)
            {
                block[(int)(k - from)] += h.Data[(int)(k - h.Start)];
            }

            if (end <= to)
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

        n.Station.Push(block);
    }

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

        plan.Started = true;
        n.Station.TransmitStarted(plan.Id, Clock);
        n.Transmitting = true;
        float[] tx = plan.Samples.ToArray();
        if (n.TransmitLowPassHz is { } hz)
        {
            new Filter(hz).Process(tx);
        }

        int swallowed = Math.Min(n.KeyUpSamples, tx.Length);
        tx.AsSpan(0, swallowed).Clear();
        long start = Clock;
        int length = tx.Length;
        n.Transmissions.Add(new SimTransmission(plan.Id, start, length));
        foreach (SimNode rx in _nodes)
        {
            if (rx == n || !_links.TryGetValue((n.Index, rx.Index), out SimLink? link) || link.Gain == 0)
            {
                continue;
            }

            float[] heard = Resample(tx, link.Ppm, link.Gain);
            long at = start + link.DelaySamples;
            rx.Heard.Add(new Incoming { Start = at, Data = heard });
            if (rx.CarrierDelaySamples is { } delay && link.CarrierHeard)
            {
                SimNode r = rx;
                Schedule(at + swallowed + delay, () => Carrier(r, +1));
                Schedule(at + length + delay, () => Carrier(r, -1));
            }
        }

        Schedule(start + length, () =>
        {
            n.Station.TransmitEnded(plan.Id, Clock);
            n.Transmitting = false;
            n.DeafUntil = Math.Max(n.DeafUntil, Clock + n.DeafSamples);
            n.Plans.Remove(plan.Id);
        });
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
            n.Received.Add(new SimReceived(s.SampleCount, e.SenderNumber, e.ToEveryone, e.Datagram.ToArray(), e.QualityDb));
            Say(string.Create(CultureInfo.InvariantCulture, $"rx from {e.SenderNumber} name {(e.Sender is { } who ? who.ToString() : "-")} everyone {e.ToEveryone} q {e.QualityDb:R} data {Convert.ToHexString(e.Datagram.Span)}"));
        };
        s.PathReport += e =>
        {
            n.PathReports.Add(e);
            Say(string.Create(CultureInfo.InvariantCulture, $"path {e.Station} fmt {e.RecommendedFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"} failed {e.Failed}"));
        };
        s.LinkState += e =>
        {
            n.LinkStates.Add(e);
            Say(string.Create(CultureInfo.InvariantCulture, $"link {e.Station} fmt {e.Format?.ToString(CultureInfo.InvariantCulture) ?? "-"} rate {e.RateBitsPerSecond?.ToString("R", CultureInfo.InvariantCulture) ?? "-"} room {e.DatagramRoom} {e.Knowledge} age {e.AgeSeconds:R} gone {e.Gone}"));
        };
        s.Transmitted += e =>
        {
            n.Transmitted.Add((s.SampleCount, e));
            Say(string.Create(CultureInfo.InvariantCulture, $"transmitted tag {e.Tag.Value} to {e.Station} burst {e.Burst} fmt {e.Format} rate {e.RateBitsPerSecond:R} airtime {e.AirtimeSeconds:R} out {e.TimeOutSeconds:R}"));
        };
        s.Dropped += e =>
        {
            n.Dropped.Add(e);
            Say($"dropped tag {e.Tag.Value} {e.Reason}");
        };
        s.Congestion += e =>
        {
            n.Congestion.Add(e);
            Say($"congestion {e.Station} writable {e.Writable}");
        };
        s.ModemSent += e =>
        {
            n.ModemSent.Add(e);
            Say($"modem sent {e.Kind} to {(e.Station is { } to ? to.ToString() : "-")} fmt {e.Format}");
        };
        s.BurstNotDecoded += e =>
        {
            long[] tags = [.. e.Tags.Select(t => t.Value)];
            n.NotDecoded.Add((s.SampleCount, e.Station, tags));
            Say($"notdecoded {e.Station} tags {string.Join(",", tags)}");
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
