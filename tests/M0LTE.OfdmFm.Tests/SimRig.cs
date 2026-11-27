using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Several inline stations, numbered 1 up and named "S1", "S2" and so on, on one <see cref="SimChannel"/>, each given the address of every other, as if a conversation had located them (or as <c>locates</c> says: station i has station j's address located when it returns true), all pairs connected. Each is given its identity unless <c>identified</c> says false for its number.</summary>
internal sealed class SimRig
{
    /// <summary>The first-contact format's rate, bit/s.</summary>
    public static readonly double FirstContactRate = StandardFormats.Rate(StandardFormats.FirstContact) * 1000.0;

    public SimRig(int stations, int block = 4800, int seed = 1, double noise = 0.004, double gain = 1.0, bool connect = true, Func<int, int, bool>? locates = null, Func<int, bool>? identified = null, int[]? numbers = null, uint[]? ids = null, bool randomNumbers = false)
    {
        Channel = new SimChannel(block, seed);
        for (int i = 1; i <= stations; i++)
        {
            // The seam fixes each station's number (its index unless <numbers> says) and id, so that the scenarios keep their flows; <randomNumbers> makes the stations take their own, as a host's would.
            Station s = randomNumbers
                ? new(new Random((seed * 100) + i), runJobsInline: true)
                : new(numbers?[i - 1] ?? i, ids?[i - 1] ?? (0xC0DE0000u | (uint)(numbers?[i - 1] ?? i)), new Random((seed * 100) + i), runJobsInline: true);
            if (identified?.Invoke(i) ?? true)
            {
                s.WithIdentity($"M0S{i}");
            }

            s.IdentificationSuspended = true; // identification has its own tests; the others see only what they are about (a test of it sets this back)
            Stations.Add(s);
            Nodes.Add(Channel.Add(s, $"S{i}"));
        }

        for (int i = 1; i <= stations; i++)
        {
            for (int j = 1; j <= stations; j++)
            {
                if (i != j && (locates?.Invoke(i, j) ?? true))
                {
                    Stations[i - 1].Locate(Address(j), randomNumbers ? Stations[j - 1].StationNumber : numbers?[j - 1] ?? j);
                }
            }
        }

        if (connect)
        {
            for (int i = 0; i < stations; i++)
            {
                for (int j = i + 1; j < stations; j++)
                {
                    Channel.Connect(Nodes[i], Nodes[j], gain, noise);
                }
            }
        }
    }

    /// <summary>The numbers the stations were given at the start (their indexes unless a test said), or, with random numbers, the ones they took.</summary>
    public int[] StartNumbers => [.. Stations.Select(s => s.StationNumber)];

    public SimChannel Channel { get; }

    public List<Station> Stations { get; } = [];

    public List<SimNode> Nodes { get; } = [];

    /// <summary>The link address of station <paramref name="number"/>'s host: the one datagrams for it go to and, from that station, the one they are sent from.</summary>
    public static LinkAddress Address(int number) => LinkAddress.FromAscii($"S{number}");

    /// <summary>Runs for <paramref name="seconds"/> more of simulated time.</summary>
    public void Run(double seconds) => Channel.Run(Channel.Clock + (long)(seconds * 48000));

    /// <summary>Runs until the clock reads <paramref name="seconds"/>.</summary>
    public void RunTo(double seconds) => Channel.Run((long)(seconds * 48000));

    /// <summary>A datagram of <paramref name="length"/> bytes whose content depends on <paramref name="tag"/>.</summary>
    public static byte[] Datagram(int tag, int length) => TestBurst.Datagram(tag, length);

    /// <summary>Station <paramref name="from"/> sends datagram number <paramref name="tag"/> to station <paramref name="to"/>.</summary>
    public SendResult Send(int from, int to, int tag, int length = 30, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false) =>
        Send(from, Address(to), tag, length, priority, reportWanted);

    /// <summary>Station <paramref name="from"/> sends datagram number <paramref name="tag"/> to an address (or everyone), from its own address.</summary>
    public SendResult Send(int from, LinkAddress to, int tag, int length = 30, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false) =>
        Stations[from - 1].Send(Datagram(tag, length), to, Address(from), LinkProtocol.NoLayer3, new HostTag(tag), reportWanted, priority);
}
