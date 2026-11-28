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

/// <summary>What a note from the channel-access rules is about (for the station's trace: nothing else reads it).</summary>
internal enum AccessNoteKind
{
    /// <summary>A request was queued.</summary>
    Requested,

    /// <summary>The random wait for ordinary traffic was drawn: <see cref="AccessNote.A"/> is the draw, <see cref="AccessNote.B"/> the number of slots it was drawn from (-1 for the single delay, where the draw is samples).</summary>
    Drawn,

    /// <summary>The head of the queue will be granted at <see cref="AccessNote.Sample"/> if the channel stays clear.</summary>
    Scheduled,

    /// <summary>A request was handed to the audio layer.</summary>
    Granted,

    /// <summary>A request handed over and not keyed was taken back; <see cref="AccessNote.Cause"/> says why.</summary>
    Withdrawn,
}

/// <summary>Why the channel is busy, or why a request was taken back. One cause: the most telling.</summary>
internal enum AccessCause
{
    /// <summary>None.</summary>
    None,

    /// <summary>The station's own transmission, or its tail.</summary>
    OwnTransmission,

    /// <summary>The host's carrier indication.</summary>
    Carrier,

    /// <summary>A burst (or an unreadable candidate) heard from another station.</summary>
    HeardBurst,

    /// <summary>A candidate waiting for its header to be judged.</summary>
    Judging,

    /// <summary>Ordinary traffic held off after a lone report for this station.</summary>
    ReportHoldOff,

    /// <summary>The station's number changed, so what was built is built again.</summary>
    NumberChanged,

    /// <summary>The audio layer would not key it.</summary>
    Refused,

    /// <summary>Another station's identification burst announced that it goes on sending (its callsign in Morse) after the burst: nothing goes until that time has run.</summary>
    Identifying,
}

/// <summary>A note from the channel-access rules to the station's trace. A value, so that making one allocates nothing.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Request">The request it concerns.</param>
/// <param name="Sample">The sample it concerns (see <see cref="AccessNoteKind"/>), or the clock.</param>
/// <param name="A">See <see cref="AccessNoteKind.Drawn"/>.</param>
/// <param name="B">See <see cref="AccessNoteKind.Drawn"/>.</param>
/// <param name="Cause">For a withdrawal.</param>
internal readonly record struct AccessNote(AccessNoteKind Kind, ChannelAccess.Request Request, long Sample, long A = 0, long B = 0, AccessCause Cause = AccessCause.None);

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
    private long _tailUntil;
    private long _ordinaryNotBefore;
    private long _straightOnFrom = -1;
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

        /// <summary>The clock when it last joined the queue (queued, or queued again after a withdrawal), for the trace.</summary>
        public long QueuedAt;

        /// <summary>Where it stands.</summary>
        public RequestState State;

        /// <summary>The audio layer's id for the latest hand-over (the owner keeps it).</summary>
        public long HandshakeId;

        /// <summary>For a burst the host did not ask for (a lone report, an identification): what to tell the host when it starts. Null for the host's own datagrams.</summary>
        public ModemSentEvent? Modem;

        /// <summary>True: the request goes ahead of every other waiting request, whatever its class (the station's own identification).</summary>
        public bool First;
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

    /// <summary>
    /// Called when the head of the queue may go, before it is handed over. If it returns true the owner has put something ahead of it (queued a request with <see cref="Request.First"/> set), and the head is left waiting: the rules run again,
    /// and the new request goes first. Null: nothing is put ahead.
    /// </summary>
    public Func<Request, bool>? Gate { get; set; }

    /// <summary>Told of what the rules decide, for the station's trace. Null: nothing is told. Called with a value and nothing built; the listener decides whether anyone wants it.</summary>
    public Action<AccessNote>? Note { get; set; }

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
    public long ClearSince => Math.Max(Math.Max(Math.Max(_holdUntil, _tailUntil), _externalUntil), Math.Max(_ownUntil, StartUpListen));

    /// <summary>Queues a transmission and returns it. It may be granted before this returns.</summary>
    public Request Enqueue(ReadOnlyMemory<float> samples, TransmitClass cls, ModemSentEvent? modem = null)
    {
        var request = new Request { Id = _nextId++, Class = cls, Samples = samples, Sequence = _nextSequence++, State = RequestState.Waiting, Modem = modem };
        Insert(request);
        Note?.Invoke(new AccessNote(AccessNoteKind.Requested, request, _clock.Now));
        Update();
        return request;
    }

    /// <summary>
    /// Queues a request the owner keeps and reuses (its samples come from <see cref="Prepare"/>), in <paramref name="cls"/>. The request must be new or <see cref="RequestState.Done"/>. It may be granted before this returns.
    /// </summary>
    public Request Enqueue(Request request, TransmitClass cls, bool first = false)
    {
        request.Id = _nextId++;
        request.Class = cls;
        request.First = first;
        request.Samples = default;
        request.Sequence = _nextSequence++;
        request.State = RequestState.Waiting;
        request.Modem = null;
        Insert(request);
        Note?.Invoke(new AccessNote(AccessNoteKind.Requested, request, _clock.Now));
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

    /// <summary>
    /// Another station's identification burst said it goes on sending until <paramref name="until"/> (exclusive): busy until then, for everything including reports, and shown as its own cause. A past sample counts only for when the channel was last busy.
    /// </summary>
    public void HoldForTail(long until)
    {
        _tailUntil = Math.Max(_tailUntil, until);
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

    /// <summary>
    /// The host's carrier indication changed. Busy while set and for <see cref="ExternalHold"/> after; the first call with a value, either way, says the host supplies one.
    /// Null says there is none (any more): the station forgets it had one, the channel is not held busy by it from now, and the slot goes back to the detection latency until a value arrives again.
    /// The channel counts as having been busy by the indication up to this moment, so that a wait counted from it is not cut short.
    /// </summary>
    public void SetExternal(bool? busy)
    {
        if (busy is null)
        {
            _externalUntil = _externalSet ? _clock.Now : Math.Min(_externalUntil, _clock.Now);
            _externalSet = false;
            _externalSupplied = false;
            Update();
            return;
        }

        _externalSupplied = true;
        if (busy.Value)
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

    /// <summary>
    /// After a lone report this station sent: ordinary traffic goes without the 0.8 s wait and the random draw, a first-slot time after the channel was clear (the protocol: "the station that sent it goes straight on with its own data").
    /// It holds for this clear period only: anything that turns the channel busy again moves <see cref="ClearSince"/> past the point recorded here and the ordinary wait applies once more.
    /// </summary>
    public void GoStraightOn()
    {
        _straightOnFrom = ClearSince;
        Update();
    }

    /// <summary>
    /// Takes back a request handed to the audio layer and not keyed, whatever the channel is doing, and queues it again: what it carries is stale (the station's number changed). The rules then run, and may hand it over again at once.
    /// Nothing happens for a request that is not granted.
    /// </summary>
    public void TakeBack(Request request)
    {
        if (request.State != RequestState.Granted || !ReferenceEquals(_active, request))
        {
            return;
        }

        _active = null;
        request.State = RequestState.Waiting;
        Insert(request);
        _withdraw(request);
        _raise?.Invoke(new AccessEvent(request.Id, AccessStage.Withdrawn, _clock.Now));
        Note?.Invoke(new AccessNote(AccessNoteKind.Withdrawn, request, _clock.Now, Cause: AccessCause.NumberChanged));
        Update();
    }

    /// <summary>
    /// The audio layer says it will not play <paramref name="request"/>, which it was handed and has not keyed: it goes back to waiting, as after a withdrawal (its samples are kept), and the channel counts as having been busy up to now,
    /// so that it is handed over again only after the clear time of its class and slots. Nothing happens for a request that is not granted.
    /// </summary>
    public void Refused(Request request)
    {
        if (request.State != RequestState.Granted || !ReferenceEquals(_active, request))
        {
            return;
        }

        _holdUntil = Math.Max(_holdUntil, _clock.Now);
        _active = null;
        request.State = RequestState.Waiting;
        Insert(request);
        _raise?.Invoke(new AccessEvent(request.Id, AccessStage.Withdrawn, _clock.Now));
        Note?.Invoke(new AccessNote(AccessNoteKind.Withdrawn, request, _clock.Now, Cause: AccessCause.Refused));
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
        bool foreign = _pending || _externalSet || now < _holdUntil || now < _tailUntil || now < _externalUntil;

        // Never transmit while busy: what is handed over and not keyed goes back if another station's signal, or the rule for ordinary traffic, says so.
        if (_active is { State: RequestState.Granted } granted && (foreign || (granted.Class == TransmitClass.Ordinary && now < _ordinaryNotBefore)))
        {
            _active = null;
            granted.State = RequestState.Waiting;
            Insert(granted);
            _withdraw(granted);
            _raise?.Invoke(new AccessEvent(granted.Id, AccessStage.Withdrawn, now));
            Note?.Invoke(new AccessNote(AccessNoteKind.Withdrawn, granted, now, Cause: foreign ? CauseNow() : AccessCause.ReportHoldOff));
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
        long horizon = Math.Max(Math.Max(Math.Max(_holdUntil, _tailUntil), _externalUntil), _ownUntil);
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
                if (Gate is not null && Gate(head))
                {
                    _again = true;
                    return;
                }

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
                Note?.Invoke(new AccessNote(AccessNoteKind.Scheduled, head, target));
            }
        }
        else if (_accessAt >= 0)
        {
            _clock.CancelTimer(_accessTimer);
            _accessAt = -1;
        }
    }

    /// <summary>The most telling reason the channel is busy now, or <see cref="AccessCause.None"/>: for the station's trace.</summary>
    public AccessCause CauseNow()
    {
        long now = _clock.Now;
        if (_active is not null || now < _ownUntil)
        {
            return AccessCause.OwnTransmission;
        }

        if (_externalSet || now < _externalUntil)
        {
            return AccessCause.Carrier;
        }

        if (now < _tailUntil)
        {
            return AccessCause.Identifying;
        }

        if (now < _holdUntil)
        {
            return AccessCause.HeardBurst;
        }

        return _pending ? AccessCause.Judging : AccessCause.None;
    }

    /// <summary>The sample at which the head of the queue may go, if the channel stays clear.</summary>
    private long TargetFor(Request request)
    {
        long clear = ClearSince;
        if (request.Class != TransmitClass.Ordinary)
        {
            return clear + FirstSlotClear;
        }

        if (_straightOnFrom >= 0 && clear == _straightOnFrom)
        {
            return Math.Max(clear + FirstSlotClear, _ordinaryNotBefore);
        }

        long wait;
        if (Mode == AccessMode.Slotted)
        {
            if (_draw < 0)
            {
                int slots = SlotWindow();
                _draw = _random.Next(slots);
                Note?.Invoke(new AccessNote(AccessNoteKind.Drawn, request, _clock.Now, _draw, slots));
            }

            wait = OrdinaryClear + ((long)_draw * NoticeSamples);
        }
        else
        {
            if (_draw < 0)
            {
                _draw = _random.Next(SingleDelaySpread + 1);
                Note?.Invoke(new AccessNote(AccessNoteKind.Drawn, request, _clock.Now, _draw, -1));
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
        Note?.Invoke(new AccessNote(AccessNoteKind.Granted, request, now));
    }

    private void Insert(Request request)
    {
        request.QueuedAt = _clock.Now;
        int at = _waiting.Count;
        while (at > 0 && Before(request, _waiting[at - 1]))
        {
            at--;
        }

        _waiting.Insert(at, request);
    }

    /// <summary>True if <paramref name="a"/> goes ahead of <paramref name="b"/>: one marked first, then the more urgent class, then the earlier arrival.</summary>
    private static bool Before(Request a, Request b)
    {
        if (a.First != b.First)
        {
            return a.First;
        }

        return a.Class < b.Class || (a.Class == b.Class && a.Sequence < b.Sequence);
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
