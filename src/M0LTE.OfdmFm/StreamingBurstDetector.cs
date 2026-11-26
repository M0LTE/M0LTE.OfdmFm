namespace M0LTE.OfdmFm;

/// <summary>A burst start found by the streaming detector.</summary>
/// <param name="Opening">The opening whose training waveform matched.</param>
/// <param name="Index">Count of samples pushed before the burst's first training sample.</param>
/// <param name="Peak">The normalised correlation there, 0 to 1.</param>
internal readonly record struct DetectedBurst(FormatOpening Opening, long Index, double Peak);

/// <summary>
/// <see cref="BurstFinder"/> for a stream: audio is pushed in blocks of any size and the burst starts of the three openings
/// come out, each confirmed a symbol or more after it happened (a peak is only known to be the largest within a symbol
/// either side once a symbol more has been seen), plus up to one block of transform delay.
/// </summary>
/// <remarks>
/// <para><b>Correlation.</b> The audio is cut into frames of <see cref="TransformSize"/> samples that overlap by the longest
/// reference less one (overlap-save), so each frame yields <see cref="Hop"/> new correlation values per opening. Each frame
/// is transformed once and the spectrum is shared; each opening has its own precomputed reference spectrum. Two openings'
/// products are packed into one complex inverse transform (the first in the real part, the second in the imaginary part: both
/// results are real), the third has one to itself, so a frame costs one forward and two inverse transforms. The value is
/// the one <c>BurstFinder.Find</c> computes: the absolute correlation divided by the square root of the energy under
/// the reference window times the reference's energy. The energy is a running sum of squares started afresh in every frame, so
/// its rounding cannot drift over hours of audio.</para>
/// <para><b>Peaks.</b> Find's rule is a strict local maximum (a flat top counts once, at its middle), at or over the threshold,
/// then a greedy suppression from the highest peak down in which any peak closer than a symbol to a kept one is removed
/// (equal heights: lower index first). That is not the same as "largest within a symbol either side": a lower peak beside a
/// removed higher one is kept. This class applies the same greedy rule to a short list of pending peaks, and decides a peak
/// when every higher peak within a symbol is decided (so it can be settled as soon as one symbol after it has been seen, in the
/// ordinary case where no higher peak is near, and sooner when a kept one already covers it). Candidates come out in order of
/// index per opening.</para>
/// <para><b>Threshold.</b> A fixed one, or per opening <see cref="RunningThreshold"/>. A peak is judged against the threshold
/// as it stands when the peak is first seen. The estimate can be paused and resumed from a sample index.</para>
/// <para>Memory is fixed at construction (about 4 MB), nothing allocates per push, and there are no threads, clocks or statics.
/// Not thread-safe. If the candidate queue is full the newest is dropped and counted in <see cref="Lost"/>.</para>
/// </remarks>
internal sealed class StreamingBurstDetector
{
    /// <summary>Samples per transform: the next power of two over the longest reference plus a block worth having.</summary>
    public const int TransformSize = 32768;

    /// <summary>New correlation values per frame, per opening: the transform size less the longest reference plus one.</summary>
    public const int Hop = TransformSize - (6 * BurstRenderer.SymbolSamples) + 1;

    private const int Sym = BurstRenderer.SymbolSamples;
    private const int Openings = 3;
    private const int PendingCapacity = 4096;
    private const int QueueCapacity = 1024;
    private const int MarkCapacity = 64;

    private const byte Undecided = 0;
    private const byte Kept = 1;
    private const byte Removed = 2;
    private const byte Emitted = 3;

    private readonly double? _fixed;
    private readonly double _floor;
    private readonly ComplexFft _fft = new(TransformSize);
    private readonly double[] _frame = new double[TransformSize];
    private readonly double[] _cumulative = new double[TransformSize + 1];
    private readonly double[] _spectrumRe = new double[TransformSize];
    private readonly double[] _spectrumIm = new double[TransformSize];
    private readonly double[] _workRe = new double[TransformSize];
    private readonly double[] _workIm = new double[TransformSize];
    private readonly Channel[] _channels = new Channel[Openings];
    private readonly DetectedBurst[] _queue = new DetectedBurst[QueueCapacity];
    private int _queueHead;
    private int _queueCount;
    private readonly long[] _markIndex = new long[MarkCapacity];
    private readonly bool[] _markPaused = new bool[MarkCapacity];
    private long _marksWritten;
    private int _fill;
    private long _frameStart;
    private long _pushed;
    private bool _finished;

    /// <summary>A detector with the running threshold (<paramref name="fixedThreshold"/> null) or a fixed one for tests and tools.</summary>
    public StreamingBurstDetector(double? fixedThreshold = null)
    {
        _fixed = fixedThreshold;
        _floor = fixedThreshold ?? BurstFinder.DefaultLeast;
        for (int o = 0; o < Openings; o++)
        {
            _channels[o] = new Channel(this, (FormatOpening)o);
        }
    }

    /// <summary>Samples pushed so far; the clock of every index here.</summary>
    public long SamplesPushed => _pushed;

    /// <summary>Candidates dropped because the queue was full.</summary>
    public long Lost { get; private set; }

    /// <summary>Candidates waiting to be taken.</summary>
    public int Available => _queueCount;

    /// <summary>The threshold in force for an opening now (the fixed one, or the running one).</summary>
    public double ThresholdOf(FormatOpening opening) => _fixed ?? _channels[(int)opening].Estimator.Threshold;

    /// <summary>Whether an opening's running estimate has 5 s of values and so is in use.</summary>
    public bool EstimateWarm(FormatOpening opening) => _fixed is not null || _channels[(int)opening].Estimator.Warm;

    /// <summary>
    /// Stops the running estimate taking values from the windows starting at <paramref name="fromIndex"/> (default: the samples
    /// pushed so far, so every window not yet fully inside the past). The estimate keeps its last 30 s of unpaused values.
    /// Indexes must not go backwards between calls. No effect with a fixed threshold.
    /// </summary>
    public void PauseEstimate(long? fromIndex = null) => Mark(fromIndex ?? _pushed, true);

    /// <summary>Lets the estimate take values again from the windows starting at <paramref name="fromIndex"/> (default: now).</summary>
    public void ResumeEstimate(long? fromIndex = null) => Mark(fromIndex ?? _pushed, false);

    private void Mark(long index, bool paused)
    {
        if (_fixed is not null)
        {
            return;
        }

        long oldest = long.MaxValue;
        foreach (Channel c in _channels)
        {
            oldest = Math.Min(oldest, c.MarksRead);
        }

        if (_marksWritten - oldest >= MarkCapacity)
        {
            throw new InvalidOperationException("Too many pause changes waiting for the detector to catch up.");
        }

        if (_marksWritten > 0 && index < _markIndex[(_marksWritten - 1) % MarkCapacity])
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Pause indexes must not go backwards.");
        }

        _markIndex[_marksWritten % MarkCapacity] = index;
        _markPaused[_marksWritten % MarkCapacity] = paused;
        _marksWritten++;
    }

    /// <summary>Takes one candidate if there is one.</summary>
    public bool TryTake(out DetectedBurst burst)
    {
        if (_queueCount == 0)
        {
            burst = default;
            return false;
        }

        burst = _queue[_queueHead];
        _queueHead = (_queueHead + 1) % QueueCapacity;
        _queueCount--;
        return true;
    }

    /// <summary>Pushes received audio, any number of samples.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The stream has been finished.");
        }

        _pushed += samples.Length;
        while (samples.Length > 0)
        {
            int take = Math.Min(samples.Length, TransformSize - _fill);
            double[] frame = _frame;
            int at = _fill;
            for (int i = 0; i < take; i++)
            {
                frame[at + i] = samples[i];
            }

            _fill += take;
            samples = samples[take..];
            if (_fill == TransformSize)
            {
                ProcessFrame(final: false);
                Array.Copy(frame, Hop, frame, 0, TransformSize - Hop);
                _fill = TransformSize - Hop;
                _frameStart += Hop;
            }
        }
    }

    /// <summary>
    /// Ends the stream: correlates what is left, settles every pending peak as if no more audio would come, and queues what
    /// is confirmed. For tests and tools (a live station never ends); a peak at the very end is never confirmed, as in the offline form.
    /// </summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        ProcessFrame(final: true);
        _finished = true;
        foreach (Channel c in _channels)
        {
            c.Settle(long.MaxValue);
        }
    }

    private void ProcessFrame(bool final)
    {
        double[] frame = _frame;
        int valid = final ? _fill : TransformSize;
        long total = _frameStart + valid;
        bool any = false;
        foreach (Channel c in _channels)
        {
            c.LastStart = final ? total - c.Length : _frameStart + TransformSize - c.Length;
            any |= c.Next <= c.LastStart;
        }

        if (!any)
        {
            return;
        }

        if (final)
        {
            Array.Clear(frame, valid, TransformSize - valid);
        }

        double[] cum = _cumulative;
        double sum = 0;
        cum[0] = 0;
        for (int i = 0; i < TransformSize; i++)
        {
            double x = frame[i];
            sum += x * x;
            cum[i + 1] = sum;
        }

        double[] sre = _spectrumRe;
        double[] sim = _spectrumIm;
        Array.Copy(frame, sre, TransformSize);
        Array.Clear(sim);
        _fft.Forward(sre, sim);

        Channel a = _channels[0];
        Channel b = _channels[1];
        Channel c3 = _channels[2];
        double[] wre = _workRe;
        double[] wim = _workIm;
        if (a.Next <= a.LastStart || b.Next <= b.LastStart)
        {
            // Z = P_a + i P_b with P = C * conj(R): the inverse has corr_a in its real part and corr_b in its imaginary part.
            double[] ar = a.RefRe, ai = a.RefIm, br = b.RefRe, bi = b.RefIm;
            for (int k = 0; k < TransformSize; k++)
            {
                double cr = sre[k];
                double ci = sim[k];
                double par = (cr * ar[k]) + (ci * ai[k]);
                double pai = (ci * ar[k]) - (cr * ai[k]);
                double pbr = (cr * br[k]) + (ci * bi[k]);
                double pbi = (ci * br[k]) - (cr * bi[k]);
                wre[k] = par - pbi;
                wim[k] = pai + pbr;
            }

            _fft.InverseUnscaled(wre, wim);
            a.Consume(wre, cum, _frameStart);
            b.Consume(wim, cum, _frameStart);
        }

        if (c3.Next <= c3.LastStart)
        {
            double[] rr = c3.RefRe, ri = c3.RefIm;
            for (int k = 0; k < TransformSize; k++)
            {
                double cr = sre[k];
                double ci = sim[k];
                wre[k] = (cr * rr[k]) + (ci * ri[k]);
                wim[k] = (ci * rr[k]) - (cr * ri[k]);
            }

            _fft.InverseUnscaled(wre, wim);
            c3.Consume(wre, cum, _frameStart);
        }

        foreach (Channel c in _channels)
        {
            c.Settle(null);
        }
    }

    private void Enqueue(FormatOpening opening, long index, double peak)
    {
        if (_queueCount == QueueCapacity)
        {
            Lost++;
            return;
        }

        _queue[(_queueHead + _queueCount) % QueueCapacity] = new DetectedBurst(opening, index, peak);
        _queueCount++;
    }

    /// <summary>One opening's reference, scanner, pending peaks and estimate.</summary>
    private sealed class Channel
    {
        private readonly StreamingBurstDetector _owner;
        private readonly FormatOpening _opening;
        private readonly double _scale;

        // The scanner: a run of equal values, whether it rose into, and the values fed.
        private bool _hasRun;
        private double _runValue;
        private long _runStart;
        private bool _rising;
        private long _fed;

        private readonly long[] _index = new long[PendingCapacity];
        private readonly double[] _height = new double[PendingCapacity];
        private readonly byte[] _status = new byte[PendingCapacity];
        private int _head;
        private int _count;

        private bool _paused;

        public Channel(StreamingBurstDetector owner, FormatOpening opening)
        {
            _owner = owner;
            _opening = opening;
            double[] reference = BurstFinder.Reference(opening);
            Length = reference.Length;
            double energy = 0;
            foreach (double x in reference)
            {
                energy += x * x;
            }

            _scale = 1.0 / TransformSize / Math.Sqrt(energy);
            RefRe = new double[TransformSize];
            RefIm = new double[TransformSize];
            reference.CopyTo(RefRe, 0);
            owner._fft.Forward(RefRe, RefIm);
        }

        public int Length { get; }

        public double[] RefRe { get; }

        public double[] RefIm { get; }

        public RunningThreshold Estimator { get; } = new();

        public long MarksRead { get; private set; }

        /// <summary>The next window start to be correlated.</summary>
        public long Next { get; private set; }

        /// <summary>The last window start the current frame can give.</summary>
        public long LastStart { get; set; }

        private double Threshold => _owner._fixed ?? Estimator.Threshold;

        /// <summary>Feeds the correlation values of the window starts from Next to LastStart.</summary>
        public void Consume(double[] raw, double[] cumulative, long frameStart)
        {
            if (Next > LastStart)
            {
                return;
            }

            int length = Length;
            int m = (int)(Next - frameStart);
            int end = (int)(LastStart - frameStart);
            double scale = _scale;
            for (; m <= end; m++)
            {
                double energy = Math.Max(cumulative[m + length] - cumulative[m], 1e-12);
                double c = raw[m];

                // |c| * scale / sqrt(energy): the transform's 1/N and the reference's energy are in the scale.
                Feed(Math.Abs(c) * scale / Math.Sqrt(energy));
            }

            Next = frameStart + end + 1;
        }

        private void Feed(double value)
        {
            long t = _fed++;
            if ((t & (RunningThreshold.Every - 1)) == 0 && _owner._fixed is null)
            {
                while (MarksRead < _owner._marksWritten && _owner._markIndex[MarksRead % MarkCapacity] <= t)
                {
                    _paused = _owner._markPaused[MarksRead % MarkCapacity];
                    MarksRead++;
                }

                if (!_paused)
                {
                    Estimator.Add(value);
                }
            }

            if (!_hasRun)
            {
                _hasRun = true;
                _runValue = value;
                _runStart = t;
                _rising = false;
                return;
            }

            if (value == _runValue)
            {
                return;
            }

            if (_rising && value < _runValue && _runValue >= Threshold)
            {
                Add((_runStart + t - 1) / 2, _runValue);
            }

            _rising = value > _runValue;
            _runValue = value;
            _runStart = t;
        }

        private void Add(long index, double height)
        {
            if (_count == PendingCapacity)
            {
                if (_head > 0)
                {
                    int live = _count - _head;
                    Array.Copy(_index, _head, _index, 0, live);
                    Array.Copy(_height, _head, _height, 0, live);
                    Array.Copy(_status, _head, _status, 0, live);
                    _count = live;
                    _head = 0;
                }
                else
                {
                    // Pathological (a threshold so low that thousands of peaks wait): settle everything with what is known.
                    Settle(long.MaxValue);
                    _head = 0;
                    _count = 0;
                }
            }

            _index[_count] = index;
            _height[_count] = height;
            _status[_count] = Undecided;
            _count++;
        }

        /// <summary>
        /// Decides what can be decided, queues the confirmed in order, and forgets what can no longer matter. <paramref name="horizon"/>
        /// is the index below which every peak has been seen (null: worked out from the scanner).
        /// </summary>
        public void Settle(long? horizon)
        {
            long h = horizon ?? Horizon();
            bool changed;
            do
            {
                changed = false;
                for (int p = _head; p < _count; p++)
                {
                    if (_status[p] != Undecided)
                    {
                        continue;
                    }

                    long ip = _index[p];
                    double hp = _height[p];
                    bool covered = false;
                    bool waiting = false;
                    for (int q = p - 1; q >= _head && ip - _index[q] < Sym; q--)
                    {
                        Judge(q, ip, hp, ref covered, ref waiting);
                    }

                    for (int q = p + 1; q < _count && _index[q] - ip < Sym; q++)
                    {
                        Judge(q, ip, hp, ref covered, ref waiting);
                    }

                    if (covered)
                    {
                        _status[p] = Removed;
                        changed = true;
                    }
                    else if (!waiting && (h == long.MaxValue || h >= ip + Sym))
                    {
                        _status[p] = Kept;
                        changed = true;
                    }
                }
            }
            while (changed);

            for (int p = _head; p < _count; p++)
            {
                byte s = _status[p];
                if (s == Undecided)
                {
                    break;
                }

                if (s == Kept)
                {
                    _owner.Enqueue(_opening, _index[p], _height[p]);
                    _status[p] = Emitted;
                }
            }

            // Forget the oldest while it is decided and a symbol or more behind every peak yet to be seen.
            while (_head < _count && _status[_head] != Undecided && (h == long.MaxValue || _index[_head] + Sym <= h))
            {
                _head++;
            }

            if (_head == _count)
            {
                _head = 0;
                _count = 0;
            }
        }

        private void Judge(int q, long ip, double hp, ref bool covered, ref bool waiting)
        {
            double hq = _height[q];
            if (hq > hp || (hq == hp && _index[q] < ip))
            {
                byte s = _status[q];
                if (s == Kept || s == Emitted)
                {
                    covered = true;
                }
                else if (s == Undecided)
                {
                    waiting = true;
                }
            }
        }

        // Every peak with a middle below this has been seen. A run that rose into and could still reach the threshold may yet
        // be a peak whose middle is at or after its start; otherwise only runs yet to be fed can be.
        private long Horizon()
        {
            if (_hasRun && _rising && _runValue >= _owner._floor)
            {
                return _runStart;
            }

            return _fed;
        }
    }
}
