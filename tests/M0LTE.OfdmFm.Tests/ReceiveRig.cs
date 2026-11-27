using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>How a rig orders the receive jobs it has parked when it lets them go.</summary>
public enum ReleaseOrder
{
    /// <summary>Oldest burst first: the order a single worker would finish them in.</summary>
    Natural,

    /// <summary>Newest first.</summary>
    Reversed,

    /// <summary>A seeded shuffle.</summary>
    Shuffled,
}

/// <summary>
/// A station and what a test needs around it: every event as text in order, the datagrams, the calls made on the adaptation rules, and, for a queued station, worker threads coordinated
/// by signals (never by sleeping or timing). A queued rig can hold each burst's receive job at the start of its run until the test lets it go, in an order the test picks,
/// to make jobs finish in an order of the test's choosing. The timeouts here only turn a hang into a failure.
/// </summary>
internal sealed class ReceiveRig : IDisposable
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(120);

    private readonly List<Thread> _threads = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _changed = new(0);
    private readonly Dictionary<long, SemaphoreSlim> _parkedGates = [];
    private readonly object _parkLock = new();
    private readonly bool _park;
    private readonly int _workers;
    private volatile bool _stop;
    private int _inRun;
    private int _completed;
    private int _parked;

    /// <summary>Makes a rig. <paramref name="workers"/> 0 is an inline station; more start that many worker threads. <paramref name="park"/> holds each receive job until released.</summary>
    public ReceiveRig(int workers, bool park = false, int number = 1)
    {
        _workers = workers;
        _park = park && workers > 0;
        Station = new Station(number, new Random(5), runJobsInline: workers == 0).WithIdentity("M0LTE");

        // A listening station: the carrier indication is held set, so the channel is never free and the station never keys. Step 6 sends lone reports, and a station that sent them would call the rules (ReportFor, Choose)
        // in ways the tests that replay the recorded calls by hand do not know of.
        Station.SetExternalBusy(true);
        Station.Received += e =>
        {
            Datagrams.Add((Station.HearingSender, e.Addressing != Addressing.ToThisStation, e.Datagram.ToArray()));
            Log.Add(string.Create(CultureInfo.InvariantCulture, $"rx from {Station.HearingSender} {e.Addressing} for {(e.ForKey is { } k ? k.ToString("X8", CultureInfo.InvariantCulture) : "-")} q {e.QualityDb:R} data {Convert.ToHexString(e.Datagram.Span)}"));
        };
        Station.PathReport += e => Log.Add(string.Create(CultureInfo.InvariantCulture, $"path {Text(e.Address)} fmt {e.RecommendedFormat?.ToString(CultureInfo.InvariantCulture) ?? "-"} failed {e.Failed}"));
        Station.LinkState += e => Log.Add(string.Create(CultureInfo.InvariantCulture, $"link {Text(e.Address)} located {e.Located} fmt {e.Format?.ToString(CultureInfo.InvariantCulture) ?? "-"} rate {e.RateBitsPerSecond?.ToString("R", CultureInfo.InvariantCulture) ?? "-"} room {e.DatagramRoom} {e.Knowledge} age {e.AgeSeconds:R} gone {e.Gone}"));
        Station.AdaptationCalled = c =>
        {
            Calls.Add(c);
            Log.Add(string.Create(CultureInfo.InvariantCulture, $"adapt {(c.IsReport ? "report" : "data")} {c.Sender}->{c.Dest} fmt {c.Format} decoded {c.Decoded} now {c.Now:R} footing {c.FootingDb?.ToString("R", CultureInfo.InvariantCulture) ?? "-"} sudden {c.Sudden} theirs {c.TheirReport} cap {c.Capability} returned {c.Returned} q {Hash(c.Quality)}"));
        };
        if (workers > 0)
        {
            Station.WorkAvailable += () => _wake.Release();
            Station.JobStarting = OnJobStarting;

            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(Work) { IsBackground = true, Name = "rig worker " + i };
                _threads.Add(t);
                t.Start();
            }
        }
    }

    /// <summary>With parking on: whether <see cref="Push"/> lets the parked jobs go as soon as every worker holds one (the default).</summary>
    public bool AutoRelease { get; set; } = true;

    /// <summary>The station.</summary>
    public Station Station { get; }

    /// <summary>The address of the host's neighbour at station number <paramref name="number"/>: "#" and the number, as text.</summary>
    public static LinkAddress Address(int number) => LinkAddress.FromAscii("#" + number.ToString(CultureInfo.InvariantCulture));

    /// <summary>The address of the host's own, which its datagrams are sent from.</summary>
    public static LinkAddress Own { get; } = LinkAddress.FromAscii("OWN");

    /// <summary>
    /// The host sends one small datagram to the address of each station number given, which the station has located there (as a conversation would have), so that events about those stations are raised by address.
    /// The station never transmits (the carrier indication is held), so the datagrams stay queued.
    /// </summary>
    public void SendTo(params int[] numbers)
    {
        foreach (int n in numbers)
        {
            Station.Locate(Address(n), n);
            Station.Send(new byte[] { 1 }, Address(n), Own, new HostTag(n)).Should().Be(SendResult.Accepted);
        }
    }

    /// <summary>Every event and every call on the adaptation rules, as text, in the order they happened.</summary>
    public List<string> Log { get; } = [];

    /// <summary>The datagrams delivered, in order: the sender's number, whether it was for everyone, the bytes.</summary>
    public List<(int Sender, bool Everyone, byte[] Data)> Datagrams { get; } = [];

    /// <summary>The first sample of each burst whose parked receive job was let go, in the order they were let go.</summary>
    public List<long> ReleasedOrder { get; } = [];

    /// <summary>The calls made on the adaptation rules, in order.</summary>
    public List<Station.AdaptationCall> Calls { get; } = [];

    /// <summary>Bytes of an event's station name as text.</summary>
    private static string Text(LinkAddress name) => System.Text.Encoding.ASCII.GetString(name.Bytes);

    private static string Hash(double[] values)
    {
        // The values themselves, so that "identical" means identical bits.
        long h = 17;
        foreach (double v in values)
        {
            h = unchecked((h * 31) + BitConverter.DoubleToInt64Bits(v));
        }

        return values.Length + ":" + h.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>Pushes the stream in blocks, <paramref name="block"/> giving each block's size.</summary>
    public void Push(float[] stream, Func<int> block, bool settleHeaders = false)
    {
        int at = 0;
        while (at < stream.Length)
        {
            int n = Math.Min(block(), stream.Length - at);
            if (!_park && _workers > 0)
            {
                // A host whose workers keep up: audio does not run so far ahead of the jobs that a burst is dropped for want of a worker.
                while (Station.BurstsQueued >= Station.MaxQueuedBursts - 1)
                {
                    if (!_changed.Wait(Safety))
                    {
                        throw new TimeoutException("The workers did not drain the queue.");
                    }
                }
            }

            Station.Push(stream.AsSpan(at, n));
            at += n;
            if (settleHeaders && _workers > 0)
            {
                SettleHeaders();
            }

            if (_park && AutoRelease && _parked >= _workers)
            {
                Settle();
                ReleaseParked(_order);
            }
        }
    }

    private ReleaseOrder _order = ReleaseOrder.Reversed;
    private int _shuffleSeed = 1;

    /// <summary>With parking on, how parked jobs are let go.</summary>
    public void SetOrder(ReleaseOrder order, int shuffleSeed = 1)
    {
        _order = order;
        _shuffleSeed = shuffleSeed;
    }

    /// <summary>Pushes silence, then, for a queued rig, runs every job to the end, letting parked ones go.</summary>
    public void Finish(double seconds = 4)
    {
        Push(new float[(int)(seconds * 48000)], () => 4096, settleHeaders: true);
        if (_workers == 0)
        {
            Station.DeliverResults();
            return;
        }

        for (int round = 0; round < 200; round++)
        {
            Settle();
            if (_parked == 0)
            {
                break;
            }

            ReleaseParked(_order);
        }

        Settle();
        Station.DeliverResults();
    }

    private void Work()
    {
        while (!_stop)
        {
            try
            {
                _wake.Wait(Safety);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _inRun);
            try
            {
                while (Station.RunOne())
                {
                    if (t_receive)
                    {
                        Interlocked.Increment(ref _completed);
                    }

                    _changed.Release();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inRun);
                _changed.Release();
            }
        }
    }

    [ThreadStatic]
    private static bool t_receive;

    private void OnJobStarting(Station.JobStart job)
    {
        t_receive = !job.IsHeader;
        if (job.IsHeader || !_park)
        {
            return;
        }

        var gate = new SemaphoreSlim(0);
        lock (_parkLock)
        {
            _parkedGates[job.Start] = gate;
            _parked++;
        }

        _changed.Release();
        if (!gate.Wait(Safety))
        {
            throw new TimeoutException("A parked job was never let go.");
        }
    }

    /// <summary>Applies what can be applied and waits until every worker is idle or parked and no header read is out.</summary>
    public void Settle()
    {
        for (;;)
        {
            Station.DeliverResults();
            if (!Station.HeaderJobPending && Volatile.Read(ref _inRun) == Volatile.Read(ref _parked) && (!Station.HasWork || Volatile.Read(ref _parked) >= _workers))
            {
                return;
            }

            if (!_changed.Wait(Safety))
            {
                throw new TimeoutException("The workers did not settle.");
            }
        }
    }

    /// <summary>Waits for the header read in flight, if any, taking its result in (a host whose workers keep up).</summary>
    public void SettleHeaders()
    {
        for (;;)
        {
            Station.DeliverResults();
            if (!Station.HeaderJobPending)
            {
                return;
            }

            // A header read waits for a worker that is not holding a receive: with every worker parked, let them go.
            if (_park && Volatile.Read(ref _parked) >= _workers)
            {
                ReleaseParked(_order);
                continue;
            }

            if (!_changed.Wait(Safety))
            {
                throw new TimeoutException("A header read did not finish.");
            }
        }
    }

    /// <summary>Lets the parked jobs go in the chosen order, one at a time, each finishing before the next is let go.</summary>
    public void ReleaseParked(ReleaseOrder order)
    {
        List<long> starts;
        lock (_parkLock)
        {
            starts = [.. _parkedGates.Keys.Order()];
        }

        IEnumerable<long> sequence = order switch
        {
            ReleaseOrder.Reversed => starts.OrderByDescending(s => s),
            ReleaseOrder.Shuffled => Shuffled(starts),
            _ => starts,
        };
        foreach (long s in sequence.ToList())
        {
            SemaphoreSlim gate;
            lock (_parkLock)
            {
                gate = _parkedGates[s];
                _parkedGates.Remove(s);
                _parked--;
            }

            int before = Volatile.Read(ref _completed);
            ReleasedOrder.Add(s);
            gate.Release();
            while (Volatile.Read(ref _completed) == before)
            {
                if (!_changed.Wait(Safety))
                {
                    throw new TimeoutException("A released job did not finish.");
                }
            }
        }
    }

    private List<long> Shuffled(List<long> list)
    {
        var rng = new Random(_shuffleSeed++);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// <summary>The state of every neighbour the rules hold, as text with every double in full.</summary>
    public static string Neighbours(LinkAdaptation rules)
    {
        var sb = new System.Text.StringBuilder();
        for (int who = 1; who < 64; who++)
        {
            if (!rules.TryGetNeighbour(who, out LinkNeighbour? nb) || nb is null)
            {
                continue;
            }

            string R(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? "-";
            sb.Append(CultureInfo.InvariantCulture, $"[{who}] margin {R(nb.Margin)} rec {nb.Recommended} pending {nb.Pending} heard {R(nb.LastHeard)} told {nb.Told} cap {nb.Cap} good {nb.Good} measures {nb.Measures} measuredAt {R(nb.MeasuredAt)} ");
            sb.Append(CultureInfo.InvariantCulture, $"reportFormat {nb.ReportFormat} reportTime {R(nb.ReportTime)} silences {nb.Silences} unanswered {nb.Unanswered} capability {nb.Capability} est ");
            foreach (double d in nb.EstimateDb)
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop = true;
        lock (_parkLock)
        {
            foreach (SemaphoreSlim gate in _parkedGates.Values)
            {
                gate.Release();
            }
        }

        for (int i = 0; i < _threads.Count; i++)
        {
            _wake.Release();
        }

        foreach (Thread t in _threads)
        {
            t.Join(TimeSpan.FromSeconds(30));
        }
    }
}
