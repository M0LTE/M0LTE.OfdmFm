namespace M0LTE.OfdmFm;

// Step 6 of docs/station-design.md: the stations tell each other what the path supports, and the host is told when a burst it sent was heard and not decoded.
//
// What a verdict leaves behind. For each neighbour the station holds at most one report it owes and has not built yet (a slot, the newest verdict replacing the one before it), of one of three kinds:
//   Failed  a payload addressed to this station did not decode. Goes alone, as soon as the channel has been clear for the first slot's time, however late the verdict is (decoding takes time). Never cancelled by data.
//   Drop    a burst addressed to this station decoded and the recommendation for its sender fell below what the sender was last told. Goes alone in the first slot; cancelled if data to that station carrying a recommendation goes first.
//   Asked   the burst decoded and its prelude said "report wanted". The recommendation rides on whatever the host sends back to that station; if nothing has gone by AskedReportWaitSamples after the burst's end, it goes alone.
// Nothing is owed for a burst addressed to everyone or to another station: the rules return no report for those and the station asks for none.
//
// One transmit request with the channel-access rules (class Report) stands for every owed report that is ready to go. When it is granted, the station picks the oldest ready one, decides what goes (the same moment a data burst's prelude
// is fixed, so ReportFor is called there, and for an asked report only there), renders it and hands it over. Like a data burst, a report once built is kept: if the request is withdrawn it is handed over again unchanged.

public sealed partial class Station
{
    /// <summary>
    /// How long a requested report ("report wanted" on a burst that decoded) waits for data going back to that station to ride on, from the end of the burst it answers: 1.5 s, in samples. The prototype's figure, kept fixed (the protocol says
    /// only "once the first slot has passed"; ordinary data goes 0.8 s after the channel was last busy, so it has the better part of a second to take the report). The wait is from the burst's end, not from the verdict, which is later by the decode time.
    /// </summary>
    internal const int AskedReportWaitSamples = 72000;

    private enum OwedKind : byte
    {
        Failed,
        Drop,
        Asked,
    }

    private struct OwedSlot
    {
        public bool Active;
        public OwedKind Kind;
        public int? Format;
        public long BurstEnd;
        public long Due;
        public long Sequence;
    }

    /// <summary>The lone report the station has built and not finished with: handed to the audio layer, on air, or withdrawn and waiting to be handed over again.</summary>
    private sealed class BuiltReport
    {
        public bool Built;
        public bool Started;
        public int Dest;
        public OwedKind Kind;
        public int Recommended;
        public long Since;

        /// <summary>True when the burst is an announcement (a data burst with no datagrams), not a lone report.</summary>
        public bool Announcement;

        /// <summary>For an announcement: the key of the address it tells.</summary>
        public uint AnnounceKey;
        public float[]? Audio;
        public int Length;
    }

    /// <summary>The latest burst sent to a station: the address it was for, the tags of its datagrams and the sample it ended at (the audio layer's report). What a "failed" report from that station can be about.</summary>
    private sealed class SentRecord
    {
        public LinkAddress Address;
        public readonly List<HostTag> Tags = [];
        public long EndedAt = -1;
    }

    private readonly OwedSlot[] _owedSlots = new OwedSlot[64];
    private readonly BuiltReport _report = new();
    private readonly SentRecord?[] _sent = new SentRecord?[64];
    private ChannelAccess.Request? _reportRequest;
    private Action _onAskedTimer = null!;
    private TimerHandle _askedTimer;
    private long _askedTimerAt = -1;
    private long _owedSequence;

    /// <summary>The on-air number of the station the burst whose <see cref="ModemSent"/> is being raised is for (0 for everyone). For tests: no host sees it.</summary>
    internal int ModemSentTo { get; private set; }

    private void InitialiseReports() => _onAskedTimer = OnAskedTimer;

    /// <summary>An announcement owed: the station that sent a burst to everyone for one of this station's own addresses, the key of that address, and when it goes alone if nothing carries it first.</summary>
    private readonly record struct Announcement(int Station, uint Key, long Due);

    private readonly List<Announcement> _announcements = [];

    /// <summary>
    /// A burst to everyone for one of this station's own addresses has been decoded (it ended at <paramref name="burstEnd"/>): owe its sender an announcement of where the address lives, a data burst with no datagrams and a From run of count 0.
    /// It rides on data if it can: a data burst built for that station within <see cref="AskedReportWaitSamples"/> whose From runs include the key covers it. Otherwise it goes alone when that wait runs out. One is owed for each (station, key); a newer one replaces it.
    /// </summary>
    private void OweAnnouncement(int station, uint key, long burstEnd)
    {
        var owed = new Announcement(station, key, burstEnd + AskedReportWaitSamples);
        for (int i = 0; i < _announcements.Count; i++)
        {
            if (_announcements[i].Station == station && _announcements[i].Key == key)
            {
                _announcements[i] = owed;
                RequestReportCheck();
                return;
            }
        }

        _announcements.Add(owed);
        RequestReportCheck();
    }

    /// <summary>A data burst is being built for station <paramref name="dest"/>: an announcement owed to it whose key is in the burst's From runs is covered and is not sent.</summary>
    private void CoverAnnouncements(int dest)
    {
        for (int i = _announcements.Count - 1; i >= 0; i--)
        {
            if (_announcements[i].Station != dest)
            {
                continue;
            }

            foreach (Pending p in _burst.Items)
            {
                if (p.From == _announcements[i].Key)
                {
                    if (WantsDetail())
                    {
                        Detail(TraceText.AnnouncementCovered(AddressText(_announcements[i].Key), StationName(dest)));
                    }

                    _announcements.RemoveAt(i);
                    ReportsCovered++;
                    break;
                }
            }
        }
    }

    /// <summary>The announcement that is due and has waited longest, or -1.</summary>
    private int NextAnnouncement()
    {
        int best = -1;
        for (int i = 0; i < _announcements.Count; i++)
        {
            if (_announcements[i].Due <= SampleCount && (best < 0 || _announcements[i].Due < _announcements[best].Due))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Announcements owed and not yet sent, for a test.</summary>
    internal int AnnouncementsOwed => _announcements.Count;

    /// <summary>
    /// Reports owed to neighbours that have not yet gone on air (a failure or a drop; a requested report is not listed), by neighbour, the newest for each, the one built and waiting for the audio layer included.
    /// </summary>
    internal IReadOnlyList<OwedReport> OwedReports
    {
        get
        {
            var list = new List<OwedReport>();
            for (int who = 1; who < _owedSlots.Length; who++)
            {
                OwedSlot slot = _owedSlots[who];
                if (slot.Active && slot.Kind != OwedKind.Asked)
                {
                    list.Add(new OwedReport(who, new LinkReport(slot.Format, slot.Kind == OwedKind.Failed), slot.BurstEnd));
                }
            }

            if (_report.Built && !_report.Started && !_report.Announcement && _report.Kind != OwedKind.Asked)
            {
                list.Add(new OwedReport(_report.Dest, new LinkReport(_report.Recommended, _report.Kind == OwedKind.Failed), _report.Since));
            }

            list.Sort((a, b) => a.Station.CompareTo(b.Station));
            return list;
        }
    }

    /// <summary>Requested reports waiting for data to ride on or for their time to run out.</summary>
    internal int AskedReportsOwed
    {
        get
        {
            int count = 0;
            foreach (OwedSlot slot in _owedSlots)
            {
                if (slot.Active && slot.Kind == OwedKind.Asked)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Lone reports built so far (each one handed to the audio layer at least once).</summary>
    internal long ReportsBuilt { get; private set; }

    /// <summary>How many reports of each kind a verdict has owed, for the tests: failed, drop, requested.</summary>
    internal (long Failed, long Drop, long Asked) ReportsOwedCount => (_owedFailed, _owedDrop, _owedAsked);

    /// <summary>Reports owed and then not sent alone because data going to that station carried the recommendation (drop or requested), or because the host's expedited datagram took the slot.</summary>
    internal long ReportsCovered { get; private set; }

    private long _owedFailed;
    private long _owedDrop;
    private long _owedAsked;

    // ---- what a verdict owes ----

    /// <summary>
    /// A burst addressed to this station has been judged. <paramref name="owed"/> is what the rules returned (a report "failed" or "drop"); <paramref name="asked"/> is true when the burst decoded and its prelude said "report wanted".
    /// A newer report owed replaces an unsent older one for the same station; a requested report is owed only if nothing else is.
    /// </summary>
    private void NoteVerdict(int sender, long burstEnd, LinkReport? owed, bool asked, bool decoded)
    {
        if (Callsign is null)
        {
            // A listener only: nothing is owed, so nothing stale goes out once an identity is set.
            return;
        }

        if (decoded && owed is null && _owedSlots[sender] is { Active: true, Kind: OwedKind.Failed })
        {
            // The failed bit says "your last burst reached me and I could not decode it". A later burst from the same station has now decoded, so the last one did not fail: the report that goes is the recommendation alone, as a drop is.
            // Sent as failed, the sender's hint would name the burst that arrived and not the one that did not.
            _owedSlots[sender].Kind = OwedKind.Drop;
            _owedFailed--;
            _owedDrop++;
        }

        if (owed is { } report)
        {
            if (report.Format is null)
            {
                // The rules have nothing to recommend yet: "the modem sends nothing".
                return;
            }

            Put(sender, report.Failed ? OwedKind.Failed : OwedKind.Drop, report.Format, burstEnd, burstEnd);
            if (WantsDetail())
            {
                Detail(TraceText.OwingReport(StationName(sender), report.Failed ? "its burst was not decoded" : "the path from it got worse"));
            }
        }
        else if (asked && _link.TryGetNeighbour(sender, out LinkNeighbour? nb) && nb?.Recommended is not null)
        {
            // Peeked, not taken: ReportFor marks the station as told, which is for the moment the recommendation goes on the air.
            if (_owedSlots[sender].Active)
            {
                return;
            }

            Put(sender, OwedKind.Asked, null, burstEnd, burstEnd + AskedReportWaitSamples);
            if (WantsDetail())
            {
                Detail(TraceText.OwingReport(StationName(sender), "it asked for one"));
            }
        }
        else
        {
            return;
        }

        RequestReportCheck();
    }

    private bool _applyingResults;
    private bool _reportCheckDeferred;

    /// <summary>
    /// A verdict has owed something. While results are being applied the request with the channel-access rules waits until all of them are: a channel that is clear grants it at once, the report would be built before the verdict of a later burst
    /// from the same station, and could not take that burst into account (a failed report about an older burst, sent when a newer one had decoded).
    /// </summary>
    private void RequestReportCheck()
    {
        if (_applyingResults)
        {
            _reportCheckDeferred = true;
        }
        else
        {
            EnsureReportRequest();
        }
    }

    private void Put(int who, OwedKind kind, int? format, long burstEnd, long due)
    {
        switch (kind)
        {
            case OwedKind.Failed:
                _owedFailed++;
                break;
            case OwedKind.Drop:
                _owedDrop++;
                break;
            default:
                _owedAsked++;
                break;
        }

        _owedSlots[who] = new OwedSlot { Active = true, Kind = kind, Format = format, BurstEnd = burstEnd, Due = due, Sequence = _owedSequence++ };
    }

    /// <summary>A data burst to <paramref name="dest"/> carries the recommendation in its prelude: a report owed for a drop or a request is covered. A failed one is not (it always goes alone).</summary>
    private void DropCoveredReport(int dest)
    {
        if (_owedSlots[dest] is { Active: true, Kind: not OwedKind.Failed })
        {
            _owedSlots[dest].Active = false;
            ReportsCovered++;
            if (WantsDetail())
            {
                Detail(TraceText.ReportCovered(StationName(dest)));
            }
        }
    }

    private bool IsReady(in OwedSlot slot) => slot.Active && (slot.Kind != OwedKind.Asked || slot.Due <= SampleCount);

    private int NextReady()
    {
        int best = -1;
        for (int who = 1; who < _owedSlots.Length; who++)
        {
            if (IsReady(_owedSlots[who]) && (best < 0 || _owedSlots[who].Sequence < _owedSlots[best].Sequence))
            {
                best = who;
            }
        }

        return best;
    }

    // ---- the request with the channel-access rules ----

    /// <summary>Keeps the one request that stands for the reports ready to go: made when one is ready and none is outstanding, taken away if none is left, and the timer for the next requested report set.</summary>
    private void EnsureReportRequest()
    {
        bool ready = NextReady() >= 0 || NextAnnouncement() >= 0;
        ChannelAccess.Request? request = _reportRequest;
        if (request is null)
        {
            if (ready)
            {
                _reportRequest = new ChannelAccess.Request();
                _access.Enqueue(_reportRequest, TransmitClass.Report);
            }
        }
        else if (request.State == ChannelAccess.RequestState.Done)
        {
            if (ready)
            {
                _access.Enqueue(request, TransmitClass.Report);
            }
        }
        else if (request.State == ChannelAccess.RequestState.Waiting && !_report.Built && !ready)
        {
            _access.Remove(request);
        }

        ScheduleAskedTimer();
    }

    private void ScheduleAskedTimer()
    {
        long due = long.MaxValue;
        foreach (OwedSlot slot in _owedSlots)
        {
            if (slot.Active && slot.Kind == OwedKind.Asked && slot.Due > SampleCount && slot.Due < due)
            {
                due = slot.Due;
            }
        }

        foreach (Announcement a in _announcements)
        {
            if (a.Due > SampleCount && a.Due < due)
            {
                due = a.Due;
            }
        }

        if (due == long.MaxValue)
        {
            if (_askedTimerAt >= 0)
            {
                CancelTimer(_askedTimer);
                _askedTimerAt = -1;
            }

            return;
        }

        if (_askedTimerAt == due)
        {
            return;
        }

        if (_askedTimerAt >= 0)
        {
            CancelTimer(_askedTimer);
        }

        _askedTimer = ScheduleAt(due, _onAskedTimer);
        _askedTimerAt = due;
    }

    private void OnAskedTimer()
    {
        _askedTimerAt = -1;
        EnsureReportRequest();
    }

    /// <summary>
    /// The request has been granted and the channel is free: decide which report goes and build it. A drop or a requested report is not sent alone if the host has an expedited datagram queued for that station, which goes in this slot
    /// and carries the recommendation in its prelude (the data goes first). Returns false when nothing is left to send.
    /// </summary>
    private bool PrepareReport(ChannelAccess.Request request)
    {
        if (_report.Built)
        {
            return true;
        }

        for (;;)
        {
            int dest = NextReady();
            if (dest < 0)
            {
                return BuildAnnouncement(request);
            }

            if (_owedSlots[dest].Kind != OwedKind.Failed && HasQueuedAt(dest, expeditedOnly: true))
            {
                _owedSlots[dest].Active = false;
                ReportsCovered++;
                if (WantsDetail())
                {
                    Detail(TraceText.ReportCovered(StationName(dest)));
                }

                continue;
            }

            if (BuildReport(dest, request))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Builds the lone report for <paramref name="dest"/>. It opens the way this station's own data to that station would and at the same drive: the format <c>Choose</c> gives now (it changes nothing but the forgetting of a neighbour
    /// unheard for ten minutes, and this one has just been heard), its opening and its drive. The recommendation is the one the rules returned with the verdict (they have recorded it as told), or for a requested report whatever
    /// <c>ReportFor</c> gives now, which is when it records the station as told.
    /// </summary>
    private bool BuildReport(int dest, ChannelAccess.Request request)
    {
        OwedSlot slot = _owedSlots[dest];
        _owedSlots[dest].Active = false;
        double now = ClockSeconds(SampleCount);
        LinkChoice choice = _link.Choose(dest, now);
        int? recommended = slot.Kind == OwedKind.Asked ? _link.ReportFor(dest) : slot.Format;
        if (recommended is not int format)
        {
            return false;
        }

        RaiseLinkState(dest, now); // what this station has told them may have changed
        if (WantsFlow())
        {
            Flow(TraceText.Told(StationName(dest), StandardFormats.Rate(format), slot.Kind == OwedKind.Failed));
        }

        FormatSpec spec = StandardFormats.Describe(choice.Format);
        _renderer ??= new BurstRenderer();
        int total = _renderer.ReportSamples(spec.Opening);
        float[] audio = RentAudio(total);
        _renderer.RenderReport(BurstHeader.Report(_number, dest, format, slot.Kind == OwedKind.Failed), spec.Opening, spec.DriveDb, audio);
        _report.Built = true;
        _report.Started = false;
        _report.Announcement = false;
        _report.Dest = dest;
        _report.Kind = slot.Kind;
        _report.Recommended = format;
        _report.Since = slot.BurstEnd;
        _report.Audio = audio;
        _report.Length = total;
        ReportsBuilt++;
        request.Samples = audio.AsMemory(0, total);
        request.Modem = new ModemSentEvent(ModemSentKind.Report, choice.Format);
        return true;
    }

    /// <summary>
    /// Builds the announcement that is due: a data burst to the station that asked, with the recommendation for it in the prelude if there is one, "report wanted" not set, a From item of one run with the address's key, a protocol byte of 0 and a count of 0,
    /// and no datagrams, at the format the rules choose for that station. Returns false when none is due after all.
    /// </summary>
    private bool BuildAnnouncement(ChannelAccess.Request request)
    {
        int i = NextAnnouncement();
        if (i < 0)
        {
            return false;
        }

        Announcement a = _announcements[i];
        _announcements.RemoveAt(i);
        double now = ClockSeconds(SampleCount);
        int format = _link.Choose(a.Station, now).Format;
        int? recommended = _link.ReportFor(a.Station);
        PayloadCodec.WriteRun(a.Key, 0, 0, _fromValue);
        _addressItems[0] = new ExtensionItem(PayloadCodec.FromTag, _fromValue.AsMemory(0, PayloadCodec.RunBytes));
        int length = PayloadCodec.Pack(new PayloadPrelude(false, recommended, false, null), _addressItems.AsSpan(0, 1), default, _payload);
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        int number = _burstNumber;
        _burstNumber = (_burstNumber + 1) & 15;
        _renderer ??= new BurstRenderer();
        int total = _renderer.SamplesFor(format, symbols);
        float[] audio = RentAudio(total);
        _renderer.RenderData(BurstHeader.Data(_number, a.Station, format, symbols, number), _payload.AsSpan(0, length), audio);
        _report.Built = true;
        _report.Started = false;
        _report.Announcement = true;
        _report.AnnounceKey = a.Key;
        _report.Dest = a.Station;
        _report.Kind = OwedKind.Drop;
        _report.Audio = audio;
        _report.Length = total;
        request.Samples = audio.AsMemory(0, total);
        request.Modem = new ModemSentEvent(ModemSentKind.Announcement, format);
        if (recommended is not null)
        {
            RaiseLinkState(a.Station, now);
        }

        if (WantsFlow())
        {
            Flow(TraceText.ToldHere(StationName(a.Station), AddressText(a.Key)));
        }

        return true;
    }

    private void OnReportBurstStarted() => _report.Started = true;

    /// <summary>
    /// The lone report has ended. The station that sent it goes straight on with its own data to that station, if it has any and is not still waiting for an answer to a burst of its own (it heard the other station last,
    /// so the turn is its own): the ordinary wait is replaced by the first slot's. The station that heard it leaves the channel clear for a second (step 4).
    /// </summary>
    private void OnReportBurstEnded(long sampleIndex)
    {
        _ = sampleIndex;
        int dest = _report.Dest;
        if (_report.Audio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        _report.Audio = null;
        _report.Built = false;
        _report.Started = false;
        if (HasQueuedAt(dest, expeditedOnly: false) && !AwaitingAnswerFrom(dest))
        {
            if (WantsDetail())
            {
                Detail(TraceText.GoingStraightOn(StationName(dest)));
            }

            _access.GoStraightOn();
        }

        EnsureReportRequest();
    }

    /// <summary>True while a burst of this station's to <paramref name="dest"/> that asked for a report has had no word from that station since it ended and its answer wait has not run out.</summary>
    internal bool AwaitingAnswerFrom(int dest)
    {
        foreach ((long _, int who, long ended) in _answerChecks)
        {
            if (who == dest && _heardAt[dest] <= ended)
            {
                return true;
            }
        }

        return false;
    }

    // ---- what this station sent, for the hint ----

    private void RememberBurst(int dest, LinkAddress address, long endedAt)
    {
        SentRecord record = _sent[dest] ??= new SentRecord();
        record.Address = address;
        record.Tags.Clear();
        foreach (Pending p in _burst.Items)
        {
            record.Tags.Add(p.Tag);
        }

        record.EndedAt = endedAt;
    }

    /// <summary>
    /// A station reported "failed". If the latest burst this station sent to it ended before the report's first training sample, that is the burst it could not decode: <see cref="BurstNotDecoded"/> is raised with the tags of its datagrams,
    /// once. A report that began before the burst ended (or at its last sample) cannot be about it: the other station cannot have decoded, or failed to decode, a burst it had not finished receiving.
    /// </summary>
    private void RaiseNotDecoded(int station, long reportStart)
    {
        SentRecord? record = _sent[station];
        if (record is null || record.Tags.Count == 0 || record.EndedAt > reportStart)
        {
            return;
        }

        HostTag[] tags = [.. record.Tags];
        record.Tags.Clear();
        BurstNotDecoded?.Invoke(new BurstNotDecodedEvent(record.Address, tags));
        if (WantsDetail())
        {
            Detail(TraceText.HostToldNotDecoded(DestText(record.Address), tags.Length));
        }
    }
}
