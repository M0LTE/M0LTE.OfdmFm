namespace M0LTE.OfdmFm;

// Step 7 of docs/station-design.md: the station identifies itself, and learns the callsigns of the stations that do.
//
// The rule (it is the host program's existing Morse ident's rule): an identification is due when ten minutes have passed since the station's last identification (or it has never identified)
// AND the station has transmitted since then. "Last identification" is when its burst started on the air (TransmitStarted), not when it fell due, so a long wait for the channel does not shorten the next interval.
// The identification burst is not a transmission that makes another owed; anything else the station sends is, a lone report included.
//   - Nothing sent since the last identification: nothing is owed and nothing is ever sent.
//   - A transmission starts when the last identification was ten minutes ago or more (or there was none): due at once. IdentificationDue is raised when that transmission ends, not in the middle of it
//     (the station is no longer keyed, so the audio layer can send its Morse ident), and the burst is queued at that moment.
//   - A transmission starts within the ten minutes: a timer on the sample clock is set for the end of the ten minutes. When it runs out the identification falls due (at the end of the transmission, if one is on the air then),
//     so a station that identified, sent something, and went quiet keys once, to cover it.
// An identification owed stays owed, however long the channel is busy, until its burst starts: IdentificationDue is raised once, and transmissions in the meantime are covered by the burst that follows.

public sealed partial class Station
{
    /// <summary>
    /// Ten minutes of the sample clock, in samples: the interval of the host program's existing Morse ident, which the project owner chose for the modem's own identification (26 November 2026). Fixed, not a setting.
    /// </summary>
    internal const long IdentificationIntervalSamples = 600L * SampleRate;

    private long _lastIdentStart = -1;
    private bool _transmittedSince;
    private bool _identOwed;
    private bool _identDueWaiting;
    private long _identTimerAt = -1;
    private TimerHandle _identTimer;
    private Action _onIdentificationDue = null!;
    private ChannelAccess.Request? _identRequest;
    private float[]? _identAudio;
    private bool _identBuilt;
    private long _identificationsDue;
    private long _identificationsSent;
    private long _identificationInterval = IdentificationIntervalSamples;

    /// <summary>
    /// The interval the identification rule uses, in samples. A test's seam: it is <see cref="IdentificationIntervalSamples"/> and a test sets a shorter one so that a run does not push ten minutes of audio. Sound because the interval is read in one place only
    /// (when a transmission starts) and enters nothing but the due sample of one timer and one comparison; set it before the station first transmits. Not for a host.
    /// </summary>
    internal long IdentificationInterval
    {
        get => _identificationInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _identificationInterval = value;
        }
    }

    /// <summary>
    /// A test's seam: while set, nothing transmitted makes an identification owed, so that a test of something else (access, sending, reports) sees only the bursts it is about. Not for a host, which cannot switch identification off.
    /// Set before the station first transmits.
    /// </summary>
    internal bool IdentificationSuspended { get; set; }

    /// <summary>The sample the interval since the last identification runs out at, while a timer waits for it; otherwise null.</summary>
    internal long? IdentificationDueAt => _identTimerAt >= 0 ? _identTimerAt : null;

    /// <summary>True from the sample an identification falls due until its burst goes on air.</summary>
    internal bool IdentificationOwed => _identOwed;

    /// <summary>True when nothing has been sent since the last identification (or ever): nothing is owed and no timer runs.</summary>
    internal bool IdentificationResting => !_transmittedSince && !_identOwed;

    /// <summary>The sample the last identification burst started at, or null if none has.</summary>
    internal long? LastIdentificationStart => _lastIdentStart >= 0 ? _lastIdentStart : null;

    /// <summary>How many times an identification has fallen due.</summary>
    internal long IdentificationsDue => _identificationsDue;

    /// <summary>How many identification bursts have gone on air.</summary>
    internal long IdentificationsSent => _identificationsSent;

    private void InitialiseIdentification() => _onIdentificationDue = OnIdentificationTimer;

    /// <summary>Anything but the identification burst has started: something has been sent since the last identification.</summary>
    private void OnOtherTransmissionStarted(long sampleIndex)
    {
        if (_identOwed || IdentificationSuspended || Callsign is null)
        {
            return;
        }

        _transmittedSince = true;
        if (_lastIdentStart < 0 || sampleIndex >= _lastIdentStart + _identificationInterval)
        {
            BecomeDue();
        }
        else if (_identTimerAt < 0)
        {
            _identTimerAt = _lastIdentStart + _identificationInterval;
            _identTimer = ScheduleAt(_identTimerAt, _onIdentificationDue);
        }
    }

    private void OnIdentificationTimer()
    {
        _identTimerAt = -1;
        if (_transmittedSince && !_identOwed)
        {
            BecomeDue();
        }
    }

    /// <summary>Owed from now. If the station is keyed, IdentificationDue and the request wait for the end of the transmission (<see cref="OnOtherTransmissionEnded"/>).</summary>
    private void BecomeDue()
    {
        _identOwed = true;
        if (_identTimerAt >= 0)
        {
            CancelTimer(_identTimer);
            _identTimerAt = -1;
        }

        if (IsTransmitting)
        {
            _identDueWaiting = true;
            return;
        }

        RaiseIdentificationDue();
    }

    private void OnOtherTransmissionEnded()
    {
        if (_identDueWaiting)
        {
            _identDueWaiting = false;
            RaiseIdentificationDue();
        }
    }

    private void RaiseIdentificationDue()
    {
        _identificationsDue++;
        IdentificationDue?.Invoke(new IdentificationDueEvent(Seconds));
        _identRequest ??= new ChannelAccess.Request();
        _access.Enqueue(_identRequest, TransmitClass.Report);
    }

    /// <summary>
    /// The request has been granted and the channel is free: build the identification burst, at the first-contact format (the most robust, as for any burst to everyone), unless it was built already and is being handed over again after a withdrawal.
    /// The burst number is the station's own count, so that each identification is a different waveform.
    /// </summary>
    private bool PrepareIdentification(ChannelAccess.Request request)
    {
        if (_identBuilt)
        {
            return true;
        }

        int format = StandardFormats.FirstContact;
        _renderer ??= new BurstRenderer();
        int length = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(Callsign!), CapabilityWord, default, _payload);
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        int number = _burstNumber;
        _burstNumber = (_burstNumber + 1) & 15;
        int total = _renderer.SamplesFor(format, symbols);
        float[] audio = RentAudio(total);
        _renderer.RenderData(BurstHeader.Identification(StationNumber, format, symbols, number), _payload.AsSpan(0, length), audio);
        _identAudio = audio;
        _identBuilt = true;
        request.Samples = audio.AsMemory(0, total);
        request.Modem = new ModemSentEvent(ModemSentKind.Identification, null, format);
        return true;
    }

    /// <summary>The identification burst is on air: it is the last identification, and nothing has been sent since it.</summary>
    private void OnIdentificationBurstStarted(long sampleIndex)
    {
        _lastIdentStart = sampleIndex;
        _identOwed = false;
        _transmittedSince = false;
        _identificationsSent++;
        if (_identTimerAt >= 0)
        {
            CancelTimer(_identTimer);
            _identTimerAt = -1;
        }
    }

    private void OnIdentificationBurstEnded()
    {
        if (_identAudio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        _identAudio = null;
        _identBuilt = false;
    }

    // ---- receiving an identification ----

    private void ApplyIdentification(BurstRecord r)
    {
        BurstJob job = r.Job!;
        BurstHeader h = r.Header;
        double now = ClockSeconds(r.End);
        int sender = h.Sender;
        bool decoded = job.Decoded;
        ReadOnlySpan<double> quality = job.HasQuality ? job.Quality : default;
        double footing = LinkAdaptation.Footing(h.Format, opening: !decoded);
        ReadOnlySpan<byte> callsign = default;
        int capability = 0;
        bool read = decoded && PayloadCodec.TryUnpackIdentification(job.Payload.AsSpan(0, job.PayloadBytes), out callsign, out capability, out _);

        // A burst to everyone, measured like any other from that station; nothing is owed for it.
        int? word = read ? capability : null;
        LinkReport? owed = _link.HeardData(sender, 0, h.Format, decoded, quality, now, footing, job.Sudden, null, word);
        AdaptationCalled?.Invoke(new AdaptationCall(false, sender, 0, h.Format, decoded, quality.ToArray(), now, footing, job.Sudden, null, word, owed));
        if (!read)
        {
            RaiseLinkState(sender, now);
            return;
        }

        // The name: the host's own binding always stands. Otherwise the callsign becomes the number's name, and the latest identification wins (a callsign learned at another number moves here, as Bind moves a name);
        // a callsign the host bound to another number is the host's, and is not taken from it. A callsign that is this station's own is nobody else's name.
        var name = new StationName(callsign);
        string text = System.Text.Encoding.ASCII.GetString(callsign);
        bool renamed = false;
        if (!_hostBound[sender] && !(Callsign is not null && name.Equals(StationName.FromAscii(Callsign))))
        {
            int holder = NumberOf(name);
            if ((holder == 0 || !_hostBound[holder]) && (_bound[sender] is not { } current || current != name))
            {
                _bound[sender] = name;
                if (holder != 0 && holder != sender)
                {
                    _bound[holder] = null;
                    AnnounceLink(holder);
                }

                renamed = true;
            }
        }

        Identified?.Invoke(new IdentifiedEvent(sender, text, capability, NameFor(sender)));
        if (renamed)
        {
            AnnounceLink(sender);
        }
        else
        {
            RaiseLinkState(sender, now);
        }
    }
}
