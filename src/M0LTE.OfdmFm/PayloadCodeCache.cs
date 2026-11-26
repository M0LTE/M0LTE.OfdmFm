namespace M0LTE.OfdmFm;

/// <summary>
/// The most recently used payload plans of one owner (a renderer or a receiver). Not thread-safe, and it shares nothing
/// with other instances. A plan is cheap to rebuild, so the least recently used is simply dropped.
/// </summary>
internal sealed class PayloadCodeCache
{
    /// <summary>Most payload plans kept.</summary>
    public const int Capacity = 32;

    private readonly List<PayloadCode> _recent = new(Capacity + 1);

    /// <summary>Payload plans held now.</summary>
    public int Count => _recent.Count;

    /// <summary>The plan for a payload shape: from the most recently used set if it is there, otherwise built (and the least recently used dropped).</summary>
    public PayloadCode Get(int infoBits, int codedBits)
    {
        for (int i = 0; i < _recent.Count; i++)
        {
            PayloadCode p = _recent[i];
            if (p.InfoBits == infoBits && p.CodedBits == codedBits)
            {
                _recent.RemoveAt(i);
                _recent.Insert(0, p);
                return p;
            }
        }

        var built = new PayloadCode(infoBits, codedBits);
        _recent.Insert(0, built);
        if (_recent.Count > Capacity)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }

        return built;
    }
}
