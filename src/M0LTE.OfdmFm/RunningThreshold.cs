namespace M0LTE.OfdmFm;

/// <summary>
/// The streaming detector's threshold for one opening: the larger of <see cref="BurstFinder.DefaultLeast"/> and
/// <see cref="BurstFinder.DefaultFactor"/> times the median of the last 30 s of correlation values (every 16th, as
/// <c>BurstFinder.Find</c> samples them), kept as a histogram with a ring of the bins, so that memory is fixed and an update
/// costs a few operations. Until 5 s of values have been taken, only the floor applies.
/// </summary>
/// <remarks>
/// <para>A correlation value is between 0 and 1. The histogram has 16384 bins of 1/16384 each. The smallest median met in
/// practice is that of the longest opening in white noise, about 0.006, which covers about 100 bins, so the quantisation is
/// under 1 percent of the estimate (and the estimate is multiplied by 7 and compared with a peak that stands several times
/// higher). The median is the centre of the bin holding the middle value. 16384 four-byte counts are 64 KB.</para>
/// <para>The window is the last 90000 values taken, which is 30 s of unpaused audio: a pause stops values being added and does
/// not age the window.</para>
/// </remarks>
internal sealed class RunningThreshold
{
    public const int Bins = 16384;
    public const int Every = 16;
    public const int WindowValues = 30 * 48000 / Every;
    public const int WarmUpValues = 5 * 48000 / Every;

    private readonly uint[] _counts = new uint[Bins];
    private readonly ushort[] _ring = new ushort[WindowValues];
    private int _head;
    private int _count;
    private int _medianBin;
    private int _below;
    private double _threshold = BurstFinder.DefaultLeast;

    /// <summary>The threshold now.</summary>
    public double Threshold => _threshold;

    /// <summary>Values taken so far (up to the window).</summary>
    public int Count => _count;

    /// <summary>Whether the estimate is in use yet (5 s of values taken).</summary>
    public bool Warm { get; private set; }

    /// <summary>Takes one correlation value.</summary>
    public void Add(double rho)
    {
        int bin = rho > 0 ? (int)(rho * Bins) : 0;
        if (bin >= Bins)
        {
            bin = Bins - 1;
        }

        if (_count == WindowValues)
        {
            int old = _ring[_head];
            _counts[old]--;
            if (old < _medianBin)
            {
                _below--;
            }
        }
        else
        {
            _count++;
        }

        _ring[_head] = (ushort)bin;
        if (++_head == WindowValues)
        {
            _head = 0;
        }

        _counts[bin]++;
        if (bin < _medianBin)
        {
            _below++;
        }

        // Keep the bin holding the element of rank count/2: below <= rank < below + counts[median].
        int rank = _count / 2;
        while (_below > rank)
        {
            _medianBin--;
            _below -= (int)_counts[_medianBin];
        }

        while (_below + (int)_counts[_medianBin] <= rank)
        {
            _below += (int)_counts[_medianBin];
            _medianBin++;
        }

        if (_count >= WarmUpValues)
        {
            Warm = true;
            double median = (_medianBin + 0.5) / Bins;
            _threshold = Math.Max(BurstFinder.DefaultLeast, BurstFinder.DefaultFactor * median);
        }
    }
}
