namespace M0LTE.OfdmFm;

// Step 7 of docs/station-design.md: the station identifies itself, and learns the callsigns of the stations that do.
//
// The rule (it is the host program's existing Morse ident's rule): an identification is due when a transmission of the station's (data or a lone report, not an identification burst) ends and either the station has never identified
// or ten minutes have passed since its last identification burst started. Nothing else makes one due: there is no timer, and an idle station never keys to identify.
//   - A transmission ends and there was no identification, or the last was ten minutes ago or more: due at once. IdentificationDue is raised at that moment (the station is no longer keyed, so the audio layer can send its Morse ident)
//     and the burst is queued then.
//   - A transmission ends within ten minutes of the last identification's start: nothing is owed. It is covered by that identification.
// "Last identification" is when its burst started on the air (TransmitStarted), not when it fell due, so a long wait for the channel does not shorten the next interval.
// An identification owed stays owed, however long the channel is busy, until its burst starts: IdentificationDue is raised once, and transmissions in the meantime are covered by the burst that follows.

public sealed partial class Station
{
    /// <summary>
    /// Ten minutes of the sample clock, in samples: the interval of the host program's existing Morse ident, which the project owner chose for the modem's own identification (26 November 2026). Fixed, not a setting.
    /// </summary>
    internal const long IdentificationIntervalSamples = 600L * SampleRate;

    private long _lastIdentStart = -1;
    private bool _identOwed;
    private ChannelAccess.Request? _identRequest;
    private float[]? _identAudio;
    private bool _identBuilt;
    private long _identificationsDue;
    private long _identificationsSent;
    private long _identificationInterval = IdentificationIntervalSamples;

    /// <summary>
    /// The interval the identification rule uses, in samples. A test's seam: it is <see cref="IdentificationIntervalSamples"/> and a test sets a shorter one so that a run does not push ten minutes of audio. Sound because the interval is read in one place only
    /// (when a transmission ends) and enters nothing but one comparison; set it before the station first transmits. Not for a host.
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

    /// <summary>True from the sample an identification falls due until its burst goes on air.</summary>
    internal bool IdentificationOwed => _identOwed;

    /// <summary>The sample the last identification burst started at, or null if none has.</summary>
    internal long? LastIdentificationStart => _lastIdentStart >= 0 ? _lastIdentStart : null;

    /// <summary>How many times an identification has fallen due.</summary>
    internal long IdentificationsDue => _identificationsDue;

    /// <summary>How many identification bursts have gone on air.</summary>
    internal long IdentificationsSent => _identificationsSent;

    /// <summary>
    /// A transmission other than the identification burst has ended at <paramref name="sampleIndex"/>: an identification is due if the station has never identified or the interval has run out since the last one started.
    /// Anything sooner is covered by that one and owes nothing.
    /// </summary>
    private void OnOtherTransmissionEnded(long sampleIndex)
    {
        if (_identOwed || IdentificationSuspended || Callsign is null)
        {
            return;
        }

        if (_lastIdentStart < 0 || sampleIndex >= _lastIdentStart + _identificationInterval)
        {
            _identOwed = true;
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
        PayloadCodec.WriteKey(_stationId, _idItemValue);
        _identItems[0] = new ExtensionItem(PayloadCodec.StationIdTag, _idItemValue);
        int length = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(Callsign!), CapabilityWord, _identItems.AsSpan(0, 1), _payload);
        IdentificationPayloadBuilt?.Invoke(_payload.AsSpan(0, length).ToArray());
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        int number = _burstNumber;
        _burstNumber = (_burstNumber + 1) & 15;
        int total = _renderer.SamplesFor(format, symbols);
        float[] audio = RentAudio(total);
        _renderer.RenderData(BurstHeader.Identification(_number, format, symbols, number), _payload.AsSpan(0, length), audio);
        _identAudio = audio;
        _identBuilt = true;
        request.Samples = audio.AsMemory(0, total);
        request.Modem = new ModemSentEvent(ModemSentKind.Identification, format);
        return true;
    }

    /// <summary>The identification burst is on air: it is the last identification, and nothing has been sent since it.</summary>
    private void OnIdentificationBurstStarted(long sampleIndex)
    {
        _lastIdentStart = sampleIndex;
        _identOwed = false;
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

    /// <summary>
    /// The items of an identification burst that concern station numbers: the sender's station id (the first item with tag 1 and four bytes of value), which is checked against the id last heard on that number, and each Clash (tag 2, five bytes).
    /// The id is looked at before the Clashes, and before the burst is measured, so that a station found to be new at a number is measured afresh. An item of another length, or another tag, is skipped.
    /// </summary>
    private void ReadNumberItems(int sender, ExtensionReader items, long end, double now)
    {
        ExtensionReader again = items;
        bool haveId = false;
        while (items.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.StationIdTag && value.Length == PayloadCodec.KeyBytes && !haveId)
            {
                haveId = true;
                NoteStationId(sender, PayloadCodec.ReadKey(value), end, now);
            }
        }

        while (again.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.ClashTag && value.Length == PayloadCodec.ClashBytes && value[0] is >= 1 and <= 63)
            {
                HeardClash(value[0], PayloadCodec.ReadKey(value[1..]));
            }
        }
    }

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
        ExtensionReader items = default;
        bool read = decoded && PayloadCodec.TryUnpackIdentification(job.Payload.AsSpan(0, job.PayloadBytes), out callsign, out capability, out items);
        if (read)
        {
            ReadNumberItems(sender, items, r.End, now);
        }

        // A burst to everyone, measured like any other from that station; nothing is owed for it.
        int? word = read ? capability : null;
        LinkReport? owed = _link.HeardData(sender, 0, h.Format, decoded, quality, now, footing, job.Sudden, null, word);
        AdaptationCalled?.Invoke(new AdaptationCall(false, sender, 0, h.Format, decoded, quality.ToArray(), now, footing, job.Sudden, null, word, owed));
        if (!read)
        {
            RaiseLinkState(sender, now);
            return;
        }

        HearingSender = sender;
        Identified?.Invoke(new IdentifiedEvent(System.Text.Encoding.ASCII.GetString(callsign), capability));
        RaiseLinkState(sender, now);
    }
}

