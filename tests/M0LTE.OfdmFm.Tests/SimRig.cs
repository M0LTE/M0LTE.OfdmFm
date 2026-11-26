using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Several inline stations, numbered 1 up and named "S1", "S2" and so on, on one <see cref="SimChannel"/>, each bound to every other, all pairs connected.</summary>
internal sealed class SimRig
{
    /// <summary>The first-contact format's rate, bit/s.</summary>
    public static readonly double FirstContactRate = StandardFormats.Rate(StandardFormats.FirstContact) * 1000.0;

    public SimRig(int stations, int block = 4800, int seed = 1, double noise = 0.004, double gain = 1.0, bool connect = true)
    {
        Channel = new SimChannel(block, seed);
        for (int i = 1; i <= stations; i++)
        {
            Station s = new($"M0S{i}", i, new Random((seed * 100) + i), runJobsInline: true);
            s.IdentificationSuspended = true; // identification has its own tests; the others see only what they are about (a test of it sets this back)
            Stations.Add(s);
            Nodes.Add(Channel.Add(s, $"S{i}"));
        }

        for (int i = 1; i <= stations; i++)
        {
            for (int j = 1; j <= stations; j++)
            {
                if (i != j)
                {
                    Stations[i - 1].Bind(Name(j), j);
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

    public SimChannel Channel { get; }

    public List<Station> Stations { get; } = [];

    public List<SimNode> Nodes { get; } = [];

    /// <summary>The name station <paramref name="number"/> is bound under at the others.</summary>
    public static StationName Name(int number) => StationName.FromAscii($"S{number}");

    /// <summary>Runs for <paramref name="seconds"/> more of simulated time.</summary>
    public void Run(double seconds) => Channel.Run(Channel.Clock + (long)(seconds * 48000));

    /// <summary>Runs until the clock reads <paramref name="seconds"/>.</summary>
    public void RunTo(double seconds) => Channel.Run((long)(seconds * 48000));

    /// <summary>A datagram of <paramref name="length"/> bytes whose content depends on <paramref name="tag"/>.</summary>
    public static byte[] Datagram(int tag, int length) => TestBurst.Datagram(tag, length);

    /// <summary>Station <paramref name="from"/> sends datagram number <paramref name="tag"/> to station <paramref name="to"/>.</summary>
    public SendResult Send(int from, int to, int tag, int length = 30, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false) =>
        Send(from, Name(to), tag, length, priority, reportWanted);

    /// <summary>Station <paramref name="from"/> sends datagram number <paramref name="tag"/> to a name (or everyone).</summary>
    public SendResult Send(int from, StationName to, int tag, int length = 30, SendPriority priority = SendPriority.Ordinary, bool reportWanted = false) =>
        Stations[from - 1].Send(Datagram(tag, length), to, new HostTag(tag), reportWanted, priority);
}
