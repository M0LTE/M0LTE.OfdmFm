using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>A bare sample clock with timers, for the channel-access logic on its own: the test moves it, and timers run at their own samples.</summary>
internal sealed class TestClock : IChannelClock
{
    private readonly SampleTimers _timers = new();

    public long Now { get; private set; }

    public TimerHandle ScheduleAt(long sample, Action action) => _timers.Schedule(sample, action);

    public bool CancelTimer(TimerHandle handle) => _timers.Cancel(handle);

    /// <summary>Moves the clock to <paramref name="target"/>, running each timer due on the way with the clock at its own sample.</summary>
    public void AdvanceTo(long target)
    {
        while (_timers.NextDue is { } due && due <= target)
        {
            Now = Math.Max(Now, due);
            _timers.TryTakeDue(Now, out Action? action);
            action();
        }

        Now = Math.Max(Now, target);
    }

    public int Timers => _timers.Count;
}

/// <summary>The channel-access logic on a <see cref="TestClock"/>, with everything it reports kept in order.</summary>
internal sealed class AccessRig
{
    private long _handshake;

    public AccessRig(int seed, bool externalSupplied = false)
    {
        Clock = new TestClock();
        Access = new ChannelAccess(
            Clock,
            new Random(seed),
            busy => BusyLog.Add((Clock.Now, busy)),
            r =>
            {
                r.HandshakeId = ++_handshake;
                GrantLog.Add((r.Id, Clock.Now));
            },
            r => WithdrawLog.Add((r.Id, Clock.Now)),
            Events.Add);
        if (externalSupplied)
        {
            Access.SetExternal(false);
        }
    }

    public TestClock Clock { get; }

    public ChannelAccess Access { get; }

    /// <summary>Each change of the union, with the clock at which it was raised.</summary>
    public List<(long At, bool Busy)> BusyLog { get; } = [];

    public List<(long Id, long At)> GrantLog { get; } = [];

    public List<(long Id, long At)> WithdrawLog { get; } = [];

    public List<AccessEvent> Events { get; } = [];

    /// <summary>The sample a request was last granted at.</summary>
    public long GrantedAt(long id) => GrantLog.Last(g => g.Id == id).At;

    /// <summary>Starts the active request now and ends it <paramref name="length"/> samples later, as an audio layer that answers at once would.</summary>
    public void PlayActive(int length)
    {
        ChannelAccess.Request r = Access.Active ?? throw new InvalidOperationException("Nothing is granted.");
        long start = Clock.Now;
        Access.Started(r, start, false);
        Clock.AdvanceTo(start + length);
        Access.Ended(r, start + length);
    }
}
