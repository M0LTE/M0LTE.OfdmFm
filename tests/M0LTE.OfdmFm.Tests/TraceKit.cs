using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Collects what a station's trace says, as the lines a host would write.</summary>
internal sealed class TraceTap
{
    public TraceTap(Station station, TraceLevel level = TraceLevel.Flow)
    {
        station.TraceLevel = level;
        station.Trace += Events.Add;
    }

    public List<TraceEvent> Events { get; } = [];

    /// <summary>Every line, as <see cref="TraceEvent.ToString"/> writes it.</summary>
    public string[] Lines => [.. Events.Select(e => e.ToString())];

    /// <summary>The texts, with no clock.</summary>
    public string[] Texts => [.. Events.Select(e => e.Text)];

    /// <summary>The lines of one level only.</summary>
    public string[] LinesAt(TraceLevel level) => [.. Events.Where(e => e.Level == level).Select(e => e.ToString())];

    /// <summary>The first line whose text starts with <paramref name="start"/>, or null.</summary>
    public string? First(string start) => Events.FirstOrDefault(e => e.Text.StartsWith(start, StringComparison.Ordinal)).Text;

    public void Print(string title)
    {
        Console.WriteLine($"--- {title}");
        foreach (string line in Lines)
        {
            Console.WriteLine(line);
        }
    }
}

/// <summary>Scenarios whose traces the tests pin: the same runs each time, on the sample clock only.</summary>
internal static class TraceScenarios
{
    /// <summary>Two stations that know each other's addresses. S1 sends two datagrams (the burst at first contact carries one, so two bursts go), S2 answers with one of its own, and S1 sends again once it has been told what the path supports.</summary>
    public static (TraceTap S1, TraceTap S2) TwoStations()
    {
        var rig = new SimRig(2);
        var s1 = new TraceTap(rig.Stations[0]);
        var s2 = new TraceTap(rig.Stations[1]);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.Send(1, 2, 2);
        rig.RunTo(20);
        rig.Send(2, 1, 3);
        rig.RunTo(40);
        rig.Send(1, 2, 4);
        rig.RunTo(55);
        return (s1, s2);
    }

    /// <summary>
    /// Three stations that know nobody. S2 sends to S3 (not located, so to everyone for it), with S1 out of hearing of S2; then S3 answers, S1 hears S3 talking to S2, S1 asks for S2 (not located, so to everyone for it)
    /// and is answered, and S1 sends to everyone.
    /// </summary>
    public static (TraceTap S1, TraceTap S2, TraceTap S3) ThreeStations()
    {
        var rig = new SimRig(3, locates: (_, _) => false);
        var s1 = new TraceTap(rig.Stations[0]);
        var s2 = new TraceTap(rig.Stations[1]);
        var s3 = new TraceTap(rig.Stations[2]);
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 0;
        rig.RunTo(2);
        rig.Send(2, 3, 1);
        rig.RunTo(12);
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 1.0;
        rig.Send(3, 2, 2);
        rig.RunTo(22);
        rig.Send(1, 2, 3);
        rig.RunTo(40);
        rig.Send(1, LinkAddress.Everyone, 4);
        rig.RunTo(55);
        return (s1, s2, s3);
    }
}
