namespace M0LTE.OfdmFm;

/// <summary>
/// The soft demapper: equalised carrier values and a noise variance for each carrier become one soft value per bit,
/// the inverse of <see cref="Constellation"/>. Pinned to the prototype (<c>modem.demap</c> with <c>odd="rect"</c>).
/// </summary>
/// <remarks>
/// <para>A soft value is the log of the likelihood of the bit being 0 over that of its being 1, so positive means 0, and
/// the values come out in the transmit bit order: carriers in order, the in-phase axis's bits first and then the
/// quadrature axis's, first bit of each axis the most significant of its Gray pattern. The two axes of a rectangular
/// constellation are independent, so each is demapped on its own by an exact log-sum-exp over its levels (2 to 32). One
/// bit a carrier has no quadrature axis: the imaginary part is ignored, and the noise on the in-phase axis is half the
/// carrier's variance as for every other size (the prototype's treatment of it).</para>
/// <para>The noise variance is the carrier's complex variance after equalising (twice that of one axis), in the
/// constellation's unit-power units. A carrier with a loading of 0 carries nothing and is skipped. Arithmetic is in
/// <see cref="double"/>, as the prototype's is; the <see cref="float"/> forms convert last. Nothing is allocated once the
/// tables are built, on first use; the methods are safe from any thread.</para>
/// </remarks>
public static class Demapper
{
    private const int MaxAxisBits = 5;

    // Bits a carrier is 1, 2, 4, 6, 8 or 10 (the sizes of Constellation); indexed by the size.
    private static readonly double[] Scale = BuildScales();

    // PAM levels by axis size 1 to 5 bits: Levels[m][pattern]; spacing 2, symmetric about zero, Gray-mapped.
    private static readonly double[][] Levels = BuildLevels();

    /// <summary>
    /// Demaps one symbol into <paramref name="llr"/> as doubles. Carrier c takes <c>loading[c]</c> bits (0, or a supported
    /// size) and the values of the carriers follow one another.
    /// </summary>
    /// <param name="re">Real parts of the equalised carriers.</param>
    /// <param name="im">Imaginary parts.</param>
    /// <param name="noiseVariance">The complex noise variance of each carrier.</param>
    /// <param name="loading">Bits on each carrier.</param>
    /// <param name="llr">Room for the sum of the loading.</param>
    /// <returns>The number of soft values written.</returns>
    public static int DemapSymbol(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> noiseVariance, ReadOnlySpan<byte> loading, Span<double> llr)
    {
        CheckSymbol(re, im, noiseVariance, loading);
        int k = 0;
        for (int c = 0; c < loading.Length; c++)
        {
            int b = loading[c];
            if (b == 0)
            {
                continue;
            }

            if (k + b > llr.Length)
            {
                throw new ArgumentException("llr is too short for the loading.", nameof(llr));
            }

            Carrier(re[c], im[c], noiseVariance[c], b, llr.Slice(k, b));
            k += b;
        }

        return k;
    }

    /// <summary>The same as <see cref="DemapSymbol(ReadOnlySpan{double}, ReadOnlySpan{double}, ReadOnlySpan{double}, ReadOnlySpan{byte}, Span{double})"/>, written as floats.</summary>
    public static int DemapSymbol(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> noiseVariance, ReadOnlySpan<byte> loading, Span<float> llr)
    {
        CheckSymbol(re, im, noiseVariance, loading);
        Span<double> one = stackalloc double[10];
        int k = 0;
        for (int c = 0; c < loading.Length; c++)
        {
            int b = loading[c];
            if (b == 0)
            {
                continue;
            }

            if (k + b > llr.Length)
            {
                throw new ArgumentException("llr is too short for the loading.", nameof(llr));
            }

            Carrier(re[c], im[c], noiseVariance[c], b, one[..b]);
            for (int i = 0; i < b; i++)
            {
                llr[k + i] = (float)one[i];
            }

            k += b;
        }

        return k;
    }

    /// <summary>
    /// Demaps a whole payload: <paramref name="symbols"/> symbols of <c>loading.Length</c> carriers each (symbol by symbol in
    /// <paramref name="re"/> and <paramref name="im"/>), all on the same loading and noise variances, to symbols times
    /// bits-a-symbol soft values, symbol after symbol.
    /// </summary>
    /// <returns>The number of soft values written.</returns>
    public static int DemapPayload(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> noiseVariance, ReadOnlySpan<byte> loading, int symbols, Span<float> llr)
    {
        int carriers = loading.Length;
        if (symbols < 0 || re.Length != symbols * carriers || im.Length != re.Length || noiseVariance.Length != carriers)
        {
            throw new ArgumentException("re and im must hold symbols times carriers values, and noiseVariance one per carrier.");
        }

        int written = 0;
        for (int s = 0; s < symbols; s++)
        {
            int at = s * carriers;
            written += DemapSymbol(re.Slice(at, carriers), im.Slice(at, carriers), noiseVariance, loading, llr[written..]);
        }

        return written;
    }

    /// <summary>
    /// As <see cref="DemapPayload"/>, but only the carriers flagged in <paramref name="only"/> (one flag for each carrier, the same in every symbol) are
    /// demapped: the others' soft values are left as they are in <paramref name="llr"/>, which must already be laid out for the whole payload.
    /// </summary>
    internal static void DemapCarriers(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> noiseVariance, ReadOnlySpan<byte> loading, int symbols, ReadOnlySpan<bool> only, Span<float> llr)
    {
        int carriers = loading.Length;
        if (symbols < 0 || re.Length != symbols * carriers || im.Length != re.Length || noiseVariance.Length != carriers || only.Length != carriers)
        {
            throw new ArgumentException("re and im must hold symbols times carriers values, and noiseVariance and only one per carrier.");
        }

        Span<double> one = stackalloc double[10];
        int k = 0;
        for (int s = 0; s < symbols; s++)
        {
            int at = s * carriers;
            for (int c = 0; c < carriers; c++)
            {
                int b = loading[c];
                if (b == 0)
                {
                    continue;
                }

                if (only[c])
                {
                    Carrier(re[at + c], im[at + c], noiseVariance[c], b, one[..b]);
                    for (int i = 0; i < b; i++)
                    {
                        llr[k + i] = (float)one[i];
                    }
                }

                k += b;
            }
        }
    }

    /// <summary>The bits a symbol on this loading carries.</summary>
    public static int BitsPerSymbol(ReadOnlySpan<byte> loading)
    {
        int sum = 0;
        for (int c = 0; c < loading.Length; c++)
        {
            sum += loading[c];
        }

        return sum;
    }

    private static void CheckSymbol(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> noiseVariance, ReadOnlySpan<byte> loading)
    {
        if (re.Length != loading.Length || im.Length != loading.Length || noiseVariance.Length != loading.Length)
        {
            throw new ArgumentException("re, im, noiseVariance and loading must hold one value per carrier.");
        }

        for (int c = 0; c < loading.Length; c++)
        {
            int b = loading[c];
            if (b != 0 && !Constellation.IsSupported(b))
            {
                throw new ArgumentException("Loadings are 0, 1, 2, 4, 6, 8 or 10 bits.", nameof(loading));
            }
        }
    }

    // One carrier of b bits (a supported size): in-phase bits first, then quadrature bits.
    private static void Carrier(double re, double im, double variance, int b, Span<double> llr)
    {
        int mi = (b + 1) / 2;
        int mq = b / 2;
        double scale = Scale[b];
        double axisVariance = variance / 2 / (scale * scale);
        Axis(re / scale, mi, axisVariance, llr[..mi]);
        if (mq != 0)
        {
            Axis(im / scale, mq, axisVariance, llr.Slice(mi, mq));
        }
    }

    // The exact soft values of the m bits of one PAM axis: for each bit, log-sum-exp over the levels where it is 0
    // minus that over the levels where it is 1.
    private static void Axis(double y, int m, double variance, Span<double> llr)
    {
        double[] levels = Levels[m];
        int n = levels.Length;
        int half = n >> 1;
        Span<double> d = stackalloc double[1 << MaxAxisBits];
        double twoVariance = 2 * variance;
        for (int p = 0; p < n; p++)
        {
            double t = y - levels[p];
            d[p] = -(t * t) / twoVariance;
        }

        for (int k = 0; k < m; k++)
        {
            // The levels where the bit is 0 and where it is 1, each in level-pattern order: pattern q of the other bits with the bit put in at this position.
            int shift = m - 1 - k;
            int bit = 1 << shift;
            int low = bit - 1;
            double max0 = double.NegativeInfinity;
            double max1 = double.NegativeInfinity;
            for (int q = 0; q < half; q++)
            {
                int p0 = ((q & ~low) << 1) | (q & low);
                max0 = Math.Max(max0, d[p0]);
                max1 = Math.Max(max1, d[p0 | bit]);
            }

            // Terms are summed in pattern order as ever. A term whose exponent is below -746 is exactly 0, and one below -37 (under 8.5e-17)
            // added to a sum of at least 1 is rounded away, and from the pattern that holds the maximum on (the term of exactly 1) the sum is at
            // least 1: those are skipped, everything else is added in the same order, so the sums are the same bit for bit. (NaN fails both tests.)
            double sum0 = 0;
            double sum1 = 0;
            bool seen0 = false;
            bool seen1 = false;
            for (int q = 0; q < half; q++)
            {
                int p0 = ((q & ~low) << 1) | (q & low);
                double d0 = d[p0];
                double d1 = d[p0 | bit];
                double e0 = d0 - max0;
                double e1 = d1 - max1;
                seen0 |= d0 == max0;
                seen1 |= d1 == max1;
                if (!(e0 < -746.0) && !(seen0 && e0 < -37.0))
                {
                    sum0 += Math.Exp(e0);
                }

                if (!(e1 < -746.0) && !(seen1 && e1 < -37.0))
                {
                    sum1 += Math.Exp(e1);
                }
            }

            // The log of exactly 1 is exactly 0.
            llr[k] = (max0 + (sum0 == 1.0 ? 0.0 : Math.Log(sum0))) - (max1 + (sum1 == 1.0 ? 0.0 : Math.Log(sum1)));
        }
    }

    private static double[] BuildScales()
    {
        var scale = new double[11];
        foreach (int b in new[] { 1, 2, 4, 6, 8, 10 })
        {
            scale[b] = Constellation.For(b).HalfDistance;
        }

        return scale;
    }

    private static double[][] BuildLevels()
    {
        var all = new double[MaxAxisBits + 1][];
        for (int m = 1; m <= MaxAxisBits; m++)
        {
            var lv = new double[1 << m];
            for (int g = 0; g < lv.Length; g++)
            {
                int v = g;
                for (int s = g >> 1; s != 0; s >>= 1)
                {
                    v ^= s;
                }

                lv[g] = (2.0 * v) - ((1 << m) - 1);
            }

            all[m] = lv;
        }

        return all;
    }
}
