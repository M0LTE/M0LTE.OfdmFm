namespace M0LTE.OfdmFm;

// Step 5 of docs/station-design.md: the host's side of the interface (Send, Cancel, Snapshot) and the bursts that carry what it sends.
//
// The shape: datagrams wait in one queue per link address they are for (two first-in-first-out lists in it, expedited and ordinary). While anything is queued the station keeps one transmit request with the channel-access rules,
// of the class its most urgent datagram needs. The request carries no audio until the rules grant it: at that moment (the channel is free) the station picks a destination, asks the adaptation rules
// what format to use (Choose), takes datagrams for it, writes the prelude and the From item, packs, renders and hands the samples over. An address the station has not located goes in a burst to everyone with a For item. A burst is built once: if the channel turns busy and the request is withdrawn, the burst keeps
// what it was built with and is handed over again unchanged (see "Withdrawal" in docs/station-design.md, "Step 5 as built").

public sealed partial class Station
{
    /// <summary>
    /// How much airtime a destination's queue may hold, in seconds at the format in use: 30 s. At least the longest burst (11.5 s) twice over, so that the channel is not left idle waiting for the host between bursts at the bottom
    /// of the ladder, and no more than a link layer's retry timer could be expected to wait at that rate. A starting value, fixed. The queue is full when a datagram accepted takes it to this; a datagram that fits one burst is
    /// always accepted into an empty queue.
    /// </summary>
    internal const double QueueLimitSeconds = 30.0;

    /// <summary>A full queue is writable again once its airtime is below this fraction of <see cref="QueueLimitSeconds"/>, so that it does not flap with every burst: half.</summary>
    internal const double QueueResumeFraction = 0.5;

    /// <summary>A burst of several datagrams is filled to this much airtime, lead-in and opening included, in samples (2.5 s): the prototype's. One datagram that needs longer still goes whole.</summary>
    internal const int BurstCeilingSamples = 120000;

    /// <summary>The fixed part of the answer wait, in samples (3 s): the design's Choice 2.</summary>
    internal const int AnswerWaitBaseSamples = 3 * SampleRate;

    /// <summary>
    /// After a burst that asked for a report, nothing heard from the station within this many samples of the burst's end counts as a silence: 3 s plus twice the burst's airtime (its opening and payload, not its lead-in).
    /// The far station cannot answer, or report a failure, before it has finished receiving, and a Raspberry Pi 4 takes up to about 1.55 times a dense burst's airtime to do that (3.7 s for a 2.4 s burst), so a fixed 3 s would count
    /// a silence against a station still decoding. A starting value, to be replaced when stations can tell each other how fast they decode (packet-net/pdn-soundmodem#883).
    /// </summary>
    internal static int AnswerWaitSamples(int audioSamples) => AnswerWaitBaseSamples + (2 * Math.Max(0, audioSamples - BurstRenderer.DefaultLeadSamples));

    /// <summary>The capability word this station advertises: revision 0 of the format list in the top four bits, no optional features (none are defined). Sent in the prelude whenever "report wanted" is.</summary>
    internal const int CapabilityWord = 0x0000;

    /// <summary>The prelude's largest size: two bytes and the capability word.</summary>
    private const int PreludeBytes = 4;

    /// <summary>The length that precedes each datagram in a payload.</summary>
    private const int LengthBytes = 2;

    private static readonly int MaxPayloadBytes = ComputeMaxPayloadBytes();

    /// <summary>The most runs one burst's From item carries (an item's value is at most 253 bytes, 42 runs; this leaves room for the For item in the same block).</summary>
    internal const int MaxRuns = 40;

    private sealed class Pending
    {
        public byte[] Data = [];
        public int Length;
        public HostTag Tag;
        public bool ReportWanted;
        public bool Expedited;

        /// <summary>The key of the address the host sent it from.</summary>
        public uint From;

        /// <summary>Its protocol byte.</summary>
        public byte Protocol;
    }

    /// <summary>What is waiting for one link address (or for everyone), and what the host was last told of the address.</summary>
    private sealed class AddressQueue
    {
        public AddressQueue(LinkAddress address)
        {
            Address = address;
            Key = address.IsEveryone ? 0 : address.Key;
        }

        public readonly LinkAddress Address;

        /// <summary>The address's key (0 for everyone, which has none).</summary>
        public readonly uint Key;

        public readonly List<Pending> Expedited = [];
        public readonly List<Pending> Ordinary = [];

        /// <summary>Bits queued, each datagram counted with its two-byte length.</summary>
        public long Bits;

        public bool Full;

        /// <summary>When the address was last given a burst, on a counter; the smallest goes next (the fair rule).</summary>
        public long LastServed;

        /// <summary>True once the station has located the address and then forgotten it, until it is located again.</summary>
        public bool Forgotten;

        // What the host was last told by <see cref="LinkState"/>.
        public bool Shown;
        public bool ShownLocated;
        public bool ShownGone;
        public int ShownFormat;
        public int ShownRoom;
        public LinkKnowledge ShownKnowledge;
        public double? ShownTold;

        public int Count => Expedited.Count + Ordinary.Count;

        /// <summary>The runs the From item of a burst carrying everything queued would need, in the order the datagrams would go (expedited first).</summary>
        public int Runs()
        {
            int runs = 0;
            int length = 0;
            Pending? last = null;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Pending p in pass == 0 ? Expedited : Ordinary)
                {
                    if (last is null || last.From != p.From || last.Protocol != p.Protocol || length == 255)
                    {
                        runs++;
                        length = 0;
                    }

                    length++;
                    last = p;
                }
            }

            return runs;
        }
    }

    /// <summary>The burst the station has built and not finished with: handed to the audio layer, on air, or withdrawn and waiting to be handed over again.</summary>
    private sealed class Burst
    {
        public bool Built;

        /// <summary>The station it goes to, or 0 for everyone.</summary>
        public int Dest;

        /// <summary>The address the datagrams are for.</summary>
        public AddressQueue? Queue;
        public int Format;
        public int BurstNumber;
        public bool AskedReport;
        public bool Started;
        public long StartedAt;
        public float[]? Audio;
        public int Length;
        public readonly List<Pending> Items = [];
    }

    private readonly Dictionary<uint, AddressQueue> _byKey = [];
    private AddressQueue? _everyoneQueue;
    private readonly List<AddressQueue> _queueList = [];
    private readonly List<AddressQueue> _affected = [];
    private readonly byte[] _fromValue = new byte[MaxRuns * PayloadCodec.RunBytes];
    private readonly byte[] _forValue = new byte[PayloadCodec.KeyBytes];
    private readonly ExtensionItem[] _addressItems = new ExtensionItem[2];
    private readonly Stack<Pending> _freePending = new();
    private readonly Stack<float[]> _freeAudio = new();
    private readonly Burst _burst = new();
    private readonly long[] _heardAt = new long[64];
    private readonly List<(long Due, int Dest, long Ended)> _answerChecks = [];
    private readonly byte[] _payload = new byte[MaxPayloadBytes];
    private ReadOnlyMemory<byte>[] _pack = new ReadOnlyMemory<byte>[16];
    private BurstRenderer? _renderer;
    private ChannelAccess.Request? _data;
    private Action _onAnswerTimer = null!;
    private TimerHandle _answerTimer;
    private long _answerTimerAt = -1;
    private int _queuedCount;
    private int _expeditedCount;
    private long _served;
    private int _burstNumber;

    private void InitialiseSend()
    {
        _onAnswerTimer = OnAnswerTimer;
        _access.Prepare = PrepareBurst;
        InitialiseReports();
    }

    private static int ComputeMaxPayloadBytes()
    {
        int most = 0;
        for (int n = 0; n < StandardFormats.Count; n++)
        {
            most = Math.Max(most, PayloadCodec.Capacity(n, StandardFormats.MaxSymbols));
        }

        return most;
    }

    // ---- the host's operations ----

    /// <summary>
    /// Queues a datagram for a link address, or for everyone, and says whether it was taken. The datagram's bytes are copied: the caller's buffer is its own again when this returns.
    /// Each accepted datagram ends in exactly one <see cref="Transmitted"/> or one <see cref="Dropped"/>. Audio thread, serialised with <see cref="Push"/>; it may raise <see cref="TransmitRequest"/>, <see cref="LinkState"/> and <see cref="Congestion"/> before it returns.
    /// <para>
    /// The result: <see cref="SendResult.NoIdentity"/> (checked first) when the station has no identity yet (see <see cref="SetIdentity"/>); <see cref="SendResult.TooLarge"/> when the datagram is larger than one burst to the address carries at the format the station
    /// would use now, which is the longest burst's payload at that format (252 symbols) less what the burst needs besides the datagram (the prelude when the address is located, the block of items with one From key, a For key when it is not) and the datagram's own length (2 bytes),
    /// the figure <see cref="LinkStateEvent.DatagramRoom"/> gives; <see cref="SendResult.QueueFull"/> when the address's queue holds 30 s of airtime at that format (see <see cref="Congestion"/>).
    /// A datagram accepted can still be dropped later with <see cref="DropReason.NoLongerFits"/> if the format falls, or the address is forgotten, before it goes.
    /// </para>
    /// <para>
    /// Where it goes: the queue is for the address. When a burst is built the station looks up where the address lives. If it has located it, the burst goes to that station, at the format the adaptation rules give, with a prelude (and a report if the rules or the host want one).
    /// If not, the burst goes to everyone at the first-contact format with a For item and no prelude: "report wanted" has no effect and no answer is waited for. A datagram for everyone goes the same way with no For. The station that
    /// the address lives at can change between Send and the burst, and the datagram follows it.
    /// </para>
    /// <para>
    /// Order: first in first out, expedited ahead of ordinary ones to the same address, and an expedited datagram goes in the first slot after a burst. Several addresses take turns, the one served longest ago first,
    /// expedited traffic before ordinary. A burst carries datagrams for one address and from any of the host's: the From item lists the distinct addresses they were sent from.
    /// </para>
    /// <para>
    /// Allocation: the copy goes into a buffer taken from the station's own pool, which grows to the largest datagram seen and is refilled when the datagram has gone or been dropped, so a steady flow of datagrams to addresses already sent to allocates nothing.
    /// </para>
    /// </summary>
    /// <param name="datagram">The datagram, 1 to 65535 bytes.</param>
    /// <param name="to">The link address it is for, or <see cref="LinkAddress.Everyone"/>. An address made with <see cref="LinkAddress.FromKey"/> does as well as the full one: a host answering a datagram whose sender it knows only by key sends to that.</param>
    /// <param name="from">The link address it is sent from: one of the host's own, not <see cref="LinkAddress.Everyone"/>. Its key goes in a run of the burst's From item.</param>
    /// <param name="protocol">The datagram's protocol byte (see <see cref="LinkProtocol"/>), carried in its run and given to the receiving host. The modem does nothing else with it.</param>
    /// <param name="tag">The host's tag, returned in <see cref="Transmitted"/> and <see cref="Dropped"/> and used by <see cref="Cancel(HostTag)"/>. Several datagrams may share one.</param>
    /// <param name="reportWanted">Ask the station the address lives at to answer with a report on the path: set in the prelude of the burst this datagram goes in. For a host with no acknowledgements of its own. No effect while the address is not located.</param>
    /// <param name="priority">Expedited for an acknowledgement or response.</param>
    /// <exception cref="ArgumentException">The datagram is empty or over 65535 bytes, <paramref name="to"/> is empty, or <paramref name="from"/> is empty or everyone.</exception>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public SendResult Send(ReadOnlyMemory<byte> datagram, LinkAddress to, LinkAddress from, byte protocol, HostTag tag, bool reportWanted = false, SendPriority priority = SendPriority.Ordinary)
    {
        EnsureNotAdvancing();
        if (datagram.IsEmpty || datagram.Length > 65535)
        {
            throw new ArgumentException("A datagram is 1 to 65535 bytes.", nameof(datagram));
        }

        if (to.IsEmpty)
        {
            throw new ArgumentException("A datagram needs an address to go to.", nameof(to));
        }

        if (from.IsEmpty || from.IsEveryone)
        {
            throw new ArgumentException("A datagram needs one of the host's own addresses to be sent from.", nameof(from));
        }

        if (Callsign is null)
        {
            return SendResult.NoIdentity;
        }

        _advancing = true;
        try
        {
            double now = ClockSeconds(SampleCount);
            AddressQueue? q = FindQueue(to);
            uint toKey = to.IsEveryone ? 0 : to.Key;
            int number = to.IsEveryone ? 0 : LocatedAt(toKey, now);
            int format = FormatFor(number, now);
            if (datagram.Length > RoomFor(format, number != 0, number == 0 && !to.IsEveryone))
            {
                return SendResult.TooLarge;
            }

            if (q is { Full: true })
            {
                return SendResult.QueueFull;
            }

            if (q is null)
            {
                q = new AddressQueue(to);
                if (to.IsEveryone)
                {
                    _everyoneQueue = q;
                }
                else
                {
                    _byKey[toKey] = q;
                }

                _queueList.Add(q);
                if (!to.IsEveryone)
                {
                    RefreshAddress(q, now);
                }
            }

            Pending p = _freePending.Count > 0 ? _freePending.Pop() : new Pending();
            if (p.Data.Length < datagram.Length)
            {
                p.Data = new byte[Math.Max(datagram.Length, 64)];
            }

            datagram.Span.CopyTo(p.Data);
            p.Length = datagram.Length;
            p.Tag = tag;
            p.ReportWanted = reportWanted;
            p.Expedited = priority == SendPriority.Expedited;
            p.From = from.Key;
            p.Protocol = protocol;
            _ownKeys.Add(p.From);
            (p.Expedited ? q.Expedited : q.Ordinary).Add(p);
            q.Bits += (datagram.Length + LengthBytes) * 8L;
            _queuedCount++;
            if (p.Expedited)
            {
                _expeditedCount++;
            }

            if (AirtimeOf(q, number, format) >= QueueLimitSeconds)
            {
                q.Full = true;
                Congestion?.Invoke(new CongestionEvent(q.Address, false));
            }

            EnsureDataRequest();
            return SendResult.Accepted;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// Drops every queued datagram with this tag and raises <see cref="Dropped"/> (<see cref="DropReason.Cancelled"/>) for each, and returns how many. A datagram already in a burst that has been built, whether handed to the audio layer,
    /// on air, or taken back and waiting to be handed over again, is not recalled. Audio thread, serialised with <see cref="Push"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public int Cancel(HostTag tag)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            int count = 0;
            for (int n = 0; n < _queueList.Count; n++)
            {
                AddressQueue q = _queueList[n];
                if (q.Count == 0)
                {
                    continue;
                }

                int before = count;
                count += CancelWhere(q, tag, null);
                if (count != before)
                {
                    AfterRemoval(q);
                }
            }

            if (count > 0)
            {
                EnsureDataRequest();
            }

            return count;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>As <see cref="Cancel(HostTag)"/> for everything queued for a link address (or for everyone), in the order it would have gone; returns how many. An address nothing was sent to drops nothing.</summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public int Cancel(LinkAddress address)
    {
        EnsureNotAdvancing();
        _advancing = true;
        try
        {
            AddressQueue? q = FindQueue(address);
            if (q is null || q.Count == 0)
            {
                return 0;
            }

            int count = CancelWhere(q, default, true);
            AfterRemoval(q);
            EnsureDataRequest();
            return count;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// What the station knows of every link address the host has sent to, in the order they were first sent to, as <see cref="LinkStateEvent"/> values taken now: an address not located has <see cref="LinkStateEvent.Located"/> false
    /// (and <see cref="LinkStateEvent.Gone"/> set when it was located and has been forgotten). Audio thread, serialised with <see cref="Push"/>. Allocates the list.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public IReadOnlyList<LinkStateEvent> Snapshot()
    {
        EnsureNotAdvancing();
        double now = ClockSeconds(SampleCount);
        var list = new List<LinkStateEvent>();
        foreach (AddressQueue q in _queueList)
        {
            if (!q.Address.IsEveryone)
            {
                list.Add(DescribeAddress(q, now, out _));
            }
        }

        return list;
    }

    /// <summary>
    /// A test's way of putting an address where a conversation would have told the station it lives, so that a scenario can start from stations that know each other. Not a host operation: a host never says where an address lives.
    /// </summary>
    internal void Locate(LinkAddress address, int number)
    {
        double now = ClockSeconds(SampleCount);
        _table.Learn(address.Key, number, now);
        if (FindQueue(address) is { } q)
        {
            RefreshAddress(q, now);
        }
    }

    /// <summary>Where the station has the address, or 0 (for a test).</summary>
    internal int LocatedNow(LinkAddress address) => address.IsEveryone ? 0 : LocatedAt(address.Key, ClockSeconds(SampleCount));

    /// <summary>The learning table, for a test.</summary>
    internal AddressTable LearnedAddresses => _table;

    // ---- queues ----

    /// <summary>The keys of the addresses the host has sent from: the addresses this station answers for (a burst to everyone for one of them is answered with a report).</summary>
    private readonly HashSet<uint> _ownKeys = [];

    /// <summary>The queue for an address (by key, so an address made from its key alone finds the same one), or null if the host has not sent to it.</summary>
    private AddressQueue? FindQueue(LinkAddress address) =>
        address.IsEveryone ? _everyoneQueue : _byKey.TryGetValue(address.Key, out AddressQueue? q) ? q : null;

    /// <summary>The format a burst to station <paramref name="number"/> would go at now (everyone: first contact).</summary>
    private int FormatFor(int number, double now) => number == 0 ? StandardFormats.FirstContact : _link.Choose(number, now).Format;

    /// <summary>
    /// The largest datagram one burst carries: the longest burst's payload at the format, less the datagram's length and what the burst needs besides: a prelude and a block with one From key when it goes to a station,
    /// a block with one From key (and a For key when <paramref name="withFor"/>) when it goes to everyone.
    /// </summary>
    private static int RoomFor(int format, bool located, bool withFor) =>
        Math.Max(0, PayloadCodec.Capacity(format, StandardFormats.MaxSymbols) - LengthBytes - (located ? PreludeBytes : 0) - PayloadCodec.AddressBlockSize(1, !located && withFor));

    /// <summary>Airtime of what is queued for an address at the format in use, the block of items one burst needs for the From keys queued included. Bursts to the address that carry a prelude are not counted for it.</summary>
    private static double AirtimeOf(AddressQueue q, int number, int format)
    {
        long bits = q.Bits + (8L * PayloadCodec.AddressBlockSize(q.Runs(), number == 0 && !q.Address.IsEveryone));
        return bits / (StandardFormats.Rate(format) * 1000.0);
    }

    /// <summary>Called after datagrams left a queue: a full queue that has drained below the resume mark is writable again, and the totals the data request depends on are brought up to date by the caller.</summary>
    private void AfterRemoval(AddressQueue q)
    {
        if (!q.Full)
        {
            return;
        }

        double now = ClockSeconds(SampleCount);
        int number = q.Address.IsEveryone ? 0 : LocatedAt(q.Key, now);
        if (AirtimeOf(q, number, FormatFor(number, now)) < QueueLimitSeconds * QueueResumeFraction)
        {
            q.Full = false;
            Congestion?.Invoke(new CongestionEvent(q.Address, true));
        }
    }

    private void Release(Pending p) => _freePending.Push(p);

    private void Unqueue(AddressQueue q, Pending p)
    {
        q.Bits -= (p.Length + LengthBytes) * 8L;
        _queuedCount--;
        if (p.Expedited)
        {
            _expeditedCount--;
        }
    }

    /// <summary>Drops what matches (a tag, or everything when <paramref name="all"/> is set), expedited first, raising <see cref="Dropped"/> for each. Returns how many.</summary>
    private int CancelWhere(AddressQueue q, HostTag tag, bool? all)
    {
        int count = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            List<Pending> list = pass == 0 ? q.Expedited : q.Ordinary;
            for (int i = 0; i < list.Count;)
            {
                Pending p = list[i];
                if (all == true || p.Tag == tag)
                {
                    list.RemoveAt(i);
                    Unqueue(q, p);
                    Dropped?.Invoke(new DroppedEvent(p.Tag, DropReason.Cancelled));
                    Release(p);
                    count++;
                }
                else
                {
                    i++;
                }
            }
        }

        return count;
    }

    /// <summary>True when anything is queued for an address that lives at station <paramref name="number"/> now (expedited only, when <paramref name="expeditedOnly"/>).</summary>
    private bool HasQueuedAt(int number, bool expeditedOnly)
    {
        double now = ClockSeconds(SampleCount);
        for (int i = 0; i < _queueList.Count; i++)
        {
            AddressQueue q = _queueList[i];
            if ((expeditedOnly ? q.Expedited.Count : q.Count) > 0 && !q.Address.IsEveryone && LocatedAt(q.Key, now) == number)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the one transmit request that asks for a burst: made when something is queued and none is outstanding, in the class of the most urgent datagram (reclassified while it waits unbuilt, so that an expedited datagram
    /// sent after an ordinary one takes the first slot), and taken away when nothing is left to send. Must run with the station's busy flag set.
    /// </summary>
    private void EnsureDataRequest()
    {
        TransmitClass cls = _expeditedCount > 0 ? TransmitClass.Expedited : TransmitClass.Ordinary;
        ChannelAccess.Request? request = _data;
        if (request is null || request.State == ChannelAccess.RequestState.Done)
        {
            if (_queuedCount > 0)
            {
                _data ??= new ChannelAccess.Request();
                _access.Enqueue(_data, cls);
            }

            return;
        }

        if (request.State == ChannelAccess.RequestState.Waiting && !_burst.Built)
        {
            if (_queuedCount == 0)
            {
                _access.Remove(request);
            }
            else
            {
                _access.Reclassify(request, cls);
                _access.Update();
            }
        }
    }

    // ---- building a burst (called by the channel-access rules when it grants the request) ----

    /// <summary>
    /// The request has been granted and the channel is free: decide what goes. Returns true with the samples set, or false when nothing is left to send (every datagram queued turned out too large for the format now).
    /// A burst already built (the request was withdrawn and granted again) is left as it is.
    /// </summary>
    private bool PrepareBurst(ChannelAccess.Request request)
    {
        if (ReferenceEquals(request, _reportRequest))
        {
            return PrepareReport(request);
        }

        if (ReferenceEquals(request, _identRequest))
        {
            return PrepareIdentification(request);
        }

        if (!ReferenceEquals(request, _data))
        {
            // A burst that carries its samples already.
            return true;
        }

        if (_burst.Built)
        {
            return true;
        }

        double now = ClockSeconds(SampleCount);
        while (_queuedCount > 0)
        {
            AddressQueue? q = PickQueue(request.Class);
            if (q is null)
            {
                break;
            }

            if (TryBuild(q, request, now))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The fair rule: among the addresses with something queued of the granted class (expedited, for an expedited grant), the one served longest ago, and the one sent to first on a tie. A grant of ordinary traffic
    /// that finds only expedited datagrams, or the other way round, takes whoever has anything.
    /// </summary>
    private AddressQueue? PickQueue(TransmitClass cls)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            AddressQueue? best = null;
            for (int n = 0; n < _queueList.Count; n++)
            {
                AddressQueue q = _queueList[n];
                if (q.Count == 0 || (pass == 0 && cls == TransmitClass.Expedited && q.Expedited.Count == 0))
                {
                    continue;
                }

                if (best is null || q.LastServed < best.LastServed)
                {
                    best = q;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The most payload symbols a burst of several datagrams may have at this format: the airtime ceiling, at least 4.</summary>
    private int CeilingSymbols(int format)
    {
        int symbols = 4;
        for (int s = 8; s <= StandardFormats.MaxSymbols; s += 4)
        {
            if (_renderer!.SamplesFor(format, s) > BurstCeilingSamples)
            {
                break;
            }

            symbols = s;
        }

        return symbols;
    }

    private bool TryBuild(AddressQueue q, ChannelAccess.Request request, double now)
    {
        int dest = q.Address.IsEveryone ? 0 : LocatedAt(q.Key, now);
        bool everyone = dest == 0;
        bool withFor = everyone && !q.Address.IsEveryone;
        _renderer ??= new BurstRenderer();
        LinkChoice choice = everyone ? new LinkChoice(StandardFormats.FirstContact, false) : _link.Choose(dest, now);
        int format = choice.Format;
        int overhead = everyone ? 0 : PreludeBytes;

        // A datagram accepted when the link was faster, or the address located, may not fit one burst now: it is dropped, and the host sees the new room in LinkState.
        int largest = PayloadCodec.Capacity(format, StandardFormats.MaxSymbols);
        int aloneBlock = PayloadCodec.AddressBlockSize(1, withFor);
        for (;;)
        {
            Pending? head = q.Expedited.Count > 0 ? q.Expedited[0] : q.Ordinary.Count > 0 ? q.Ordinary[0] : null;
            if (head is null || head.Length + LengthBytes + overhead + aloneBlock <= largest)
            {
                break;
            }

            (head.Expedited ? q.Expedited : q.Ordinary).RemoveAt(0);
            Unqueue(q, head);
            Dropped?.Invoke(new DroppedEvent(head.Tag, DropReason.NoLongerFits));
            Release(head);
        }

        if (q.Count == 0)
        {
            AfterRemoval(q);
            return false;
        }

        int ceiling = PayloadCodec.Capacity(format, CeilingSymbols(format));
        int datagramBytes = 0;
        bool wanted = !everyone && choice.ReportWanted;
        _burst.Items.Clear();
        int runs = 0;
        int runLength = 0;
        Pending? previous = null;
        bool full = false;
        for (int pass = 0; pass < 2 && !full; pass++)
        {
            List<Pending> list = pass == 0 ? q.Expedited : q.Ordinary;
            int taken = 0;
            while (taken < list.Count)
            {
                Pending p = list[taken];
                bool newRun = previous is null || previous.From != p.From || previous.Protocol != p.Protocol || runLength == 255;
                int nextRuns = runs + (newRun ? 1 : 0);
                int need = overhead + PayloadCodec.AddressBlockSize(nextRuns, withFor) + datagramBytes + LengthBytes + p.Length;
                if (_burst.Items.Count > 0 && (need > ceiling || nextRuns > MaxRuns))
                {
                    full = true;
                    break;
                }

                _burst.Items.Add(p);
                runs = nextRuns;
                runLength = newRun ? 1 : runLength + 1;
                previous = p;

                datagramBytes += LengthBytes + p.Length;
                wanted |= !everyone && p.ReportWanted;
                taken++;
            }

            list.RemoveRange(0, taken);
        }

        if (_pack.Length < _burst.Items.Count)
        {
            _pack = new ReadOnlyMemory<byte>[Math.Max(_pack.Length * 2, _burst.Items.Count)];
        }

        for (int i = 0; i < _burst.Items.Count; i++)
        {
            Pending p = _burst.Items[i];
            _pack[i] = p.Data.AsMemory(0, p.Length);
            Unqueue(q, p);
        }

        // The block of items every data burst carries: From, the runs of datagrams in order (the address they were sent from, their protocol, how many), and For when the burst is for one address the station has not located.
        int written = 0;
        for (int i = 0; i < _burst.Items.Count;)
        {
            Pending first = _burst.Items[i];
            int count = 1;
            while (i + count < _burst.Items.Count && count < 255 && _burst.Items[i + count].From == first.From && _burst.Items[i + count].Protocol == first.Protocol)
            {
                count++;
            }

            PayloadCodec.WriteRun(first.From, first.Protocol, count, _fromValue.AsSpan(written));
            written += PayloadCodec.RunBytes;
            i += count;
        }

        int itemCount = 0;
        _addressItems[itemCount++] = new ExtensionItem(PayloadCodec.FromTag, _fromValue.AsMemory(0, written));
        if (withFor)
        {
            PayloadCodec.WriteKey(q.Key, _forValue);
            _addressItems[itemCount++] = new ExtensionItem(PayloadCodec.ForTag, _forValue);
        }

        PayloadPrelude? prelude = everyone ? null : new PayloadPrelude(wanted, _link.ReportFor(dest), false, wanted ? CapabilityWord : null);
        if (prelude is { Recommended: not null })
        {
            // The recommendation rides on this burst, so the station has told them: a lone report owed for a drop or a request is not needed (a failed one always goes alone).
            DropCoveredReport(dest);
        }

        if (!everyone)
        {
            RaiseLinkState(dest, now); // what this station has told them may have changed
        }

        int length = PayloadCodec.Pack(prelude, _addressItems.AsSpan(0, itemCount), _pack.AsSpan(0, _burst.Items.Count), _payload);
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        int number = _burstNumber;
        _burstNumber = (_burstNumber + 1) & 15;
        var header = BurstHeader.Data(StationNumber, dest, format, symbols, number);
        int total = _renderer.SamplesFor(format, symbols);
        float[] audio = RentAudio(total);
        _renderer.RenderData(header, _payload.AsSpan(0, length), audio);
        Array.Clear(_pack, 0, _burst.Items.Count);

        _burst.Built = true;
        _burst.Dest = dest;
        _burst.Queue = q;
        _burst.Format = format;
        _burst.BurstNumber = number;
        _burst.AskedReport = !everyone && wanted;
        _burst.Started = false;
        _burst.Audio = audio;
        _burst.Length = total;
        request.Samples = audio.AsMemory(0, total);
        _served++;
        q.LastServed = _served;
        AfterRemoval(q);
        return true;
    }

    private float[] RentAudio(int samples)
    {
        if (_freeAudio.Count > 0)
        {
            float[] audio = _freeAudio.Pop();
            if (audio.Length >= samples)
            {
                return audio;
            }
        }

        return new float[Math.Max(samples, 32768)];
    }

    // ---- bookkeeping when the audio layer starts and ends the burst ----

    private void OnDataBurstStarted(long sampleIndex)
    {
        if (!_burst.Built || _burst.Started)
        {
            return;
        }

        _burst.Started = true;
        _burst.StartedAt = sampleIndex;
        if (_burst.Dest != 0)
        {
            _link.Sent(_burst.Dest);
        }
    }

    private void OnDataBurstEnded(long sampleIndex)
    {
        if (!_burst.Built)
        {
            return;
        }

        if (_burst.Dest != 0)
        {
            RememberBurst(_burst.Dest, _burst.Queue!.Address, sampleIndex);
        }
        else if (!_burst.Queue!.Address.IsEveryone)
        {
            ForgetFloods(sampleIndex);
            _floods.Add(new Flood(_burst.Queue.Key, sampleIndex, sampleIndex + AnswerWaitSamples(_burst.Length)));
        }

        DataBurstSent?.Invoke(_burst.Dest, _burst.AskedReport, sampleIndex, _burst.Queue!.Address);
        double timeOut = ClockSeconds(sampleIndex);
        double airtime = (sampleIndex - _burst.StartedAt) / (double)SampleRate;
        double rate = StandardFormats.Rate(_burst.Format) * 1000.0;
        LinkAddress address = _burst.Queue!.Address;
        foreach (Pending p in _burst.Items)
        {
            Transmitted?.Invoke(new TransmittedEvent(p.Tag, address, _burst.BurstNumber, _burst.Format, rate, airtime, timeOut));
            Release(p);
        }

        _burst.Items.Clear();
        if (_burst.Audio is { } audio)
        {
            _freeAudio.Push(audio);
        }

        if (_burst.AskedReport)
        {
            _answerChecks.Add((sampleIndex + AnswerWaitSamples(_burst.Length), _burst.Dest, sampleIndex));
            RescheduleAnswerTimer();
        }

        _burst.Audio = null;
        _burst.Built = false;
        _burst.Started = false;
        EnsureDataRequest();
    }

    /// <summary>Drops the floods whose answer wait ran out before <paramref name="before"/>.</summary>
    private void ForgetFloods(long before)
    {
        for (int i = _floods.Count - 1; i >= 0; i--)
        {
            if (_floods[i].Due < before)
            {
                _floods.RemoveAt(i);
            }
        }
    }

    /// <summary>A data burst has ended on air: the station number it went to (0 for everyone), whether it asked for a report, the sample it ended at and the address its datagrams were for. For tests: no host sees it.</summary>
    internal event Action<int, bool, long, LinkAddress>? DataBurstSent;

    /// <summary>A silence was counted against a station: its number and the count now. For tests.</summary>
    internal event Action<int, int>? SilenceCounted;

    private void RescheduleAnswerTimer()
    {
        if (_answerChecks.Count == 0)
        {
            return;
        }

        long due = _answerChecks[0].Due;
        if (_answerTimerAt == due)
        {
            return;
        }

        if (_answerTimerAt >= 0)
        {
            CancelTimer(_answerTimer);
        }

        _answerTimer = ScheduleAt(due, _onAnswerTimer);
        _answerTimerAt = due;
    }

    /// <summary>
    /// A burst that asked for a report ended one answer wait ago (<see cref="AnswerWaitSamples"/>): if no header of any burst from the station was read since it ended, that is one silence for <c>LinkAdaptation.NoAnswer</c>. "Read" is the clock at the header read,
    /// which is a frame boundary of the detector, so the same audio gives the same answer however it is cut into blocks; a reply that began inside the 3 s may be read after them and is then too late.
    /// </summary>
    private void OnAnswerTimer()
    {
        _answerTimerAt = -1;
        while (_answerChecks.Count > 0 && _answerChecks[0].Due <= SampleCount)
        {
            (long due, int dest, long ended) = _answerChecks[0];
            _answerChecks.RemoveAt(0);
            if (_heardAt[dest] <= ended)
            {
                _link.NoAnswer(dest);
                SilenceCounted?.Invoke(dest, _link.TryGetNeighbour(dest, out LinkNeighbour? counted) && counted is not null ? counted.Silences : -1);
                if (_link.TryGetNeighbour(dest, out LinkNeighbour? nb) && nb is { Silences: >= 3 })
                {
                    // Three bursts running have met silence: the station falls back to first contact, and what is known of where its addresses live goes with it.
                    ForgetAddressesAt(dest, ClockSeconds(due));
                }

                RaiseLinkState(dest, ClockSeconds(due));
            }
        }

        RescheduleAnswerTimer();
    }

    // ---- what the tests read ----

    /// <summary>Datagrams queued for a link address, not counting those in a burst.</summary>
    internal int QueuedFor(LinkAddress address) => FindQueue(address)?.Count ?? 0;

    /// <summary>Datagrams queued for every address, not counting those in a burst.</summary>
    internal int QueuedTotal => _queuedCount;

    /// <summary>The address's queue's airtime now at the format in use, seconds.</summary>
    internal double QueuedAirtimeFor(LinkAddress address)
    {
        if (FindQueue(address) is not { } q)
        {
            return 0;
        }

        double now = ClockSeconds(SampleCount);
        int number = address.IsEveryone ? 0 : LocatedAt(q.Key, now);
        return AirtimeOf(q, number, FormatFor(number, now));
    }

    /// <summary>True while a burst has been built and not finished: handed to the audio layer, on air, or withdrawn and waiting.</summary>
    internal bool BurstBuilt => _burst.Built;

    /// <summary>The next burst number the station will use.</summary>
    internal int NextBurstNumber => _burstNumber;

    /// <summary>The sample the earliest timer is due at, or null; the simulated channel cuts its blocks there.</summary>
    internal long? NextTimerDue => _timers.NextDue;

    /// <summary>Samples until the detector completes its next frame; the simulated channel cuts its blocks there.</summary>
    internal int SamplesToNextFrame => _detector.SamplesToNextFrame;
}
