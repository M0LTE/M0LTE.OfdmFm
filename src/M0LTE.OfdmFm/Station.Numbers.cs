namespace M0LTE.OfdmFm;

// Step two of docs/station-numbers.md: the station picks its own number and station id, and the modems settle a number held by two stations (or one that has changed hands) among themselves.
//
//   Taking a number. When the host gives the identity the station picks, at random from its own source, a number from 1 to 63 that it has not heard as the sender of a burst in the last ten minutes of the sample clock, and 32 random
//   bits for its station id. If it has heard all 63 it takes the one unheard longest. It does not listen first. A station with no identity has neither, and no burst is for it.
//   In hearing of each other. A station that reads a burst header whose sender is its own number, when it is not transmitting and not inside its own mute tail, takes a new number and a new id at once. Nothing is announced.
//   One rule for the rest. Every identification burst carries the sender's station id. For each number a station remembers the id last heard there and when. A different id on a known number means the number has changed hands or is held
//   by two stations: what the station measured for it and the addresses it had there are forgotten exactly as three silences forget a station (the host is told the addresses have gone, and datagrams queued for them stay queued and go to everyone with For), and, if the earlier id was heard
//   inside ten minutes, a Clash is owed: an identification burst that names the number and the id heard more recently. The station named takes a new number. The Clash waits a random 0 to 10 s, then goes by the ordinary access rules, and does not move the identification clock. When a station sends a Clash, or decodes one, it forgets the id it had on record for the number.
//
// A burst the station had built and not yet put on air (handed to the audio layer and not keyed, or taken back and waiting) carries the old number in its header and its scrambler seed. It is not patched: it is discarded, what it held goes
// back where it came from (datagrams to the front of their queue, an owed report or announcement to what is owed), and the burst is built again, with the new number, when the rules grant the request again.

public sealed partial class Station
{
    /// <summary>The window a number counts as heard, and the time inside which two ids on one number make a Clash: ten minutes of the sample clock, the time a neighbour is forgotten after.</summary>
    private const long NumberWindowSamples = (long)LinkAdaptation.ForgetSeconds * SampleRate;

    private int _number;
    private uint _stationId;
    private int _forcedNumber;
    private uint _forcedId;
    private readonly long[] _senderHeardAt = new long[64];
    private readonly int[] _candidateNumbers = new int[63];

    // The id last heard on each number, in an identification burst that was read, and when that burst ended.
    private readonly bool[] _idKnown = new bool[64];
    private readonly uint[] _idValue = new uint[64];
    private readonly long[] _idAt = new long[64];

    private readonly byte[] _idItemValue = new byte[PayloadCodec.KeyBytes];
    private readonly byte[] _clashItemValue = new byte[PayloadCodec.ClashBytes];
    private readonly ExtensionItem[] _identItems = new ExtensionItem[2];

    // Clashes owed: the number and the id that is to move. One goes in each burst.
    private readonly List<(int Number, uint Id, long ReadyAt)> _clashesOwed = [];
    private Action _onClashReady = null!;
    private ChannelAccess.Request? _clashRequest;
    private bool _clashBuilt;
    private int _clashNumber;
    private uint _clashId;
    private float[]? _clashAudio;
    private long _numberChanges;
    private long _clashesSent;
    private long _clashesOwedCount;

    /// <summary>How many times this station has moved to another number (after the first). For tests.</summary>
    internal long NumberChanges => _numberChanges;

    /// <summary>How many Clash bursts have gone on air. For tests.</summary>
    internal long ClashesSent => _clashesSent;

    /// <summary>How many Clashes have been owed (a Clash is owed once for each detection, and may be cancelled before it goes). For tests.</summary>
    internal long ClashesOwedCount => _clashesOwedCount;

    /// <summary>Clashes owed and not on air yet, the one built and waiting for the audio layer included. For tests.</summary>
    internal int ClashesPending => _clashesOwed.Count + (_clashBuilt ? 1 : 0);

    /// <summary>A test's hook: called with the payload (before padding) of each identification burst the station builds, a Clash included. Null in a host: nothing is copied.</summary>
    internal Action<byte[]>? IdentificationPayloadBuilt { get; set; }

    /// <summary>A test's seam: as if a burst header with this sender had just been read (the path a real header read takes). Audio thread, serialised with <see cref="Push"/>.</summary>
    internal void HearHeaderFrom(int sender)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            NoteSenderOfHeader(sender);
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>A test's seam: as if a burst from this number had been heard now, without audio.</summary>
    internal void HearNumber(int number) => _senderHeardAt[number] = SampleCount;

    /// <summary>The id last heard on a number in an identification burst, or null. For tests.</summary>
    internal uint? IdHeardOn(int number) => _idKnown[number] ? _idValue[number] : null;

    private void TakeFirstNumber()
    {
        if (_forcedNumber != 0)
        {
            _number = _forcedNumber;
            _stationId = _forcedId;
            _forcedNumber = 0;
        }
        else
        {
            _number = PickNumber();
            _stationId = NewId();
        }

        _link.Number = _number;
        if (WantsFlow())
        {
            Flow(TraceText.TookNumber(_number));
        }
    }

    private uint NewId() => unchecked((uint)_random.NextInt64(0x1_0000_0000L));

    /// <summary>
    /// A number from 1 to 63 not heard as the sender of a burst in the last ten minutes, chosen at random; if all 63 were heard, the one unheard longest (the lowest on a tie). The number the station holds, if it holds one, counts as heard now:
    /// it has just been found to belong to someone else, or the station is being told to leave it.
    /// </summary>
    private int PickNumber()
    {
        long now = SampleCount;
        if (_number != 0)
        {
            _senderHeardAt[_number] = now;
        }

        int count = 0;
        int oldest = 0;
        for (int n = 1; n <= 63; n++)
        {
            long heard = _senderHeardAt[n];
            if (heard == long.MinValue || now - heard > NumberWindowSamples)
            {
                _candidateNumbers[count++] = n;
            }

            // The number held now is not a choice: a station that moves goes somewhere else.
            if (n != _number && (oldest == 0 || heard < _senderHeardAt[oldest]))
            {
                oldest = n;
            }
        }

        return count > 0 ? _candidateNumbers[_random.Next(count)] : oldest;
    }

    /// <summary>
    /// The station takes a new number and a new id, at once, and deals with what it had built under the old one. Called when another station is found to hold the number (a header read, a Clash naming this station).
    /// What the station knows of other stations is untouched.
    /// </summary>
    private void MoveToNewNumber(bool told)
    {
        int was = _number;
        _number = PickNumber();
        _stationId = NewId();
        _link.Number = _number;
        _numberChanges++;
        if (WantsFlow())
        {
            Flow(told ? TraceText.NumberChangedTold(was, _number) : TraceText.NumberChangedInUse(was, _number));
        }

        DiscardBuiltBursts();
    }

    /// <summary>
    /// Whatever the station has built and not put on air carries the old number: take each back, put what it held where it came from, and let the rules build it again when they grant the request. A burst on air is left: it is the audio layer's now.
    /// </summary>
    private void DiscardBuiltBursts()
    {
        if (_burst.Built && !_burst.Started)
        {
            if (WantsDetail())
            {
                Detail(TraceText.BuiltDiscarded("burst"));
            }

            RestoreBurstToQueue();
            EnsureDataRequest();
        }

        if (_report.Built && !_report.Started)
        {
            if (WantsDetail())
            {
                Detail(TraceText.BuiltDiscarded(_report.Announcement ? "announcement" : "report"));
            }

            if (_report.Announcement)
            {
                _announcements.Add(new Announcement(_report.Dest, _report.AnnounceKey, SampleCount));
            }
            else if (!_owedSlots[_report.Dest].Active)
            {
                // What it was to say is still owed, as a report that goes alone (a requested one has been told the recommendation already).
                _owedSlots[_report.Dest] = new OwedSlot
                {
                    Active = true,
                    Kind = _report.Kind == OwedKind.Asked ? OwedKind.Drop : _report.Kind,
                    Format = _report.Recommended,
                    BurstEnd = _report.Since,
                    Due = SampleCount,
                    Sequence = _owedSequence++,
                };
            }

            FreeReportAudio();
            if (_reportRequest is { } report)
            {
                _access.TakeBack(report);
            }

            EnsureReportRequest();
        }

        if (_identBuilt && _identRequest is { State: not ChannelAccess.RequestState.Started } ident)
        {
            if (WantsDetail())
            {
                Detail(TraceText.BuiltDiscarded("identification"));
            }

            if (_identAudio is { } audio)
            {
                _freeAudio.Push(audio);
            }

            _identAudio = null;
            _identBuilt = false;
            _access.TakeBack(ident);
        }

        if (_clashBuilt && _clashRequest is { State: not ChannelAccess.RequestState.Started } clash)
        {
            if (WantsDetail())
            {
                Detail(TraceText.BuiltDiscarded("notice"));
            }

            // The Clash names the number, not this station: it is built again as it was.
            _clashesOwed.Insert(0, (_clashNumber, _clashId, SampleCount));
            DiscardClash();
            _access.TakeBack(clash);
            EnsureClashRequest();
        }
    }

    /// <summary>Takes a built burst that is not on the air back: its datagrams go to the front of their queue in order, its audio is freed and its request is taken back from the audio layer.</summary>
    private void RestoreBurstToQueue(bool takeBack = true)
    {
        AddressQueue q = _burst.Queue!;
        for (int i = _burst.Items.Count - 1; i >= 0; i--)
        {
            Pending p = _burst.Items[i];
            (p.Expedited ? q.Expedited : q.Ordinary).Insert(0, p);
            q.Bits += (p.Length + LengthBytes) * 8L;
            _queuedCount++;
            if (p.Expedited)
            {
                _expeditedCount++;
            }
        }

        _burst.Items.Clear();
        FreeBurstAudio();
        _burst.Built = false;
        if (takeBack && _data is { } data)
        {
            _access.TakeBack(data);
        }
    }

    private void FreeBurstAudio()
    {
        if (_burst.Audio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        _burst.Audio = null;
        _burst.Started = false;
    }

    private void FreeReportAudio()
    {
        if (_report.Audio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        _report.Audio = null;
        _report.Built = false;
        _report.Started = false;
    }

    // ---- hearing a header with this station's own number ----

    /// <summary>
    /// A header has been read whose sender is <paramref name="sender"/>. If it is this station's number, and this station did not send it (it is neither transmitting nor inside the mute tail of its own transmission),
    /// another station holds the number: this one moves.
    /// </summary>
    private void NoteSenderOfHeader(int sender)
    {
        if (_number == 0 || sender != _number)
        {
            _senderHeardAt[sender] = SampleCount;
            return;
        }

        if (IsTransmitting || InOwnWindow(SampleCount))
        {
            return;
        }

        MoveToNewNumber(told: false);
        _senderHeardAt[sender] = SampleCount;
    }

    // ---- hearing a station id, and a Clash ----

    /// <summary>
    /// An identification burst from <paramref name="number"/> was read and carried station id <paramref name="id"/>, the burst ending at <paramref name="at"/>. A different id from the one last heard there is the one rule: the number has changed
    /// hands, or two stations hold it. What was known of the number is forgotten, and if the earlier id was heard inside ten minutes a Clash is owed for the later one.
    /// </summary>
    private void NoteStationId(int number, uint id, long at, double now)
    {
        if (_idKnown[number] && _idValue[number] != id)
        {
            bool clash = at - _idAt[number] <= NumberWindowSamples;
            if (WantsFlow())
            {
                Flow(clash
                    ? (Callsign is null ? TraceText.NumberClashListening(number) : TraceText.NumberClash(number))
                    : TraceText.NumberChangedHands(number));
            }

            ForgetNumber(number, now);
            if (clash)
            {
                OweClash(number, id);
            }
        }

        _idKnown[number] = true;
        _idValue[number] = id;
        _idAt[number] = at;
    }

    /// <summary>What the station measured for a number, the addresses it had there and what it owed that station are forgotten, as when three silences forget a station. The host is told the addresses have gone (<see cref="LinkStateEvent.Gone"/>); what was queued for them stays queued and goes to everyone with For.</summary>
    private void ForgetNumber(int who, double now)
    {
        bool restored = false;
        if (_burst.Built && !_burst.Started && _burst.Dest == who)
        {
            // A burst for the station that is no longer there: its datagrams go back to their queue and follow the address, to everyone.
            RestoreBurstToQueue(takeBack: false); // taken back below, once the address is forgotten, so that it is built again for everyone
            restored = true;
        }

        ForgetAddressesAt(who, now, ForgetWhy.ChangedHands);
        _link.Forget(who);
        _stationShown[who] = default;
        _owedSlots[who] = default;
        _sent[who] = null;
        for (int i = _announcements.Count - 1; i >= 0; i--)
        {
            if (_announcements[i].Station == who)
            {
                _announcements.RemoveAt(i);
            }
        }

        for (int i = _answerChecks.Count - 1; i >= 0; i--)
        {
            if (_answerChecks[i].Dest == who)
            {
                _answerChecks.RemoveAt(i);
            }
        }

        if (_report.Built && !_report.Started && _report.Dest == who)
        {
            FreeReportAudio();
            if (_reportRequest is { } report)
            {
                _access.TakeBack(report);
            }
        }

        if (restored && _data is { } data)
        {
            _access.TakeBack(data);
        }

        EnsureDataRequest();
        EnsureReportRequest();
    }

    /// <summary>A Clash is owed for <paramref name="number"/>: the station with <paramref name="id"/> is to move. Only a station with an identity can send one. One is owed for a number at a time, the newer id replacing the older.</summary>
    private void OweClash(int number, uint id)
    {
        if (Callsign is null || number == _number)
        {
            return;
        }

        if (_clashBuilt && _clashNumber == number)
        {
            return;
        }

        for (int i = 0; i < _clashesOwed.Count; i++)
        {
            if (_clashesOwed[i].Number == number)
            {
                _clashesOwed[i] = (number, id, _clashesOwed[i].ReadyAt);
                return;
            }
        }

        // A Clash is not urgent: it asks for the channel only after a delay drawn from 0 to 10 s, so that two stations that owe the same one do not go together.
        long readyAt = SampleCount + _random.NextInt64((10L * SampleRate) + 1);
        _clashesOwed.Add((number, id, readyAt));
        _clashesOwedCount++;
        ScheduleAt(readyAt, _onClashReady);
        EnsureClashRequest();
    }

    /// <summary>A Clash for <paramref name="number"/> and <paramref name="id"/> was decoded, from whoever sent it.</summary>
    private void HeardClash(int number, uint id)
    {
        // Said by somebody: the id record for the number is cleared (the next identification heard there is only recorded).
        _idKnown[number] = false;
        if (WantsFlow())
        {
            Flow(TraceText.HeardMoveNotice(number));
        }

        // A station about to send a Clash for the number does not send its own.
        bool cancelled = false;
        for (int i = _clashesOwed.Count - 1; i >= 0; i--)
        {
            if (_clashesOwed[i].Number == number)
            {
                _clashesOwed.RemoveAt(i);
                cancelled = true;
            }
        }

        if (_clashBuilt && _clashNumber == number && _clashRequest is { State: not ChannelAccess.RequestState.Started } clash)
        {
            DiscardClash();
            _access.TakeBack(clash);
            cancelled = true;
        }

        if (cancelled)
        {
            if (WantsDetail())
            {
                Detail(TraceText.NoticeCancelled(number));
            }

            EnsureClashRequest();
        }

        // One that names this station's number and id: this station moves. One that names its number and another id is for the other station.
        if (_number != 0 && number == _number && id == _stationId)
        {
            MoveToNewNumber(told: true);
        }
    }

    private void DiscardClash()
    {
        if (_clashAudio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        _clashAudio = null;
        _clashBuilt = false;
    }

    /// <summary>Keeps the one request that stands for the Clashes owed: in the ordinary class (the channel clear for 0.8 s and then a drawn number of slots), made when one is owed and none is outstanding, taken away when none is left.</summary>
    private void EnsureClashRequest()
    {
        ChannelAccess.Request? request = _clashRequest;
        bool owed = HasClashReady();
        if (request is null)
        {
            if (owed)
            {
                _clashRequest = new ChannelAccess.Request();
                _access.Enqueue(_clashRequest, TransmitClass.Ordinary);
            }
        }
        else if (request.State == ChannelAccess.RequestState.Done)
        {
            if (owed)
            {
                _access.Enqueue(request, TransmitClass.Ordinary);
            }
        }
        else if (request.State == ChannelAccess.RequestState.Waiting && !_clashBuilt && !owed)
        {
            _access.Remove(request);
        }
    }

    /// <summary>
    /// The request has been granted and the channel is free: build the Clash that has waited longest, an identification burst like any other (the callsign, the capability word, the station id) with the Clash item after the id. At the first-contact format,
    /// as an identification burst goes. Returns false when none is owed after all.
    /// </summary>
    private bool PrepareClash(ChannelAccess.Request request)
    {
        if (_clashBuilt)
        {
            return true;
        }

        int first = -1;
        for (int i = 0; i < _clashesOwed.Count; i++)
        {
            if (_clashesOwed[i].ReadyAt <= SampleCount && (first < 0 || _clashesOwed[i].ReadyAt < _clashesOwed[first].ReadyAt))
            {
                first = i;
            }
        }

        if (first < 0)
        {
            return false;
        }

        (int number, uint id, _) = _clashesOwed[first];
        _clashesOwed.RemoveAt(first);
        int format = StandardFormats.FirstContact;
        _renderer ??= new BurstRenderer();
        PayloadCodec.WriteKey(_stationId, _idItemValue);
        PayloadCodec.WriteClash(number, id, _clashItemValue);
        _identItems[0] = new ExtensionItem(PayloadCodec.StationIdTag, _idItemValue);
        _identItems[1] = new ExtensionItem(PayloadCodec.ClashTag, _clashItemValue);
        int length = PayloadCodec.PackIdentification(System.Text.Encoding.ASCII.GetBytes(Callsign!), CapabilityWord, _identItems, _payload);
        IdentificationPayloadBuilt?.Invoke(_payload.AsSpan(0, length).ToArray());
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        int burstNumber = _burstNumber;
        _burstNumber = (_burstNumber + 1) & 15;
        int total = _renderer.SamplesFor(format, symbols);
        float[] audio = RentAudio(total);
        _renderer.RenderData(BurstHeader.Identification(_number, format, symbols, burstNumber), _payload.AsSpan(0, length), audio);
        _clashAudio = audio;
        _clashBuilt = true;
        _clashNumber = number;
        _clashId = id;
        request.Samples = audio.AsMemory(0, total);
        request.Modem = new ModemSentEvent(ModemSentKind.Clash, format);
        return true;
    }

    private bool HasClashReady()
    {
        foreach ((int _, uint _, long readyAt) in _clashesOwed)
        {
            if (readyAt <= SampleCount)
            {
                return true;
            }
        }

        return false;
    }

    private void OnClashBurstStarted()
    {
        _clashesSent++;
        if (WantsFlow())
        {
            Flow(TraceText.ToldToMove(_clashNumber));
        }

        // The Clash has been said: what was remembered as the last id heard on the number is cleared, so that the next identification heard there is only recorded.
        _idKnown[_clashNumber] = false;
    }

    private void OnClashBurstEnded()
    {
        DiscardClash();
        EnsureClashRequest();
    }
}
