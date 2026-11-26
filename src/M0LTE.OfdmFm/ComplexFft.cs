namespace M0LTE.OfdmFm;

/// <summary>
/// A fixed-size radix-2 complex transform with its tables built once, for the streaming detector, which runs one size for ever.
/// Twiddles come straight from <see cref="Math.Cos(double)"/> and <see cref="Math.Sin(double)"/>, not from a recurrence, so the
/// rounding does not grow with the size. Allocates only in the constructor.
/// </summary>
internal sealed class ComplexFft
{
    private readonly int _n;
    private readonly int[] _reverse;
    private readonly double[] _wRe;
    private readonly double[] _wIm;

    public ComplexFft(int size)
    {
        if (size < 4 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "a power of two of at least 4");
        }

        _n = size;
        _reverse = new int[size];
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        _wRe = new double[size / 2];
        _wIm = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double a = 2 * Math.PI * k / size;
            _wRe[k] = Math.Cos(a);
            _wIm[k] = -Math.Sin(a);
        }
    }

    public int Size => _n;

    /// <summary>In-place forward transform (no scaling).</summary>
    public void Forward(double[] re, double[] im)
    {
        int n = _n;
        int[] rev = _reverse;
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int i = 0; i < n; i += 2)
        {
            double tr = re[i + 1];
            double ti = im[i + 1];
            re[i + 1] = re[i] - tr;
            im[i + 1] = im[i] - ti;
            re[i] += tr;
            im[i] += ti;
        }

        double[] wRe = _wRe;
        double[] wIm = _wIm;
        for (int len = 4; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;
            for (int i = 0; i < n; i += len)
            {
                int t = 0;
                for (int a = i; a < i + half; a++, t += step)
                {
                    int b = a + half;
                    double wr = wRe[t];
                    double wi = wIm[t];
                    double vr = (re[b] * wr) - (im[b] * wi);
                    double vi = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - vr;
                    im[b] = im[a] - vi;
                    re[a] += vr;
                    im[a] += vi;
                }
            }
        }
    }

    /// <summary>
    /// In-place inverse transform, without the 1/N scale: the forward transform with the two parts swapped, which gives the
    /// inverse times N.
    /// </summary>
    public void InverseUnscaled(double[] re, double[] im) => Forward(im, re);
}
