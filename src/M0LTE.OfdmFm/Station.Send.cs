namespace M0LTE.OfdmFm;

// Step 5 of docs/station-design.md: the host's side of the interface (Bind, Send, Cancel, Snapshot) and the bursts that carry what it sends.
//
// The shape: datagrams wait in one queue per destination (two first-in-first-out lists in it, expedited and ordinary). While anything is queued the station keeps one transmit request with the channel-access rules,
// of the class its most urgent datagram needs. The request carries no audio until the rules grant it: at that moment (the channel is free) the station picks a destination, asks the adaptation rules
// what format to use (Choose), takes datagrams for it, writes the prelude, packs, renders and hands the samples over. A burst is built once: if the channel turns busy and the request is withdrawn, the burst keeps
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

    private sealed class Pending
    {
        public byte[] Data = [];
        public int Length;
        public HostTag Tag;
        public bool ReportWanted;
        public bool Expedited;
    }

    private sealed class DestQueue
    {
        public readonly List<Pending> Expedited = [];
        public readonly List<Pending> Ordinary = [];

        /// <summary>Bits queued, each datagram counted with its two-byte length.</summary>
        public long Bits;

        public bool Full;

        /// <summary>When the destination was last given a burst, on a counter; the smallest goes next (the fair rule).</summary>
        public long LastServed;

        public int Count => Expedited.Count + Ordinary.Count;
    }

    /// <summary>The burst the station has built and not finished with: handed to the audio layer, on air, or withdrawn and waiting to be handed over again.</summary>
    private sealed class Burst
    {
        public bool Built;
        public int Dest;
        public int Format;
        public int BurstNumber;
        public bool AskedReport;
        public bool Started;
        public long StartedAt;
        public float[]? Audio;
        public int Length;
        public readonly List<Pending> Items = [];
    }

    private readonly DestQueue?[] _queues = new DestQueue?[64];
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
    /// Gives a station name a station number: the interim way a name gets a number (docs/station-design.md I). From now on events about that number carry the name and not the placeholder (<see cref="PlaceholderName"/>),
    /// <see cref="Send"/> accepts the name, and a station heard before it was bound is announced once more in a <see cref="LinkState"/> event under its new name. Events raised earlier keep the placeholder they carried.
    /// A name has one number and a number one name: binding a name already bound to another number moves it (the old number goes back to its placeholder), and binding a number that has another name replaces that name.
    /// Datagrams already queued stay where they are, queued by number. Bindings are never dropped, not when the link is forgotten after ten minutes of silence either. Audio thread, serialised with <see cref="Push"/>.
    /// </summary>
    /// <param name="name">The host's name for the station: opaque bytes, not empty, not <see cref="StationName.Everyone"/>, and not a placeholder ("#" and a number).</param>
    /// <param name="number">The station's on-air number, 1 to 63 and not this station's own.</param>
    /// <exception cref="ArgumentException">The name is empty, everyone, or looks like a placeholder.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The number is outside 1 to 63 or is this station's.</exception>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public void Bind(StationName name, int number)
    {
        EnsureNotAdvancing();
        if (name.IsEmpty || name.IsEveryone)
        {
            throw new ArgumentException("A name must have at least one byte and cannot be everyone.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 63);
        if (number == StationNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "A station does not bind its own number.");
        }

        for (int n = 0; n < _placeholders.Length; n++)
        {
            if (_placeholders[n] == name)
            {
                throw new ArgumentException("A placeholder name cannot be bound.", nameof(name));
            }
        }

        _advancing = true;
        try
        {
            int moved = NumberOf(name);
            if (moved == number)
            {
                return;
            }

            _bound[number] = name;
            if (moved != 0)
            {
                _bound[moved] = null;
                AnnounceLink(moved);
            }

            AnnounceLink(number);
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// Queues a datagram for a station, or for everyone, and says whether it was taken. The datagram's bytes are copied: the caller's buffer is its own again when this returns.
    /// Each accepted datagram ends in exactly one <see cref="Transmitted"/> or one <see cref="Dropped"/>. Audio thread, serialised with <see cref="Push"/>; it may raise <see cref="TransmitRequest"/> and <see cref="Congestion"/> before it returns.
    /// <para>
    /// The result: <see cref="SendResult.NotBound"/> when the name has no number (see <see cref="Bind"/>); <see cref="SendResult.TooLarge"/> when the datagram is larger than one burst to the station carries at the format the station
    /// would use now, which is the longest burst's payload at that format (252 symbols) less the prelude's largest size (4 bytes) and the datagram's own length (2 bytes), the figure <see cref="LinkStateEvent.DatagramRoom"/> gives
    /// (for everyone: the first-contact format and no prelude); <see cref="SendResult.QueueFull"/> when the destination's queue holds 30 s of airtime at that format (see <see cref="Congestion"/>).
    /// A datagram accepted can still be dropped later with <see cref="DropReason.NoLongerFits"/> if the format falls before it goes.
    /// </para>
    /// <para>
    /// Order: first in first out, expedited ahead of ordinary ones to the same destination, and an expedited datagram goes in the first slot after a burst. Several destinations take turns, the one served longest ago first,
    /// expedited traffic before ordinary. A datagram for everyone goes at the first-contact format with no prelude and no report; <paramref name="reportWanted"/> is ignored for it.
    /// </para>
    /// <para>
    /// Allocation: the copy goes into a buffer taken from the station's own pool, which grows to the largest datagram seen and is refilled when the datagram has gone or been dropped, so a steady flow of datagrams allocates nothing.
    /// </para>
    /// </summary>
    /// <param name="datagram">The datagram, 1 to 65535 bytes.</param>
    /// <param name="destination">The station's bound name, or <see cref="StationName.Everyone"/>.</param>
    /// <param name="tag">The host's tag, returned in <see cref="Transmitted"/> and <see cref="Dropped"/> and used by <see cref="Cancel(HostTag)"/>. Several datagrams may share one.</param>
    /// <param name="reportWanted">Ask the station to answer with a report on the path: set in the prelude of the burst this datagram goes in. For a host with no acknowledgements of its own.</param>
    /// <param name="priority">Expedited for an acknowledgement or response.</param>
    /// <exception cref="ArgumentException">The datagram is empty or over 65535 bytes.</exception>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public SendResult Send(ReadOnlyMemory<byte> datagram, StationName destination, HostTag tag, bool reportWanted = false, SendPriority priority = SendPriority.Ordinary)
    {
        EnsureNotAdvancing();
        if (datagram.IsEmpty || datagram.Length > 65535)
        {
            throw new ArgumentException("A datagram is 1 to 65535 bytes.", nameof(datagram));
        }

        int number = destination.IsEveryone ? 0 : NumberOf(destination);
        if (!destination.IsEveryone && number == 0)
        {
            return SendResult.NotBound;
        }

        _advancing = true;
        try
        {
            double now = ClockSeconds(SampleCount);
            int format = FormatFor(number, now);
            if (datagram.Length > RoomFor(format, number == 0))
            {
                return SendResult.TooLarge;
            }

            DestQueue q = QueueFor(number);
            if (q.Full)
            {
                return SendResult.QueueFull;
            }

            Pending p = _freePending.Count > 0 ? _freePending.Pop() : new Pending();
            if (p.Data.Length < datagram.Length)
            {
                p.Data = new byte[Math.Max(datagram.Length, 64)];
            }

            datagram.Span.CopyTo(p.Data);
            p.Length = datagram.Length;
            p.Tag = tag;
            p.ReportWanted = reportWanted && number != 0;
            p.Expedited = priority == SendPriority.Expedited;
            (p.Expedited ? q.Expedited : q.Ordinary).Add(p);
            q.Bits += (datagram.Length + LengthBytes) * 8L;
            _queuedCount++;
            if (p.Expedited)
            {
                _expeditedCount++;
            }

            if (AirtimeOf(q, format) >= QueueLimitSeconds)
            {
                q.Full = true;
                Congestion?.Invoke(new CongestionEvent(NameOfDestination(number), false));
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
            for (int n = 0; n < _queues.Length; n++)
            {
                DestQueue? q = _queues[n];
                if (q is null || q.Count == 0)
                {
                    continue;
                }

                int before = count;
                count += CancelWhere(q, tag, null);
                if (count != before)
                {
                    AfterRemoval(n, q);
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

    /// <summary>As <see cref="Cancel(HostTag)"/> for everything queued for a station (or for everyone), in the order it would have gone; returns how many. A name that is not bound drops nothing.</summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public int Cancel(StationName station)
    {
        EnsureNotAdvancing();
        int number = station.IsEveryone ? 0 : NumberOf(station);
        if (!station.IsEveryone && number == 0)
        {
            return 0;
        }

        _advancing = true;
        try
        {
            DestQueue? q = _queues[number];
            if (q is null || q.Count == 0)
            {
                return 0;
            }

            int count = CancelWhere(q, default, true);
            AfterRemoval(number, q);
            EnsureDataRequest();
            return count;
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>
    /// What the station knows of every station it has heard or been bound to, in station-number order, as <see cref="LinkStateEvent"/> values taken now: a station bound and never heard is at first contact (format null), and one not heard
    /// for ten minutes has <see cref="LinkStateEvent.Gone"/> set. Audio thread, serialised with <see cref="Push"/>. Allocates the list.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside an event handler or a timer action.</exception>
    public IReadOnlyList<LinkStateEvent> Snapshot()
    {
        EnsureNotAdvancing();
        double now = ClockSeconds(SampleCount);
        var list = new List<LinkStateEvent>();
        for (int who = 1; who < _bound.Length; who++)
        {
            if (who == StationNumber)
            {
                continue;
            }

            bool heard = _link.TryGetNeighbour(who, out LinkNeighbour? nb) && nb?.LastHeard is not null;
            if (!heard && _bound[who] is null)
            {
                continue;
            }

            if (heard && now - nb!.LastHeard!.Value > LinkAdaptation.ForgetSeconds)
            {
                list.Add(new LinkStateEvent(NameFor(who), null, null, DatagramRoomFor(StandardFormats.FirstContact), LinkKnowledge.FirstContact, 0, true));
            }
            else if (heard && DescribeLink(who, now, out LinkStateEvent link, out _))
            {
                list.Add(link);
            }
            else
            {
                list.Add(new LinkStateEvent(NameFor(who), null, null, DatagramRoomFor(StandardFormats.FirstContact), LinkKnowledge.FirstContact, 0, false));
            }
        }

        return list;
    }

    // ---- names ----

    private int NumberOf(StationName name)
    {
        for (int n = 1; n < _bound.Length; n++)
        {
            if (_bound[n] is { } bound && bound == name)
            {
                return n;
            }
        }

        return 0;
    }

    private StationName NameOfDestination(int number) => number == 0 ? StationName.Everyone : NameFor(number);

    /// <summary>Announces a station that has been heard under whatever name it has now (after a bind), once.</summary>
    private void AnnounceLink(int number)
    {
        _shown[number] = default;
        RaiseLinkState(number, ClockSeconds(SampleCount));
    }

    // ---- queues ----

    private DestQueue QueueFor(int number) => _queues[number] ??= new DestQueue();

    /// <summary>The format a burst to <paramref name="number"/> would go at now (everyone: first contact).</summary>
    private int FormatFor(int number, double now) => number == 0 ? StandardFormats.FirstContact : _link.Choose(number, now).Format;

    private static int RoomFor(int format, bool everyone) => Math.Max(0, PayloadCodec.Capacity(format, StandardFormats.MaxSymbols) - LengthBytes - (everyone ? 0 : PreludeBytes));

    private static double AirtimeOf(DestQueue q, int format) => q.Bits / (StandardFormats.Rate(format) * 1000.0);

    /// <summary>Called after datagrams left a queue: a full queue that has drained below the resume mark is writable again, and the totals the data request depends on are brought up to date by the caller.</summary>
    private void AfterRemoval(int number, DestQueue q)
    {
        if (q.Full && AirtimeOf(q, FormatFor(number, ClockSeconds(SampleCount))) < QueueLimitSeconds * QueueResumeFraction)
        {
            q.Full = false;
            Congestion?.Invoke(new CongestionEvent(NameOfDestination(number), true));
        }
    }

    private void Release(Pending p) => _freePending.Push(p);

    private void Unqueue(DestQueue q, Pending p)
    {
        q.Bits -= (p.Length + LengthBytes) * 8L;
        _queuedCount--;
        if (p.Expedited)
        {
            _expeditedCount--;
        }
    }

    /// <summary>Drops what matches (a tag, or everything when <paramref name="all"/> is set), expedited first, raising <see cref="Dropped"/> for each. Returns how many.</summary>
    private int CancelWhere(DestQueue q, HostTag tag, bool? all)
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
        if (!ReferenceEquals(request, _data))
        {
            // A burst the station built for itself some other way (steps 6 and 7) carries its samples already.
            return true;
        }

        if (_burst.Built)
        {
            return true;
        }

        double now = ClockSeconds(SampleCount);
        while (_queuedCount > 0)
        {
            int dest = PickDestination(request.Class);
            if (dest < 0)
            {
                break;
            }

            if (TryBuild(dest, request, now))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The fair rule: among the destinations with something queued of the granted class (expedited, for an expedited grant), the one served longest ago, and the lowest number on a tie (everyone first). A grant of ordinary traffic
    /// that finds only expedited datagrams, or the other way round, takes whoever has anything.
    /// </summary>
    private int PickDestination(TransmitClass cls)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int best = -1;
            for (int n = 0; n < _queues.Length; n++)
            {
                DestQueue? q = _queues[n];
                if (q is null || q.Count == 0 || (pass == 0 && cls == TransmitClass.Expedited && q.Expedited.Count == 0))
                {
                    continue;
                }

                if (best < 0 || q.LastServed < _queues[best]!.LastServed)
                {
                    best = n;
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return -1;
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

    private bool TryBuild(int dest, ChannelAccess.Request request, double now)
    {
        DestQueue q = _queues[dest]!;
        bool everyone = dest == 0;
        _renderer ??= new BurstRenderer();
        LinkChoice choice = everyone ? new LinkChoice(StandardFormats.FirstContact, false) : _link.Choose(dest, now);
        int format = choice.Format;
        int overhead = everyone ? 0 : PreludeBytes;

        // A datagram accepted when the link was faster may not fit one burst now: it is dropped, and the host sees the new room in LinkState.
        int largest = PayloadCodec.Capacity(format, StandardFormats.MaxSymbols);
        for (;;)
        {
            Pending? head = q.Expedited.Count > 0 ? q.Expedited[0] : q.Ordinary.Count > 0 ? q.Ordinary[0] : null;
            if (head is null || head.Length + LengthBytes + overhead <= largest)
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
            AfterRemoval(dest, q);
            return false;
        }

        int ceiling = PayloadCodec.Capacity(format, CeilingSymbols(format));
        int used = overhead;
        bool wanted = !everyone && choice.ReportWanted;
        _burst.Items.Clear();
        bool full = false;
        for (int pass = 0; pass < 2 && !full; pass++)
        {
            List<Pending> list = pass == 0 ? q.Expedited : q.Ordinary;
            int taken = 0;
            while (taken < list.Count)
            {
                Pending p = list[taken];
                int need = used + LengthBytes + p.Length;
                if (_burst.Items.Count > 0 && need > ceiling)
                {
                    full = true;
                    break;
                }

                _burst.Items.Add(p);
                used = need;
                wanted |= p.ReportWanted;
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

        PayloadPrelude? prelude = everyone ? null : new PayloadPrelude(wanted, _link.ReportFor(dest), false, wanted ? CapabilityWord : null);
        int length = PayloadCodec.Pack(prelude, _pack.AsSpan(0, _burst.Items.Count), _payload);
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
        _burst.Format = format;
        _burst.BurstNumber = number;
        _burst.AskedReport = !everyone && wanted;
        _burst.Started = false;
        _burst.Audio = audio;
        _burst.Length = total;
        request.Samples = audio.AsMemory(0, total);
        _served++;
        q.LastServed = _served;
        AfterRemoval(dest, q);
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

        double timeOut = ClockSeconds(sampleIndex);
        double airtime = (sampleIndex - _burst.StartedAt) / (double)SampleRate;
        double rate = StandardFormats.Rate(_burst.Format) * 1000.0;
        StationName name = NameOfDestination(_burst.Dest);
        foreach (Pending p in _burst.Items)
        {
            Transmitted?.Invoke(new TransmittedEvent(p.Tag, name, _burst.BurstNumber, _burst.Format, rate, airtime, timeOut));
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
                RaiseLinkState(dest, ClockSeconds(due));
            }
        }

        RescheduleAnswerTimer();
    }

    // ---- what the tests read ----

    /// <summary>Datagrams queued for a station (0 for everyone), not counting those in a burst.</summary>
    internal int QueuedFor(int number) => _queues[number]?.Count ?? 0;

    /// <summary>The queue's airtime now at the format in use, seconds.</summary>
    internal double QueuedAirtimeFor(int number) => _queues[number] is { } q ? AirtimeOf(q, FormatFor(number, ClockSeconds(SampleCount))) : 0;

    /// <summary>True while a burst has been built and not finished: handed to the audio layer, on air, or withdrawn and waiting.</summary>
    internal bool BurstBuilt => _burst.Built;

    /// <summary>The next burst number the station will use.</summary>
    internal int NextBurstNumber => _burstNumber;

    /// <summary>The sample the earliest timer is due at, or null; the simulated channel cuts its blocks there.</summary>
    internal long? NextTimerDue => _timers.NextDue;

    /// <summary>Samples until the detector completes its next frame; the simulated channel cuts its blocks there.</summary>
    internal int SamplesToNextFrame => _detector.SamplesToNextFrame;
}
