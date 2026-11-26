using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// A stand-in for the audio layer. It hears the station's requests and withdrawals, and "plays" them by calling
/// <see cref="Station.TransmitStarted"/> and <see cref="Station.TransmitEnded"/> at the sample indexes it is told to. It feeds the station silence
/// in blocks, stopping at each scripted index so that the call arrives with the station's clock exactly there, as a real layer's report would.
/// A script can answer late, answer a request the station has withdrawn, or drop samples.
/// </summary>
internal sealed class FakeAudioLayer
{
    private readonly Station _station;
    private readonly float[] _silence;
    private readonly List<(long Index, long Order, Action Action)> _script = [];
    private long _order;

    public FakeAudioLayer(Station station, int blockSize = 480)
    {
        _station = station;
        _silence = new float[blockSize];
        station.TransmitRequest += r => Requests.Add(r);
        station.WithdrawRequest += w => Withdrawals.Add(w.Id);
    }

    /// <summary>Every request heard, in order.</summary>
    public List<TransmitRequestEvent> Requests { get; } = [];

    /// <summary>Every withdrawal heard, in order.</summary>
    public List<long> Withdrawals { get; } = [];

    /// <summary>Runs <paramref name="action"/> when the clock reaches <paramref name="index"/>.</summary>
    public void At(long index, Action action) => _script.Add((index, _order++, action));

    /// <summary>Reports request <paramref name="id"/> started at <paramref name="index"/>.</summary>
    public void StartAt(long id, long index) => At(index, () => _station.TransmitStarted(id, index));

    /// <summary>Reports request <paramref name="id"/> ended at <paramref name="index"/>.</summary>
    public void EndAt(long id, long index) => At(index, () => _station.TransmitEnded(id, index));

    /// <summary>Plays request <paramref name="id"/>: started at <paramref name="start"/>, ended after its samples.</summary>
    public void Play(TransmitRequestEvent request, long start)
    {
        StartAt(request.Id, start);
        EndAt(request.Id, start + request.Samples.Length);
    }

    /// <summary>Drops <paramref name="count"/> samples at <paramref name="index"/>.</summary>
    public void DropAt(long index, int count) => At(index, () => _station.Skip(count));

    /// <summary>Plays each request as it arrives, <paramref name="latency"/> samples after the clock or its earliest, whichever is later.</summary>
    public void PlayEveryRequest(int latency)
    {
        _station.TransmitRequest += r => Play(r, Math.Max(r.EarliestSample, _station.SampleCount + latency));
    }

    /// <summary>Feeds silence until the clock reaches <paramref name="until"/>, running the script at its indexes.</summary>
    public void Run(long until)
    {
        while (_station.SampleCount < until || _script.Any(s => s.Index <= _station.SampleCount))
        {
            foreach (var due in _script.Where(s => s.Index <= _station.SampleCount).OrderBy(s => s.Index).ThenBy(s => s.Order).ToList())
            {
                _script.Remove(due);
                due.Action();
            }

            if (_station.SampleCount >= until)
            {
                break;
            }

            long target = until;
            if (_script.Count > 0)
            {
                target = Math.Min(target, _script.Min(s => s.Index));
            }

            int take = (int)Math.Min(_silence.Length, Math.Max(1, target - _station.SampleCount));
            _station.Push(_silence.AsSpan(0, take));
        }
    }
}
