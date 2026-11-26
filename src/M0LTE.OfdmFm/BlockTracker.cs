using System.Numerics;

namespace M0LTE.OfdmFm;

/// <summary>
/// Follows the delay of a capture of a burst against the burst's samples, a few milliseconds at a time: the prototype's
/// <c>lab.track</c> as the station calls it (<c>at</c> 0, blocks of 1024 samples every 256, 200 to 8050 hertz, three
/// iterations), returning the centre of each block and its delay relative to the burst's mean delay.
/// </summary>
/// <remarks>
/// <para>The blocks are those whose reference energy is more than half the median block's (the lead-in's silence goes). Each
/// block of the reference and of the capture is windowed (Hann) and transformed; a static channel is fitted from the blocks
/// around the middle of the burst, each block's delay is found by a grid search of 750 delays four microseconds apart
/// (minus 1500 to plus 1496), and then three rounds of: the channel over all blocks, each block's residual phase fitted
/// against frequency through the origin, and each block's gain. The delays are returned less their mean.</para>
/// <para>One instance per receiver; not thread-safe. The buffers are for the longest burst; a call allocates nothing.</para>
/// </remarks>
internal sealed class BlockTracker
{
    /// <summary>Samples in a block.</summary>
    public const int Block = 1024;

    /// <summary>Samples between the starts of blocks.</summary>
    public const int Hop = 256;

    /// <summary>Delays tried by the grid search.</summary>
    public const int GridSteps = 750;

    private const double SampleRate = BurstRenderer.SampleRate;
    private const double LowHz = 200.0;
    private const double HighHz = 8050.0;
    private const int Iterations = 3;

    // Blocks whose grid searches share one pass over the steering table.
    private const int Tile = 32;

    private static readonly double[] Window = BuildWindow();
    private static readonly int FirstBin;
    private static readonly int Bins;
    private static readonly double[] Hz;

    // The steering table, grouped by as many grid delays as a vector holds: [((chunk * Bins) + bin) * Lanes + lane] is the steering of delay chunk * Lanes + lane
    // (zero beyond the grid), so that a vector of delays is searched at once and each delay's sum over the bins is still taken in bin order.
    private static readonly double[] SteerRe;
    private static readonly double[] SteerIm;
    private static readonly int Lanes = Vector<double>.Count;
    private static readonly int Chunks;
    private static readonly double[] Grid;

    // 2 pi times each bin's distance from the middle of the band, in radians per second: how fast a bin's phase moves against a delay, once the band's centre is taken out.
    private static readonly double[] SpreadOmega;

    private readonly int _maxBlocks;
    private readonly int[] _starts;
    private readonly double[] _energy;
    private readonly double[] _sorted;
    private readonly double[] _xRe;
    private readonly double[] _xIm;
    private readonly double[] _yRe;
    private readonly double[] _yIm;
    private readonly double[] _tau;
    private readonly double[] _gain;
    private readonly double[] _fftRe = new double[Block];
    private readonly double[] _fftIm = new double[Block];
    private readonly double[] _px;
    private readonly double[] _hRe;
    private readonly double[] _hIm;
    private readonly double[] _cRe;
    private readonly double[] _cIm;
    private readonly double[] _sumRe;
    private readonly double[] _sumIm;
    private readonly double[] _tileRe = new double[Tile * Bins];
    private readonly double[] _tileIm = new double[Tile * Bins];
    private readonly double[] _tileBest = new double[Tile];
    private readonly int[] _tileGrid = new int[Tile];
    private readonly double[] _tileMag = new double[Tile * GridSteps];
    private readonly bool[] _tileDone = new bool[Tile * Chunks];
    private readonly double[] _tileAbs = new double[Tile];
    private readonly double[] _tileSlope = new double[Tile];
    private readonly int[] _need = new int[Tile];
    private readonly double[] _rotRe;
    private readonly double[] _rotIm;

    static BlockTracker()
    {
        double spacing = 1.0 / (Block * (1.0 / SampleRate));
        int first = -1;
        int count = 0;
        for (int k = 0; k <= Block / 2; k++)
        {
            double f = k * spacing;
            if (f >= LowHz && f <= HighHz)
            {
                if (first < 0)
                {
                    first = k;
                }

                count++;
            }
        }

        FirstBin = first;
        Bins = count;
        Hz = new double[Bins];
        for (int b = 0; b < Bins; b++)
        {
            Hz[b] = (FirstBin + b) * spacing;
        }

        SpreadOmega = new double[Bins];
        double middle = (Hz[0] + Hz[Bins - 1]) / 2;
        for (int b = 0; b < Bins; b++)
        {
            SpreadOmega[b] = 2 * Math.PI * Math.Abs(Hz[b] - middle);
        }

        // numpy.arange(-1500e-6, 1500e-6, 4e-6): start plus i times the difference of the first two values.
        double start = -1500e-6;
        double delta = (start + 4e-6) - start;
        Grid = new double[GridSteps];
        Chunks = (GridSteps + Lanes - 1) / Lanes;
        SteerRe = new double[Chunks * Bins * Lanes];
        SteerIm = new double[Chunks * Bins * Lanes];
        for (int g = 0; g < GridSteps; g++)
        {
            Grid[g] = start + (g * delta);
            for (int b = 0; b < Bins; b++)
            {
                double angle = 2 * Math.PI * Hz[b] * Grid[g];
                int at = ((((g / Lanes) * Bins) + b) * Lanes) + (g % Lanes);
                SteerRe[at] = Math.Cos(angle);
                SteerIm[at] = Math.Sin(angle);
            }
        }
    }

    /// <summary>Makes a tracker with room for a burst of <paramref name="maxSamples"/> samples (lead-in included).</summary>
    public BlockTracker(int maxSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, Block);
        _maxBlocks = ((maxSamples - Block) / Hop) + 1;
        _starts = new int[_maxBlocks];
        _energy = new double[_maxBlocks];
        _sorted = new double[_maxBlocks];
        _xRe = new double[_maxBlocks * Bins];
        _xIm = new double[_maxBlocks * Bins];
        _yRe = new double[_maxBlocks * Bins];
        _yIm = new double[_maxBlocks * Bins];
        _tau = new double[_maxBlocks];
        _gain = new double[_maxBlocks];
        _rotRe = new double[_maxBlocks * Bins];
        _rotIm = new double[_maxBlocks * Bins];
        _px = new double[Bins];
        _hRe = new double[Bins];
        _hIm = new double[Bins];
        _cRe = new double[Bins];
        _cIm = new double[Bins];
        _sumRe = new double[Bins];
        _sumIm = new double[Bins];
    }

    /// <summary>
    /// When set (by the benchmark), <see cref="Track"/> adds the stopwatch ticks of each piece to this array: 0 reference energies and block selection, 1 windowed transforms,
    /// 2 power and bootstrap channel, 3 grid search, then for each iteration 4 the channel over all blocks, 5 the delay fit, 6 the gain, and 7 the rest.
    /// </summary>
    internal long[]? PhaseTicks { get; set; }

    private long _mark;

    /// <summary>When set, the grid search tries every delay of every block instead of skipping those that cannot win; the same delays come out either way (the tests compare them).</summary>
    internal bool ExhaustiveGridSearch { get; set; }

    /// <summary>Chunks of delays summed so far by the grid search, over all calls, and the number an exhaustive search would have summed.</summary>
    internal long ChunksSearched { get; private set; }

    /// <summary>See <see cref="ChunksSearched"/>.</summary>
    internal long ChunksPossible { get; private set; }

    private void Mark(int phase)
    {
        if (PhaseTicks is { } ticks)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            ticks[phase] += now - _mark;
            _mark = now;
        }
    }

    /// <summary>Most blocks a call can return.</summary>
    public int MaxBlocks => _maxBlocks;

    /// <summary>
    /// Measures <paramref name="capture"/> against <paramref name="reference"/>, both from the same sample (the reference's
    /// leading zeros are the lead-in). Writes each kept block's centre time (seconds from the reference's first non-zero sample)
    /// and delay (seconds, positive later) and returns how many. The capture must be at least as long as the reference.
    /// </summary>
    public int Track(ReadOnlySpan<float> capture, ReadOnlySpan<double> reference, Span<double> times, Span<double> delays)
    {
        int end = reference.Length;
        if (capture.Length < end)
        {
            throw new ArgumentException("The capture is shorter than the reference.", nameof(capture));
        }

        if (end >= Block && ((end - Block) / Hop) + 1 > _maxBlocks)
        {
            throw new ArgumentException("The reference is longer than the longest burst.", nameof(reference));
        }

        _mark = PhaseTicks is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        int lead = 0;
        while (lead < end && reference[lead] == 0)
        {
            lead++;
        }

        if (lead == end)
        {
            lead = 0;
        }

        int total = 0;
        for (int s = lead; s <= end - Block; s += Hop)
        {
            double e = 0;
            for (int j = 0; j < Block; j++)
            {
                double v = reference[s + j];
                e += v * v;
            }

            _starts[total] = s;
            _energy[total] = e;
            _sorted[total] = e;
            total++;
        }

        if (total == 0)
        {
            return 0;
        }

        _sorted.AsSpan(0, total).Sort();
        double median = (total & 1) == 1 ? _sorted[total / 2] : (_sorted[(total / 2) - 1] + _sorted[total / 2]) / 2;
        double floor = 0.5 * median;
        int m = 0;
        for (int i = 0; i < total; i++)
        {
            if (_energy[i] > floor)
            {
                _starts[m++] = _starts[i];
            }
        }

        if (m == 0)
        {
            return 0;
        }

        if (times.Length < m || delays.Length < m)
        {
            throw new ArgumentException("Need room for every block.", nameof(times));
        }

        Mark(0);
        for (int blk = 0; blk < m; blk++)
        {
            int s = _starts[blk];
            for (int j = 0; j < Block; j++)
            {
                _fftRe[j] = Window[j] * reference[s + j];
                _fftIm[j] = 0;
            }

            RealFft.Forward(_fftRe, _fftIm);
            Array.Copy(_fftRe, FirstBin, _xRe, blk * Bins, Bins);
            Array.Copy(_fftIm, FirstBin, _xIm, blk * Bins, Bins);
            for (int j = 0; j < Block; j++)
            {
                _fftRe[j] = Window[j] * capture[s + j];
                _fftIm[j] = 0;
            }

            RealFft.Forward(_fftRe, _fftIm);
            Array.Copy(_fftRe, FirstBin, _yRe, blk * Bins, Bins);
            Array.Copy(_fftIm, FirstBin, _yIm, blk * Bins, Bins);
        }

        Mark(1);
        Array.Clear(_px);
        for (int blk = 0; blk < m; blk++)
        {
            int o = blk * Bins;
            for (int b = 0; b < Bins; b++)
            {
                _px[b] += (_xRe[o + b] * _xRe[o + b]) + (_xIm[o + b] * _xIm[o + b]);
            }
        }

        _gain.AsSpan(0, m).Fill(1.0);

        // The bootstrap channel, from the blocks around the middle of the burst (its first symbols occupy only the bottom of the band).
        int mid = m / 2;
        int span = Math.Max(4, (int)(0.04 * SampleRate / Hop));
        int from = Math.Max(0, mid - (span / 2));
        int to = Math.Min(m, mid + (span / 2));
        Array.Clear(_sumRe);
        Array.Clear(_sumIm);
        Array.Clear(_cRe);
        for (int blk = from; blk < to; blk++)
        {
            int o = blk * Bins;
            for (int b = 0; b < Bins; b++)
            {
                // Y * conj(X)
                _sumRe[b] += (_yRe[o + b] * _xRe[o + b]) + (_yIm[o + b] * _xIm[o + b]);
                _sumIm[b] += (_yIm[o + b] * _xRe[o + b]) - (_yRe[o + b] * _xIm[o + b]);
                _cRe[b] += (_xRe[o + b] * _xRe[o + b]) + (_xIm[o + b] * _xIm[o + b]);
            }
        }

        for (int b = 0; b < Bins; b++)
        {
            double d = Math.Max(_cRe[b], 1e-30);
            _hRe[b] = _sumRe[b] / d;
            _hIm[b] = _sumIm[b] / d;
        }

        // Each block's delay by a grid search of the correlation of what is left after the channel with a delay's steering.
        Mark(2);
        GridSearch(m);
        Mark(3);

        // The rotation of each bin of each block by its delay: cosine and sine of 2 pi f tau for the current delays. The gain step's rotation by the new delay
        // is the next round's, with its sine's sign changed (cosine is even and sine odd, exactly), so each is worked out once.
        for (int blk = 0; blk < m; blk++)
        {
            int o = blk * Bins;
            double tau = _tau[blk];
            for (int b = 0; b < Bins; b++)
            {
                double angle = 2 * Math.PI * Hz[b] * tau;
                _rotRe[o + b] = Math.Cos(angle);
                _rotIm[o + b] = Math.Sin(angle);
            }
        }

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            // H over all blocks, with each block's delay taken out and its gain divided out.
            Array.Clear(_sumRe);
            Array.Clear(_sumIm);
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double gain = _gain[blk];
                for (int b = 0; b < Bins; b++)
                {
                    double rr = _rotRe[o + b];
                    double ri = _rotIm[o + b];
                    double pr = (_yRe[o + b] * rr) - (_yIm[o + b] * ri);
                    double pi = (_yRe[o + b] * ri) + (_yIm[o + b] * rr);
                    _sumRe[b] += ((pr * _xRe[o + b]) + (pi * _xIm[o + b])) / gain;
                    _sumIm[b] += ((pi * _xRe[o + b]) - (pr * _xIm[o + b])) / gain;
                }
            }

            for (int b = 0; b < Bins; b++)
            {
                double d = Math.Max(_px[b], 1e-30);
                _hRe[b] = _sumRe[b] / d;
                _hIm[b] = _sumIm[b] / d;
            }

            Mark(4);

            // Each block's residual phase fitted against frequency through the origin.
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double tau = _tau[blk];
                double num = 0;
                double den = 0;
                for (int b = 0; b < Bins; b++)
                {
                    double rr = _rotRe[o + b];
                    double ri = _rotIm[o + b];
                    double pr = (_yRe[o + b] * rr) - (_yIm[o + b] * ri);
                    double pi = (_yRe[o + b] * ri) + (_yIm[o + b] * rr);
                    double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                    double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                    double cr = (pr * hxRe) + (pi * hxIm);
                    double ci = (pi * hxRe) - (pr * hxIm);
                    double w = Math.Sqrt((cr * cr) + (ci * ci));
                    double ang = Math.Atan2(ci, cr);
                    num += w * ang * Hz[b];
                    den += w * Hz[b] * Hz[b];
                }

                _tau[blk] = tau - (num / den / (2 * Math.PI));
            }

            Mark(5);

            // Each block's gain against the channel and its new delay.
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double tau = _tau[blk];
                double cross = 0;
                double power = 0;
                for (int b = 0; b < Bins; b++)
                {
                    double angle = -2 * Math.PI * Hz[b] * tau;
                    double rr = Math.Cos(angle);
                    double ri = Math.Sin(angle);
                    _rotRe[o + b] = rr;
                    _rotIm[o + b] = -ri;
                    double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                    double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                    double refRe = (hxRe * rr) - (hxIm * ri);
                    double refIm = (hxRe * ri) + (hxIm * rr);
                    cross += (_yRe[o + b] * refRe) + (_yIm[o + b] * refIm);
                    power += (refRe * refRe) + (refIm * refIm);
                }

                _gain[blk] = cross / Math.Max(power, 1e-30);
            }

            Mark(6);
        }

        double sum = 0;
        for (int blk = 0; blk < m; blk++)
        {
            sum += _tau[blk];
        }

        double mean = sum / m;
        for (int blk = 0; blk < m; blk++)
        {
            times[blk] = (_starts[blk] + (Block / 2.0) - lead) / SampleRate;
            delays[blk] = _tau[blk] - mean;
        }

        Mark(7);
        return m;
    }

    // Each block's delay by a grid search. The correlation at each delay is summed over the bins in bin order, one delay to a lane, so every sum is
    // exactly the scalar loop's; blocks are taken a tile at a time so the steering table is read once for the tile, and four blocks at a time so
    // their independent sums overlap.
    //
    // Delays that cannot win are not summed. The sum at a delay is the correlation S(tau) of the block with that delay's steering, and S moves by at most
    // slope * |change in tau|, where slope is the sum over the bins of |c| times the bin's distance from the band's centre in radians per second (a phase
    // that turns by x changes the sum by at most |x| per unit of |c|, and the centre's own turn changes nothing in |S|). So once some delays are summed, any
    // other delay whose bound from its summed neighbours falls below the best power found is not the winner, and is skipped. The winner is the delay with the
    // greatest power, the earliest of equals, exactly as in an exhaustive search; the bound is padded by a part in 10^9 of the sum of |c|, far above the
    // rounding in the sums, and anything not a number is never skipped. The chunks go in levels: every top-stride chunk first, then every other chunk
    // between them that is still in the running, and so on down to every chunk.
    private void GridSearch(int m)
    {
        int lanes = Lanes;
        // The first level sums every chunk 32 delays apart: at wider gaps the bound rarely rules anything out.
        int top = ExhaustiveGridSearch ? 1 : Math.Max(1, 32 / lanes);
        for (int first = 0; first < m; first += Tile)
        {
            int count = Math.Min(Tile, m - first);
            for (int t = 0; t < count; t++)
            {
                int o = (first + t) * Bins;
                int q = t * Bins;
                double sum = 0;
                double slope = 0;
                for (int b = 0; b < Bins; b++)
                {
                    // C = Y * conj(H * X)
                    double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                    double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                    double cRe = (_yRe[o + b] * hxRe) + (_yIm[o + b] * hxIm);
                    double cIm = (_yIm[o + b] * hxRe) - (_yRe[o + b] * hxIm);
                    _tileRe[q + b] = cRe;
                    _tileIm[q + b] = cIm;
                    double mag = Math.Sqrt((cRe * cRe) + (cIm * cIm));
                    sum += mag;
                    slope += mag * SpreadOmega[b];
                }

                _tileAbs[t] = sum;
                _tileSlope[t] = slope;
                _tileBest[t] = -1;
                _tileGrid[t] = 0;
            }

            Array.Clear(_tileDone, 0, count * Chunks);
            ChunksPossible += (long)count * Chunks;
            for (int stride = top; stride >= 1; stride >>= 1)
            {
                bool prune = stride != top;
                int step = prune ? 2 * stride : stride;
                for (int chunk = prune ? stride : 0; chunk < Chunks; chunk += step)
                {
                    int need = 0;
                    for (int t = 0; t < count; t++)
                    {
                        if (!prune || InTheRunning(t, chunk))
                        {
                            _need[need++] = t;
                        }
                    }

                    ChunksSearched += need;
                    int table = chunk * Bins * lanes;
                    int k = 0;
                    for (; k + 4 <= need; k += 4)
                    {
                        Search4(chunk, table, _need[k], _need[k + 1], _need[k + 2], _need[k + 3]);
                    }

                    for (; k < need; k++)
                    {
                        Search1(chunk, table, _need[k]);
                    }
                }
            }

            for (int t = 0; t < count; t++)
            {
                _tau[first + t] = Grid[_tileGrid[t]];
            }
        }
    }

    // False only if no delay of the chunk can beat the best power found for the block, by the bound from the nearest summed chunks either side.
    private bool InTheRunning(int t, int chunk)
    {
        int lanes = Lanes;
        int done = t * Chunks;
        int mag = t * GridSteps;
        int left = chunk - 1;
        while (left >= 0 && !_tileDone[done + left])
        {
            left--;
        }

        int right = chunk + 1;
        while (right < Chunks && !_tileDone[done + right])
        {
            right++;
        }

        double slope = _tileSlope[t];
        double worst = 0;
        for (int l = 0; l < lanes; l++)
        {
            int g = (chunk * lanes) + l;
            if (g >= GridSteps)
            {
                break;
            }

            double bound = double.PositiveInfinity;
            if (left >= 0)
            {
                for (int k = 0; k < lanes; k++)
                {
                    int a = (left * lanes) + k;
                    double v = _tileMag[mag + a] + (slope * (Grid[g] - Grid[a]));
                    if (v < bound)
                    {
                        bound = v;
                    }
                }
            }

            if (right < Chunks)
            {
                for (int k = 0; k < lanes; k++)
                {
                    int a = (right * lanes) + k;
                    if (a >= GridSteps)
                    {
                        break;
                    }

                    double v = _tileMag[mag + a] + (slope * (Grid[a] - Grid[g]));
                    if (v < bound)
                    {
                        bound = v;
                    }
                }
            }

            if (!(bound <= worst))
            {
                worst = bound;
            }
        }

        return !((worst + (1e-9 * _tileAbs[t])) * (1 + 1e-9) < Math.Sqrt(_tileBest[t]));
    }

    private void Search4(int chunk, int table, int t0, int t1, int t2, int t3)
    {
        int lanes = Lanes;
        int q0 = t0 * Bins;
        int q1 = t1 * Bins;
        int q2 = t2 * Bins;
        int q3 = t3 * Bins;
        Vector<double> re0 = Vector<double>.Zero, im0 = Vector<double>.Zero;
        Vector<double> re1 = Vector<double>.Zero, im1 = Vector<double>.Zero;
        Vector<double> re2 = Vector<double>.Zero, im2 = Vector<double>.Zero;
        Vector<double> re3 = Vector<double>.Zero, im3 = Vector<double>.Zero;
        for (int b = 0; b < Bins; b++)
        {
            var sr = new Vector<double>(SteerRe, table + (b * lanes));
            var si = new Vector<double>(SteerIm, table + (b * lanes));
            var cr = new Vector<double>(_tileRe[q0 + b]);
            var ci = new Vector<double>(_tileIm[q0 + b]);
            re0 += (cr * sr) - (ci * si);
            im0 += (cr * si) + (ci * sr);
            cr = new Vector<double>(_tileRe[q1 + b]);
            ci = new Vector<double>(_tileIm[q1 + b]);
            re1 += (cr * sr) - (ci * si);
            im1 += (cr * si) + (ci * sr);
            cr = new Vector<double>(_tileRe[q2 + b]);
            ci = new Vector<double>(_tileIm[q2 + b]);
            re2 += (cr * sr) - (ci * si);
            im2 += (cr * si) + (ci * sr);
            cr = new Vector<double>(_tileRe[q3 + b]);
            ci = new Vector<double>(_tileIm[q3 + b]);
            re3 += (cr * sr) - (ci * si);
            im3 += (cr * si) + (ci * sr);
        }

        Pick(chunk, t0, (re0 * re0) + (im0 * im0));
        Pick(chunk, t1, (re1 * re1) + (im1 * im1));
        Pick(chunk, t2, (re2 * re2) + (im2 * im2));
        Pick(chunk, t3, (re3 * re3) + (im3 * im3));
    }

    private void Search1(int chunk, int table, int t)
    {
        int lanes = Lanes;
        int q = t * Bins;
        Vector<double> re = Vector<double>.Zero, im = Vector<double>.Zero;
        for (int b = 0; b < Bins; b++)
        {
            var sr = new Vector<double>(SteerRe, table + (b * lanes));
            var si = new Vector<double>(SteerIm, table + (b * lanes));
            var cr = new Vector<double>(_tileRe[q + b]);
            var ci = new Vector<double>(_tileIm[q + b]);
            re += (cr * sr) - (ci * si);
            im += (cr * si) + (ci * sr);
        }

        Pick(chunk, t, (re * re) + (im * im));
    }

    // Keeps the delay of greatest power, the earliest of equals (the chunks do not come in delay order), and records the chunk as summed.
    private void Pick(int chunk, int t, Vector<double> power)
    {
        _tileDone[(t * Chunks) + chunk] = true;
        for (int l = 0; l < Lanes; l++)
        {
            int g = (chunk * Lanes) + l;
            if (g >= GridSteps)
            {
                break;
            }

            double p = power[l];
            _tileMag[(t * GridSteps) + g] = Math.Sqrt(p);
            if (p > _tileBest[t] || (p == _tileBest[t] && g < _tileGrid[t]))
            {
                _tileBest[t] = p;
                _tileGrid[t] = g;
            }
        }
    }

    // numpy.hanning(1024).
    private static double[] BuildWindow()
    {
        var w = new double[Block];
        for (int i = 0; i < Block; i++)
        {
            double n = (1 - Block) + (2 * i);
            w[i] = 0.5 + (0.5 * Math.Cos(Math.PI * n / (Block - 1)));
        }

        return w;
    }
}
