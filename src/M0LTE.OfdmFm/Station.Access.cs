namespace M0LTE.OfdmFm;

// Step 4 of docs/station-design.md: the station knows when the channel is busy and decides when it may transmit.
// The rules are in ChannelAccess; this file joins them to the station's clock, receive path and audio-layer handshake.

public sealed partial class Station
{
    /// <summary>
    /// Candidates whose first sample lies from this many samples before a transmission's start (a symbol) are ignored: the audio is muted or full of the station's own signal.
    /// The design's "a pad before"; the figure is a starting value.
    /// </summary>
    internal const int MuteBeforeOwnTransmission = BurstRenderer.SymbolSamples;

    /// <summary>
    /// Candidates up to this many samples after a transmission ends (100 ms) are ignored, as above: the same figure as the tail the channel stays busy after the station's own transmission, <see cref="ChannelAccess.OwnTail"/>,
    /// so that the two are one figure. It must be shorter than the first slot's clear time plus the lead-in (<see cref="ChannelAccess.FirstSlotClear"/> and <see cref="BurstRenderer.DefaultLeadSamples"/>: 2400 + 4224, 138 ms):
    /// a reply in the first slot has its first training sample that long after the end of the burst it answers, and a longer mute makes the station deaf to exactly the replies the slot exists for. The margin is 38 ms.
    /// Step 4 had the design's 0.25 s here, which step 5 found to be that fault. How long a real radio takes to hear again is packet-net/pdn-soundmodem#891's question.
    /// </summary>
    internal const int MuteAfterOwnTransmission = ChannelAccess.OwnTail;

    private ChannelAccess _access = null!;
    private readonly List<(long Start, long End)> _ownWindows = [];
    private readonly Dictionary<long, ChannelAccess.Request> _byHandshake = [];
    private long _candidatesMuted;

    // Candidates whose header would not read, held back until every candidate waiting has been judged (see SettleUnreadable).
    private readonly List<(long Index, long Until)> _unreadable = [];

    /// <summary>A change in an access request: granted, withdrawn and queued again, started, ended. Raised on the audio thread. Step 5 reads it; tests do now.</summary>
    internal event Action<AccessEvent>? AccessChanged;

    /// <summary>A burst or hold the station learned of: what the rules decided (a test's hook, called as a header is taken in).</summary>
    internal Action<long, long>? BurstKnown { get; set; }

    /// <summary>Candidates ignored because they fell in the window of the station's own transmission.</summary>
    internal long CandidatesMuted => _candidatesMuted;

    /// <summary>The union of the causes: true while the channel is busy, as of the last event.</summary>
    internal bool IsChannelBusy => _access.Busy;

    /// <summary>The sample the channel was last busy until, which the waits are counted from (the 5 s start-up listen counted in).</summary>
    internal long ClearSince => _access.ClearSince;

    /// <summary>The station's own time to notice a carrier, in samples: one slot of the random wait.</summary>
    internal int NoticeSamples => _access.NoticeSamples;

    /// <summary>The longest burst detection takes to notice a burst's start, in samples, from the detector's sizes (0.903 s).</summary>
    internal static int DetectionLatencySamples => ChannelAccess.DetectionLatency;

    /// <summary>The rule for ordinary traffic's random wait. The station's is slotted; a test can set the design's original single delay to compare.</summary>
    internal AccessMode AccessMode
    {
        get => _access.Mode;
        set => _access.Mode = value;
    }

    /// <summary>The draw for the current clear period (slots, or samples for the single delay), or -1 before one is made.</summary>
    internal int AccessDraw => _access.Draw;

    /// <summary>Requests waiting for their turn.</summary>
    internal int AccessWaiting => _access.WaitingCount;

    /// <summary>
    /// The host's carrier indication (a radio's squelch or busy line) changed: the channel is busy while it is set and for 100 ms after it drops. The first call with a value, set or clear, tells the station the host supplies one,
    /// so that its slot for the random wait becomes 100 ms and not the detection latency (see the class's documentation). Null says the indication is no longer available (the radio stopped answering): the station forgets it had one,
    /// the channel is no longer held busy by it (it counts as busy up to this moment, no further), and the slot is the detection latency again until a value arrives. Audio thread, serialised with <see cref="Push"/>; the change takes effect at the clock's present sample.
    /// </summary>
    /// <param name="busy">True: carrier present. False: none. Null: no indication available.</param>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public void SetExternalBusy(bool? busy)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            if (WantsDetail())
            {
                Detail(TraceText.CarrierFromRadio(busy));
            }

            _access.SetExternal(busy);
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// The audio layer will not transmit request <paramref name="id"/>, which it was handed by <see cref="TransmitRequest"/> and has not keyed (a failed key-up, a dead sound device, the transmitter given to something else in the gap).
    /// The station keeps what it built, exactly as after its own <see cref="WithdrawRequest"/> (no event is raised: the layer already knows), and the request goes back to waiting. The refusal counts as the channel having been busy up to now,
    /// so the request is handed over again, unchanged, only after the usual clear time for its class and slots: a layer that keeps refusing is asked once for each wait, not in a loop.
    /// An id that is not the request outstanding is ignored and changes nothing: one never issued or already forgotten, one that has started or ended, one the station has withdrawn or that was refused already, and one handed over
    /// again since (an older id of the same burst). A request refused and then keyed anyway is treated as a withdrawn one that started (the tolerated race). Audio thread, serialised with <see cref="Push"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public void TransmitRefused(long id)
    {
        EnsureNotAdvancing();
        if (!_requests.TryGetValue(id, out Request? handshake) || handshake.State != TransmitState.Requested)
        {
            return;
        }

        if (!_byHandshake.TryGetValue(id, out ChannelAccess.Request? request) || request.HandshakeId != id || !ReferenceEquals(_access.Active, request))
        {
            return;
        }

        handshake.State = TransmitState.Withdrawn;
        _advancing = true;
        try
        {
            _access.Refused(request);
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// Asks to transmit <paramref name="samples"/> as <paramref name="cls"/> when the rules allow, and returns the request's id. The station raises <see cref="TransmitRequest"/> when it grants it, and <see cref="AccessChanged"/> for each
    /// change (granted, withdrawn and queued again, started, ended). Audio thread, serialised with <see cref="Push"/>; the request may be granted before this returns.
    /// </summary>
    /// <param name="samples">What to play.</param>
    /// <param name="cls">The wait it needs.</param>
    /// <param name="modem">For a burst the host did not ask for: the <see cref="ModemSent"/> event raised when it goes on air. Steps 6 and 7 use it.</param>
    /// <exception cref="ArgumentException">No samples.</exception>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    internal long Enqueue(ReadOnlyMemory<float> samples, TransmitClass cls, ModemSentEvent? modem = null)
    {
        EnsureNotAdvancing();
        if (samples.IsEmpty)
        {
            throw new ArgumentException("A transmission needs samples.", nameof(samples));
        }

        _advancing = true;
        try
        {
            return _access.Enqueue(samples, cls, modem).Id;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>Takes in a burst the station knows of, as a header read does: busy to <paramref name="end"/>, which may already be past. For tests that need a burst without the audio.</summary>
    internal void NoteBurst(long end)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            BurstKnown?.Invoke(SampleCount, end);
            _access.HoldUntil(end);
        }
        finally
        {
            _advancing = false;
        }
    }

    private sealed class AccessClock(Station station) : IChannelClock
    {
        public long Now => station.SampleCount;

        public TimerHandle ScheduleAt(long sample, Action action) => station.ScheduleAt(sample, action);

        public bool CancelTimer(TimerHandle handle) => station.CancelTimer(handle);
    }

    private void InitialiseAccess()
    {
        _access = new ChannelAccess(
            new AccessClock(this),
            _random,
            OnChannelBusy,
            GrantRequest,
            request => WithdrawTransmit(request.HandshakeId),
            e => AccessChanged?.Invoke(e));
        _access.Note = OnAccessNote;
        InitialiseSend();
    }

    private void OnChannelBusy(bool busy)
    {
        ChannelBusy?.Invoke(new ChannelBusyEvent(busy));
        TraceChannel(busy);
    }

    private void GrantRequest(ChannelAccess.Request request)
    {
        request.HandshakeId = RequestTransmit(request.Samples, SampleCount);
        _byHandshake[request.HandshakeId] = request;
    }

    // ---- what the audio layer's reports mean for the channel ----

    private void OnTransmitStarted(long id, Request handshake, long sampleIndex)
    {
        if (!_byHandshake.TryGetValue(id, out ChannelAccess.Request? request))
        {
            return;
        }

        bool late = handshake.WithdrawnBeforeStart;
        if (ReferenceEquals(request, _identRequest))
        {
            OnIdentificationBurstStarted(sampleIndex);
        }

        if (ReferenceEquals(request, _data))
        {
            OnDataBurstStarted(sampleIndex);
        }
        else if (ReferenceEquals(request, _reportRequest))
        {
            OnReportBurstStarted();
        }
        else if (ReferenceEquals(request, _clashRequest))
        {
            OnClashBurstStarted();
        }

        if (request.Modem is { } modem)
        {
            ModemSentTo = ReferenceEquals(request, _reportRequest) ? _report.Dest : 0;
            ModemSent?.Invoke(modem);
        }

        _ownWindows.Add((sampleIndex - MuteBeforeOwnTransmission, long.MaxValue));
        PauseEstimateOver(sampleIndex, sampleIndex + request.Samples.Length + MuteAfterOwnTransmission);

        _advancing = true;
        try
        {
            if (late && request.State == ChannelAccess.RequestState.Granted && request.HandshakeId != id && StateOf(request.HandshakeId) == TransmitState.Requested)
            {
                // It was withdrawn and granted again, and the first hand-over started anyway: the second is not wanted.
                WithdrawTransmit(request.HandshakeId);
            }

            request.HandshakeId = id;
            _access.Started(request, sampleIndex, late);
        }
        finally
        {
            _advancing = false;
        }
    }

    private void OnTransmitEnded(long id, long sampleIndex)
    {
        if (!_byHandshake.TryGetValue(id, out ChannelAccess.Request? request))
        {
            return;
        }

        for (int i = _ownWindows.Count - 1; i >= 0; i--)
        {
            if (_ownWindows[i].End == long.MaxValue)
            {
                _ownWindows[i] = (_ownWindows[i].Start, sampleIndex + MuteAfterOwnTransmission);
                break;
            }
        }

        _forget.Clear();
        foreach (KeyValuePair<long, ChannelAccess.Request> kv in _byHandshake)
        {
            if (ReferenceEquals(kv.Value, request))
            {
                _forget.Add(kv.Key);
            }
        }

        foreach (long key in _forget)
        {
            _byHandshake.Remove(key);
        }

        _advancing = true;
        try
        {
            _access.Ended(request, sampleIndex);
            if (ReferenceEquals(request, _data))
            {
                OnDataBurstEnded(sampleIndex);
            }
            else if (ReferenceEquals(request, _reportRequest))
            {
                OnReportBurstEnded(sampleIndex);
            }
            else if (ReferenceEquals(request, _identRequest))
            {
                OnIdentificationBurstEnded();
            }
            else if (ReferenceEquals(request, _clashRequest))
            {
                OnClashBurstEnded();
            }

            if (!ReferenceEquals(request, _identRequest) && !ReferenceEquals(request, _clashRequest))
            {
                OnOtherTransmissionEnded(sampleIndex);
            }
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>True if a candidate whose first sample is <paramref name="index"/> lies in the window of one of the station's own transmissions.</summary>
    private bool InOwnWindow(long index)
    {
        foreach ((long start, long end) in _ownWindows)
        {
            if (index >= start && index < end)
            {
                return true;
            }
        }

        return false;
    }

    private void ForgetOldWindows()
    {
        long keepFrom = SampleCount - (4L * Frame);
        for (int i = _ownWindows.Count - 1; i >= 0; i--)
        {
            if (_ownWindows[i].End < keepFrom)
            {
                _ownWindows.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// True while a candidate is waiting for its header to be judged (in the list, or being read). A candidate the station already has an explanation for (inside a burst it knows, or within a symbol of its start:
    /// the same burst under another opening) is not waited for.
    /// </summary>
    private bool CandidatePending()
    {
        foreach (Candidate c in _candidates)
        {
            if (!IsExplained(c.Index))
            {
                return true;
            }
        }

        lock (_gate)
        {
            return _headerPhase != HeaderPhase.Idle && !IsExplained(_headerCandidate.Index);
        }
    }

    private bool IsExplained(long index) => InsideKnownBurst(index) || IsShadow(index);

    private bool IsShadow(long index)
    {
        foreach (Extent e in _extents)
        {
            if (Math.Abs(e.Start - index) <= Sym)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tells the channel whether a candidate is waiting for its header to be judged: busy while one is.</summary>
    private void RefreshPending() => _access.SetPending(CandidatePending());

    // ---- what a header read means for the channel (E) ----

    /// <summary>
    /// A header that would not read, or read as nothing a burst can be: busy for the opening and a second more from the candidate's first sample, then judged again. The hold is kept back until every candidate
    /// waiting has been judged: a real burst is found under each opening whose reference it resembles, and the readings under the wrong ones fail, so a failed candidate that lies within a symbol of the start of a burst
    /// the station knows is that burst's shadow and holds nothing.
    /// </summary>
    private void HoldForUnreadable(Candidate c)
    {
        if (_unreadable.Count == 64)
        {
            _unreadable.RemoveAt(0);
        }

        _unreadable.Add((c.Index, c.Index + HeaderReader.OpeningSamples(c.Opening) + ChannelAccess.UnreadableHold));
    }

    /// <summary>Applies the holds of the candidates that would not read, once none is left to judge, except those that are the shadow of a burst that did.</summary>
    private void SettleUnreadable()
    {
        if (_unreadable.Count == 0 || CandidatePending())
        {
            return;
        }

        foreach ((long index, long until) in _unreadable)
        {
            if (!IsShadow(index))
            {
                BurstKnown?.Invoke(SampleCount, until);
                _access.HoldUntil(until);
            }
        }

        _unreadable.Clear();
    }

    /// <summary>A header of a version this station does not know: busy a second from now; the next candidate that reads the same adds another.</summary>
    private void HoldForUnknownVersion()
    {
        long until = SampleCount + ChannelAccess.UnknownVersionHold;
        BurstKnown?.Invoke(SampleCount, until);
        _access.HoldUntil(until);
    }

    /// <summary>A burst the station knows the length of: busy to its last sample.</summary>
    private void HoldForBurst(long end)
    {
        BurstKnown?.Invoke(SampleCount, end);
        _access.HoldUntil(end);
    }
}
