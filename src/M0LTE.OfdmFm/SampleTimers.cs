namespace M0LTE.OfdmFm;

/// <summary>A timer scheduled on a sample clock; pass it to cancel.</summary>
internal readonly record struct TimerHandle(long Sequence);

/// <summary>
/// Actions due at sample indexes, run in order (by due index, then by the order scheduled). Knows nothing of the clock: its owner
/// asks for the next due index and runs what is due. A few timers at a time is the expected use, so a sorted list serves.
/// </summary>
internal sealed class SampleTimers
{
    private sealed class Entry
    {
        public long Due;
        public long Sequence;
        public Action Action = null!;
    }

    private readonly List<Entry> _entries = [];
    private readonly Stack<Entry> _free = new();
    private long _nextSequence = 1;

    /// <summary>How many timers are waiting.</summary>
    public int Count => _entries.Count;

    /// <summary>The due index of the earliest timer, or null if none.</summary>
    public long? NextDue => _entries.Count == 0 ? null : _entries[0].Due;

    /// <summary>Schedules <paramref name="action"/> for index <paramref name="due"/>.</summary>
    public TimerHandle Schedule(long due, Action action)
    {
        // Entries are reused, so that a station that schedules and runs timers all day allocates nothing for them.
        Entry entry = _free.Count > 0 ? _free.Pop() : new Entry();
        entry.Due = due;
        entry.Sequence = _nextSequence++;
        entry.Action = action;
        int at = _entries.Count;
        while (at > 0 && _entries[at - 1].Due > due)
        {
            at--;
        }

        _entries.Insert(at, entry);
        return new TimerHandle(entry.Sequence);
    }

    /// <summary>Cancels a timer. False if it has already run or been cancelled.</summary>
    public bool Cancel(TimerHandle handle)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Sequence == handle.Sequence)
            {
                _entries[i].Action = null!;
                _free.Push(_entries[i]);
                _entries.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes and returns the earliest timer if it is due at or before <paramref name="now"/>.</summary>
    public bool TryTakeDue(long now, out Action action)
    {
        if (_entries.Count > 0 && _entries[0].Due <= now)
        {
            action = _entries[0].Action;
            _entries[0].Action = null!;
            _free.Push(_entries[0]);
            _entries.RemoveAt(0);
            return true;
        }

        action = null!;
        return false;
    }
}
