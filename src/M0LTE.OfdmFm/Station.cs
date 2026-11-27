namespace M0LTE.OfdmFm;

/// <summary>Where a transmit request stands.</summary>
internal enum TransmitState
{
    /// <summary>Asked for; the audio layer has not answered.</summary>
    Requested,

    /// <summary>The audio layer has keyed it.</summary>
    Started,

    /// <summary>It has finished.</summary>
    Ended,

    /// <summary>The station took it back before it was keyed.</summary>
    Withdrawn,
}

/// <summary>
/// The live station: one per radio channel. So far (steps 1 to 6 of docs/station-design.md) it keeps a sample clock with timers on it, runs the transmit handshake with the audio layer,
/// receives (audio pushed in comes out as <see cref="Received"/> datagrams, <see cref="PathReport"/> and <see cref="LinkState"/> events and calls on the adaptation rules), knows when the channel is busy and when it may transmit,
/// and sends what the host gives it (<see cref="Send"/>, <see cref="Cancel(HostTag)"/>, <see cref="Snapshot"/>; events <see cref="Transmitted"/>, <see cref="Dropped"/>, <see cref="Congestion"/>).
/// It tells the stations it hears what the path supports, by the protocol's rules (lone reports, step 6), and tells the host when a burst it sent was heard and not decoded (<see cref="BurstNotDecoded"/>).
/// And it identifies itself (step 7): while it has been transmitting it sends an identification burst by that rule and raises <see cref="IdentificationDue"/> for the audio layer's Morse ident, and it learns the callsigns of the stations it hears do the same (<see cref="Identified"/>).
/// <para>
/// <b>The clock.</b> The only clock is the count of receive samples at 48 kHz, advanced by <see cref="Push"/> and <see cref="Skip"/> and by nothing else.
/// Nothing here reads a wall clock, sleeps or waits. The adaptation rules are given each burst's end on this clock, never the time a verdict arrived.
/// </para>
/// <para>
/// <b>Receiving.</b> <see cref="Push"/> keeps the last 16.4 s of audio in a ring, feeds the streaming detector for the three openings, and queues what it finds as jobs: a header read for each
/// candidate (one at a time, in the order the candidates came), then, for each burst whose header read and whose last sample is in the ring, a receive of its payload. <see cref="Push"/> itself does no
/// header read and no receive. Jobs are run by <see cref="RunOne"/> and <see cref="RunPending"/> on threads the caller owns, or inside <see cref="Push"/> when the station was made with <c>runJobsInline</c>.
/// Whatever order jobs finish in, their results are applied in the order the bursts started, on the thread that calls <see cref="Push"/>, <see cref="Skip"/> or <see cref="DeliverResults"/>,
/// so the same audio gives the same events and the same adaptation state whether jobs run inline, on one worker or on several, and in whatever block sizes it was pushed.
/// </para>
/// <para>
/// <b>The channel.</b> The channel is busy while any of these holds, each ending on the sample clock, and <see cref="ChannelBusy"/> is raised on each change of the union:
/// a burst the station knows of, from the frame that read its header to its last sample (the length is in the header); a candidate whose header will not read, for its opening and one second from its first sample;
/// a header of a version the station does not know, one second at a time while more arrive; a candidate still waiting to be judged; the station's own transmission, from the grant to the end and 100 ms after; and the
/// host's carrier indication (<see cref="SetExternalBusy"/>), while set and for 100 ms after (none while the host says it has none). Audio from a symbol before the station's own transmission to 0.25 s after it is ignored.
/// A burst is found only after its opening has been heard and the next correlation frame has run, so a carrier is noticed late by burst detection alone: up to 0.9 s after another station keys, against 0.1 s with a carrier indication.
/// </para>
/// <para>
/// <b>Sending.</b> <see cref="Send"/> copies a datagram into the queue for its destination (one queue per link address, bounded at 30 s of airtime at the format in use). While anything is queued the station keeps one request with the
/// channel-access rules, in the class of its most urgent datagram. When the channel is free and the rules grant it, the station picks a destination (the one served longest ago, expedited traffic first), asks the adaptation rules for the format
/// (<c>LinkAdaptation.Choose</c>), takes datagrams for it in order while they fit (up to 2.5 s of airtime, or one datagram if it needs longer), writes the prelude (the recommendation for the destination, "report wanted" when the rules or the host ask,
/// and then the capability word), packs and renders the burst and hands the samples to the audio layer in <see cref="TransmitRequest"/>. A burst for everyone goes at the first-contact format with no prelude. The burst number counts up by one a burst, modulo 16.
/// <see cref="TransmitStarted"/> tells the rules a burst was sent (<c>Sent</c>); <see cref="TransmitEnded"/> raises <see cref="Transmitted"/> for each datagram in it. If the burst asked for a report and no header of any burst from the station was read within the answer wait
/// (3 s plus twice the burst's airtime) of its end, the rules are told of one silence (<c>NoAnswer</c>). A request withdrawn before it is keyed, or one the audio layer refuses (<see cref="TransmitRefused"/>), keeps its burst, built as it was, and is handed over again unchanged.
/// </para>
/// <para>
/// <b>Channel access.</b> Transmissions are requested as a report, an expedited datagram or ordinary traffic. A report or expedited datagram is granted once the channel has been
/// clear for 50 ms. Ordinary traffic is granted once it has been clear for 0.8 s plus a whole number of slots drawn from the seeded source afresh each time the channel was last busy, where a slot is the station's own time to notice
/// a carrier. Nothing is granted while the channel is busy or in the first five seconds, and a request handed to the audio layer and not yet keyed is withdrawn, and queued again, if the channel turns busy. After a lone report for this
/// station, ordinary traffic waits a second from the report's last sample. The values are fixed and named in <c>ChannelAccess</c>.
/// </para>
/// <para>
/// <b>Threads.</b> A station has no threads of its own. Two groups of members exist.
/// The <i>audio-thread</i> members are <see cref="Push"/>, <see cref="Skip"/>, <see cref="DeliverResults"/>, <see cref="TransmitStarted"/>, <see cref="TransmitEnded"/>, <see cref="TransmitRefused"/>, <see cref="SetExternalBusy"/>, the host's operations
/// <see cref="Send"/>, <see cref="Cancel(HostTag)"/>, <see cref="Cancel(LinkAddress)"/> and <see cref="Snapshot"/>, the internal <c>Enqueue</c> and the properties
/// <see cref="SampleCount"/> and <see cref="Seconds"/>; the caller must serialise every call to them on one station (one thread, or a lock: a host that sends from other threads takes the lock its audio thread takes around <see cref="Push"/>), events are raised on the thread that made the call that
/// caused them, and an event handler must not call any of these members (they throw <see cref="InvalidOperationException"/>, as they do from a timer action).
/// The <i>worker</i> members are <see cref="RunOne"/>, <see cref="RunPending"/> and <see cref="HasWork"/>; they may be called from any number of threads at the same time, and from the audio thread too when it is not inside a call of its own (a single-threaded host can run its own jobs between pushes),
/// each call using its own pair of receivers (made on first use, one pair for each thread running a job at that moment, and kept). The <see cref="WorkAvailable"/> event is raised on the audio thread
/// and its handler must only signal. A worker call never raises an event, never calls the adaptation rules and never touches the clock or anything else the audio thread owns.
/// Forbidden: two audio-thread members running at once on one station, any of them called from an event handler or a timer action, and expecting an inline station to give workers anything to do
/// (its <see cref="HasWork"/> is false and <see cref="RunOne"/> returns false).
/// </para>
/// <para>
/// <b>Reports.</b> A burst addressed to this station that failed to decode owes its sender a lone report marked failed, sent as soon as the channel has been clear for the first slot's time (50 ms), however late the verdict is.
/// One that decoded owes a lone report, in the first slot, if the recommendation for its sender fell below what that sender was last told; and one that decoded with "report wanted" in its prelude owes the recommendation, which rides on whatever
/// the host sends back to that station and goes alone if 1.5 s from the burst's end pass with nothing going back. Nothing is owed for a burst addressed to everyone or to another station. A lone report opens the way this station's own data to
/// that station would and at the same drive, and raises <see cref="ModemSent"/>. After one, the station that sent it goes straight on with its own data if it is not still waiting for an answer, and the station that heard it leaves the channel clear for about a second.
/// A report heard satisfies the answer wait and, if it says failed, raises <see cref="BurstNotDecoded"/> with the datagrams of the latest burst sent to that station that ended before the report began.
/// </para>
/// <para>
/// <b>Identification.</b> The callsign (given by the host with <see cref="SetIdentity"/>; the station does not transmit until it has one: 1 to 16 characters, upper-case letters, digits, '/' and '-') goes on the air, and the clock that decides when runs on the sample clock only while the station transmits:
/// an identification is due when a transmission of the station's ends and either it has never identified or ten minutes have passed since its last identification started: at once after the first transmission of a quiet station, and after the first transmission to end past the ten-minute mark for a station that keeps transmitting. Transmissions sooner are covered by the last identification, and an idle station never keys to identify. See <c>docs/station-design.md</c>, "Step 7 as built".
/// </para>
/// <para>
/// <b>Numbers.</b> The station's on-air number (1 to 63) and its station id (32 random bits) are its own business: it takes them, from its random source, the first time <see cref="SetIdentity"/> is accepted (a number not heard as a sender in the last ten minutes of the
/// sample clock), carries the id in every identification burst, takes new ones when it reads a header with its own number that it did not send or decodes a Clash that names its number and id, and, as a third station, resets what it knew of a number whose id changed and sends a
/// Clash for the later id when the earlier was heard inside ten minutes. No member of the host interface shows a number or an id. A burst built before a move is built again. See docs/station-numbers.md.
/// </para>
/// </summary>
public sealed partial class Station
{
    /// <summary>The sample rate the clock counts at, Hz.</summary>
    public const int SampleRate = 48000;

    private sealed class Request
    {
        public TransmitState State;
        public long EarliestSample;
        public long? StartedAt;
        public long? EndedAt;
        public bool WithdrawnBeforeStart;
    }

    private readonly Random _random;
    private readonly SampleTimers _timers = new();
    private readonly Dictionary<long, Request> _requests = [];
    private readonly Stack<Request> _freeRequests = new();
    private readonly List<long> _forget = [];
    private long _nextRequestId = 1;
    private long? _transmittingId;
    private bool _advancing;

    /// <summary>Makes a station. It has no number until the host gives it an identity with <see cref="SetIdentity"/>: it then picks one by itself (see <see cref="SetIdentity"/>).</summary>
    /// <param name="random">The source of every random choice the station makes, its number and station id included. The caller seeds it; one seed and the same calls give the same run.</param>
    /// <param name="runJobsInline">False (the default): header reads and receives are jobs for <see cref="RunOne"/>, run by worker threads the caller owns. True: <see cref="Push"/> runs every job itself, before it returns; for tests and simple hosts that can afford the time (a receive takes a good part of a burst's own length on a slow processor).</param>
    public Station(Random random, bool runJobsInline = false)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
        _inline = runJobsInline;
        Array.Fill(_senderHeardAt, long.MinValue);
        _onClashReady = EnsureClashRequest;
        InitialiseReceive();
        InitialiseAccess();
    }

    /// <summary>
    /// A test's seam: a station whose first number and station id are fixed, taken when it is given its identity, so that a scenario can have stations with the numbers it names (and two with one number). Later numbers (after a clash) are chosen by the rule.
    /// Not for a host: no host sees or chooses a number.
    /// </summary>
    internal Station(int number, uint stationId, Random random, bool runJobsInline = false)
        : this(random, runJobsInline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 63);
        _forcedNumber = number;
        _forcedId = stationId;
    }

    /// <summary>The same, with a station id made from the number (so that stations of different numbers have different ids).</summary>
    internal Station(int number, Random random, bool runJobsInline = false)
        : this(number, 0xC0DE0000u | (uint)number, random, runJobsInline)
    {
    }

    /// <summary>The callsign the station identifies with (the transmitter identifier), or null until the host gives it with <see cref="SetIdentity"/>. A station without one is a listener only.</summary>
    public string? Callsign { get; private set; }

    /// <summary>
    /// Gives the station the callsign it identifies with. Until it has one the station receives, measures and reports as usual but never transmits: <see cref="Send"/> returns <see cref="SendResult.NoIdentity"/>, no report is owed
    /// and <see cref="TransmitRequest"/> is never raised. The callsign is 1 to 16 characters, upper-case letters, digits, '/' and '-'. Giving the same one again is accepted and changes nothing; a different one is refused
    /// (<see cref="IdentityResult.Different"/>): to change callsign, make a new station. There is no way to clear it. It is not an address: several stations may use the same one.
    /// The first time it is accepted the station takes its on-air number and makes up its station id (docs/station-numbers.md, "Station numbers"); until then it has neither, and no burst is for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public IdentityResult SetIdentity(string callsign)
    {
        EnsureNotAdvancing();
        if (callsign is null || !PayloadCodec.IsValidCallsign(callsign))
        {
            return IdentityResult.Invalid;
        }

        if (Callsign is null)
        {
            Callsign = callsign;
            if (WantsFlow())
            {
                Flow(TraceText.CallsignSet(callsign));
            }

            TakeFirstNumber();
            return IdentityResult.Accepted;
        }

        return Callsign == callsign ? IdentityResult.Accepted : IdentityResult.Different;
    }

    /// <summary>The station's on-air number, 1 to 63, or 0 while it has no identity. Not part of the host interface: no host sees a station number.</summary>
    internal int StationNumber => _number;

    /// <summary>The station's id: 32 random bits made up with the number, sent in every identification burst. Zero while it has no identity. Not part of the host interface.</summary>
    internal uint StationId => _stationId;

    /// <summary>The sample clock: receive samples so far, counting those the audio layer reported dropped.</summary>
    public long SampleCount { get; private set; }

    /// <summary>The sample clock in seconds: <see cref="SampleCount"/> over <see cref="SampleRate"/>.</summary>
    public double Seconds => SampleCount / (double)SampleRate;

    /// <summary>Raised to ask the audio layer to transmit.</summary>
    public event Action<TransmitRequestEvent>? TransmitRequest;

    /// <summary>Raised to take back a request that has not been keyed.</summary>
    public event Action<WithdrawRequestEvent>? WithdrawRequest;

    /// <summary>
    /// Raised for each datagram received, addressed to this station or to everyone, in the order the bursts started. The datagram's memory is valid only during the call: copy it to keep it.
    /// The event says how the burst was addressed, and the For key when it was for an address not yet located. The station does not filter: a burst to everyone is delivered whether or not it has a For.
    /// </summary>
    public event Action<ReceivedEvent>? Received;

    /// <summary>
    /// Raised when a report for this station is heard, alone or in the prelude of a data burst that gives a recommendation. Raised once for each address the host has sent to that lives at the station that sent the report.
    /// </summary>
    public event Action<PathReportEvent>? PathReport;

    /// <summary>Raised when what the station knows of a neighbour changes (its format, the room in a burst or where the format came from), and when it is forgotten after ten minutes without a word from it. Raised for each address the host has sent to (when it first sends to it, and when what is known of it changes), not located included.</summary>
    public event Action<LinkStateEvent>? LinkState;

    /// <summary>
    /// Raised when the channel becomes busy or clear, on the audio thread, in the call that caused it (<see cref="Push"/>, <see cref="Skip"/>, <see cref="DeliverResults"/>, <see cref="SetExternalBusy"/>, <see cref="TransmitStarted"/>, <see cref="TransmitEnded"/> or <see cref="TransmitRefused"/>),
    /// with the clock at the exact sample the rule gives. Never twice running with the same value. The union of what the station hears of other stations (see the class's documentation), its own transmission and the host's carrier indication.
    /// </summary>
    public event Action<ChannelBusyEvent>? ChannelBusy;

    /// <summary>Raised on the audio thread when a job becomes available to <see cref="RunOne"/>. The handler must only signal a worker (a semaphore, say); it must not run work or call the station.</summary>
    public event Action? WorkAvailable;

    /// <summary>
    /// Raised once for each datagram, when the burst carrying it has ended on the air (the audio layer's <see cref="TransmitEnded"/>): its tag, the link address it was for, the burst number, the format, the format's rate, the time the burst was on air
    /// (from the audio layer's reported start to its reported end, lead-in included) and the sample clock at the end. Raised on the audio thread, in the call to <see cref="TransmitEnded"/>. Not raised for a datagram that was dropped.
    /// </summary>
    public event Action<TransmittedEvent>? Transmitted;

    /// <summary>
    /// Raised for a datagram that will not go out: cancelled by the host (<see cref="Cancel(HostTag)"/>, <see cref="Cancel(LinkAddress)"/>), or no longer fitting one burst after the link's format fell. On the thread that made the call that caused it.
    /// A datagram never vanishes silently: each accepted one gets exactly one <see cref="Transmitted"/> or one <see cref="Dropped"/>.
    /// </summary>
    public event Action<DroppedEvent>? Dropped;

    /// <summary>
    /// Raised when the queue to a station becomes full (<see cref="CongestionEvent.Writable"/> false: <see cref="Send"/> is refused with <see cref="SendResult.QueueFull"/>) and when it has drained enough to take datagrams again (true).
    /// On the thread that made the call that caused it.
    /// </summary>
    public event Action<CongestionEvent>? Congestion;

    /// <summary>Raised when a burst the host did not ask for goes on air: a lone report (identification, step 7, is not yet sent). On the audio thread, in the call to <see cref="TransmitStarted"/>.</summary>
    public event Action<ModemSentEvent>? ModemSent;

    /// <summary>
    /// Raised when a station reports that it heard a burst of this station's and could not decode it: the tags of the datagrams in the latest burst this station sent to it that ended before the report began, once for that burst.
    /// A hint: the library resends nothing. Raised on the audio thread, in the call that applies the report (<see cref="Push"/>, <see cref="Skip"/> or <see cref="DeliverResults"/>), after <see cref="PathReport"/>.
    /// </summary>
    public event Action<BurstNotDecodedEvent>? BurstNotDecoded;

    /// <summary>
    /// Raised when the station's identification is due: a transmission of the station's has ended and either it has never identified or ten minutes of the sample clock have passed since the last identification started; raised at that end, when the station is not keyed (see <c>docs/station-design.md</c>, "Step 7 as built"), so that the audio layer can key its Morse ident.
    /// The identification burst is queued at the same moment and raises <see cref="ModemSent"/> when it goes on air. Once for each identification, on the audio thread, in the call to <see cref="TransmitEnded"/>.
    /// </summary>
    public event Action<IdentificationDueEvent>? IdentificationDue;

    /// <summary>
    /// Raised when an identification burst from another station has been decoded and read, in the order the bursts started: its callsign and its capability word (see <see cref="IdentifiedEvent"/>).
    /// Not raised for a burst that did not decode or whose payload is not an identification. A datagram is never raised for it: <see cref="Received"/> is for datagrams.
    /// </summary>
    public event Action<IdentifiedEvent>? Identified;

    /// <summary>
    /// Takes received audio of any length and advances the clock by that many samples. Timers due within the block run, in order, each at its own sample:
    /// the clock reads exactly the due index while the action runs, not the end of the block. A timer due at index N runs once N samples have been received.
    /// The audio goes into the ring and the detector, which is the work of correlating a frame once every 0.42 s of audio (about 1 ms on a desktop processor); header reads and receives are jobs,
    /// not done here unless the station was made inline. Results ready to be applied are applied before it returns, and raise their events here.
    /// Allocates nothing when no timer runs and no burst is received.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside a timer action.</exception>
    public void Push(ReadOnlySpan<float> samples) => Advance(samples, samples.Length);

    /// <summary>
    /// The audio layer dropped <paramref name="count"/> samples: the clock advances by that many, silence goes through the ring and the detector in their place, and timers passed run at their own samples, as for <see cref="Push"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Called from inside a timer action.</exception>
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Advance(default, count);
    }

    /// <summary>
    /// The audio layer has keyed request <paramref name="id"/>, at receive-sample <paramref name="sampleIndex"/> (not before the request's earliest, not after the clock now).
    /// Tolerated race: a request the station has just withdrawn may still start, because the audio layer may have keyed it before it saw the withdrawal; the station then counts as
    /// transmitting.
    /// </summary>
    /// <exception cref="InvalidOperationException">The id was never issued or is long finished, the request has started already, another transmission is on, or the index is out of range.</exception>
    public void TransmitStarted(long id, long sampleIndex)
    {
        EnsureNotAdvancing();
        Request request = Find(id, nameof(TransmitStarted));
        if (request.State is TransmitState.Started or TransmitState.Ended)
        {
            throw new InvalidOperationException($"Request {id} has already started.");
        }

        if (_transmittingId is { } on)
        {
            throw new InvalidOperationException($"Request {id} cannot start while request {on} is still transmitting.");
        }

        if (sampleIndex < request.EarliestSample)
        {
            throw new InvalidOperationException($"Request {id} started at sample {sampleIndex}, before its earliest, {request.EarliestSample}.");
        }

        if (sampleIndex > SampleCount)
        {
            throw new InvalidOperationException($"Request {id} started at sample {sampleIndex}, which is in the future (the clock is {SampleCount}).");
        }

        request.WithdrawnBeforeStart = request.State == TransmitState.Withdrawn;
        request.State = TransmitState.Started;
        request.StartedAt = sampleIndex;
        _transmittingId = id;
        OnTransmitStarted(id, request, sampleIndex);

        // The audio layer answers in the order of the requests, so anything older that is over and done with can be forgotten (and its record reused: a station that sends all day allocates nothing for these).
        _forget.Clear();
        foreach (KeyValuePair<long, Request> old in _requests)
        {
            if (old.Key < id && old.Value.State is TransmitState.Ended or TransmitState.Withdrawn)
            {
                _forget.Add(old.Key);
            }
        }

        foreach (long key in _forget)
        {
            _freeRequests.Push(_requests[key]);
            _requests.Remove(key);
        }
    }

    /// <summary>The audio layer has finished request <paramref name="id"/>, at receive-sample <paramref name="sampleIndex"/> (not before the start, not after the clock now).</summary>
    /// <exception cref="InvalidOperationException">The id was never issued or is long finished, the request has not started (ended before started, even after a withdrawal), it has ended already, or the index is out of range.</exception>
    public void TransmitEnded(long id, long sampleIndex)
    {
        EnsureNotAdvancing();
        Request request = Find(id, nameof(TransmitEnded));
        switch (request.State)
        {
            case TransmitState.Requested:
            case TransmitState.Withdrawn:
                throw new InvalidOperationException($"Request {id} ended before it started.");
            case TransmitState.Ended:
                throw new InvalidOperationException($"Request {id} has already ended.");
        }

        if (sampleIndex < request.StartedAt)
        {
            throw new InvalidOperationException($"Request {id} ended at sample {sampleIndex}, before it started, {request.StartedAt}.");
        }

        if (sampleIndex > SampleCount)
        {
            throw new InvalidOperationException($"Request {id} ended at sample {sampleIndex}, which is in the future (the clock is {SampleCount}).");
        }

        request.State = TransmitState.Ended;
        request.EndedAt = sampleIndex;
        _transmittingId = null;
        OnTransmitEnded(id, sampleIndex);
    }

    /// <summary>True while a request has been started and not ended: the station is transmitting.</summary>
    internal bool IsTransmitting => _transmittingId is not null;

    /// <summary>How many timers are waiting.</summary>
    internal int PendingTimers => _timers.Count;

    /// <summary>The state of a request the station still remembers, or null.</summary>
    internal TransmitState? StateOf(long id) => _requests.TryGetValue(id, out Request? r) ? r.State : null;

    /// <summary>When a request started, if it has.</summary>
    internal long? StartedAt(long id) => _requests.TryGetValue(id, out Request? r) ? r.StartedAt : null;

    /// <summary>When a request ended, if it has.</summary>
    internal long? EndedAt(long id) => _requests.TryGetValue(id, out Request? r) ? r.EndedAt : null;

    /// <summary>True if a request started after the station had withdrawn it (the tolerated race).</summary>
    internal bool StartedAfterWithdrawal(long id) => _requests.TryGetValue(id, out Request? r) && r.WithdrawnBeforeStart;

    /// <summary>
    /// Asks the audio layer to transmit <paramref name="samples"/> at receive-sample <paramref name="earliestSample"/> or after, and returns the request's id.
    /// This is the handshake with the audio layer, below the channel-access rules: <see cref="Enqueue"/> is what applies them and what step 5 calls; this one asks at once, whatever the channel is doing.
    /// </summary>
    internal long RequestTransmit(ReadOnlyMemory<float> samples, long earliestSample)
    {
        // The one place a request is put to the audio layer: a station with no identity never transmits. The specific checks (Send, the verdicts, identification) keep it from getting here.
        if (Callsign is null)
        {
            throw new InvalidOperationException("A station with no identity does not transmit: give it one with SetIdentity.");
        }

        long id = _nextRequestId++;
        Request request = _freeRequests.Count > 0 ? _freeRequests.Pop() : new Request();
        request.State = TransmitState.Requested;
        request.EarliestSample = earliestSample;
        request.StartedAt = null;
        request.EndedAt = null;
        request.WithdrawnBeforeStart = false;
        _requests[id] = request;
        TransmitRequest?.Invoke(new TransmitRequestEvent(id, samples, earliestSample));
        return id;
    }

    /// <summary>Takes back a request that has not started. A request that has started cannot be recalled.</summary>
    /// <exception cref="InvalidOperationException">The request is unknown, or has started, ended or been withdrawn already.</exception>
    internal void WithdrawTransmit(long id)
    {
        Request request = Find(id, nameof(WithdrawTransmit));
        if (request.State != TransmitState.Requested)
        {
            throw new InvalidOperationException($"Request {id} is {request.State.ToString().ToLowerInvariant()} and cannot be withdrawn.");
        }

        request.State = TransmitState.Withdrawn;
        WithdrawRequest?.Invoke(new WithdrawRequestEvent(id));
    }

    /// <summary>
    /// Schedules <paramref name="action"/> at absolute sample index <paramref name="sampleIndex"/>. An index already passed (or now) runs at the start of the next
    /// <see cref="Push"/> or <see cref="Skip"/>, with the clock where it stands; it never runs inside this call.
    /// </summary>
    internal TimerHandle ScheduleAt(long sampleIndex, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _timers.Schedule(sampleIndex, action);
    }

    /// <summary>Schedules <paramref name="action"/> <paramref name="delaySamples"/> from now (the clock at this call, or at its due sample if made from a timer action).</summary>
    internal TimerHandle ScheduleIn(long delaySamples, Action action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delaySamples);
        return ScheduleAt(SampleCount + delaySamples, action);
    }

    /// <summary>
    /// Schedules <paramref name="action"/> after <paramref name="delaySamples"/> plus a random 0 to <paramref name="jitterSamples"/> more, drawn now from the station's
    /// random source. This is the one use of the source in step 1, so that a test can see that a seed fixes a run and another seed changes it.
    /// </summary>
    internal TimerHandle ScheduleJittered(long delaySamples, long jitterSamples, Action action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitterSamples);
        return ScheduleIn(delaySamples + _random.NextInt64(jitterSamples + 1), action);
    }

    /// <summary>Cancels a timer. False if it has run or been cancelled.</summary>
    internal bool CancelTimer(TimerHandle handle) => _timers.Cancel(handle);

    private Request Find(long id, string call)
    {
        if (_requests.TryGetValue(id, out Request? request))
        {
            return request;
        }

        throw new InvalidOperationException(id >= 1 && id < _nextRequestId
            ? $"{call}: request {id} is finished and forgotten."
            : $"{call}: request {id} was never issued.");
    }

    private void EnsureNotAdvancing()
    {
        if (_advancing)
        {
            throw new InvalidOperationException("The station cannot be called from inside a timer action.");
        }
    }

    /// <summary>
    /// Advances the clock by <paramref name="length"/> samples, stopping at each due timer so that its action sees the clock at its own index.
    /// <paramref name="samples"/> is the audio for them, or empty for a skip (silence is fed in its place). Actions scheduled for an index already reached run in the same pass.
    /// Results of the receive jobs that are ready are applied last, in order.
    /// </summary>
    private void Advance(ReadOnlySpan<float> samples, int length)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            long end = SampleCount + length;
            int offset = 0;
            RunDue();
            while (SampleCount < end)
            {
                long stop = _timers.NextDue is { } due && due < end ? Math.Max(due, SampleCount) : end;
                int take = (int)(stop - SampleCount);
                if (take > 0)
                {
                    FeedAudio(samples.IsEmpty ? default : samples.Slice(offset, take), take);
                    offset += take;
                }

                RunDue();
            }

            Pump();
            ApplyReadyResults();
        }
        finally
        {
            _advancing = false;
        }
    }

    private void RunDue()
    {
        while (_timers.TryTakeDue(SampleCount, out Action? action))
        {
            action();
        }
    }
}
