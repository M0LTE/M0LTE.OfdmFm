namespace M0LTE.OfdmFm;

/// <summary>
/// Where each link address lives, as the station has learned it from the data bursts it decoded: an address key to the on-air number of the station that sent it, and the time the burst that said so was heard
/// (seconds on the sample clock). Every station learns from every data burst it decodes, whoever the burst is for, and the latest burst heard wins. The table forgets nothing by itself: the station removes what points at a station
/// that has been forgotten, and what points at a station that three bursts running have met silence from (<see cref="ForgetStation"/>). Once an address is in it, learning it again allocates nothing.
/// </summary>
internal sealed class AddressTable
{
    internal readonly record struct Entry(int Number, double HeardAt);

    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly List<uint> _scratch = [];

    /// <summary>How many addresses are located.</summary>
    public int Count => _entries.Count;

    /// <summary>Every address located, for the station's trace.</summary>
    public Dictionary<uint, Entry> Entries => _entries;

    /// <summary>Records that the address with this key lives at station <paramref name="number"/>, heard at <paramref name="now"/>. Replaces whatever was known: the latest burst wins. Returns the station it lived at before, or 0 when it was not located.</summary>
    public int Learn(uint key, int number, double now)
    {
        int before = _entries.TryGetValue(key, out Entry old) ? old.Number : 0;
        _entries[key] = new Entry(number, now);
        return before;
    }

    /// <summary>The station an address lives at, or false when it is not located.</summary>
    public bool TryGet(uint key, out Entry entry) => _entries.TryGetValue(key, out entry);

    /// <summary>Forgets every address that lives at station <paramref name="number"/>. Returns how many.</summary>
    public int ForgetStation(int number)
    {
        _scratch.Clear();
        foreach (KeyValuePair<uint, Entry> pair in _entries)
        {
            if (pair.Value.Number == number)
            {
                _scratch.Add(pair.Key);
            }
        }

        foreach (uint key in _scratch)
        {
            _entries.Remove(key);
        }

        return _scratch.Count;
    }

    /// <summary>Forgets one address. False when it was not there.</summary>
    public bool Forget(uint key) => _entries.Remove(key);
}
