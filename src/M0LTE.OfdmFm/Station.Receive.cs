using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace M0LTE.OfdmFm;

// Step 3 of docs/station-design.md: reception. Audio in, datagrams and adaptation calls out.
//
// Who touches what. The audio thread (whoever calls Push, Skip or DeliverResults) owns everything not under _gate: the ring's writes, the detector, the candidate list, the burst table,
// the adaptation rules and every event. Workers (RunOne) touch only what is under _gate, the ring's reads (checked after the copy), their own receivers and the burst job they were handed.
//
// Why the pipeline looks as it does. The same audio must give the same events and the same adaptation state however the audio is cut into blocks and however fast the jobs run, so:
//  * the detector works in frames, whatever the block size, and the station feeds it in pieces that end at each frame's end; candidates are taken after each frame;
//  * a candidate becomes eligible for a header read only at a frame end (_boundary), once its opening is in the ring; reads go one at a time in the order candidates came, so that
//    the decision "this candidate lies inside a burst already known, so ignore it" is made with every earlier candidate settled;
//  * the threshold estimate is paused over a burst when its header read has been taken in, which is before the next frame is correlated unless a worker is starved for a frame's worth of audio
//    (docs/station-design.md, "Step 3 as built"); everything else the station does is a function of the audio alone;
//  * results are applied in the order bursts started, when no earlier candidate or burst can still turn up (the detector's CandidateFloor), with the burst's end as the time.
public sealed partial class Station
{
    /// <summary>Samples the ring keeps: 16.4 s, the longest burst (261 symbols, 11.5 s) with its lead-in and the wait for a candidate to come out of the detector, and 4 s over.</summary>
    internal const int RingSamples = 786432;

    /// <summary>More bursts than this waiting for a worker, and the oldest is dropped.</summary>
    internal const int MaxQueuedBursts = 4;

    /// <summary>
    /// Candidates kept waiting for a header read. A station whose workers do not run must not grow without bound: past this the oldest are dropped, unread. A strong 11.5 s burst gives about 300,
    /// nearly all of them ignored unread once its header is in, so a station whose workers keep up never comes near this.
    /// </summary>
    internal const int MaxCandidatesWaiting = 256;

    /// <summary>Samples before a burst's first one that are copied with it: the receiver's lead-in.</summary>
    internal const int PrePadSamples = BurstRenderer.DefaultLeadSamples;

    /// <summary>Samples after a burst's last one that are copied with it, for the resampler's reach.</summary>
    internal const int PostPadSamples = 512;

    private const int Sym = BurstRenderer.SymbolSamples;
    private const int Frame = StreamingBurstDetector.TransformSize;
    private const int HeaderBitCount = BurstHeaderCodec.HeaderBits;
    private const int Carriers = BurstRenderer.Carriers;
    private const int MaxNoted = 64;

    /// <summary>The overhead a datagram has in a burst to one station: the largest prelude (4 bytes), the block of items with one run in its From (9 bytes) and the datagram's own length (2 bytes).</summary>
    private const int DatagramOverhead = 4 + 9 + 2;

    private readonly bool _inline;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[RingSamples];
    private readonly float[] _silence = new float[4096];
    private readonly StreamingBurstDetector _detector = new();
    private LinkAdaptation _link = null!;
    private readonly StationShown[] _stationShown = new StationShown[64];
    private readonly AddressTable _table = new();
    private readonly List<Candidate> _candidates = [];
    private readonly List<BurstRecord> _bursts = [];
    private readonly List<Extent> _extents = [];
    private readonly List<NotedHeader> _noted = [];
    private readonly Stack<BurstJob> _freeJobs = new();
    private readonly Stack<BurstRecord> _freeRecords = new();
    private readonly List<BurstRecord> _jobQueue = [];
    private readonly Stack<WorkerContext> _freeContexts = new();

    private long _ringEnd;
    private long _framesSeen;
    private long _boundary;
    private long _lastMark;
    private long _burstSequence;
    private bool _notify;

    // Under _gate: the one header job in flight.
    private HeaderPhase _headerPhase;
    private Candidate _headerCandidate;
    private HeaderStatus _headerStatus;
    private BurstHeader _headerRead;
    private readonly byte[] _headerBits = new byte[HeaderBitCount];

    private long _candidatesSeen;
    private long _candidatesIgnored;
    private long _candidatesDropped;
    private long _headerReads;
    private long _headersLost;
    private long _burstsDropped;
    private long _burstsLost;
    private long _burstsApplied;
    private long _burstsMerged;
    private long _pausedSamples;

    private enum HeaderPhase
    {
        Idle,
        Released,
        Running,
        Done,
    }

    private enum HeaderStatus
    {
        Unreadable,
        Read,
        Lost,
    }

    private enum BurstState
    {
        /// <summary>Header read; the burst's last sample is not in yet.</summary>
        Known,

        /// <summary>Copied; waiting for a worker.</summary>
        Queued,

        /// <summary>A worker has it.</summary>
        Running,

        /// <summary>Received (decoded or not).</summary>
        Done,

        /// <summary>Taken off the queue because more than four were waiting.</summary>
        Dropped,

        /// <summary>The ring had overwritten its start before its header was read or before it was copied.</summary>
        Lost,

        /// <summary>A report: nothing to receive.</summary>
        Report,
    }

    private readonly record struct Candidate(long Index, FormatOpening Opening);

    private readonly record struct Extent(long Start, long End, BurstHeader Header);

    /// <summary>Whether a station has been announced through <see cref="LinkState"/> (some address of the host's lives at it) and whether it has been forgotten since.</summary>
    private record struct StationShown(bool Shown, bool Gone);

    private sealed class BurstRecord
    {
        public long Sequence;
        public long Start;
        public long End;
        public BurstHeader Header;
        public byte[] Bits = new byte[HeaderBitCount];
        public FormatOpening Opening;
        public volatile BurstState State;
        public BurstJob? Job;
    }

    /// <summary>A burst's audio and the room for its result. Pooled: the arrays only grow.</summary>
    private sealed class BurstJob
    {
        public float[] Samples = [];
        public byte[] Payload = [];
        public readonly double[] Quality = new double[Carriers];
        public int Length;
        public int Pre;
        public bool Decoded;
        public int PayloadBytes;
        public bool Sudden;
        public bool HasQuality;
    }

    /// <summary>What one thread running a job needs. Large, so made on first use and kept.</summary>
    private sealed class WorkerContext
    {
        public readonly HeaderReader Reader = new();
        public readonly TrackedReceiver Tracked = new();
        public readonly float[] Scratch = new float[HeaderReader.OpeningSamples(FormatOpening.UpTo1500Hz)];
    }

    /// <summary>Why a header that read was noted and not acted on.</summary>
    internal enum NoteReason
    {
        /// <summary>A version this code does not know: nothing else of the header means anything.</summary>
        UnknownVersion,

        /// <summary>A version 0 burst type this code does not receive (one of the spare types).</summary>
        UnsupportedKind,

        /// <summary>A header that cannot describe a burst: sender 0, or a data burst the receiver cannot read.</summary>
        Invalid,
    }

    /// <summary>A header whose check passed and that the station took no further action on (step 4 reads these for busy detection).</summary>
    internal readonly record struct NotedHeader(long Index, FormatOpening Opening, BurstHeader Header, NoteReason Reason);

    /// <summary>A report this station owes a neighbour, from the rules, that has not yet gone on air (step 6 sends it: see <c>Station.Reports.cs</c>).</summary>
    /// <param name="Station">The neighbour's number.</param>
    /// <param name="Report">The report: "failed" or "drop".</param>
    /// <param name="SinceSample">The sample the burst that caused it ended at.</param>
    internal readonly record struct OwedReport(int Station, LinkReport Report, long SinceSample);

    /// <summary>A job a worker is about to run, for a test's hook.</summary>
    /// <param name="IsHeader">True for a header read, false for a burst's receive.</param>
    /// <param name="Start">The candidate's index or the burst's first sample.</param>
    internal readonly record struct JobStart(bool IsHeader, long Start);

    /// <summary>One call into the adaptation rules, as made, for a test's hook.</summary>
    internal readonly record struct AdaptationCall(bool IsReport, int Sender, int Dest, int Format, bool Decoded, double[] Quality, double Now, double? FootingDb, bool Sudden, LinkReport? TheirReport, int? Capability, LinkReport? Returned);

    /// <summary>A test's hook, called by a worker just before it runs a job (it may block, to hold the job until the test lets it go).</summary>
    internal Action<JobStart>? JobStarting { get; set; }

    /// <summary>
    /// A test's seam: while set, no burst's receive job is taken (header reads still are), so that a verdict can be made to arrive as late as the test likes without any real time passing. Setting it back and calling
    /// <see cref="DeliverResults"/> (or pushing) lets the held receives run, inline or on a worker. Audio thread.
    /// </summary>
    internal bool ReceivesHeld
    {
        get => _receivesHeld;
        set => _receivesHeld = value;
    }

    private volatile bool _receivesHeld;

    /// <summary>A test's hook, called just after each call into the adaptation rules.</summary>
    internal Action<AdaptationCall>? AdaptationCalled { get; set; }

    /// <summary>The adaptation rules this station drives.</summary>
    internal LinkAdaptation Adaptation => _link;

    /// <summary>Headers that read and were not acted on, oldest first, the latest 64.</summary>
    internal IReadOnlyList<NotedHeader> NotedHeaders => _noted;

    /// <summary>Candidates the detector gave the station.</summary>
    internal long CandidatesSeen => Volatile.Read(ref _candidatesSeen);

    /// <summary>Candidates dropped without a header read because they lay inside a burst already known.</summary>
    internal long CandidatesIgnored => Volatile.Read(ref _candidatesIgnored);

    /// <summary>How many candidates may wait for a header read: <see cref="MaxCandidatesWaiting"/>, lower for a test.</summary>
    internal int CandidateLimit { get; set; } = MaxCandidatesWaiting;

    /// <summary>Candidates dropped unread because more than <see cref="MaxCandidatesWaiting"/> were waiting.</summary>
    internal long CandidatesDropped => Volatile.Read(ref _candidatesDropped);

    /// <summary>Candidates waiting for a header read, and the one being read.</summary>
    internal int CandidatesWaiting => _candidates.Count;

    /// <summary>Header reads begun.</summary>
    internal long HeaderReads => Volatile.Read(ref _headerReads);

    /// <summary>Candidates whose opening the ring had overwritten before a worker could read it.</summary>
    internal long HeadersLost => Volatile.Read(ref _headersLost);

    /// <summary>Bursts dropped because more than four were waiting for a worker. Nothing is raised for one.</summary>
    internal long BurstsDropped => Volatile.Read(ref _burstsDropped);

    /// <summary>Bursts whose start the ring had overwritten before they could be copied.</summary>
    internal long BurstsLost => Volatile.Read(ref _burstsLost);

    /// <summary>Bursts whose result has been applied.</summary>
    internal long BurstsApplied => Volatile.Read(ref _burstsApplied);

    /// <summary>Candidates that read a header already known for a burst starting within a symbol, and so were the same burst.</summary>
    internal long BurstsMerged => Volatile.Read(ref _burstsMerged);

    /// <summary>Samples the detector's threshold estimate has been told to ignore: the length of each burst known, less what overlapped one before it.</summary>
    internal long PausedSamples => _pausedSamples;

    /// <summary>Takes a header read from candidate <paramref name="index"/> of <paramref name="opening"/> in as if a worker had read it. For a test of what a header the audio cannot show does (an unknown version).</summary>
    internal void TakeInHeader(long index, FormatOpening opening, BurstHeader header, ReadOnlySpan<byte> bits)
    {
        bits[..HeaderBitCount].CopyTo(_headerBits);
        HandleHeader(new Candidate(index, opening), HeaderStatus.Read, header);
    }

    /// <summary>Bursts known and not yet applied.</summary>
    internal int BurstsInFlight => _bursts.Count;

    /// <summary>True while a header read is released, running or finished and not yet taken in.</summary>
    internal bool HeaderJobPending
    {
        get
        {
            lock (_gate)
            {
                return _headerPhase != HeaderPhase.Idle;
            }
        }
    }

    /// <summary>Bursts waiting for a worker now.</summary>
    internal int BurstsQueued
    {
        get
        {
            lock (_gate)
            {
                return _jobQueue.Count;
            }
        }
    }

    /// <summary>The adaptation rules' time for a sample index: seconds on the sample clock.</summary>
    private static double ClockSeconds(long sample) => sample / (double)SampleRate;

    /// <summary>True when a job waits for <see cref="RunOne"/>: a header read, or a burst to receive. Always false for an inline station. Safe from any thread.</summary>
    public bool HasWork
    {
        get
        {
            lock (_gate)
            {
                return _headerPhase == HeaderPhase.Released || _jobQueue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Runs one job if one is waiting and returns true; false if none was. A header read is taken before a burst's receive, since a read is short and the pause of the threshold estimate waits on it; receives go oldest first. It cannot interrupt a receive already running, so a host should run at least two workers: with one, a header read waits behind a receive that can take seconds.
    /// Safe to call from any number of threads at the same time, the audio thread's included between its own calls. The thread uses a pair of receivers of its own (made the first time, and kept), so a call can take a good part of the
    /// burst's own length on a slow processor. It raises no event and calls nothing outside the library; its result is applied by the audio thread (see <see cref="DeliverResults"/>).
    /// </summary>
    public bool RunOne() => RunJob();

    /// <summary>Runs jobs until none is waiting and returns how many ran. Safe from any number of threads at the same time.</summary>
    public int RunPending()
    {
        int count = 0;
        while (RunJob())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Takes in the jobs finished since the last call and applies every result that is ready, in the order the bursts started: <see cref="Received"/>, <see cref="PathReport"/> and <see cref="LinkState"/> are raised on this thread, and the
    /// adaptation rules are called, each with the burst's end on the sample clock. <see cref="Push"/> and <see cref="Skip"/> do this before they return; call it from the audio thread after workers finish when no audio is coming. A result
    /// is held back until no earlier burst can still turn up, which is a fraction of a second of audio after a short burst ends; the clock moving on releases it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public void DeliverResults()
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            Pump();
            ApplyReadyResults();
        }
        finally
        {
            _advancing = false;
        }
    }

    private void InitialiseReceive()
    {
        _link = new LinkAdaptation();
    }

    // ---- feeding audio ----

    /// <summary>Feeds <paramref name="count"/> samples of audio (silence if <paramref name="audio"/> is empty) to the ring and the detector, in pieces that end at each frame's end.</summary>
    private void FeedAudio(ReadOnlySpan<float> audio, int count)
    {
        int done = 0;
        while (done < count)
        {
            // Before the next samples: take in finished jobs and start the next header read, so that a pause is in before a frame that needs it.
            Pump();
            int n = Math.Min(count - done, _detector.SamplesToNextFrame);
            ReadOnlySpan<float> piece;
            if (audio.IsEmpty)
            {
                n = Math.Min(n, _silence.Length);
                piece = _silence.AsSpan(0, n);
            }
            else
            {
                piece = audio.Slice(done, n);
            }

            RingWrite(piece);
            _detector.Push(piece);
            SampleCount += n;
            done += n;
            if (_detector.FramesProcessed != _framesSeen)
            {
                OnFrame();

                // The results that are ready now are applied now, at the frame's end, whatever the block size: what a result sets going (a lone report, step 6) then starts at a sample that is a function of the audio.
                Pump();
                ApplyReadyResults();
            }
        }
    }

    private void RingWrite(ReadOnlySpan<float> piece)
    {
        int head = (int)(SampleCount % RingSamples);
        int first = Math.Min(piece.Length, RingSamples - head);
        piece[..first].CopyTo(_ring.AsSpan(head));
        piece[first..].CopyTo(_ring);
        Volatile.Write(ref _ringEnd, SampleCount + piece.Length);
    }

    /// <summary>Copies the ring from absolute sample <paramref name="from"/> to <paramref name="destination"/>; the audio thread's own read, which nothing can overwrite.</summary>
    private void RingCopy(long from, Span<float> destination)
    {
        int at = (int)(from % RingSamples);
        int first = Math.Min(destination.Length, RingSamples - at);
        _ring.AsSpan(at, first).CopyTo(destination);
        _ring.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }

    /// <summary>
    /// A worker's read of the ring. The audio thread keeps writing, up to one frame beyond what it has published, so the copy is good only if the start is still a frame's worth inside the ring afterwards.
    /// </summary>
    private bool TryRingCopy(long from, Span<float> destination)
    {
        long end = Volatile.Read(ref _ringEnd);
        if (from < 0 || from + destination.Length > end || end + Frame - from > RingSamples)
        {
            return false;
        }

        RingCopy(from, destination);
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _ringEnd) + Frame - from <= RingSamples;
    }

    /// <summary>A frame has just been correlated: take its candidates, move the boundary, and forget what can no longer matter.</summary>
    private void OnFrame()
    {
        _framesSeen = _detector.FramesProcessed;
        _boundary = SampleCount;

        // One frame's candidates go in by index (then opening), so that a burst's own start is read before the candidates other openings find inside it, which are then ignored unread.
        int first = _candidates.Count;
        while (_detector.TryTake(out DetectedBurst d))
        {
            _candidatesSeen++;
            if (InOwnWindow(d.Index))
            {
                // The audio is muted or full of this station's own signal: nothing to find there.
                _candidatesMuted++;
                continue;
            }

            _candidates.Add(new Candidate(d.Index, d.Opening));
        }

        // (An insertion sort: a frame gives a handful, and List.Sort makes a delegate each time it runs.)
        for (int i = first + 1; i < _candidates.Count; i++)
        {
            Candidate c = _candidates[i];
            int j = i - 1;
            while (j >= first && (_candidates[j].Index > c.Index || (_candidates[j].Index == c.Index && _candidates[j].Opening > c.Opening)))
            {
                _candidates[j + 1] = _candidates[j];
                j--;
            }

            _candidates[j + 1] = c;
        }

        if (_candidates.Count > CandidateLimit)
        {
            int excess = _candidates.Count - CandidateLimit;
            _candidates.RemoveRange(0, excess);
            _candidatesDropped += excess;
        }

        // A candidate can come out up to two frames after the start it names, so a burst is remembered for that long past its end.
        long keepFrom = SampleCount - (4L * Frame);
        for (int i = _extents.Count - 1; i >= 0; i--)
        {
            if (_extents[i].End < keepFrom)
            {
                _extents.RemoveAt(i);
            }
        }

        ForgetOldWindows();
        RefreshPending();
    }

    // ---- pumping: the audio thread takes in finished jobs and starts the next ----

    private void Pump()
    {
        for (;;)
        {
            if (ConsumeHeader())
            {
                continue;
            }

            if (ReleaseHeader())
            {
                if (_inline)
                {
                    RunJob();
                }

                continue;
            }

            break;
        }

        CompleteBursts();
        if (_inline)
        {
            while (RunJob())
            {
            }
        }

        SettleUnreadable();
        RefreshPending();
        if (_notify)
        {
            _notify = false;
            if (!_inline)
            {
                WorkAvailable?.Invoke();
            }
        }
    }

    /// <summary>Takes in the finished header read, if there is one.</summary>
    private bool ConsumeHeader()
    {
        Candidate candidate;
        HeaderStatus status;
        BurstHeader header;
        lock (_gate)
        {
            if (_headerPhase != HeaderPhase.Done)
            {
                return false;
            }

            candidate = _headerCandidate;
            status = _headerStatus;
            header = _headerRead;
            _headerPhase = HeaderPhase.Idle;
        }

        HandleHeader(candidate, status, header);
        return true;
    }

    /// <summary>
    /// Starts the next candidate's header read if none is going: the first in line whose opening is in the ring as of the last frame's end. A candidate inside a burst already known is dropped here,
    /// unread. True if a read was started.
    /// </summary>
    private bool ReleaseHeader()
    {
        lock (_gate)
        {
            if (_headerPhase != HeaderPhase.Idle)
            {
                return false;
            }
        }

        while (_candidates.Count > 0)
        {
            Candidate c = _candidates[0];
            if (InOwnWindow(c.Index))
            {
                _candidates.RemoveAt(0);
                _candidatesMuted++;
                continue;
            }

            // A candidate inside a burst already known, or within a symbol of its start (the same burst found again under another opening, which can be a frame or two later), is not read: reading
            // it could only find the same burst again, and waiting for its opening would hold up the candidates behind it.
            if (IsExplained(c.Index))
            {
                _candidates.RemoveAt(0);
                _candidatesIgnored++;
                continue;
            }

            if (c.Index + HeaderReader.OpeningSamples(c.Opening) > _boundary)
            {
                return false;
            }

            _candidates.RemoveAt(0);

            lock (_gate)
            {
                _headerCandidate = c;
                _headerPhase = HeaderPhase.Released;
            }

            _notify = true;
            return true;
        }

        return false;
    }

    private bool InsideKnownBurst(long index)
    {
        foreach (Extent e in _extents)
        {
            if (index > e.Start + Sym && index < e.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A header read has come back: a new burst, the same as one already known, or something to note.</summary>
    private void HandleHeader(Candidate c, HeaderStatus status, BurstHeader header)
    {
        if (status == HeaderStatus.Lost)
        {
            _headersLost++;
            HoldForUnreadable(c);
            return;
        }

        if (status != HeaderStatus.Read)
        {
            HoldForUnreadable(c);
            return;
        }

        if (!header.IsKnownVersion)
        {
            Note(c, header, NoteReason.UnknownVersion);
            HoldForUnknownVersion();
            return;
        }

        if (header.Kind is not (BurstKind.Data or BurstKind.Report or BurstKind.Identification))
        {
            // An unknown type under a known version still carries its length: the opening and the payload symbols it names.
            Note(c, header, NoteReason.UnsupportedKind);
            HoldForBurst(c.Index + HeaderReader.OpeningSamples(c.Opening) + (header.PayloadSymbols * Sym));
            return;
        }

        if (header.Sender == 0)
        {
            Note(c, header, NoteReason.Invalid);
            HoldForUnreadable(c);
            return;
        }

        int length;
        if (header.Kind is BurstKind.Data or BurstKind.Identification)
        {
            length = PayloadReceiver.SamplesNeeded(header);
            if (length < 0 || StandardFormats.Describe(header.Format).Opening != c.Opening || (header.Kind == BurstKind.Identification && header.Destination != 0))
            {
                // A burst with a payload opens the way its format says (a read under another opening is not a burst), and an identification burst is for everyone.
                Note(c, header, NoteReason.Invalid);
                HoldForUnreadable(c);
                return;
            }
        }
        else
        {
            length = HeaderReader.OpeningSamples(c.Opening);
        }

        // A header with this station's own number, that it did not send, means another station holds it: this one moves (docs/station-numbers.md). Everything else is noted as heard from that number.
        NoteSenderOfHeader(header.Sender);
        if (header.Sender != _number)
        {
            // Anything heard from a station, whatever it was for: the answer wait of step 5 reads this (sample clock at the read).
            _heardAt[header.Sender] = SampleCount;
        }

        foreach (Extent e in _extents)
        {
            if (e.Header == header && Math.Abs(e.Start - c.Index) <= Sym)
            {
                _burstsMerged++;
                HoldForBurst(e.End);
                return;
            }
        }

        BurstRecord record = _freeRecords.Count > 0 ? _freeRecords.Pop() : new BurstRecord();
        record.Sequence = _burstSequence++;
        record.Start = c.Index;
        record.End = c.Index + length;
        record.Header = header;
        record.Opening = c.Opening;
        record.Job = null;
        _headerBits.AsSpan().CopyTo(record.Bits);
        record.State = header.Kind == BurstKind.Report ? BurstState.Report : BurstState.Known;
        int at = _bursts.Count;
        while (at > 0 && _bursts[at - 1].Start > record.Start)
        {
            at--;
        }

        _bursts.Insert(at, record);
        _extents.Add(new Extent(record.Start, record.End, header));
        PauseEstimateOver(record.Start, record.End);
        HoldForBurst(record.End);
    }

    private void Note(Candidate c, BurstHeader header, NoteReason reason)
    {
        if (_noted.Count == MaxNoted)
        {
            _noted.RemoveAt(0);
        }

        _noted.Add(new NotedHeader(c.Index, c.Opening, header, reason));
    }

    /// <summary>
    /// Keeps the detector's threshold estimate from taking values over a burst. Marks go in by sample index and must not go backwards, so a burst that starts inside one already marked
    /// is clipped to begin where the last mark ended.
    /// </summary>
    private void PauseEstimateOver(long start, long end)
    {
        long from = Math.Max(start, _lastMark);
        if (end <= from || _detector.FreeMarks < 2)
        {
            return;
        }

        _detector.PauseEstimate(from);
        _detector.ResumeEstimate(end);
        _lastMark = end;
        _pausedSamples += end - from;
    }

    /// <summary>Makes a receive job of each known burst whose last sample (and its pad) is in the ring.</summary>
    private void CompleteBursts()
    {
        for (int i = 0; i < _bursts.Count; i++)
        {
            BurstRecord r = _bursts[i];
            if (r.State != BurstState.Known || SampleCount < r.End + PostPadSamples)
            {
                continue;
            }

            long oldest = SampleCount - RingSamples;
            long from = Math.Max(Math.Max(0, r.Start - PrePadSamples), oldest);
            if (from > r.Start)
            {
                r.State = BurstState.Lost;
                _burstsLost++;
                continue;
            }

            int total = (int)(r.End + PostPadSamples - from);
            BurstJob job = RentJob(total, PayloadReceiver.PayloadRoom(r.Header));
            RingCopy(from, job.Samples.AsSpan(0, total));
            job.Length = total;
            job.Pre = (int)(r.Start - from);
            job.Decoded = false;
            job.PayloadBytes = 0;
            job.Sudden = false;
            job.HasQuality = false;
            r.Job = job;
            lock (_gate)
            {
                r.State = BurstState.Queued;
                _jobQueue.Add(r);
                while (_jobQueue.Count > MaxQueuedBursts)
                {
                    BurstRecord oldestQueued = _jobQueue[0];
                    _jobQueue.RemoveAt(0);
                    oldestQueued.State = BurstState.Dropped;
                    _burstsDropped++;
                    ReturnJob(oldestQueued.Job!);
                    oldestQueued.Job = null;
                }
            }

            _notify = true;
        }
    }

    private BurstJob RentJob(int samples, int payloadRoom)
    {
        BurstJob job = _freeJobs.Count > 0 ? _freeJobs.Pop() : new BurstJob();
        if (job.Samples.Length < samples)
        {
            job.Samples = new float[samples];
        }

        if (job.Payload.Length < payloadRoom)
        {
            job.Payload = new byte[payloadRoom];
        }

        return job;
    }

    private void ReturnJob(BurstJob job) => _freeJobs.Push(job);

    // ---- the jobs themselves, run by whoever calls RunOne (or Push, for an inline station) ----

    private bool RunJob()
    {
        BurstRecord? burst = null;
        Candidate candidate = default;
        bool header;
        lock (_gate)
        {
            if (_headerPhase == HeaderPhase.Released)
            {
                _headerPhase = HeaderPhase.Running;
                candidate = _headerCandidate;
                header = true;
            }
            else if (_jobQueue.Count > 0 && !ReceivesHeld)
            {
                burst = _jobQueue[0];
                _jobQueue.RemoveAt(0);
                burst.State = BurstState.Running;
                header = false;
            }
            else
            {
                return false;
            }
        }

        WorkerContext context = RentContext();
        try
        {
            JobStarting?.Invoke(new JobStart(header, header ? candidate.Index : burst!.Start));
            if (header)
            {
                RunHeaderRead(context, candidate);
            }
            else
            {
                RunReceive(context, burst!);
            }
        }
        catch
        {
            // Settle the job as failed so that the audio thread is not left waiting for it, then let the caller see the fault.
            lock (_gate)
            {
                if (header)
                {
                    _headerStatus = HeaderStatus.Unreadable;
                    _headerPhase = HeaderPhase.Done;
                }
                else
                {
                    burst!.Job!.Decoded = false;
                    burst.Job.HasQuality = false;
                    burst.State = BurstState.Done;
                }
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _freeContexts.Push(context);
            }
        }

        return true;
    }

    private WorkerContext RentContext()
    {
        lock (_gate)
        {
            if (_freeContexts.Count > 0)
            {
                return _freeContexts.Pop();
            }
        }

        return new WorkerContext();
    }

    private void RunHeaderRead(WorkerContext context, Candidate c)
    {
        int n = HeaderReader.OpeningSamples(c.Opening);
        HeaderStatus status;
        BurstHeader read = default;
        if (!TryRingCopy(c.Index, context.Scratch.AsSpan(0, n)))
        {
            status = HeaderStatus.Lost;
        }
        else
        {
            Interlocked.Increment(ref _headerReads);
            status = context.Reader.TryRead(context.Scratch, 0, c.Opening, out read) ? HeaderStatus.Read : HeaderStatus.Unreadable;
        }

        lock (_gate)
        {
            _headerStatus = status;
            _headerRead = read;
            if (status == HeaderStatus.Read)
            {
                context.Reader.Bits[..HeaderBitCount].CopyTo(_headerBits);
            }

            _headerPhase = HeaderPhase.Done;
        }
    }

    private void RunReceive(WorkerContext context, BurstRecord r)
    {
        BurstJob job = r.Job!;
        ReadOnlySpan<float> capture = job.Samples.AsSpan(0, job.Length);
        PayloadReceiveResult result = context.Tracked.Receive(capture, job.Pre, r.Header, r.Bits, job.Payload);
        job.Sudden = context.Tracked.Sudden;
        if (result.Accepted && result.CheckPassed)
        {
            job.Decoded = true;
            job.PayloadBytes = result.PayloadBytes;
            ReadOnlySpan<double> quality = context.Tracked.Quality;
            if (quality.Length == Carriers)
            {
                quality.CopyTo(job.Quality);
                job.HasQuality = true;
            }
        }
        else
        {
            job.Decoded = false;
            job.HasQuality = context.Tracked.MeasureOpening(capture, job.Pre, r.Header, r.Bits, job.Quality);
        }

        lock (_gate)
        {
            r.State = BurstState.Done;
        }
    }

    // ---- applying results, in order ----

    private bool UnsettledBefore(long start)
    {
        foreach (Candidate c in _candidates)
        {
            if (c.Index < start)
            {
                return true;
            }
        }

        lock (_gate)
        {
            return _headerPhase != HeaderPhase.Idle && _headerCandidate.Index < start;
        }
    }

    private void ApplyReadyResults()
    {
        if (_inline && SampleCount != _boundary)
        {
            // An inline station applies its results at a frame's end only, so that the same audio gives the same events at the same samples however it is cut into blocks (a station with workers applies them as they come).
            return;
        }

        long floor = _detector.CandidateFloor;
        _applyingResults = true;
        try
        {
            ApplyBursts(floor);
        }
        finally
        {
            _applyingResults = false;
        }

        if (_reportCheckDeferred)
        {
            _reportCheckDeferred = false;
            EnsureReportRequest();
        }

        // With nothing earlier to come, a neighbour can be seen to have gone quiet for good.
        long safe = floor;
        if (_bursts.Count > 0)
        {
            safe = Math.Min(safe, _bursts[0].Start);
        }

        foreach (Candidate c in _candidates)
        {
            safe = Math.Min(safe, c.Index);
        }

        lock (_gate)
        {
            if (_headerPhase != HeaderPhase.Idle)
            {
                safe = Math.Min(safe, _headerCandidate.Index);
            }
        }

        CheckGone(ClockSeconds(Math.Min(safe, SampleCount)));
    }

    private void ApplyBursts(long floor)
    {
        while (_bursts.Count > 0)
        {
            BurstRecord r = _bursts[0];
            BurstState state = r.State;
            if (state is BurstState.Known or BurstState.Queued or BurstState.Running)
            {
                break;
            }

            if (floor < r.Start || UnsettledBefore(r.Start))
            {
                break;
            }

            _bursts.RemoveAt(0);
            CheckGone(ClockSeconds(r.End));
            _burstsApplied++;
            switch (state)
            {
                case BurstState.Done when r.Header.Kind == BurstKind.Identification:
                    ApplyIdentification(r);
                    break;
                case BurstState.Done:
                    ApplyData(r);
                    break;
                case BurstState.Report:
                    ApplyReport(r);
                    break;
            }

            if (r.Job is { } job)
            {
                ReturnJob(job);
                r.Job = null;
            }

            _freeRecords.Push(r);
        }
    }

    private void ApplyData(BurstRecord r)
    {
        BurstJob job = r.Job!;
        BurstHeader h = r.Header;
        double now = ClockSeconds(r.End);
        int sender = h.Sender;
        bool mine = _number != 0 && h.Destination == _number;
        bool everyone = h.Destination == 0;
        bool decoded = job.Decoded;
        ReadOnlySpan<double> quality = job.HasQuality ? job.Quality : default;
        double footing = LinkAdaptation.Footing(h.Format, opening: !decoded);
        LinkReport? their = null;
        int? capability = null;
        PayloadPrelude prelude = default;
        bool unpacked = false;
        ReadOnlySpan<byte> payload = job.Payload.AsSpan(0, job.PayloadBytes);
        DatagramReader datagrams = default;
        ExtensionReader items = default;
        if (decoded)
        {
            // Every data burst is unpacked, whoever it is for: its From says where its sender's addresses live.
            unpacked = PayloadCodec.TryUnpack(payload, everyone, out prelude, out items, out datagrams);
            if (unpacked && mine)
            {
                their = new LinkReport(prelude.Recommended, prelude.Failed);
                capability = prelude.Capability;
            }
        }

        LinkReport? owed = _link.HeardData(sender, h.Destination, h.Format, decoded, quality, now, footing, job.Sudden, their, capability);
        AdaptationCalled?.Invoke(new AdaptationCall(false, sender, h.Destination, h.Format, decoded, quality.ToArray(), now, footing, job.Sudden, their, capability, owed));
        if (unpacked)
        {
            // The block's From runs must add up to the datagrams: a burst whose do not (or that has none) is treated as malformed, and nothing is learned from it or delivered from it.
            bool valid = ReadAddressItems(items, datagrams, sender, now, out uint? forKey, out ReadOnlySpan<byte> runs);
            if (mine && prelude.Recommended is int recommended)
            {
                RaisePathReport(sender, recommended, prelude.Failed, now);
            }

            if (valid && (mine || everyone))
            {
                HearingSender = sender;
                double db = MeanQualityDb(job.Quality, StandardFormats.Describe(h.Format).Carriers);
                Addressing addressing = mine ? Addressing.ToThisStation : forKey is null ? Addressing.ToEveryone : Addressing.ForAddressNotYetLocated;
                int run = 0;
                int left = 0;
                uint fromKey = 0;
                byte protocol = 0;
                while (datagrams.TryNext(out ReadOnlySpan<byte> datagram))
                {
                    while (left == 0)
                    {
                        fromKey = PayloadCodec.ReadKey(runs[(run * PayloadCodec.RunBytes)..]);
                        protocol = runs[(run * PayloadCodec.RunBytes) + PayloadCodec.KeyBytes];
                        left = runs[(run * PayloadCodec.RunBytes) + PayloadCodec.KeyBytes + 1];
                        run++;
                    }

                    left--;
                    int offset = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(payload), ref MemoryMarshal.GetReference(datagram));
                    Received?.Invoke(new ReceivedEvent(job.Payload.AsMemory(offset, datagram.Length), fromKey, protocol, addressing, forKey, db));
                }
            }

            if (valid && everyone && forKey is uint asked && _ownKeys.Contains(asked) && Callsign is not null)
            {
                // A burst to everyone for one of this station's own addresses: owe the sender an announcement of where the address lives (it rides on data going back, or goes alone after the requested-report wait).
                OweAnnouncement(sender, asked, r.End);
            }
        }

        if (mine)
        {
            NoteVerdict(sender, r.End, owed, decoded && unpacked && prelude.Wanted, decoded);
        }

        RaiseLinkState(sender, now);
    }

    /// <summary>The on-air number of the station whose burst is being reported to the host, while <see cref="Received"/> or <see cref="Identified"/> is raised. For tests: no host sees it.</summary>
    internal int HearingSender { get; private set; }

    /// <summary>
    /// Reads the items of a decoded data burst's block. The From item's runs must be whole runs (6 bytes each; a count of 0 is an announcement of an address, which belongs to no datagram) whose counts add up to the datagrams in the burst; if they do, each key lives at the sender from now (the latest
    /// burst heard wins) and true is returned, with the runs and the For key if there is one. If they do not (no From, a length that is not whole runs, counts that do not add up), nothing is learned and false is returned. Items with other tags are skipped, as is a For item
    /// of the wrong length, and only the first From counts. Allocates nothing once the addresses are known.
    /// </summary>
    private bool ReadAddressItems(ExtensionReader items, DatagramReader datagrams, int sender, double now, out uint? forKey, out ReadOnlySpan<byte> runs)
    {
        forKey = null;
        runs = default;
        bool haveFrom = false;
        while (items.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            if (tag == PayloadCodec.FromTag && !haveFrom)
            {
                haveFrom = true;
                runs = value;
            }
            else if (tag == PayloadCodec.ForTag && value.Length == PayloadCodec.KeyBytes)
            {
                forKey = PayloadCodec.ReadKey(value);
            }
        }

        int count = 0;
        while (datagrams.TryNext(out _))
        {
            count++;
        }

        if (!haveFrom || runs.Length == 0 || runs.Length % PayloadCodec.RunBytes != 0)
        {
            return false;
        }

        int total = 0;
        for (int i = 0; i < runs.Length; i += PayloadCodec.RunBytes)
        {
            int n = runs[i + PayloadCodec.KeyBytes + 1];
            total += n;
        }

        if (total != count)
        {
            return false;
        }

        for (int i = 0; i < runs.Length; i += PayloadCodec.RunBytes)
        {
            _table.Learn(PayloadCodec.ReadKey(runs[i..]), sender, now);
        }

        return true;
    }

    private void ApplyReport(BurstRecord r)
    {
        BurstHeader h = r.Header;
        if (_number == 0 || h.Destination != _number)
        {
            // A report for another station, or for everyone, is read and neither measured nor acted on.
            return;
        }

        // After a lone report for this station, ordinary traffic leaves the channel clear for about a second from the report's last sample.
        _access.HoldOffOrdinaryUntil(r.End + ChannelAccess.ReportHoldOff);
        double now = ClockSeconds(r.End);
        _link.HeardReport(h.Sender, h.Format, h.Failed, now);
        AdaptationCalled?.Invoke(new AdaptationCall(true, h.Sender, h.Destination, h.Format, false, [], now, null, false, new LinkReport(h.Format, h.Failed), null, null));
        RaisePathReport(h.Sender, h.Format, h.Failed, now);
        if (h.Failed)
        {
            RaiseNotDecoded(h.Sender, r.Start);
        }

        RaiseLinkState(h.Sender, now);
    }

    private static double MeanQualityDb(ReadOnlySpan<double> quality, int carriers)
    {
        double sum = 0;
        int count = 0;
        for (int c = 0; c < carriers && c < quality.Length; c++)
        {
            double q = quality[c];
            if (double.IsFinite(q))
            {
                sum += 10 * Math.Log10(Math.Max(q, 1e-12));
                count++;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    // ---- what the station knows of each address the host has sent to ----

    /// <summary>The largest datagram one burst to a station carries at this format: the longest burst's payload less the largest prelude, the block of items with one From key and the datagram's length.</summary>
    private static int DatagramRoomFor(int format) => Math.Max(0, PayloadCodec.Capacity(format, StandardFormats.MaxSymbols) - DatagramOverhead);

    /// <summary>What the station knows of neighbour <paramref name="who"/>, with the format <c>Choose</c> gives now. False when the rules hold nothing about it.</summary>
    private bool DescribeStation(int who, double now, out int chosen, out LinkKnowledge knowledge, out double age, out int told)
    {
        told = -1;
        chosen = 0;
        knowledge = LinkKnowledge.FirstContact;
        age = 0;
        if (!_link.TryGetNeighbour(who, out LinkNeighbour? nb) || nb is null)
        {
            return false;
        }

        chosen = _link.Choose(who, now).Format;
        told = nb.Told ?? -1;
        if (nb.ReportFormat is not null)
        {
            knowledge = LinkKnowledge.Report;
            age = now - nb.ReportTime.GetValueOrDefault(now);
        }
        else if (nb.Recommended is not null || nb.Measures > 0)
        {
            knowledge = LinkKnowledge.OwnEstimate;
            age = now - (nb.MeasuredAt ?? nb.LastHeard ?? now);
        }

        return true;
    }

    /// <summary>The station an address lives at now, or 0 when the station has not located it, or has forgotten it (the station it was at has gone unheard for ten minutes).</summary>
    private int LocatedAt(uint key, double now)
    {
        if (!_table.TryGet(key, out AddressTable.Entry entry))
        {
            return 0;
        }

        if (_link.TryGetNeighbour(entry.Number, out LinkNeighbour? nb) && nb?.LastHeard is double heard && now - heard > LinkAdaptation.ForgetSeconds)
        {
            return 0;
        }

        return entry.Number;
    }

    /// <summary>What the host should know of an address it has sent to, as a <see cref="LinkStateEvent"/>, with the format <c>Choose</c> gives now (or -1 when it is not located).</summary>
    private LinkStateEvent DescribeAddress(AddressQueue q, double now, out int chosen)
    {
        int who = LocatedAt(q.Key, now);
        if (who == 0)
        {
            chosen = -1;
            return new LinkStateEvent(q.Address, false, null, null, null, RoomFor(StandardFormats.FirstContact, false, true), LinkKnowledge.FirstContact, 0, q.Forgotten);
        }

        if (!DescribeStation(who, now, out chosen, out LinkKnowledge knowledge, out double age, out int told))
        {
            chosen = -1;
            return new LinkStateEvent(q.Address, true, null, null, null, DatagramRoomFor(StandardFormats.FirstContact), LinkKnowledge.FirstContact, 0, false);
        }

        bool known = knowledge != LinkKnowledge.FirstContact;
        return new LinkStateEvent(q.Address, true, known ? chosen : null, known ? StandardFormats.Rate(chosen) * 1000.0 : null, told >= 0 ? StandardFormats.Rate(told) * 1000.0 : null, DatagramRoomFor(chosen), knowledge, Math.Max(0, age), false);
    }

    /// <summary>Raises <see cref="LinkState"/> for an address the host has sent to if what it would say differs from what it said last.</summary>
    private void RefreshAddress(AddressQueue q, double now)
    {
        LinkStateEvent link = DescribeAddress(q, now, out int chosen);
        if (link.Located)
        {
            q.Forgotten = false;
        }

        if (q.Shown && q.ShownLocated == link.Located && q.ShownGone == link.Gone && q.ShownFormat == chosen && q.ShownRoom == link.DatagramRoom && q.ShownKnowledge == link.Knowledge && q.ShownTold == link.RecommendedRateBitsPerSecond)
        {
            return;
        }

        q.Shown = true;
        q.ShownLocated = link.Located;
        q.ShownGone = link.Gone;
        q.ShownFormat = chosen;
        q.ShownRoom = link.DatagramRoom;
        q.ShownTold = link.RecommendedRateBitsPerSecond;
        q.ShownKnowledge = link.Knowledge;
        LinkState?.Invoke(link);
    }

    /// <summary>Raises <see cref="LinkState"/> for each address the host has sent to that lives at station <paramref name="who"/>, when what it knows of them changed.</summary>
    private void RaiseLinkState(int who, double now)
    {
        if (!_link.TryGetNeighbour(who, out LinkNeighbour? nb) || nb is null)
        {
            return;
        }

        _stationShown[who] = new StationShown(true, false);
        for (int i = 0; i < _queueList.Count; i++)
        {
            AddressQueue q = _queueList[i];
            if (!q.Address.IsEveryone && LocatedAt(q.Key, now) == who)
            {
                RefreshAddress(q, now);
            }
        }
    }

    /// <summary>Raises <see cref="PathReport"/> for each address the host has sent to that lives at station <paramref name="who"/>.</summary>
    private void RaisePathReport(int who, int? recommended, bool failed, double now)
    {
        for (int i = 0; i < _queueList.Count; i++)
        {
            AddressQueue q = _queueList[i];
            if (!q.Address.IsEveryone && LocatedAt(q.Key, now) == who)
            {
                PathReport?.Invoke(new PathReportEvent(q.Address, recommended, failed));
            }
        }
    }

    /// <summary>
    /// Forgets where every address at station <paramref name="who"/> lives: the station was unheard for ten minutes, or three bursts running to it met silence. Each address the host has sent to
    /// that lived there is told with <see cref="LinkState"/> (not located, <see cref="LinkStateEvent.Gone"/>); a datagram waiting for it goes to everyone from now on, or, when <paramref name="dropQueued"/> is set (the number changed hands), is dropped with <see cref="DropReason.LinkForgotten"/>.
    /// </summary>
    private void ForgetAddressesAt(int who, double now, bool dropQueued = false)
    {
        _affected.Clear();
        for (int i = 0; i < _queueList.Count; i++)
        {
            AddressQueue q = _queueList[i];
            if (!q.Address.IsEveryone && _table.TryGet(q.Key, out AddressTable.Entry entry) && entry.Number == who)
            {
                q.Forgotten = true;
                _affected.Add(q);
            }
        }

        _table.ForgetStation(who);
        foreach (AddressQueue q in _affected)
        {
            if (dropQueued && q.Count > 0)
            {
                // The number changed hands: what waited for the address was for a station that is not there. It is dropped, and the host told with the reason.
                CancelWhere(q, default, true, DropReason.LinkForgotten);
                AfterRemoval(q);
            }

            RefreshAddress(q, now);
        }

        if (dropQueued)
        {
            EnsureDataRequest();
        }
    }

    /// <summary>Forgets what is known of each neighbour the rules forget by <paramref name="seconds"/>: ten minutes without a word. The rules drop the record themselves the next time they are given the time for it.</summary>
    private void CheckGone(double seconds)
    {
        for (int who = 1; who < _stationShown.Length; who++)
        {
            if (_stationShown[who].Shown && !_stationShown[who].Gone && _link.TryGetNeighbour(who, out LinkNeighbour? nb) && nb?.LastHeard is double heard
                && seconds - heard > LinkAdaptation.ForgetSeconds)
            {
                _stationShown[who] = _stationShown[who] with { Gone = true };
                ForgetAddressesAt(who, seconds);
            }
        }
    }
}
