using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

// Diagnostic only (packet-net/pdn-soundmodem#918): not for main. Same path, same absolute noise, several formats: what does each read on the carriers under 1.5 kHz, after the footing correction?
public class Diag918Measure
{
    private static string F(double v) => double.IsNaN(v) ? "nan" : v.ToString("F1", CultureInfo.InvariantCulture);

    [Fact]
    public void Compare()
    {
        int[] formats = [192, 208, 214, 220, 124, 31, 0];
        const double Thr192 = -4.5; // docs/station-design.md, measured by SuddenBench.Threshold
        double[] overs = [10, 20, 30, 40, 50, double.PositiveInfinity];
        const int Payloads = 8;
        double f192 = LinkAdaptation.Footing(192);
        foreach (double over in overs)
        {
            bool quiet = double.IsPositiveInfinity(over);
            double snr192 = Thr192 + over;
            double truth = snr192 - f192; // per-carrier SNR of a full-band burst at full drive, white noise
            var row = new List<string>();
            foreach (int fmt in formats)
            {
                double snr = snr192 - f192 + LinkAdaptation.Footing(fmt);
                var means = new List<double>();
                var ownBand = new List<double>();
                int ones = 0;
                int decoded = 0;
                var receiver = new TrackedReceiver();
                for (int k = 0; k < Payloads; k++)
                {
                    TestBurst a = SuddenBench.Burst(fmt, 2, 500_000 + (fmt * 1000) + k);
                    (float[] c, int st) = SuddenBench.Capture(a, quiet ? SuddenBench.SimClean : snr, 700_000 + (fmt * 1000) + k);
                    SuddenBench.Heard h = SuddenBench.Receive(receiver, a, c, st);
                    if (!h.Decoded)
                    {
                        continue;
                    }

                    decoded++;
                    ReadOnlySpan<double> q = receiver.Quality;
                    double footing = LinkAdaptation.Footing(fmt);
                    double s = 0;
                    for (int i = 0; i < 56; i++)
                    {
                        s += (10 * Math.Log10(q[i])) - footing;
                    }

                    means.Add(s / 56);
                    int used = StandardFormats.Describe(fmt).Carriers;
                    double s2 = 0;
                    for (int i = 0; i < used; i++)
                    {
                        s2 += (10 * Math.Log10(q[i])) - footing;
                    }

                    ownBand.Add(s2 / used);
                    for (int i = 0; i < 333; i++)
                    {
                        if (q[i] == 1.0)
                        {
                            ones++;
                        }
                    }

                    Hostile.Say($"diag918 over {F(over)} fmt {fmt} payload {k} symbols {h.Symbols} mean<1.5k corrected {F(s / 56)} own band {F(s2 / used)}");
                }

                string sd = means.Count > 1 ? F(Math.Sqrt(means.Select(m => (m - means.Average()) * (m - means.Average())).Sum() / (means.Count - 1))) : "-";
                row.Add($"{fmt}: dec {decoded}/{Payloads} <1.5k {(means.Count > 0 ? F(means.Average()) : "-")} (sd {sd}, min {(means.Count > 0 ? F(means.Min()) : "-")}, max {(means.Count > 0 ? F(means.Max()) : "-")}) own {(ownBand.Count > 0 ? F(ownBand.Average()) : "-")} carriers=1.0 per burst {(decoded > 0 ? ones / decoded : 0)}");
            }

            Hostile.Say($"diag918 SUMMARY over192thr {(quiet ? "quiet" : F(over))} truth {(quiet ? "-" : F(truth))} | " + string.Join(" | ", row));
        }
    }
}
