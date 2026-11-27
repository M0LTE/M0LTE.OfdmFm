using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// What the step 8 scenarios share: a ledger of every datagram the hosts send and what became of it, a small host that resends what a station says it could not decode, counts of bursts that overlapped on the air,
/// and a way to print figures (the console, and a file when SM_HOSTILE_OUT names one) so that the pull request's numbers are the tests' own.
/// </summary>
internal static class Hostile
{
    /// <summary>Prints a figure. Every number quoted in the pull request comes through here.</summary>
    public static void Say(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SM_HOSTILE_OUT") is { Length: > 0 } path)
        {
            lock (typeof(Hostile))
            {
                File.AppendAllText(path, line + "\n");
            }
        }
    }

    public static string F(double value, string format = "F1") => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Seconds(long samples) => F(samples / 48000.0, "F2");

    /// <summary>True when the gated tier is on (SM_MASK_GATE=1).</summary>
    public static bool Gated => Environment.GetEnvironmentVariable("SM_MASK_GATE") == "1";

    public static void Gate() =>
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: minutes of simulated traffic among three stations, and the capture-margin sweeps");

    /// <summary>The airtime of a burst: from its first sample to its last, the lead-in included, seconds.</summary>
    public static double Airtime(SimTransmission t) => t.Length / 48000.0;

    /// <summary>
    /// How many bursts overlapped another station's on the air where some third station could hear both (a collision at that receiver), and how many such pairs there were. A burst that only overlapped
    /// a station that nobody who hears it also hears (a hidden pair with no one between) does not count.
    /// </summary>
    public static (int Bursts, int Pairs) Collisions(SimChannel channel, long from = 0, long until = long.MaxValue)
    {
        var all = new List<(int Node, SimTransmission T)>();
        foreach (SimNode n in channel.Nodes)
        {
            foreach (SimTransmission t in n.Transmissions.Where(t => t.Start >= from && t.Start < until))
            {
                all.Add((n.Index, t));
            }
        }

        var hit = new HashSet<(int, long)>();
        int pairs = 0;
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                (int na, SimTransmission a) = all[i];
                (int nb, SimTransmission b) = all[j];
                if (na == nb || a.Start >= b.Start + b.Length || b.Start >= a.Start + a.Length)
                {
                    continue;
                }

                bool heardByThird = channel.Nodes.Any(r => r.Index != na && r.Index != nb && channel.Link(channel.Nodes[na], r).Gain != 0 && channel.Link(channel.Nodes[nb], r).Gain != 0);
                if (heardByThird)
                {
                    pairs++;
                    hit.Add((na, a.Id));
                    hit.Add((nb, b.Id));
                }
            }
        }

        return (hit.Count, pairs);
    }

    /// <summary>Total seconds a node was on the air.</summary>
    public static double OnAir(SimNode n) => n.Transmissions.Sum(t => t.Length) / 48000.0;
}

/// <summary>What a datagram sent through a <see cref="Ledger"/> was.</summary>
internal sealed class SentItem
{
    public int From;
    public int To; // station number, 0 for everyone
    public int Seq;
    public int Length;
    public long FirstSentClock;
    public SendPriority Priority;
    public readonly List<long> Tags = [];
    public readonly List<long> TransmittedClocks = [];
    public long ResendAt = long.MaxValue;
    public long CancelAt = long.MaxValue;
    public int Cancelled;
    public int Attempts => Tags.Count;
}

/// <summary>
/// Every datagram the hosts of a rig send, with the identity put into its bytes (sender, sequence, attempt), so that what each station received can be matched to it. Optionally also the host of a link layer: it resends what a
/// station says it could not decode, and anything not delivered some time after it went out. Nothing here reads a wall clock: the host acts at the rig's own steps.
/// </summary>
internal sealed class Ledger
{
    private readonly SimRig _rig;
    private readonly Dictionary<long, SentItem> _byTag = [];
    private readonly int[] _nextSeq;
    private readonly int[] _hintsSeen;
    private readonly int[] _transmittedSeen;
    private readonly Random _jitter;

    public Ledger(SimRig rig, int seed = 1)
    {
        _rig = rig;
        _jitter = new Random(seed * 7 + 3);
        _nextSeq = new int[rig.Stations.Count + 1];
        _hintsSeen = new int[rig.Stations.Count + 1];
        _transmittedSeen = new int[rig.Stations.Count + 1];
    }

    public List<SentItem> Items { get; } = [];

    /// <summary>Datagrams a station refused (<see cref="Station.Send"/> did not say Accepted).</summary>
    public List<(SentItem Item, SendResult Result)> Refused { get; } = [];

    /// <summary>The datagram size the scenarios send unless they say otherwise.</summary>
    public const int DefaultLength = 30;

    public static long TagOf(int from, int seq, int attempt) => (from * 10_000_000L) + (seq * 100L) + attempt;

    /// <summary>The bytes of a datagram: sender, sequence (two bytes), attempt, then filler that depends on all of them.</summary>
    public static byte[] Bytes(int from, int seq, int attempt, int length)
    {
        var d = new byte[Math.Max(4, length)];
        d[0] = (byte)from;
        d[1] = (byte)(seq >> 8);
        d[2] = (byte)seq;
        d[3] = (byte)attempt;
        for (int i = 4; i < d.Length; i++)
        {
            d[i] = (byte)((i * 31) + (seq * 17) + from);
        }

        return d;
    }

    public static (int From, int Seq, int Attempt) Identify(byte[] data) => (data[0], (data[1] << 8) | data[2], data[3]);

    /// <summary>Station <paramref name="from"/> sends a new datagram to station <paramref name="to"/> (0: everyone).</summary>
    public SentItem Send(int from, int to, int length = DefaultLength, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false, long cancelAfter = 0)
    {
        var item = new SentItem { From = from, To = to, Seq = _nextSeq[from]++, Length = length, FirstSentClock = _rig.Channel.Clock, Priority = priority };
        Items.Add(item);
        Transmit(item, priority, reportWanted);
        if (cancelAfter > 0)
        {
            item.CancelAt = _rig.Channel.Clock + cancelAfter;
        }

        return item;
    }

    private void Transmit(SentItem item, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false)
    {
        int attempt = item.Attempts;
        long tag = TagOf(item.From, item.Seq, attempt);
        item.Tags.Add(tag);
        _byTag[tag] = item;
        LinkAddress to = item.To == 0 ? LinkAddress.Everyone : SimRig.Address(item.To);
        SendResult r = _rig.Stations[item.From - 1].Send(Bytes(item.From, item.Seq, attempt, item.Length), to, SimRig.Address(item.From), LinkProtocol.NoLayer3, new HostTag(tag), reportWanted, priority);
        if (r != SendResult.Accepted)
        {
            Refused.Add((item, r));
        }
    }

    /// <summary>How many times station <paramref name="receiver"/> received this datagram (any attempt), and the number of distinct attempts among them.</summary>
    public (int Copies, int Distinct) ReceivedAt(SentItem item, int receiver)
    {
        var seen = new List<int>();
        foreach (SimReceived r in _rig.Nodes[receiver - 1].Received)
        {
            if (r.Data.Length >= 4 && r.Data[0] == item.From)
            {
                (int from, int seq, int attempt) = Identify(r.Data);
                if (from == item.From && seq == item.Seq)
                {
                    seen.Add(attempt);
                }
            }
        }

        return (seen.Count, seen.Distinct().Count());
    }

    /// <summary>Whether the addressed station (or, for everyone, any listener) has the datagram.</summary>
    public bool Delivered(SentItem item) => item.To == 0 ? Enumerable.Range(1, _rig.Stations.Count).Any(n => n != item.From && ReceivedAt(item, n).Copies > 0) : ReceivedAt(item, item.To).Copies > 0;

    /// <summary>A datagram received twice with the same attempt number at one station: the same burst's datagram delivered twice. There must never be one.</summary>
    public List<string> Duplicates()
    {
        var list = new List<string>();
        for (int n = 1; n <= _rig.Stations.Count; n++)
        {
            var seen = new HashSet<(int, int, int)>();
            foreach (SimReceived r in _rig.Nodes[n - 1].Received)
            {
                if (r.Data.Length >= 4 && !seen.Add(Identify(r.Data)))
                {
                    list.Add($"station {n} got {Identify(r.Data)} twice");
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Ordinary datagrams one station sent another that arrived in a different order from the one they were sent in. The queues are first in, first out within a class, a lost burst leaves a gap and not a reordering,
    /// and a withdrawn burst is handed over again unchanged, so there must be none (expedited datagrams may overtake ordinary ones and are left out).
    /// </summary>
    public int Reordered()
    {
        int count = 0;
        for (int to = 1; to <= _rig.Stations.Count; to++)
        {
            foreach (IGrouping<int, SimReceived> fromSender in _rig.Nodes[to - 1].Received.Where(r => r.Data.Length >= 4 && !r.Everyone).GroupBy(r => (int)r.Data[0]))
            {
                var order = new List<int>();
                foreach (SimReceived r in fromSender)
                {
                    (int from, int seq, int _) = Identify(r.Data);
                    SentItem? item = Items.FirstOrDefault(i => i.From == from && i.Seq == seq);
                    if (item is { Priority: SendPriority.Ordinary })
                    {
                        order.Add(seq);
                    }
                }

                for (int i = 1; i < order.Count; i++)
                {
                    if (order[i] < order[i - 1])
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Events in a station's log whose clock is earlier than the one before it (the log is in the order raised, and the clock only goes forward).</summary>
    public static int ClocksGoingBackwards(IEnumerable<string> log)
    {
        var last = new Dictionary<string, long>();
        int count = 0;
        foreach (string line in log)
        {
            string[] w = line.Split(' ', 3);
            long clock = long.Parse(w[0], CultureInfo.InvariantCulture);
            if (last.TryGetValue(w[1], out long before) && clock < before)
            {
                count++;
            }

            last[w[1]] = clock;
        }

        return count;
    }

    /// <summary>Datagrams that were delivered but that no <see cref="Station.Transmitted"/> event named: a delivery the sender was not told went out.</summary>
    public int DeliveredWithoutTransmittedEvent()
    {
        var transmitted = new HashSet<long>();
        foreach (SimNode n in _rig.Nodes)
        {
            foreach ((long _, TransmittedEvent e) in n.Transmitted)
            {
                transmitted.Add(e.Tag.Value);
            }
        }

        int count = 0;
        foreach (SentItem item in Items)
        {
            foreach (long tag in item.Tags)
            {
                int attempt = (int)(tag % 100);
                bool arrived = item.To == 0
                    ? Enumerable.Range(1, _rig.Stations.Count).Any(n => n != item.From && _rig.Nodes[n - 1].Received.Any(r => r.Data.Length >= 4 && Identify(r.Data) == (item.From, item.Seq, attempt)))
                    : _rig.Nodes[item.To - 1].Received.Any(r => r.Data.Length >= 4 && Identify(r.Data) == (item.From, item.Seq, attempt));
                if (arrived && !transmitted.Contains(tag))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>The datagrams the stations have dropped (a <see cref="Station.Dropped"/> event), by tag.</summary>
    public int DroppedCount() => _rig.Nodes.Sum(n => n.Dropped.Count);

    /// <summary>The datagram a tag was given to, or null.</summary>
    public SentItem? ItemOf(long tag) => _byTag.GetValueOrDefault(tag);

    /// <summary>The tags a station was told were in a burst the other end heard and could not decode.</summary>
    public IEnumerable<long> Hinted(int station) => _rig.Nodes[station - 1].NotDecoded.SelectMany(h => h.Tags);

    // ---- the host that resends ----

    /// <summary>Resends made because the station named the datagram in a <see cref="Station.BurstNotDecoded"/> hint.</summary>
    public int ResendsByHint { get; private set; }

    /// <summary>Resends made because a datagram that had gone out was not delivered after <see cref="Timeout"/>.</summary>
    public int ResendsByTimeout { get; private set; }

    /// <summary>How long a sent datagram may go undelivered before the host sends it again, samples (30 s, and a seeded 0 to 10 s more, so that two hosts do not resend at the same sample).</summary>
    public long Timeout { get; set; } = 30 * 48000;

    /// <summary>The most attempts of one datagram.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>Gives the host's decisions for the stations' events so far. Called between steps of the channel.</summary>
    public void HostStep()
    {
        long now = _rig.Channel.Clock;
        for (int s = 1; s <= _rig.Stations.Count; s++)
        {
            SimNode node = _rig.Nodes[s - 1];
            for (; _hintsSeen[s] < node.NotDecoded.Count; _hintsSeen[s]++)
            {
                foreach (long tag in node.NotDecoded[_hintsSeen[s]].Tags)
                {
                    if (_byTag.TryGetValue(tag, out SentItem? item) && item.Tags[^1] == tag && !Delivered(item) && item.Attempts < MaxAttempts)
                    {
                        ResendsByHint++;
                        Transmit(item);
                    }
                }
            }

            for (; _transmittedSeen[s] < node.Transmitted.Count; _transmittedSeen[s]++)
            {
                (long clock, TransmittedEvent e) = node.Transmitted[_transmittedSeen[s]];
                if (_byTag.TryGetValue(e.Tag.Value, out SentItem? item))
                {
                    item.TransmittedClocks.Add(clock);
                    item.ResendAt = clock + Timeout + _jitter.Next(0, 10 * 48000);
                }
            }
        }

        foreach (SentItem item in Items.Where(i => i.CancelAt <= now && i.Cancelled == 0).ToList())
        {
            item.Cancelled = -1;
            if (item.TransmittedClocks.Count == 0)
            {
                item.Cancelled = _rig.Stations[item.From - 1].Cancel(new HostTag(item.Tags[^1]));
            }
        }

        foreach (SentItem item in Items.ToList())
        {
            if (item.To != 0 && item.Attempts < MaxAttempts && item.TransmittedClocks.Count == item.Attempts && now >= item.ResendAt && !Delivered(item))
            {
                ResendsByTimeout++;
                Transmit(item);
            }
        }
    }

    /// <summary>A datagram a host will send at a clock.</summary>
    public readonly record struct Plan(long Clock, int From, int To, int Length, SendPriority Priority = SendPriority.Ordinary, long CancelAfter = 0);

    /// <summary>
    /// Runs the rig for <paramref name="seconds"/>, in steps of <paramref name="step"/> seconds, with the host acting between steps and sending each datagram of <paramref name="plan"/> at exactly its clock
    /// (the plan is in order of clock).
    /// </summary>
    public void RunWithHost(double seconds, double step = 0.5, IReadOnlyList<Plan>? plan = null, Action? onStep = null)
    {
        if (plan is not null)
        {
            Schedule(plan);
        }

        plan = _plan;
        long end = _rig.Channel.Clock + (long)(seconds * 48000);
        int next = _planNext;
        while (_rig.Channel.Clock < end)
        {
            long to = Math.Min(end, _rig.Channel.Clock + (long)(step * 48000));
            while (plan is not null && next < plan.Count && plan[next].Clock <= to)
            {
                if (plan[next].Clock > _rig.Channel.Clock)
                {
                    _rig.Channel.Run(plan[next].Clock);
                }

                Plan p = plan[next++];
                Send(p.From, p.To, p.Length, p.Priority, false, p.CancelAfter);
            }

            _rig.Channel.Run(to);
            HostStep();
            onStep?.Invoke();
        }

        _planNext = next;
    }

    private IReadOnlyList<Plan>? _plan;
    private int _planNext;

    /// <summary>Sets the plan the host follows from now (in order of clock); later runs carry on through it.</summary>
    public void Schedule(IReadOnlyList<Plan> plan)
    {
        _plan = plan;
        _planNext = 0;
    }
}
