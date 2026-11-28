namespace M0LTE.OfdmFm;

// Step 7 of docs/station-design.md: the station identifies itself, and learns the callsigns of the stations that do.
//
// The rule (it is the host program's existing Morse ident's rule, with the order turned round so that the answer to a first burst finds a quiet channel): an identification is due when the station is about to make a transmission of its own accord
// (a data burst, an announcement, a notice about a station number) and either it has never identified or ten minutes have passed since its last identification burst started. Nothing else makes one due: there is no timer, and an idle station never keys
// to identify.
//   - About to transmit of its own accord, and an identification is due: the identification goes first, at once and without a fresh wait, and the transmission that made it due follows it without a random wait (one continuous turn).
//     IdentificationDue is raised at that moment, once. The host sends its Morse inside the identification's own transmission (see SetIdentificationTail) and ends it when the Morse is over.
//   - A lone report ends and an identification is due: the report went first, because the other station is waiting for exactly that; the identification follows it in the first slot, ahead of anybody's ordinary traffic. A report never waits for an identification.
//   - About to transmit and the last identification started within ten minutes: nothing is owed. The transmission is covered by that identification.
// "Last identification" is when its burst started on the air (TransmitStarted), not when it fell due, so a long wait for the channel does not shorten the next interval. A host that restarts says how long ago it was (SetLastIdentification).
// An identification owed stays owed, however long the channel is busy, until its burst starts: IdentificationDue is raised once.
// The announced tail: while the host's Morse follows the burst, the modem cannot hear it as a busy channel, so the burst carries one item with its length and every station that decodes it holds off for that long (HoldForTail).

public sealed partial class Station
{
    /// <summary>
    /// Ten minutes of the sample clock, in samples: the interval of the host program's existing Morse ident, which the project owner chose for the modem's own identification (26 November 2026). Fixed, not a setting.
    /// </summary>
    internal const long IdentificationIntervalSamples = 600L * SampleRate;

    /// <summary>A station number's announced tail is honoured at most once in this long, 60 s in samples.</summary>
    internal const long TailHonourSamples = 60L * SampleRate;

    private readonly long[] _tailHonouredAt = CreateTailTimes();
    private long _lastIdentStart = long.MinValue;
    private bool _identOwed;
    private bool _identFirst;
    private bool _straightOnAfterIdent;
    private bool _transmittedYet;
    private byte _tailTenths;
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

    /// <summary>The sample the last identification burst started at (a negative one if the host said it was before the clock started), or null if none has.</summary>
    internal long? LastIdentificationStart => _lastIdentStart != long.MinValue ? _lastIdentStart : null;

    /// <summary>The longest tail the host can announce, in seconds: a tenth of a second in one byte.</summary>
    public const double MaxIdentificationTail = 25.5;

    /// <summary>The longest "last identified" age the host can give, in seconds: a year.</summary>
    public const double MaxLastIdentificationAge = 365.0 * 86400.0;

    /// <summary>
    /// Tells the station that after each identification burst the host goes on sending for <paramref name="seconds"/> inside the same transmission (its callsign in Morse), and ends the transmission with <see cref="TransmitEnded"/> only when that is over.
    /// The station announces it in the identification burst, in an item of one byte (tenths of a second, rounded up), so that every station that decodes the burst stays off the channel until the tail has run out. Zero, the default: no item, and the
    /// burst is the same bytes as a station that does not know the call sends. The host must keep the promise: the station cannot hear the tail.
    /// A burst already built keeps the tail it was built with. Not for a station that transmits other than as said: a tail of 0 is right for a host that sends no Morse.
    /// </summary>
    /// <param name="seconds">0 to <see cref="MaxIdentificationTail"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Negative, not a number, or over <see cref="MaxIdentificationTail"/>.</exception>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public void SetIdentificationTail(double seconds)
    {
        EnsureNotAdvancing();
        if (double.IsNaN(seconds) || seconds < 0 || seconds > MaxIdentificationTail)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "The tail is 0 to 25.5 seconds.");
        }

        _tailTenths = (byte)Math.Ceiling((seconds * 10.0) - 1e-6);
    }

    /// <summary>
    /// Tells the station that it last identified <paramref name="secondsAgo"/> seconds ago, for a host that was stopped and started again (the host keeps the time of each identification; the library keeps no time and does no I/O).
    /// Within ten minutes nothing is due until ten minutes have run from that moment; longer ago, an identification is due at the first transmission, as for a station that never has. Call it before the first <see cref="Send"/> or transmission (a refusal follows a transmission, or an identification that has fallen due and not gone yet); it
    /// can be called again until then and the last call counts. The host learns when an identification went out from <see cref="ModemSent"/> with kind <see cref="ModemSentKind.Identification"/>, raised as the burst goes on air: that is the moment to write down.
    /// (<see cref="IdentificationDue"/> says only that one is owed.)
    /// </summary>
    /// <param name="secondsAgo">0 to <see cref="MaxLastIdentificationAge"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Negative, not a number, or over <see cref="MaxLastIdentificationAge"/>.</exception>
    /// <exception cref="InvalidOperationException">The station has transmitted already or an identification has fallen due, or the call is from inside an event handler or a timer action.</exception>
    public void SetLastIdentification(double secondsAgo)
    {
        EnsureNotAdvancing();
        if (double.IsNaN(secondsAgo) || secondsAgo < 0 || secondsAgo > MaxLastIdentificationAge)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsAgo), secondsAgo, "The age is 0 seconds to a year.");
        }

        if (_transmittedYet || _identificationsDue > 0)
        {
            throw new InvalidOperationException("The station has transmitted or an identification has fallen due already: it knows when it last identified.");
        }

        _lastIdentStart = SampleCount - (long)Math.Round(secondsAgo * SampleRate);
        if (WantsFlow())
        {
            Flow(TraceText.LastIdentificationTold(secondsAgo));
        }
    }

    /// <summary>How many times an identification has fallen due.</summary>
    internal long IdentificationsDue => _identificationsDue;

    /// <summary>How many identification bursts have gone on air.</summary>
    internal long IdentificationsSent => _identificationsSent;

    /// <summary>True when an identification would fall due now: the station has never identified, or the interval has run out since the last one started.</summary>
    private bool IdentificationFallsDue(long sampleIndex) =>
        !_identOwed && !IdentificationSuspended && Callsign is not null && (_lastIdentStart == long.MinValue || sampleIndex >= _lastIdentStart + _identificationInterval);

    /// <summary>
    /// A lone report has ended at <paramref name="sampleIndex"/>: the report went first, so an identification that is due follows it, in the first slot. Data, an announcement or a notice owes nothing when it ends:
    /// it was preceded by the identification, if one was due (see <see cref="GateForIdentification"/>).
    /// </summary>
    private void OnLoneReportEnded(long sampleIndex)
    {
        if (_identOwed)
        {
            return;
        }

        if (!IdentificationFallsDue(sampleIndex))
        {
            _straightOnAfterIdent = false;
            return;
        }

        if (WantsDetail())
        {
            Detail(TraceText.IdentificationDue(_lastIdentStart == long.MinValue ? TraceWords.DueFirst : TraceWords.DueInterval));
        }

        _identOwed = true;
        _identFirst = false;
        RaiseIdentificationDue();
    }

    /// <summary>
    /// The channel-access rules are about to hand over the head of the queue. If it is a transmission of the station's own accord (data, an announcement, a notice) and an identification is due, the identification is queued ahead of it and the head waits:
    /// true. The identification is granted at once (it needs only the first slot, which the head's own wait has covered), and when it has ended the head goes straight on.
    /// </summary>
    private bool GateForIdentification(ChannelAccess.Request head)
    {
        if (!IdentificationFallsDue(SampleCount) || !OfItsOwnAccord(head))
        {
            return false;
        }

        if (WantsDetail())
        {
            Detail(TraceText.IdentificationDue(_lastIdentStart == long.MinValue ? TraceWords.DueFirst : TraceWords.DueInterval));
            Detail(TraceText.IdentifyingFirst(SubjectOf(head)));
        }

        _identOwed = true;
        _identFirst = true;
        RaiseIdentificationDue();
        return true;
    }

    /// <summary>True for the request that stands for a lone report that is ready or built: it goes ahead of the station's own identification, however that came to wait.</summary>
    private bool IsLoneReport(ChannelAccess.Request request) =>
        ReferenceEquals(request, _reportRequest) && (_report.Built ? !_report.Announcement : NextReady() >= 0);

    /// <summary>True for a request that is the station's own choice to transmit: data, a notice about a number, or an announcement. Not a lone report, which answers a station that is waiting for it, and not the identification.</summary>
    private bool OfItsOwnAccord(ChannelAccess.Request request)
    {
        if (ReferenceEquals(request, _data) || ReferenceEquals(request, _clashRequest))
        {
            return true;
        }

        if (!ReferenceEquals(request, _reportRequest))
        {
            return false;
        }

        return _report.Built ? _report.Announcement : NextReady() < 0 && NextAnnouncement() >= 0;
    }

    private static long[] CreateTailTimes()
    {
        var times = new long[64];
        Array.Fill(times, long.MinValue / 2);
        return times;
    }

    private void RaiseIdentificationDue()
    {
        _identificationsDue++;
        IdentificationDue?.Invoke(new IdentificationDueEvent(Seconds));
        _identRequest ??= new ChannelAccess.Request();
        _access.Enqueue(_identRequest, TransmitClass.Report, first: true);
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
        int items = 1;
        if (_tailTenths > 0)
        {
            _tailItemValue[0] = _tailTenths;
            _identItems[1] = new ExtensionItem(PayloadCodec.TailTag, _tailItemValue);
            items = 2;
        }

        int length = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(Callsign!), CapabilityWord, _identItems.AsSpan(0, items), _payload);
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
        if (WantsFlow())
        {
            Flow(TraceText.SentIdentification(Callsign!, (_identRequest?.Samples.Length ?? 0) / (double)SampleRate));
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

        // The identification went first, or came after a report that the station's own data follows straight on: the turn goes on, without a fresh wait, while the channel is still the station's to use.
        if ((_identFirst || _straightOnAfterIdent) && _access.WaitingCount > 0)
        {
            if (WantsDetail())
            {
                Detail(TraceText.GoingStraightOnAfterIdentification());
            }

            _access.GoStraightOn();
        }

        _identFirst = false;
        _straightOnAfterIdent = false;
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

        ExtensionReader tail = again;
        bool notice = false;
        while (again.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.ClashTag && value.Length == PayloadCodec.ClashBytes && value[0] is >= 1 and <= 63)
            {
                notice = true;
                HeardClash(value[0], PayloadCodec.ReadKey(value[1..]));
            }
        }

        // A tail is honoured from an identification burst proper, not from a notice about a station number, and from one number at most once in a minute: a station that keeps sending the item cannot keep the channel closed.
        while (!notice && tail.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.TailTag && value.Length == PayloadCodec.TailBytes && value[0] != 0)
            {
                if (end - _tailHonouredAt[sender] >= TailHonourSamples)
                {
                    _tailHonouredAt[sender] = end;
                    HoldForTail(sender, end, end + (value[0] * (long)(SampleRate / 10)));
                }

                break;
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
        double signal = double.NaN;
        if (WantsFlow())
        {
            signal = job.HasQuality ? MeanQualityDb(job.Quality, StandardFormats.Describe(h.Format).Carriers) : double.NaN;
            string who = StationName(sender);
            Flow(!decoded ? TraceText.HeardNotDecoded(who)
                : !read ? TraceText.HeardUnreadable(who)
                : TraceText.HeardIdentified(who, System.Text.Encoding.ASCII.GetString(callsign), signal));
            if (job.Sudden)
            {
                Flow(TraceText.LandedOn(who));
            }
        }

        if (read)
        {
            ReadNumberItems(sender, items, r.End, now);
        }

        // A burst to everyone, measured like any other from that station; nothing is owed for it.
        int? word = read ? capability : null;
        NeighbourBefore before = Trace is not null ? CaptureNeighbour(sender) : default;
        LinkReport? owed = _link.HeardData(sender, 0, h.Format, decoded, quality, now, footing, job.Sudden, null, word);
        AdaptationCalled?.Invoke(new AdaptationCall(false, sender, 0, h.Format, decoded, quality.ToArray(), now, footing, job.Sudden, null, word, owed));
        if (Trace is not null)
        {
            TraceMeasured(sender, h.Format, decoded, signal, before);
        }

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

