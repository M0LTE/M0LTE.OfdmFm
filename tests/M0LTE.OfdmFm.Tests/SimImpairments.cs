namespace M0LTE.OfdmFm.Tests;

// Three impairments for the simulated channel (docs/channel-sharing.md, "Baseline: how the station behaves today"), each off by default. Like every other source in the sim, each is a function of the sample clock or a stream
// drawn one sample at a time in order, so that a run is exactly reproducible and does not depend on the block size: impulses are generated sample by sample from the first sample of the run, and flutter is a pure function of
// the absolute sample at which each sample of a burst reaches the receiver. A steady path change is SimChannel.StepPath.

/// <summary>
/// Short bursts of wideband noise on a receiver's input, as an FM receiver's audio shows ignition and switching noise. Events arrive at random (Poisson) at <see cref="RatePerSecond"/> per second. Each is a single impulse of 0.2 to 5 ms,
/// or, with probability <see cref="TrainProbability"/>, a train: impulses at a fixed repetition rate of 50 to 200 Hz for 0.1 to 1 s (an engine, a switching supply), each of 0.2 to 5 ms. The impulse is white Gaussian noise; the receiver's
/// own low-pass (if the node has one) shapes it afterwards. Per receiver: two stations hear different impulses.
/// </summary>
/// <param name="RatePerSecond">Mean arrivals of events (single impulses or trains) per second.</param>
/// <param name="LevelDb">The impulse's root mean square in dB relative to <see cref="SimChannel.ReferenceRms"/>, the level of a burst on a path with gain 1. To say "10 dB over the wanted signal" on a path of gain g, give 10 + 20 log10(g).</param>
/// <param name="TrainProbability">The share of events that are trains, 0 to 1.</param>
internal sealed record ImpulseModel(double RatePerSecond, double LevelDb, double TrainProbability = 0.0)
{
    /// <summary>Shortest and longest single impulse, seconds.</summary>
    public const double MinSeconds = 0.0002;

    /// <summary>The longest impulse, seconds.</summary>
    public const double MaxSeconds = 0.005;

    /// <summary>Slowest and fastest repetition rate of a train, Hz.</summary>
    public const double MinTrainHz = 50;

    /// <summary>The fastest repetition rate of a train, Hz.</summary>
    public const double MaxTrainHz = 200;

    /// <summary>Shortest and longest train, seconds.</summary>
    public const double MinTrainSeconds = 0.1;

    /// <summary>The longest train, seconds.</summary>
    public const double MaxTrainSeconds = 1.0;

    /// <summary>The impulse's level relative to the wanted signal on a path of linear gain <paramref name="pathGain"/>, in the units of <see cref="LevelDb"/>.</summary>
    public static double LevelFor(double dbOverWanted, double pathGain) => dbOverWanted + (20 * Math.Log10(pathGain));
}

/// <summary>One impulse as it happened: when it started and ended, in samples on the channel clock, and the train it belonged to (-1 for a single).</summary>
internal readonly record struct ImpulsePulse(long Start, long End, int Train);

/// <summary>The stream of impulses at one receiver. Drawn one sample at a time in order, from seeded generators, so that any block size gives the same samples.</summary>
internal sealed class ImpulseSource
{
    private readonly ImpulseModel _model;
    private readonly Random _arrivals;
    private readonly Random _noise;
    private readonly double _amplitude;
    private readonly PriorityQueue<(long Start, int Length, int Train), long> _pending = new();
    private readonly List<(long End, int Length)> _active = [];
    private long _nextEvent;
    private long _position = -1;
    private int _trains;
    private bool _haveSpare;
    private double _spare;

    public ImpulseSource(ImpulseModel model, int seed)
    {
        _model = model;
        _arrivals = new Random(seed);
        _noise = new Random(unchecked(seed * 31 + 7));
        _amplitude = SimChannel.ReferenceRms * Math.Pow(10, model.LevelDb / 20);
        _nextEvent = model.RatePerSecond > 0 ? Gap() : long.MaxValue;
    }

    /// <summary>Every impulse that has started so far, in order of start.</summary>
    public List<ImpulsePulse> Pulses { get; } = [];

    /// <summary>Events (single impulses and trains) that have arrived so far.</summary>
    public int Events { get; private set; }

    private long Gap() => Math.Max(1, (long)Math.Ceiling(-Math.Log(1 - _arrivals.NextDouble()) / _model.RatePerSecond * 48000));

    private int PulseLength() => Math.Max(1, (int)Math.Round((ImpulseModel.MinSeconds + (_arrivals.NextDouble() * (ImpulseModel.MaxSeconds - ImpulseModel.MinSeconds))) * 48000));

    private void Arrive(long at)
    {
        Events++;
        if (_arrivals.NextDouble() < _model.TrainProbability)
        {
            double hz = ImpulseModel.MinTrainHz + (_arrivals.NextDouble() * (ImpulseModel.MaxTrainHz - ImpulseModel.MinTrainHz));
            double seconds = ImpulseModel.MinTrainSeconds + (_arrivals.NextDouble() * (ImpulseModel.MaxTrainSeconds - ImpulseModel.MinTrainSeconds));
            int count = Math.Max(1, (int)(seconds * hz));
            int train = _trains++;
            for (int i = 0; i < count; i++)
            {
                long start = at + (long)Math.Round(i * 48000.0 / hz);
                _pending.Enqueue((start, PulseLength(), train), start);
            }
        }
        else
        {
            _pending.Enqueue((at, PulseLength(), -1), at);
        }
    }

    private double Gauss()
    {
        if (_haveSpare)
        {
            _haveSpare = false;
            return _spare;
        }

        double u1 = 1 - _noise.NextDouble();
        double u2 = _noise.NextDouble();
        double r = Math.Sqrt(-2 * Math.Log(u1));
        _spare = r * Math.Sin(2 * Math.PI * u2);
        _haveSpare = true;
        return r * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Adds the impulses over the samples <paramref name="from"/> onwards to <paramref name="block"/>. Calls must be contiguous.</summary>
    public void AddTo(Span<float> block, long from)
    {
        if (_position >= 0 && from != _position)
        {
            throw new InvalidOperationException("Impulses are a stream: the blocks must follow one another.");
        }

        long to = from + block.Length;
        _position = to;
        if (_active.Count == 0 && _pending.Count == 0 && _nextEvent >= to)
        {
            return;
        }

        for (long k = from; k < to; k++)
        {
            while (_nextEvent <= k)
            {
                Arrive(_nextEvent);
                _nextEvent += Gap();
            }

            while (_pending.TryPeek(out (long Start, int Length, int Train) p, out long t) && t <= k)
            {
                _pending.Dequeue();
                _active.Add((p.Start + p.Length, p.Length));
                Pulses.Add(new ImpulsePulse(p.Start, p.Start + p.Length, p.Train));
            }

            if (_active.Count > 0)
            {
                double sum = 0;
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    if (_active[i].End <= k)
                    {
                        _active.RemoveAt(i);
                    }
                    else
                    {
                        sum += Gauss() * _amplitude;
                    }
                }

                block[(int)(k - from)] += (float)sum;
            }
        }
    }

    /// <summary>Whether any impulse overlaps the samples from <paramref name="from"/> up to <paramref name="to"/>.</summary>
    public bool Overlaps(long from, long to)
    {
        // Pulses are in order of start and at most 5 ms long, so those that can overlap start within 5 ms before <from>.
        int lo = 0;
        int hi = Pulses.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Pulses[mid].Start < from - 480)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < Pulses.Count && Pulses[i].Start < to; i++)
        {
            if (Pulses[i].End > from)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A reflection off an aircraft or a moving vehicle: the path is the sum of the direct path (relative amplitude 1) and one reflected path of relative amplitude <see cref="ReflectedStrength"/> whose phase against the direct path
/// rotates, so the amplitude is |1 + a e^(j phase)|, normalised by sqrt(1 + a^2) so that the mean power through the path is unchanged: the fades are the only difference. A strength of 0.5 fades 9.5 dB peak to trough, 0.9 about 26 dB.
/// The reflection comes up and goes away over <see cref="RampSeconds"/> (raised cosine, at most a quarter of the episode each way). Outside the episode the path is plain (gain 1).
/// The fade rate is the rate of rotation of the phase in Hz: a fixed rate, or, when <see cref="Swept"/>, one that falls linearly from <see cref="FadeRateHz"/> through zero (the aircraft abeam) to the same rate on the other side, as a crossing does.
/// This is a gain on the signal level against the receiver's fixed noise, which is what an FM receiver's audio signal-to-noise sees; capture and the threshold effects of an FM receiver below its threshold are NOT modelled.
/// </summary>
/// <param name="ReflectedStrength">Relative amplitude of the reflected path, 0 to under 1.</param>
/// <param name="FadeRateHz">The fade rate, Hz (the largest, when swept).</param>
/// <param name="Swept">True: the rate sweeps from the given rate through zero and back up.</param>
/// <param name="StartSeconds">When the episode starts, seconds on the channel clock.</param>
/// <param name="DurationSeconds">How long it lasts, seconds.</param>
/// <param name="InitialPhase">The phase at the start, radians; seeds make the two directions of a path differ.</param>
/// <param name="RampSeconds">How long the reflection takes to come up and to go away.</param>
internal sealed record FlutterModel(double ReflectedStrength, double FadeRateHz, bool Swept, double StartSeconds, double DurationSeconds, double InitialPhase = 0.0, double RampSeconds = 1.0)
{
    /// <summary>The linear gain of the path at <paramref name="sample"/>.</summary>
    public double GainAt(long sample)
    {
        double tau = (sample / 48000.0) - StartSeconds;
        if (tau <= 0 || tau >= DurationSeconds)
        {
            return 1.0;
        }

        double ramp = Math.Min(RampSeconds, DurationSeconds / 4);
        double edge = Math.Min(tau, DurationSeconds - tau);
        double a = ReflectedStrength;
        if (edge < ramp)
        {
            a *= 0.5 * (1 - Math.Cos(Math.PI * edge / ramp));
        }

        double phase = InitialPhase + (2 * Math.PI * FadeRateHz * (Swept ? tau - (tau * tau / DurationSeconds) : tau));
        double re = 1 + (a * Math.Cos(phase));
        double im = a * Math.Sin(phase);
        return Math.Sqrt(((re * re) + (im * im)) / (1 + (a * a)));
    }

    /// <summary>Whether the episode is in progress at <paramref name="sample"/>.</summary>
    public bool Active(long sample)
    {
        double tau = (sample / 48000.0) - StartSeconds;
        return tau > 0 && tau < DurationSeconds;
    }
}

/// <summary>A burst as it reached a receiver: the ground truth the simulator keeps for every transmission, when <see cref="SimChannel.RecordDeliveries"/> is set.</summary>
/// <param name="From">The transmitting node's index.</param>
/// <param name="TxStart">The sample the transmission started at on the transmitter (what <see cref="SimTransmission.Start"/> says).</param>
/// <param name="At">The sample its first sample reaches the receiver (the start plus the path's delay).</param>
/// <param name="Length">Its length, samples.</param>
/// <param name="Gain">The path's steady gain when it went on the air (flutter not included).</param>
/// <param name="FlutterMin">The lowest flutter gain over the burst (1 when the path does not flutter).</param>
/// <param name="FlutterMean">The mean flutter gain over the burst.</param>
internal sealed record SimDelivery(int From, long TxStart, long At, int Length, double Gain, double FlutterMin, double FlutterMean);
