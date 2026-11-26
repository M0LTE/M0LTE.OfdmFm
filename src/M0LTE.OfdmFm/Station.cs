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
/// The live station: one per radio channel. So far (steps 1 to 3 of docs/station-design.md) it keeps a sample clock with timers on it, runs the transmit handshake with the audio layer,
/// and receives: audio pushed in comes out as <see cref="Received"/> datagrams, <see cref="PathReport"/> and <see cref="LinkState"/> events and calls on the adaptation rules.
/// It does not yet send, decide the channel is busy, or identify.
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
/// <b>Threads.</b> A station has no threads of its own. Two groups of members exist.
/// The <i>audio-thread</i> members are <see cref="Push"/>, <see cref="Skip"/>, <see cref="DeliverResults"/>, <see cref="TransmitStarted"/>, <see cref="TransmitEnded"/> and the properties
/// <see cref="SampleCount"/> and <see cref="Seconds"/>; the caller must serialise every call to them on one station (one thread, or a lock), events are raised on the thread that made the call that
/// caused them, and an event handler must not call any of these members (they throw <see cref="InvalidOperationException"/>, as they do from a timer action).
/// The <i>worker</i> members are <see cref="RunOne"/>, <see cref="RunPending"/> and <see cref="HasWork"/>; they may be called from any number of threads at the same time, and from the audio thread too when it is not inside a call of its own (a single-threaded host can run its own jobs between pushes),
/// each call using its own pair of receivers (made on first use, one pair for each thread running a job at that moment, and kept). The <see cref="WorkAvailable"/> event is raised on the audio thread
/// and its handler must only signal. A worker call never raises an event, never calls the adaptation rules and never touches the clock or anything else the audio thread owns.
/// Forbidden: two audio-thread members running at once on one station, any of them called from an event handler or a timer action, and expecting an inline station to give workers anything to do
/// (its <see cref="HasWork"/> is false and <see cref="RunOne"/> returns false).
/// </para>
/// <para>
/// <b>Not yet here.</b> The host operations arrive in later steps and are left out of the public surface until then, so that the API snapshot says what works:
/// <c>Send(ReadOnlyMemory&lt;byte&gt; datagram, StationName name, HostTag tag, bool reportWanted, SendPriority priority)</c>, <c>Cancel(HostTag)</c>, <c>Cancel(StationName)</c>,
/// <c>Snapshot()</c>, <c>SetExternalBusy(bool)</c> and <c>Bind(StationName, int)</c>. The events <see cref="Transmitted"/>, <see cref="Dropped"/>, <see cref="BurstNotDecoded"/>, <see cref="ChannelBusy"/>,
/// <see cref="Congestion"/>, <see cref="ModemSent"/> and <see cref="IdentificationDue"/> are declared and not yet raised.
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
    private long _nextRequestId = 1;
    private long? _transmittingId;
    private bool _advancing;

    /// <summary>Makes a station.</summary>
    /// <param name="callsign">The station's callsign. Required.</param>
    /// <param name="stationNumber">Its on-air station number, 1 to 63.</param>
    /// <param name="random">The source of every random choice the station makes. The caller seeds it; one seed and the same calls give the same run.</param>
    /// <param name="runJobsInline">False (the default): header reads and receives are jobs for <see cref="RunOne"/>, run by worker threads the caller owns. True: <see cref="Push"/> runs every job itself, before it returns; for tests and simple hosts that can afford the time (a receive takes a good part of a burst's own length on a slow processor).</param>
    /// <exception cref="ArgumentException">The callsign is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The station number is outside 1 to 63.</exception>
    public Station(string callsign, int stationNumber, Random random, bool runJobsInline = false)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        ArgumentOutOfRangeException.ThrowIfLessThan(stationNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stationNumber, 63);
        ArgumentNullException.ThrowIfNull(random);
        Callsign = callsign;
        StationNumber = stationNumber;
        _random = random;
        _inline = runJobsInline;
        InitialiseReceive();
    }

    /// <summary>The station's callsign.</summary>
    public string Callsign { get; }

    /// <summary>The station's on-air number.</summary>
    public int StationNumber { get; }

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
    /// The sender's name is null until a later step binds names; <see cref="ReceivedEvent.SenderNumber"/> always says who.
    /// </summary>
    public event Action<ReceivedEvent>? Received;

    /// <summary>
    /// Raised when a report for this station is heard, alone or in the prelude of a data burst that gives a recommendation. Until names are bound the station is named by <see cref="PlaceholderName"/>.
    /// </summary>
    public event Action<PathReportEvent>? PathReport;

    /// <summary>Raised when what the station knows of a neighbour changes (its format, the room in a burst or where the format came from), and when it is forgotten after ten minutes without a word from it. Named as <see cref="PathReport"/> names them.</summary>
    public event Action<LinkStateEvent>? LinkState;

    /// <summary>Raised on the audio thread when a job becomes available to <see cref="RunOne"/>. The handler must only signal a worker (a semaphore, say); it must not run work or call the station.</summary>
    public event Action? WorkAvailable;

    // Declared now so that the host interface is complete; later steps raise them and this pragma goes.
#pragma warning disable CS0067
    /// <summary>Declared, not yet raised: a datagram's burst has left the audio device.</summary>
    public event Action<TransmittedEvent>? Transmitted;

    /// <summary>Declared, not yet raised: a datagram will not go out.</summary>
    public event Action<DroppedEvent>? Dropped;

    /// <summary>Declared, not yet raised: a burst of this station's was heard and not decoded.</summary>
    public event Action<BurstNotDecodedEvent>? BurstNotDecoded;

    /// <summary>Declared, not yet raised: the channel became busy or clear.</summary>
    public event Action<ChannelBusyEvent>? ChannelBusy;

    /// <summary>Declared, not yet raised: push-back for a slow link.</summary>
    public event Action<CongestionEvent>? Congestion;

    /// <summary>Declared, not yet raised: a burst the host did not ask for went on air.</summary>
    public event Action<ModemSentEvent>? ModemSent;

    /// <summary>Declared, not yet raised: the identification interval has run out.</summary>
    public event Action<IdentificationDueEvent>? IdentificationDue;
#pragma warning restore CS0067

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

        // The audio layer answers in the order of the requests, so anything older that is over and done with can be forgotten.
        foreach (long old in _requests.Keys.Where(k => k < id && _requests[k].State is TransmitState.Ended or TransmitState.Withdrawn).ToList())
        {
            _requests.Remove(old);
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
    /// Later steps call this for real bursts; in this step tests do.
    /// </summary>
    internal long RequestTransmit(ReadOnlyMemory<float> samples, long earliestSample)
    {
        long id = _nextRequestId++;
        _requests[id] = new Request { State = TransmitState.Requested, EarliestSample = earliestSample };
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
