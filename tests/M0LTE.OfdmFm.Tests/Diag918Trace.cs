using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

// Diagnostic only (packet-net/pdn-soundmodem#918): not for main.
public class Diag918Trace
{
    private static string F(double v) => double.IsNaN(v) ? "nan" : v.ToString("F1", CultureInfo.InvariantCulture);

    // carrier ranges: 1.5 kHz band [0,56), 1.5 to 4 kHz [56,162), 4 to 8 kHz [162,333)
    private static readonly (int Lo, int Hi)[] Ranges = [(0, 56), (56, 162), (162, 333)];

    private static string EstSummary(double[]? est)
    {
        if (est is null)
        {
            return "none";
        }

        return string.Join("/", Ranges.Select(r => F(MeanDb(est, r.Lo, r.Hi))));
    }

    private static double MeanDb(double[] v, int lo, int hi)
    {
        double s = 0;
        int n = 0;
        for (int i = lo; i < hi; i++)
        {
            if (!double.IsNaN(v[i]))
            {
                s += v[i];
                n++;
            }
        }

        return n == 0 ? double.NaN : s / n;
    }

    private static string QualSummary(double[] q, double footing)
    {
        if (q.Length == 0)
        {
            return "none";
        }

        var db = q.Select(x => double.IsNaN(x) ? double.NaN : (10 * Math.Log10(Math.Max(x, 1e-6)))).ToArray();
        var corrected = db.Select(x => x - footing).ToArray();
        int nan = q.Count(double.IsNaN);
        int ones = q.Count(x => x == 1.0);
        return $"raw {string.Join("/", Ranges.Select(r => F(MeanDb(db, r.Lo, r.Hi))))} corr {string.Join("/", Ranges.Select(r => F(MeanDb(corrected, r.Lo, r.Hi))))} nan {nan} exactly1 {ones}";
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(2)]
    public void Trace(int seed)
    {
        var lines = new List<string>();
        SimRig? theRig = null;
        var last = new Dictionary<(int, int), double[]>();
        HostileScenarios.PathCutHook = rig =>
        {
            theRig = rig;
            for (int st = 0; st < 2; st++)
            {
                int me = st;
                Station s = rig.Stations[st];
                s.AdaptationCalled = c =>
                {
                    int other = me == 0 ? 2 : 1;
                    if (c.Sender != other)
                    {
                        return;
                    }

                    s.Adaptation.TryGetNeighbour(c.Sender, out LinkNeighbour? nb);
                    double[] after = nb!.EstimateDb.ToArray();
                    last.TryGetValue((me, c.Sender), out double[]? before);
                    last[(me, c.Sender)] = after;
                    if (c.IsReport)
                    {
                        lines.Add($"{rig.Channel.Clock / 48000.0:F2} at {(me == 0 ? "A" : "B")} REPORT from {c.Sender} rec {c.TheirReport?.Format} failed {c.TheirReport?.Failed}; A.ReportFormat(B) {(me == 0 ? nb.ReportFormat : null)}");
                        return;
                    }

                    lines.Add($"{rig.Channel.Clock / 48000.0:F2} at {(me == 0 ? "A" : "B")} from {c.Sender} dest {c.Dest} fmt {c.Format} dec {c.Decoded} sudden {c.Sudden} footing {F(c.FootingDb ?? double.NaN)} q[{QualSummary(c.Quality, c.FootingDb ?? 0)}] est before {EstSummary(before)} after {EstSummary(after)} measures {nb.Measures} rec {nb.Recommended} pend {nb.Pending} margin {nb.Margin:F2} cap {nb.Cap} theirRep {c.TheirReport?.Format} reportFormatHeld {nb.ReportFormat} returned {c.Returned?.Format}");
                };
            }
        };
        try
        {
            HostileScenarios.Cut r = HostileScenarios.PathCut(HostileScenarios.Default, seed);
            Hostile.Say($"==== seed {seed}: {HostileScenarios.Describe(r)}");
            double from = r.CutEnd - 20;
            foreach (string l in lines.Where(l => double.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture) >= from && double.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture) < r.CutEnd + 200))
            {
                Hostile.Say($"seed {seed} {l}");
            }

            foreach ((long clock, TransmittedEvent e) in theRig!.Nodes[0].Transmitted.Where(t => t.Event.Address == SimRig.Address(2) && t.Clock / 48000.0 >= from && t.Clock / 48000.0 < r.CutEnd + 200))
            {
                Hostile.Say($"seed {seed} {clock / 48000.0:F2} A TX to B fmt {e.Format} rate {e.RateBitsPerSecond / 1000:F1}");
            }

            foreach ((long clock, TransmittedEvent e) in theRig!.Nodes[1].Transmitted.Where(t => t.Event.Address == SimRig.Address(1) && t.Clock / 48000.0 >= from && t.Clock / 48000.0 < r.CutEnd + 200))
            {
                Hostile.Say($"seed {seed} {clock / 48000.0:F2} B TX to A fmt {e.Format} rate {e.RateBitsPerSecond / 1000:F1}");
            }
        }
        finally
        {
            HostileScenarios.PathCutHook = null;
        }
    }
}
