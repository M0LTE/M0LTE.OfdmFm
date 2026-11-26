using System.Collections.Concurrent;
using System.Numerics;

namespace M0LTE.OfdmFm;

/// <summary>
/// The parity-check structure of the link's LDPC family for one lifting size and number of kept check rows,
/// with the encoder plan for it (the peeled core solution, or a dense GF(2) inverse). It depends on nothing
/// else, so block plans share it; the set is small and finite (22 lifting sizes, at most 98 row counts) and
/// is cached for good.
/// </summary>
internal sealed class LinkLdpcStructure
{
    private const int KindSum = 0;
    private const int KindRow = 1;
    private const int KindDense = 2;

    private static readonly ConcurrentDictionary<(int Z, int Rows, bool Structured), Lazy<LinkLdpcStructure>> Cache = new();

    private readonly int _kb;
    private readonly Func<int, int, int> _shift;
    private readonly int[] _rowStart;
    private readonly int[] _edgeColumn;
    private readonly int[] _edgeShift;
    private readonly int[] _edgeBase;
    private readonly Step[] _steps;

    private LinkLdpcStructure(int z, int rows, bool structured, int kb, Func<int, int, int> shift)
    {
        _shift = shift;
        LiftingSize = z;
        Rows = rows;
        _kb = kb;
        Columns = _kb + rows;
        CodewordLength = Columns * z;
        var start = new List<int> { 0 };
        var cols = new List<int>();
        var shifts = new List<int>();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                int s = _shift(r, c);
                if (s >= 0)
                {
                    cols.Add(c);
                    shifts.Add(s);
                }
            }

            start.Add(cols.Count);
        }

        _rowStart = [.. start];
        _edgeColumn = [.. cols];
        _edgeShift = [.. shifts];
        _edgeBase = new int[_edgeColumn.Length];
        for (int e = 0; e < _edgeBase.Length; e++)
        {
            _edgeBase[e] = _edgeColumn[e] * z;
        }

        _steps = MakeSteps(structured);
    }

    /// <summary>Most iterations the decoder runs.</summary>
    public const int MaxIterations = 30;

    /// <summary>The decoder's normalisation, as a 32-bit float (the prototype's alpha).</summary>
    public const float Alpha = 0.8f;

    /// <summary>The value a known-zero (shortened or filler) bit is given before decoding.</summary>
    public const float KnownZero = 1.0e4f;

    public int LiftingSize { get; }

    public int Rows { get; }

    public int Columns { get; }

    public int CodewordLength { get; }

    public int CoreSize { get; private set; }

    public string EncoderKind => CoreSize == 0 ? "none" : _steps[0].Kind == KindSum ? "sum" : "dense";

    /// <summary>Approximate bytes this structure holds.</summary>
    public long Bytes
    {
        get
        {
            long total = 4L * (_rowStart.Length + _edgeColumn.Length + _edgeShift.Length) + 40L * _steps.Length;
            foreach (Step step in _steps)
            {
                total += 8L * (step.Inverse?.Length ?? 0);
            }

            return total;
        }
    }

    public static LinkLdpcStructure Get(int z, int rows, bool structured = true) =>
        Cache.GetOrAdd((z, rows, structured), key => new Lazy<LinkLdpcStructure>(() => new LinkLdpcStructure(key.Z, key.Rows, key.Structured, LinkLdpcTables.Kb, (r, c) => LinkLdpcTables.Shift(key.Z, r, c)))).Value;

    /// <summary>A structure for another quasi-cyclic code (the header's): <paramref name="shift"/> gives the shift of (row, column), or -1 for none. Not cached.</summary>
    internal static LinkLdpcStructure Create(int z, int rows, int kb, Func<int, int, int> shift, bool structured = true) =>
        new(z, rows, structured, kb, shift);

    /// <summary>
    /// Given <paramref name="x"/> holding the information bits (and zeros elsewhere), fills in the parity.
    /// <paramref name="lam"/> is <c>Rows * LiftingSize</c> bytes of work space.
    /// </summary>
    public void Complete(Span<byte> x, Span<byte> lam)
    {
        int z = LiftingSize;
        lam.Clear();
        // lam[r] = contribution of the information bits to check row r.
        for (int r = 0; r < Rows; r++)
        {
            Span<byte> row = lam.Slice(r * z, z);
            for (int e = _rowStart[r]; e < _rowStart[r + 1]; e++)
            {
                int c = _edgeColumn[e];
                if (c < _kb)
                {
                    XorShifted(row, x.Slice(c * z, z), _edgeShift[e]);
                }
            }
        }

        Span<byte> v = stackalloc byte[z];
        foreach (Step step in _steps)
        {
            switch (step.Kind)
            {
                case KindSum:
                    {
                        v.Clear();
                        for (int r = 0; r < step.Rows; r++)
                        {
                            XorShifted(v, lam.Slice(r * z, z), 0);
                        }

                        StoreShifted(x.Slice(step.Column * z, z), v, step.Shift);
                        break;
                    }

                case KindRow:
                    {
                        lam.Slice(step.Row * z, z).CopyTo(v);
                        for (int e = _rowStart[step.Row]; e < _rowStart[step.Row + 1]; e++)
                        {
                            int c = _edgeColumn[e];
                            if (c >= _kb && c != step.Column)
                            {
                                XorShifted(v, x.Slice(c * z, z), _edgeShift[e]);
                            }
                        }

                        StoreShifted(x.Slice(step.Column * z, z), v, step.Shift);
                        break;
                    }

                default:
                    SolveDense(step, lam, x);
                    break;
            }
        }
    }

    /// <summary>True when every check row of the kept code is satisfied by the full codeword (<see cref="CodewordLength"/> bits).</summary>
    public bool SyndromeIsZero(ReadOnlySpan<byte> codeword)
    {
        if (codeword.Length != CodewordLength)
        {
            throw new ArgumentException($"expected {CodewordLength} bits", nameof(codeword));
        }

        int z = LiftingSize;
        for (int r = 0; r < Rows; r++)
        {
            for (int i = 0; i < z; i++)
            {
                int sum = 0;
                for (int e = _rowStart[r]; e < _rowStart[r + 1]; e++)
                {
                    int j = i + _edgeShift[e];
                    if (j >= z)
                    {
                        j -= z;
                    }

                    sum ^= codeword[_edgeColumn[e] * z + j] & 1;
                }

                if (sum != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Floats of work space <see cref="Decode"/> needs: the codeword values, then one message per edge and lifted row.</summary>
    public int DecodeScratchLength => CodewordLength + _edgeColumn.Length * LiftingSize;

    /// <summary>
    /// Layered normalised min-sum, as the prototype's <c>QCLDPC.decode</c> in 32-bit floats. On entry
    /// <c>work[..CodewordLength]</c> holds the value of every codeword bit (positive means 0); on exit it holds the
    /// final values, whose signs are the hard decisions. <paramref name="work"/> must hold
    /// <see cref="DecodeScratchLength"/> floats. Returns whether every check was satisfied, and the iterations run:
    /// 0 when the input already satisfied them, <see cref="MaxIterations"/> when it never did.
    /// </summary>
    public bool Decode(Span<float> work, out int iterations)
    {
        int z = LiftingSize;
        int n = CodewordLength;
        Span<float> l = work[..n];
        Span<float> r = work.Slice(n, _edgeColumn.Length * z);
        r.Clear();
        iterations = 0;
        Span<float> min1 = z <= 1024 ? stackalloc float[z] : new float[z];
        Span<float> min2 = z <= 1024 ? stackalloc float[z] : new float[z];
        Span<int> negative = z <= 1024 ? stackalloc int[z] : new int[z];
        if (HardSyndromeIsZero(l, negative))
        {
            return true;
        }

        for (int it = 1; it <= MaxIterations; it++)
        {
            for (int row = 0; row < Rows; row++)
            {
                UpdateRow(l, r, row, min1, min2, negative);
            }

            iterations = it;
            if (HardSyndromeIsZero(l, negative))
            {
                return true;
            }
        }

        return false;
    }

    // One layer of the layered min-sum: every lifted row of one base row at once. The lifted rows are independent (a base row has one entry in each of its columns, and
    // a shift is a permutation of a column's Z positions), so they are taken a vector of them at a time; each is worked out with the scalar loop's operations.
    // An edge's Z lifted rows read a column from its shift on and then from its start: two runs, contiguous in both the values and the messages.
    private void UpdateRow(Span<float> l, Span<float> r, int row, Span<float> min1, Span<float> min2, Span<int> negative)
    {
        int z = LiftingSize;
        int e0 = _rowStart[row];
        int e1 = _rowStart[row + 1];
        int degree = e1 - e0;
        if (degree == 0)
        {
            return;
        }

        min1.Fill(float.PositiveInfinity);
        min2.Fill(float.PositiveInfinity);
        negative.Clear();
        for (int e = e0; e < e1; e++)
        {
            int shift = _edgeShift[e];
            int split = z - shift;
            int b = _edgeBase[e];
            Gather(l.Slice(b + shift, split), r.Slice(e * z, split), min1[..split], min2[..split], negative[..split]);
            Gather(l.Slice(b, shift), r.Slice((e * z) + split, shift), min1[split..], min2[split..], negative[split..]);
        }

        if (degree < 2)
        {
            // the prototype gives a row with one entry a message of zero
            min1.Fill(0f);
            min2.Fill(0f);
        }

        for (int e = e0; e < e1; e++)
        {
            int shift = _edgeShift[e];
            int split = z - shift;
            int b = _edgeBase[e];
            Scatter(l.Slice(b + shift, split), r.Slice(e * z, split), min1[..split], min2[..split], negative[..split]);
            Scatter(l.Slice(b, shift), r.Slice((e * z) + split, shift), min1[split..], min2[split..], negative[split..]);
        }
    }

    // The two smallest magnitudes and the parity of the signs of l - r over the edges seen so far, for each lifted row of a run.
    private static void Gather(ReadOnlySpan<float> l, ReadOnlySpan<float> r, Span<float> min1, Span<float> min2, Span<int> negative)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + Vector<float>.Count <= l.Length; i += Vector<float>.Count)
            {
                Vector<float> q = new Vector<float>(l[i..]) - new Vector<float>(r[i..]);
                Vector<float> mag = Vector.Abs(q);
                var m1 = new Vector<float>(min1[i..]);
                var m2 = new Vector<float>(min2[i..]);
                Vector<int> lessThanFirst = Vector.LessThan(mag, m1);
                Vector.ConditionalSelect(lessThanFirst, m1, Vector.ConditionalSelect(Vector.LessThan(mag, m2), mag, m2)).CopyTo(min2[i..]);
                Vector.ConditionalSelect(lessThanFirst, mag, m1).CopyTo(min1[i..]);
                (new Vector<int>(negative[i..]) ^ Vector.LessThan(q, Vector<float>.Zero)).CopyTo(negative[i..]);
            }
        }

        for (; i < l.Length; i++)
        {
            float q = l[i] - r[i];
            if (q < 0)
            {
                negative[i] ^= -1;
            }

            float mag = MathF.Abs(q);
            if (mag < min1[i])
            {
                min2[i] = min1[i];
                min1[i] = mag;
            }
            else if (mag < min2[i])
            {
                min2[i] = mag;
            }
        }
    }

    // Each lifted row of a run takes the other edges' smaller magnitude (or the second smallest where this edge holds the smallest), the sign the other edges' signs make,
    // and the normalisation; the message and the new value are stored.
    private static void Scatter(Span<float> l, Span<float> r, ReadOnlySpan<float> min1, ReadOnlySpan<float> min2, ReadOnlySpan<int> negative)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var sign = new Vector<float>(-0.0f);
            var alpha = new Vector<float>(Alpha);
            for (; i + Vector<float>.Count <= l.Length; i += Vector<float>.Count)
            {
                Vector<float> q = new Vector<float>(l[i..]) - new Vector<float>(r[i..]);
                Vector<float> m = Vector.ConditionalSelect(Vector.Equals(Vector.Abs(q), new Vector<float>(min1[i..])), new Vector<float>(min2[i..]), new Vector<float>(min1[i..]));
                Vector<int> flip = Vector.LessThan(q, Vector<float>.Zero) ^ new Vector<int>(negative[i..]);
                Vector<float> message = alpha * Vector.ConditionalSelect(flip, Vector.Xor(m, sign), m);
                message.CopyTo(r[i..]);
                (q + message).CopyTo(l[i..]);
            }
        }

        for (; i < l.Length; i++)
        {
            float q = l[i] - r[i];
            float m = MathF.Abs(q) == min1[i] ? min2[i] : min1[i];
            bool flip = (q < 0) ^ (negative[i] != 0);
            float message = Alpha * (flip ? -m : m);
            r[i] = message;
            l[i] = q + message;
        }
    }

    // The syndrome of the hard decisions (a value below zero is a 1): each lifted row's parity over the edges of its base row, a vector of them at a time.
    private bool HardSyndromeIsZero(ReadOnlySpan<float> l, Span<int> parity)
    {
        int z = LiftingSize;
        for (int row = 0; row < Rows; row++)
        {
            int e0 = _rowStart[row];
            int e1 = _rowStart[row + 1];
            parity.Clear();
            for (int e = e0; e < e1; e++)
            {
                int shift = _edgeShift[e];
                int split = z - shift;
                int b = _edgeBase[e];
                Signs(l.Slice(b + shift, split), parity[..split]);
                Signs(l.Slice(b, shift), parity[split..]);
            }

            for (int i = 0; i < z; i++)
            {
                if (parity[i] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Signs(ReadOnlySpan<float> l, Span<int> parity)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + Vector<float>.Count <= l.Length; i += Vector<float>.Count)
            {
                (new Vector<int>(parity[i..]) ^ Vector.LessThan(new Vector<float>(l[i..]), Vector<float>.Zero)).CopyTo(parity[i..]);
            }
        }

        for (; i < l.Length; i++)
        {
            if (l[i] < 0)
            {
                parity[i] ^= -1;
            }
        }
    }

    // dst[i] ^= src[(i + s) % Z]
    private static void XorShifted(Span<byte> dst, ReadOnlySpan<byte> src, int s)
    {
        int z = dst.Length;
        int head = z - s;
        for (int i = 0; i < head; i++)
        {
            dst[i] ^= src[i + s];
        }

        for (int i = head; i < z; i++)
        {
            dst[i] ^= src[i - head];
        }
    }

    // dst[(i + s) % Z] = v[i]
    private static void StoreShifted(Span<byte> dst, ReadOnlySpan<byte> v, int s)
    {
        int z = v.Length;
        int head = z - s;
        for (int i = 0; i < head; i++)
        {
            dst[i + s] = v[i];
        }

        for (int i = head; i < z; i++)
        {
            dst[i - head] = v[i];
        }
    }

    private void SolveDense(Step step, ReadOnlySpan<byte> lam, Span<byte> x)
    {
        int z = LiftingSize;
        int n = step.Rows * z;
        int words = (n + 63) / 64;
        Span<ulong> packed = words <= 256 ? stackalloc ulong[words] : new ulong[words];
        packed.Clear();
        for (int i = 0; i < n; i++)
        {
            if (lam[i] != 0)
            {
                packed[i >> 6] |= 1UL << (i & 63);
            }
        }

        ulong[] inv = step.Inverse!;
        int firstColumn = _kb;
        for (int a = 0; a < n; a++)
        {
            ulong acc = 0;
            int off = a * words;
            for (int w = 0; w < words; w++)
            {
                acc ^= inv[off + w] & packed[w];
            }

            x[firstColumn * z + a] = (byte)(BitOperations.PopCount(acc) & 1);
        }
    }

    // The prototype's QCLDPC._make_encoder_plan.
    private Step[] MakeSteps(bool structured)
    {
        int kb = _kb;
        int mb = Rows;
        int z = LiftingSize;

        // Entry lookup (row, column) -> shift or -1, of the kept code.
        int Base(int r, int c) => _shift(r, c);

        int g = mb;
        for (int cand = 0; cand <= mb; cand++)
        {
            bool ok = true;
            for (int i = cand; i < mb; i++)
            {
                bool bad = Base(i, kb + i) < 0;
                for (int c = kb + i + 1; !bad && c < Columns; c++)
                {
                    bad = Base(i, c) >= 0;
                }

                if (bad)
                {
                    ok = false;
                    break;
                }
            }

            if (ok && cand > 0)
            {
                for (int r = 0; ok && r < cand; r++)
                {
                    for (int c = kb + cand; c < Columns; c++)
                    {
                        if (Base(r, c) >= 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
            }

            if (ok)
            {
                g = cand;
                break;
            }
        }

        var steps = new List<Step>();
        Step[]? core = structured && g > 0 ? PeelCore(g) : null;
        if (g > 0)
        {
            if (core is null)
            {
                core = [new Step(KindDense, g, 0, kb, 0, DenseCoreInverse(g))];
            }

            steps.AddRange(core);
        }

        for (int i = g; i < mb; i++)
        {
            steps.Add(new Step(KindRow, 0, i, kb + i, Base(i, kb + i), null));
        }

        CoreSize = g;
        return [.. steps];
    }

    // The prototype's QCLDPC._peel_core, for core rows 0..g-1 and columns kb..kb+g-1; null when it does not apply.
    private Step[]? PeelCore(int g)
    {
        int kb = _kb;
        int z = LiftingSize;
        int Base(int r, int c) => _shift(r, c);

        // Sum over the core rows of each core column, as a GF(2) polynomial in the cyclic shift.
        int nonzeroColumns = 0;
        int c0 = -1;
        int c0Shift = -1;
        for (int j = 0; j < g; j++)
        {
            var parity = new Dictionary<int, int>();
            for (int r = 0; r < g; r++)
            {
                int s = Base(r, kb + j);
                if (s >= 0)
                {
                    parity[s] = parity.GetValueOrDefault(s) ^ 1;
                }
            }

            var odd = parity.Where(p => p.Value != 0).Select(p => p.Key).ToList();
            if (odd.Count > 0)
            {
                nonzeroColumns++;
                c0 = kb + j;
                c0Shift = odd[0];
                if (odd.Count != 1)
                {
                    return null;
                }
            }
        }

        if (nonzeroColumns != 1)
        {
            return null;
        }

        var steps = new List<Step> { new(KindSum, g, 0, c0, c0Shift, null) };
        var known = new HashSet<int> { c0 };
        var used = new HashSet<int>();
        while (known.Count < g)
        {
            bool progress = false;
            for (int r = 0; r < g; r++)
            {
                if (used.Contains(r))
                {
                    continue;
                }

                int unknownColumn = -1;
                int unknownCount = 0;
                for (int j = 0; j < g; j++)
                {
                    int c = kb + j;
                    if (Base(r, c) >= 0 && !known.Contains(c))
                    {
                        unknownCount++;
                        unknownColumn = c;
                    }
                }

                if (unknownCount == 1)
                {
                    steps.Add(new Step(KindRow, 0, r, unknownColumn, Base(r, unknownColumn), null));
                    known.Add(unknownColumn);
                    used.Add(r);
                    progress = true;
                }
            }

            if (!progress)
            {
                return null;
            }
        }

        return [.. steps];
    }

    // The prototype's QCLDPC._dense_core_inverse and gf2_inverse: A is (g*Z) square, A[a*Z+i, b*Z+(i+s)%Z] = 1.
    // Returned as packed rows (ulong words, bit j of a row is column j).
    private ulong[] DenseCoreInverse(int g)
    {
        int z = LiftingSize;
        int kb = _kb;
        int n = g * z;
        int words = (n + 63) / 64;
        int width = 2 * words;
        var m = new ulong[n * width];
        for (int a = 0; a < g; a++)
        {
            for (int b = 0; b < g; b++)
            {
                int s = _shift(a, kb + b);
                if (s < 0)
                {
                    continue;
                }

                for (int i = 0; i < z; i++)
                {
                    int row = a * z + i;
                    int col = b * z + (i + s) % z;
                    m[row * width + (col >> 6)] |= 1UL << (col & 63);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            m[i * width + words + (i >> 6)] |= 1UL << (i & 63);
        }

        for (int col = 0; col < n; col++)
        {
            int piv = -1;
            for (int r = col; r < n; r++)
            {
                if ((m[r * width + (col >> 6)] >> (col & 63) & 1) != 0)
                {
                    piv = r;
                    break;
                }
            }

            if (piv < 0)
            {
                throw new InvalidOperationException("the core matrix is singular over GF(2)");
            }

            if (piv != col)
            {
                for (int w = 0; w < width; w++)
                {
                    (m[col * width + w], m[piv * width + w]) = (m[piv * width + w], m[col * width + w]);
                }
            }

            for (int r = 0; r < n; r++)
            {
                if (r != col && (m[r * width + (col >> 6)] >> (col & 63) & 1) != 0)
                {
                    for (int w = 0; w < width; w++)
                    {
                        m[r * width + w] ^= m[col * width + w];
                    }
                }
            }
        }

        var inverse = new ulong[n * words];
        for (int r = 0; r < n; r++)
        {
            Array.Copy(m, r * width + words, inverse, r * words, words);
        }

        return inverse;
    }

    private sealed record Step(int Kind, int Rows, int Row, int Column, int Shift, ulong[]? Inverse);
}
