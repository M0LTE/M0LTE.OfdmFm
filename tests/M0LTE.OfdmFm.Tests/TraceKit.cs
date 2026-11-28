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
}

/// <summary>How the stations of a scenario are traced.</summary>
internal enum TraceSetting
{
    /// <summary>No handler.</summary>
    None,

    /// <summary>A handler at the Flow level.</summary>
    Flow,

    /// <summary>A handler at the Detail level.</summary>
    Detail,

    /// <summary>A handler at the Detail level that throws on every line.</summary>
    Throwing,
}

/// <summary>A scenario that has run: the rig, and the taps (null where the setting has no tap).</summary>
internal sealed record ScenarioRun(SimRig Rig, TraceTap?[] Taps)
{
    /// <summary>Everything a test can observe of the run: the channel's log of every station's every host event with its clock, and one more draw from each station's random source.</summary>
    public string Observed => string.Join("\n", Rig.Channel.Log) + "\ndraws " + string.Join(",", Rig.Randoms.Select(r => r.Next()));

    /// <summary>Line texts built and thrown away, across the stations.</summary>
    public long ThrownAway => Rig.Stations.Sum(s => s.TextsThrownAway);
}

/// <summary>Scenarios whose traces the tests pin: the same runs each time, on the sample clock only.</summary>
internal static class TraceScenarios
{
    public static TraceTap?[] Attach(IReadOnlyList<Station> stations, TraceSetting setting, TraceLevel? only = null)
    {
        var taps = new TraceTap?[stations.Count];
        for (int i = 0; i < taps.Length; i++)
        {
            switch (setting)
            {
                case TraceSetting.Flow:
                    taps[i] = new TraceTap(stations[i], only ?? TraceLevel.Flow);
                    break;
                case TraceSetting.Detail:
                    taps[i] = new TraceTap(stations[i], only ?? TraceLevel.Detail);
                    break;
                case TraceSetting.Throwing:
                    stations[i].TraceLevel = TraceLevel.Detail;
                    stations[i].Trace += _ => throw new InvalidOperationException("a faulty handler");
                    break;
            }
        }

        return taps;
    }

    /// <summary>Two stations that know each other's addresses. S1 sends two datagrams (the burst at first contact carries one, so two bursts go), S2 answers with one of its own, and S1 sends again once it has been told what the path supports.</summary>
    public static ScenarioRun TwoStationsRun(TraceSetting setting)
    {
        var rig = new SimRig(2);
        TraceTap?[] taps = Attach(rig.Stations, setting);
        rig.RunTo(2);
        rig.Send(1, 2, 1);
        rig.Send(1, 2, 2);
        rig.RunTo(20);
        rig.Send(2, 1, 3);
        rig.RunTo(40);
        rig.Send(1, 2, 4);
        rig.RunTo(55);
        return new ScenarioRun(rig, taps);
    }

    public static (TraceTap S1, TraceTap S2) TwoStations()
    {
        ScenarioRun run = TwoStationsRun(TraceSetting.Flow);
        return (run.Taps[0]!, run.Taps[1]!);
    }

    /// <summary>
    /// Three stations that know nobody. S2 sends to S3 (not located, so to everyone for it), with S1 out of hearing of S2; then S3 answers, S1 hears S3 talking to S2, S1 asks for S2 (not located, so to everyone for it)
    /// and is answered, and S1 sends to everyone.
    /// </summary>
    public static ScenarioRun ThreeStationsRun(TraceSetting setting)
    {
        var rig = new SimRig(3, locates: (_, _) => false);
        TraceTap?[] taps = Attach(rig.Stations, setting);
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
        return new ScenarioRun(rig, taps);
    }

    public static (TraceTap S1, TraceTap S2, TraceTap S3) ThreeStations()
    {
        ScenarioRun run = ThreeStationsRun(TraceSetting.Flow);
        return (run.Taps[0]!, run.Taps[1]!, run.Taps[2]!);
    }

    /// <summary>Two stations out of each other's hearing: S1 sends to S2 four times, each time after the last has met its silence.</summary>
    public static ScenarioRun SilenceRun(TraceSetting setting)
    {
        var rig = new SimRig(2, connect: false);
        TraceTap?[] taps = Attach(rig.Stations, setting);
        rig.RunTo(2);
        for (int i = 0; i < 4; i++)
        {
            rig.Send(1, 2, 10 + i);
            rig.Run(25);
        }

        return new ScenarioRun(rig, taps);
    }

    public static (SimRig Rig, TraceTap S1, TraceTap S2) Silence()
    {
        ScenarioRun run = SilenceRun(TraceSetting.Detail);
        return (run.Rig, run.Taps[0]!, run.Taps[1]!);
    }

    /// <summary>The existing interference scenario (a third station keys over the middle of a long burst from S1 to S2).</summary>
    public static ScenarioRun InterferenceRun(TraceSetting setting)
    {
        SimRig? made = null;
        TraceTap?[] taps = [];
        HostileScenarios.Interfered(HostileScenarios.Default, 0.5, 6, 1, onRig: rig =>
        {
            made = rig;
            taps = Attach(rig.Stations, setting);
        });
        return new ScenarioRun(made!, taps);
    }

    public static (TraceTap S1, TraceTap S2, TraceTap S3) Interference()
    {
        ScenarioRun run = InterferenceRun(TraceSetting.Detail);
        return (run.Taps[0]!, run.Taps[1]!, run.Taps[2]!);
    }

    /// <summary>Stations 1 and 2 both hold number 7 and cannot hear each other; station 3 hears both identify, owes a notice, and sends it, and the later station moves. <paramref name="listener"/>: station 3 has no callsign.</summary>
    public static ScenarioRun ClashRun(TraceSetting setting, bool listener = false)
    {
        var rig = new SimRig(3, numbers: [7, 7, 3], ids: [0x01010101, 0x02020202, 0x03030303], locates: (_, _) => false, connect: false, identified: listener ? i => i != 3 : null);
        rig.Channel.Connect(rig.Nodes[0], rig.Nodes[2], 1.0, 0.004);
        rig.Channel.Connect(rig.Nodes[1], rig.Nodes[2], 1.0, 0.004);
        foreach (Station s in rig.Stations)
        {
            s.IdentificationSuspended = false;
            s.IdentificationInterval = 20L * 48000;
        }

        TraceTap?[] taps = Attach(rig.Stations, setting);
        rig.RunTo(2);
        rig.Send(1, 3, 1);
        rig.RunTo(14);
        rig.Send(2, 3, 2);
        rig.RunTo(listener ? 60 : 100);
        return new ScenarioRun(rig, taps);
    }

    /// <summary>Every scenario by name, for the tests that run them all.</summary>
    public static readonly (string Name, Func<TraceSetting, ScenarioRun> Run)[] All =
    [
        ("two stations", TwoStationsRun),
        ("three stations", ThreeStationsRun),
        ("silence and forgetting", SilenceRun),
        ("interference", InterferenceRun),
        ("number clash", s => ClashRun(s)),
        ("number clash heard by a listener", s => ClashRun(s, listener: true)),
    ];
}
