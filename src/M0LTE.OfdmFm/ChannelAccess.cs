namespace M0LTE.OfdmFm;

/// <summary>Which wait a transmission needs before it may go (docs/station-design.md F). Lower is more urgent.</summary>
internal enum TransmitClass
{
    /// <summary>A lone report, or the identification the station owes: the first slot.</summary>
    Report = 0,

    /// <summary>An expedited datagram: the first slot.</summary>
    Expedited = 1,

    /// <summary>Ordinary traffic: the channel clear for the first-slot time, then a drawn number of slots.</summary>
    Ordinary = 2,
}

/// <summary>What happened to an access request.</summary>
internal enum AccessStage
{
    /// <summary>Handed to the audio layer.</summary>
    Granted,

    /// <summary>Taken back from the audio layer before it was keyed, and queued again.</summary>
    Withdrawn,

    /// <summary>The audio layer keyed it.</summary>
    Started,

    /// <summary>It finished.</summary>
    Ended,
}

/// <summary>A change in an access request, on the sample clock.</summary>
/// <param name="Id">The id <see cref="Station.Enqueue"/> returned.</param>
/// <param name="Stage">What happened.</param>
/// <param name="Sample">The clock when it did (for started and ended, the index the audio layer reported).</param>
internal readonly record struct AccessEvent(long Id, AccessStage Stage, long Sample);

/// <summary>How the channel-access logic reads and uses the sample clock. A station supplies its own; a test supplies a bare one.</summary>
internal interface IChannelClock
{
    /// <summary>The sample clock.</summary>
    long Now { get; }

    /// <summary>Runs <paramref name="action"/> when the clock reaches <paramref name="sample"/>.</summary>
    TimerHandle ScheduleAt(long sample, Action action);

    /// <summary>Cancels a timer; false if it has run.</summary>
    bool CancelTimer(TimerHandle handle);
}

/// <summary>Which rule an ordinary request uses for its random wait. Slotted is the station's; the single delay is the design's first proposal, kept so that a test can compare the two.</summary>
internal enum AccessMode
{
    /// <summary>A whole number of the station's own time-to-notice-a-carrier, drawn from a small window.</summary>
    Slotted,

    /// <summary>One delay drawn uniformly from 0 to 0.4 s, in samples. For tests only.</summary>
    SingleDelay,
}

/// <summary>
/// The channel as the station sees it, and when it may transmit (docs/station-design.md E and F). Busy is the union of causes, each of which ends on the sample clock; waiting requests are
/// granted when the channel has been clear for their class's time. Everything is measured in samples on a clock the owner supplies; nothing here reads time, sleeps or starts a thread.
/// Not thread-safe: one thread, the audio thread's.
/// </summary>
internal sealed class ChannelAccess
{
    /// <summary>Clear for this long after a burst, in samples (50 ms): the first slot, for a lone report or an expedited datagram. The design's.</summary>
    internal const int FirstSlotClear = 2400;

    /// <summary>Clear for this long before ordinary traffic draws its slots, in samples (0.8 s). The design's starting value (its T101); the protocol sets no figure.</summary>
    internal const int OrdinaryClear = 38400;

    /// <summary>The single delay's spread, in samples (0.4 s): the design's original random delay, used by <see cref="AccessMode.SingleDelay"/> only.</summary>
    internal const int SingleDelaySpread = 19200;

    /// <summary>Busy for this long after an external indication drops, in samples (100 ms). The design's (E).</summary>
    internal const int ExternalHold = 4800;

    /// <summary>The time to notice a carrier while the host supplies an external indication, in samples (100 ms): the same figure as the hold, the design's.</summary>
    internal const int ExternalNotice = 4800;

    /// <summary>Busy for this long after the station's own transmission ends, in samples (100 ms). A starting value; how long a radio takes to hear again is #883 item 8.</summary>
    internal const int OwnTail = 4800;

    /// <summary>A candidate whose header will not read holds the channel for its opening and this much more, in samples (1 s). The design's (Choice 4).</summary>
    internal const int UnreadableHold = 48000;

    /// <summary>A header of a version the station does not know holds the channel for this long, again and again while candidates keep arriving, in samples (1 s). The protocol says busy for as long as signal is heard; the design's step.</summary>
    internal const int UnknownVersionHold = 48000;

    /// <summary>After a lone report for the station, ordinary traffic leaves the channel clear for this long from the report's last sample, in samples (1 s). The protocol's "about a second".</summary>
    internal const int ReportHoldOff = 48000;

    /// <summary>The station does not transmit before this sample, 5 s: the threshold's start-up floor applies until then (design C).</summary>
    internal const int StartUpListen = 240000;

    /// <summary>
    /// How many slots ordinary traffic draws from while the host supplies a carrier indication (slot 100 ms): the draw is 0 to 3 slots, 0.3 s at most, as near the design's 0.4 s of spread as a whole number of slots gets.
    /// With four, two waiting stations share a slot one time in four; a station one slot behind the other has time to hear it.
    /// </summary>
    internal const int SlotsWithCarrierIndication = 4;

    /// <summary>
    /// How many slots ordinary traffic draws from with burst detection alone (slot about 0.9 s): 0 or 1 slot. Each slot costs most of a second of dead air, so two keeps the average extra wait to about 0.4 s,
    /// the design's 0.4 s of spread, and a pair sharing a slot half the time is the price. Making this follow the stations heard and the collisions seen is packet-net/pdn-soundmodem#892.
    /// </summary>
    internal const int SlotsWithDetectionOnly = 2;

    /// <summary>
    /// The longest a station can take to notice another station's transmission from burst detection alone, in samples from the first sample of the audio it started: the renderer's lead-in (silence before the
    /// first training sample), then for each opening the later of the reference plus a symbol (the correlation value and the symbol after it that settles the peak) and the opening that the header read needs,
    /// then a whole frame's new values for the wait for the next frame to be correlated. The slowest opening is the 1.5 kHz one: 4224 plus 19008 plus 20097, 43329 samples (0.903 s).
    /// Step 2 measured 0.24 to 0.46 s after the opening ends, which this bounds. A slot at least this long means a station one slot behind another always hears it before its own turn.
    /// </summary>
    internal static readonly int DetectionLatency = ComputeDetectionLatency();

    private readonly IChannelClock _clock;
    private readonly Random _random;
    private readonly Action<bool> _busyChanged;
    private readonly Action<Request> _grant;
    private readonly Action<Request> _withdraw;
    private readonly Action<AccessEvent>? _raise;
    private readonly Action _onHorizon;
    private readonly Action _onAccessTimer;
    private readonly List<Request> _waiting = [];

    private Request? _active;
    private long _nextId = 1;
    private long _nextSequence;
    private bool _updating;
    private bool _again;
    private bool _busy;
    private bool _pending;
    private bool _externalSet;
    private bool _externalSupplied;
    private long _holdUntil;
    private long _externalUntil;
    private long _ownUntil;
    private long _ordinaryNotBefore;
    private int _draw = -1;
    private TimerHandle _horizonTimer;
    private long _horizonAt = -1;
    private TimerHandle _accessTimer;
    private long _accessAt = -1;

    /// <summary>One transmission waiting, granted or on air.</summary>
    internal sealed class Request
    {
        /// <summary>The id the caller was given.</summary>
        public long Id;

        /// <summary>The wait it needs.</summary>
        public TransmitClass Class;

        /// <summary>What to play.</summary>
        public ReadOnlyMemory<float> Samples;

        /// <summary>Arrival order, for first in first out within a class.</summary>
        public long Sequence;

        /// <summary>Where it stands.</summary>
        public RequestState State;

        /// <summary>The audio layer's id for the latest hand-over (the owner keeps it).</summary>
        public long HandshakeId;

        /// <summary>For a burst the host did not ask for (a lone report, an identification): what to tell the host when it starts. Null for the host's own datagrams.</summary>
        public ModemSentEvent? Modem;
    }

    /// <summary>Where a request stands.</summary>
    internal enum RequestState
    {
        /// <summary>Waiting for its turn.</summary>
        Waiting,

        /// <summary>Handed to the audio layer, not keyed.</summary>
        Granted,

        /// <summary>On air.</summary>
        Started,

        /// <summary>Over.</summary>
        Done,
    }

    /// <summary>Makes the logic.</summary>
    /// <param name="clock">The sample clock and its timers.</param>
    /// <param name="random">The seeded source the draws come from.</param>
    /// <param name="busyChanged">Called on each change of the union, never twice running with the same value.</param>
    /// <param name="grant">Called to hand a request to the audio layer; sets its handshake id.</param>
    /// <param name="withdraw">Called to take a granted request back before it is keyed.</param>
    /// <param name="raise">Called for each change of a request.</param>
    public ChannelAccess(IChannelClock clock, Random random, Action<bool> busyChanged, Action<Request> grant, Action<Request> withdraw, Action<AccessEvent>? raise = null)
    {
        _clock = clock;
        _random = random;
        _busyChanged = busyChanged;
        _grant = grant;
        _withdraw = withdraw;
        _raise = raise;
        _onHorizon = () =>
        {
            _horizonAt = -1;
            Update();
        };
        _onAccessTimer = () =>
        {
            _accessAt = -1;
            Update();
        };
    }

    /// <summary>
    /// Called when a request is about to be granted, before the grant is handed over, so that what it carries can be decided once the channel is free (the design's Choice 6). Sets <see cref="Request.Samples"/>.
    /// If it returns false there is nothing to send after all: the request is finished and not handed over. Null: requests carry their samples from <see cref="Enqueue(ReadOnlyMemory{float}, TransmitClass, ModemSentEvent?)"/>.
    /// </summary>
    public Func<Request, bool>? Prepare { get; set; }

    /// <summary>The rule for the random wait. Tests set it; the station uses <see cref="AccessMode.Slotted"/>.</summary>
    public AccessMode Mode { get; set; } = AccessMode.Slotted;

    /// <summary>The union of the causes, as of the last update.</summary>
    public bool Busy => _busy;

    /// <summary>True once the host has called for an external indication at all.</summary>
    public bool ExternalSupplied => _externalSupplied;

    /// <summary>Requests waiting for their turn.</summary>
    public int WaitingCount => _waiting.Count;

    /// <summary>The draw for the current clear period, or -1 if none has been made: slots in <see cref="AccessMode.Slotted"/>, samples in <see cref="AccessMode.SingleDelay"/>.</summary>
    public int Draw => _draw;

    /// <summary>The request on air or handed to the audio layer, if any.</summary>
    public Request? Active => _active;

    /// <summary>
    /// The station's own time to notice a carrier: short while the host supplies an external indication, otherwise the detection latency of the slowest opening. One slot of the random wait.
    /// </summary>
    public int NoticeSamples => _externalSupplied ? ExternalNotice : DetectionLatency;

    /// <summary>
    /// How many slots the draw is made from now. The seam for packet-net/pdn-soundmodem#892: the window will follow the stations heard sending and the collisions seen; until then it is fixed.
    /// </summary>
    public int SlotWindow() => _externalSupplied ? SlotsWithCarrierIndication : SlotsWithDetectionOnly;

    /// <summary>The sample the channel was last busy until: when the clear time of the waits is counted from (the start-up listen counted in).</summary>
    public long ClearSince => Math.Max(Math.Max(_holdUntil, _externalUntil), Math.Max(_ownUntil, StartUpListen));

    /// <summary>Queues a transmission and returns it. It may be granted before this returns.</summary>
    public Request Enqueue(ReadOnlyMemory<float> samples, TransmitClass cls, ModemSentEvent? modem = null)
    {
        var request = new Request { Id = _nextId++, Class = cls, Samples = samples, Sequence = _nextSequence++, State = RequestState.Waiting, Modem = modem };
        Insert(request);
        Update();
        return request;
    }

    /// <summary>
    /// Queues a request the owner keeps and reuses (its samples come from <see cref="Prepare"/>), in <paramref name="cls"/>. The request must be new or <see cref="RequestState.Done"/>. It may be granted before this returns.
    /// </summary>
    public Request Enqueue(Request request, TransmitClass cls)
    {
        request.Id = _nextId++;
        request.Class = cls;
        request.Samples = default;
        request.Sequence = _nextSequence++;
        request.State = RequestState.Waiting;
        request.Modem = null;
        Insert(request);
        Update();
        return request;
    }

    /// <summary>Moves a waiting request to <paramref name="cls"/>, keeping its place in arrival order within the class. Does not run the rules: the caller follows it with <see cref="Update"/>, or is inside it.</summary>
    public void Reclassify(Request request, TransmitClass cls)
    {
        if (request.State != RequestState.Waiting || request.Class == cls)
        {
            return;
        }

        _waiting.Remove(request);
        request.Class = cls;
        Insert(request);
    }

    /// <summary>Takes a waiting request out of the queue and finishes it, then runs the rules. Nothing happens for a request that is granted, on air or done.</summary>
    public void Remove(Request request)
    {
        if (request.State != RequestState.Waiting)
        {
            return;
        }

        _waiting.Remove(request);
        request.State = RequestState.Done;
        Update();
    }

    /// <summary>A burst or hold the channel is busy for until <paramref name="until"/> (exclusive): clear from that sample. A past sample counts only for when the channel was last busy.</summary>
    public void HoldUntil(long until)
    {
        _holdUntil = Math.Max(_holdUntil, until);
        Update();
    }

    /// <summary>Whether a candidate is waiting for its header to be judged: busy while it is.</summary>
    public void SetPending(bool pending)
    {
        if (_pending == pending)
        {
            return;
        }

        _pending = pending;
        Update();
    }

    /// <summary>The host's carrier indication changed. Busy while set and for <see cref="ExternalHold"/> after; the first call, either way, says the host supplies one.</summary>
    public void SetExternal(bool busy)
    {
        _externalSupplied = true;
        if (busy)
        {
            _externalSet = true;
        }
        else if (_externalSet)
        {
            _externalSet = false;
            _externalUntil = Math.Max(_externalUntil, _clock.Now + ExternalHold);
        }

        Update();
    }

    /// <summary>Ordinary traffic may not be granted before <paramref name="sample"/>; an ordinary request already handed over and not keyed is taken back.</summary>
    public void HoldOffOrdinaryUntil(long sample)
    {
        _ordinaryNotBefore = Math.Max(_ordinaryNotBefore, sample);
        Update();
    }

    /// <summary>The audio layer keyed <paramref name="request"/>. <paramref name="late"/>: it had been withdrawn and started anyway (the tolerated race).</summary>
    public void Started(Request request, long sample, bool late)
    {
        if (late)
        {
            // It is on air whatever the station thought: it leaves the queue, and a newer hand-over of it is the owner's to take back.
            _waiting.Remove(request);
            _active = request;
        }

        request.State = RequestState.Started;
        _raise?.Invoke(new AccessEvent(request.Id, AccessStage.Started, sample));
        Update();
    }

    /// <summary>The audio layer finished <paramref name="request"/> at <paramref name="sample"/>: busy for the tail after.</summary>
    public void Ended(Request request, long sample)
    {
        if (ReferenceEquals(_active, request))
        {
            _active = null;
        }

        request.State = RequestState.Done;
        _ownUntil = Math.Max(_ownUntil, sample + OwnTail);
        _raise?.Invoke(new AccessEvent(request.Id, AccessStage.Ended, sample));
        Update();
    }

    /// <summary>Works out the union, withdraws what must be, schedules the next change and grants what may go. Runs on every event and every timer; changes nothing when nothing changed.</summary>
    public void Update()
    {
        if (_updating)
        {
            _again = true;
            return;
        }

        _updating = true;
        try
        {
            do
            {
                _again = false;
                Step();
            }
            while (_again);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Step()
    {
        long now = _clock.Now;
        bool foreign = _pending || _externalSet || now < _holdUntil || now < _externalUntil;

        // Never transmit while busy: what is handed over and not keyed goes back if another station's signal, or the rule for ordinary traffic, says so.
        if (_active is { State: RequestState.Granted } granted && (foreign || (granted.Class == TransmitClass.Ordinary && now < _ordinaryNotBefore)))
        {
            _active = null;
            granted.State = RequestState.Waiting;
            Insert(granted);
            _withdraw(granted);
            _raise?.Invoke(new AccessEvent(granted.Id, AccessStage.Withdrawn, now));
        }

        bool busy = foreign || _active is not null || now < _ownUntil;
        if (busy != _busy)
        {
            _busy = busy;
            if (busy)
            {
                // The draw is made afresh each time the channel was last busy.
                _draw = -1;
            }

            _busyChanged(busy);
        }

        // The next sample at which a time-based cause lets go.
        long horizon = Math.Max(Math.Max(_holdUntil, _externalUntil), _ownUntil);
        if (horizon > now)
        {
            if (_horizonAt != horizon)
            {
                if (_horizonAt >= 0)
                {
                    _clock.CancelTimer(_horizonTimer);
                }

                _horizonTimer = _clock.ScheduleAt(horizon, _onHorizon);
                _horizonAt = horizon;
            }
        }
        else if (_horizonAt >= 0)
        {
            _clock.CancelTimer(_horizonTimer);
            _horizonAt = -1;
        }

        if (!busy && _active is null && _waiting.Count > 0)
        {
            Request head = _waiting[0];
            long target = TargetFor(head);
            if (target <= now)
            {
                Grant(head, now);
                _again = true;
                return;
            }

            if (_accessAt != target)
            {
                if (_accessAt >= 0)
                {
                    _clock.CancelTimer(_accessTimer);
                }

                _accessTimer = _clock.ScheduleAt(target, _onAccessTimer);
                _accessAt = target;
            }
        }
        else if (_accessAt >= 0)
        {
            _clock.CancelTimer(_accessTimer);
            _accessAt = -1;
        }
    }

    /// <summary>The sample at which the head of the queue may go, if the channel stays clear.</summary>
    private long TargetFor(Request request)
    {
        long clear = ClearSince;
        if (request.Class != TransmitClass.Ordinary)
        {
            return clear + FirstSlotClear;
        }

        long wait;
        if (Mode == AccessMode.Slotted)
        {
            if (_draw < 0)
            {
                _draw = _random.Next(SlotWindow());
            }

            wait = OrdinaryClear + ((long)_draw * NoticeSamples);
        }
        else
        {
            if (_draw < 0)
            {
                _draw = _random.Next(SingleDelaySpread + 1);
            }

            wait = OrdinaryClear + _draw;
        }

        return Math.Max(clear + wait, _ordinaryNotBefore);
    }

    private void Grant(Request request, long now)
    {
        _waiting.Remove(request);
        request.State = RequestState.Granted;
        _active = request;
        if (Prepare is not null && !Prepare(request))
        {
            _active = null;
            request.State = RequestState.Done;
            return;
        }

        _grant(request);
        _raise?.Invoke(new AccessEvent(request.Id, AccessStage.Granted, now));
    }

    private void Insert(Request request)
    {
        int at = _waiting.Count;
        while (at > 0 && (_waiting[at - 1].Class > request.Class || (_waiting[at - 1].Class == request.Class && _waiting[at - 1].Sequence > request.Sequence)))
        {
            at--;
        }

        _waiting.Insert(at, request);
    }

    private static int ComputeDetectionLatency()
    {
        int worst = 0;
        foreach (FormatOpening opening in Enum.GetValues<FormatOpening>())
        {
            int reference = (HeaderReader.TrainingSymbolCount(opening) + 1) * BurstRenderer.SymbolSamples;
            worst = Math.Max(worst, Math.Max(reference, HeaderReader.OpeningSamples(opening)));
        }

        return BurstRenderer.DefaultLeadSamples + worst + StreamingBurstDetector.Hop;
    }
}
