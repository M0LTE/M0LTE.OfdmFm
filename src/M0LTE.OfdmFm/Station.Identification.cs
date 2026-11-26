namespace M0LTE.OfdmFm;

// Step 7 of docs/station-design.md: the station identifies itself, and learns the callsigns of the stations that do.
//
// The clock. It runs on the sample clock and only while the station transmits. It has three states:
//   Resting   nothing owed and no count running (the start, and after each identification). The next transmission starts the count.
//   Counting  a transmission has started since the last identification; ten minutes on from that start (IdentificationIntervalSamples) an identification is due.
//   Owed      due: IdentificationDue was raised and the burst is queued (class Report, so it goes ahead of the host's datagrams). It stays owed, however long the channel is busy, until it goes on air.
// The identification burst's own start returns the clock to Resting, and nothing it sends starts it again: only a transmission of something else does. An idle station is therefore never kept keying to identify.
// A transmission that starts while the identification is owed (one already handed to the audio layer, say) does not start a count: the burst that follows covers it.

public sealed partial class Station
{
    /// <summary>
    /// Ten minutes of the sample clock, in samples: the interval of the host program's existing Morse ident, which the project owner chose for the modem's own identification (26 November 2026). Fixed, not a setting.
    /// </summary>
    internal const long IdentificationIntervalSamples = 600L * SampleRate;

    private enum IdentificationState : byte
    {
        Resting,
        Counting,
        Owed,
    }

    private IdentificationState _identState;
    private long _identDueAt = -1;
    private TimerHandle _identTimer;
    private Action _onIdentificationDue = null!;
    private ChannelAccess.Request? _identRequest;
    private float[]? _identAudio;
    private bool _identBuilt;
    private long _identificationsDue;
    private long _identificationsSent;
    private long _identificationInterval = IdentificationIntervalSamples;

    /// <summary>
    /// The interval the identification count uses, in samples. A test's seam: it is <see cref="IdentificationIntervalSamples"/> and a test sets a shorter one so that a run does not push ten minutes of audio. Sound because the interval is read in one place only
    /// (when a transmission starts the count) and enters nothing but the due sample of one timer; set it before the station first transmits. Not for a host.
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

    /// <summary>The sample the identification is due at while the count runs, or null.</summary>
    internal long? IdentificationDueAt => _identState == IdentificationState.Counting ? _identDueAt : null;

    /// <summary>True from the sample an identification falls due until its burst goes on air.</summary>
    internal bool IdentificationOwed => _identState == IdentificationState.Owed;

    /// <summary>True while neither a count is running nor an identification owed.</summary>
    internal bool IdentificationResting => _identState == IdentificationState.Resting;

    /// <summary>How many times an identification has fallen due.</summary>
    internal long IdentificationsDue => _identificationsDue;

    /// <summary>How many identification bursts have gone on air.</summary>
    internal long IdentificationsSent => _identificationsSent;

    private void InitialiseIdentification() => _onIdentificationDue = OnIdentificationDue;

    /// <summary>Any transmission but the identification burst has started: if the clock is resting it starts counting from this sample.</summary>
    private void OnOtherTransmissionStarted(long sampleIndex)
    {
        if (_identState != IdentificationState.Resting)
        {
            return;
        }

        _identState = IdentificationState.Counting;
        _identDueAt = sampleIndex + _identificationInterval;
        _identTimer = ScheduleAt(_identDueAt, _onIdentificationDue);
    }

    private void OnIdentificationDue()
    {
        if (_identState != IdentificationState.Counting)
        {
            return;
        }

        _identState = IdentificationState.Owed;
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
        int length = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(Callsign), CapabilityWord, default, _payload);
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

    /// <summary>The identification burst is on air: the clock rests, and counts again from the next transmission of anything else.</summary>
    private void OnIdentificationBurstStarted()
    {
        _identState = IdentificationState.Resting;
        _identificationsSent++;
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
        if (!_hostBound[sender] && !name.Equals(StationName.FromAscii(Callsign)))
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
