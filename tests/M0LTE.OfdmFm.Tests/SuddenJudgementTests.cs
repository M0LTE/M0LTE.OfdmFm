using System.Collections.Concurrent;
using static System.FormattableString;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The receiver's judgement that a burst's quality collapsed part-way (packet-net/pdn-soundmodem#898; docs/station-design.md, "The sudden judgement, as fixed"): a clean burst is not judged sudden at any format or
/// signal level, a burst that another lands on part-way is, and a burst on a path too weak throughout is not. The bursts are made by <see cref="SuddenBench"/> from seeded sources.
/// </summary>
public class SuddenJudgementTests
{
    /// <summary>The formats measured: first contact, one in the middle (QPSK on the 3 kHz band, the middle opening) and a dense one.</summary>
    private static readonly int[] Formats = [StandardFormats.FirstContact, 140, 124];

    /// <summary>
    /// Each format's threshold as <see cref="SuddenBench.Threshold"/> measures it, per-carrier signal to noise in dB: the gated test measures them again and checks they still are these. The always-on tests
    /// put their noisy bursts 40 dB above them.
    /// </summary>
    private static readonly Dictionary<int, double> Thresholds = new() { [StandardFormats.FirstContact] = -4.5, [140] = 2.0, [124] = 25.0 };

    /// <summary>
    /// The prototype's rule (<c>modem.sudden</c>), which the receiver used until #898, kept here only to measure against: an earlier part whose median miss is under 1.5 times the opening's plus 0.5, and a later part whose
    /// median is over three times the earlier part's (or 1.5); each part at least two symbols, the payload at least six.
    /// </summary>
    internal static bool PrototypeRule(ReadOnlySpan<double> miss, int known)
    {
        ReadOnlySpan<double> m = miss[known..];
        if (m.Length < 6)
        {
            return false;
        }

        double reference = known != 0 ? Median(miss[..known]) : Median(m[..2]);
        for (int cut = 2; cut < m.Length - 1; cut++)
        {
            double before = Median(m[..cut]);
            double after = Median(m[cut..]);
            if (before < (1.5 * reference) + 0.5 && after > 3.0 * Math.Max(before, 0.5))
            {
                return true;
            }
        }

        return false;
    }

    private static double Median(ReadOnlySpan<double> x)
    {
        double[] s = x.ToArray();
        Array.Sort(s);
        int n = s.Length;
        return (n & 1) == 1 ? s[n / 2] : (s[(n / 2) - 1] + s[n / 2]) / 2;
    }

    /// <summary>One burst of the measurement: what it was, and what the receiver made of it.</summary>
    private sealed record Trial(int Format, string Kind, double Level, double Fraction, double RelativeDb, bool Decoded, bool Old, bool New);

    /// <summary>Receives each of <paramref name="jobs"/> (kind, level over the threshold or <see cref="SuddenBench.SimClean"/>, fraction, interferer dB, index) at <paramref name="format"/>, over the cores.</summary>
    private static List<Trial> Run(int format, double threshold, IEnumerable<(string Kind, double Level, double Fraction, double RelativeDb, int K)> jobs)
    {
        var trials = new ConcurrentBag<Trial>();
        Parallel.ForEach(jobs, () => new TrackedReceiver(), (j, _, receiver) =>
        {
            TestBurst a = SuddenBench.Burst(format, 2, (format * 100_000) + j.K);
            TestBurst? b = j.Kind == "collide" ? SuddenBench.Burst(format, 3, (format * 100_000) + 50_000 + j.K, 64, 64) : null;
            double snr = double.IsPositiveInfinity(j.Level) ? j.Level : threshold + j.Level;
            (float[] capture, int start) = SuddenBench.Capture(a, snr, (format * 7919) + (j.K * 31) + (int)(j.Level * 10) + 1, b, j.Fraction, j.RelativeDb);
            SuddenBench.Heard h = SuddenBench.Receive(receiver, a, capture, start);
            trials.Add(new Trial(format, j.Kind, j.Level, j.Fraction, j.RelativeDb, h.Decoded, PrototypeRule(h.Miss, h.Known), PayloadReceiver.IsSudden(h.Miss, h.Known)));
            return receiver;
        }, _ => { });
        return [.. trials];
    }

    [Fact]
    public void Clean_Bursts_Are_Not_Judged_Sudden_Whatever_They_Carry_At_Any_Format()
    {
        // The sims' own quiet link, where the sender's peak limit and not the noise sets each symbol's miss (the case #898 was found in), and white noise 40 dB over each format's threshold: 40 bursts of each.
        foreach (int format in Formats)
        {
            var jobs = new List<(string, double, double, double, int)>();
            for (int k = 0; k < 40; k++)
            {
                jobs.Add(("clean", SuddenBench.SimClean, 0, 0, k));
                jobs.Add(("clean", 40, 0, 0, k));
            }

            List<Trial> trials = Run(format, Thresholds[format], jobs);
            List<Trial> decoded = trials.Where(t => t.Decoded).ToList();
            Hostile.Say($"sudden: format {format}, clean bursts decoded {decoded.Count} of {trials.Count}, judged sudden by the prototype's rule {decoded.Count(t => t.Old)}, by the receiver's {decoded.Count(t => t.New)}");
            decoded.Should().HaveCount(trials.Count, "every clean burst decodes");
            decoded.Should().NotContain(t => t.New, $"no clean burst at format {format} is judged sudden");
        }
    }

    [Fact]
    public void A_Burst_Another_Lands_On_Part_Way_At_First_Contact_Is_Judged_Sudden()
    {
        // Scenario 3 of step 8 at the first-contact format: another station's burst, 6 dB stronger, from a quarter, a half and three quarters of the way through the payload to its end, on the sims' quiet link and 25 dB over the threshold.
        var jobs = new List<(string, double, double, double, int)>();
        foreach (double level in new[] { SuddenBench.SimClean, 25 })
        {
            foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
            {
                for (int k = 0; k < 4; k++)
                {
                    jobs.Add(("collide", level, fraction, 6.0, k));
                }
            }
        }

        List<Trial> trials = Run(StandardFormats.FirstContact, Thresholds[StandardFormats.FirstContact], jobs);
        Hostile.Say($"sudden: first contact, a burst 6 dB stronger landing part-way, judged sudden by the prototype's rule {trials.Count(t => t.Old)} of {trials.Count}, by the receiver's {trials.Count(t => t.New)}");
        trials.Should().HaveCount(24).And.OnlyContain(t => t.New);
    }

    [Fact]
    public void The_Judgement_Is_True_Only_For_A_Collapse_Part_Way_Below_The_Burst_s_Own_Scatter()
    {
        // A clean first-contact burst on a quiet path (rounded from one the station received in the step 6 tests, datagram 100): its symbols scatter from 0.5 to 10 with the peak limit, and its later part's median is over
        // three times its earlier part's. The prototype's rule judged it sudden; it is not.
        double[] clean =
        [
            3.39, 2.08, 1.22, 6.22, 0.58, 0.74, 2.30, 0.98, 1.40,
            5.90, 0.67, 1.65, 2.03, 1.87, 2.63, 4.92, 0.82, 1.88, 0.63, 0.62, 0.63, 0.96, 3.68, 0.95, 2.97, 0.52, 1.18, 2.29, 0.66, 1.09, 1.33,
            9.54, 0.95, 2.94, 4.48, 1.83, 4.83, 3.52, 9.96, 3.05, 4.40, 5.65, 4.14, 0.75, 1.48,
        ];
        PrototypeRule(clean, 9).Should().BeTrue();
        PayloadReceiver.IsSudden(clean, 9).Should().BeFalse();

        // The same early part, then something else on the channel: every later symbol far above anything before.
        double[] hit = [.. clean[..20], .. Enumerable.Repeat(400.0, 25)];
        PayloadReceiver.IsSudden(hit, 9).Should().BeTrue();

        // The later part must be three symbols or more, so one wild last symbol is not a collapse.
        PayloadReceiver.IsSudden([2.0, 2.0, 1.0, 1.1, 0.9, 1.0, 1.0, 1.1, 1.0, 1.0, 50.0], 2).Should().BeFalse();
        PayloadReceiver.IsSudden([2.0, 2.0, 1.0, 1.1, 0.9, 1.0, 1.0, 1.1, 50.0, 50.0, 50.0], 2).Should().BeTrue();

        // Three times the opening's median at least, however clean the earlier part.
        PayloadReceiver.IsSudden([2.0, 2.0, 0.1, 0.1, 5.0, 5.0, 5.0, 5.0], 2).Should().BeFalse();
        PayloadReceiver.IsSudden([2.0, 2.0, 0.1, 0.1, 7.0, 7.0, 7.0, 7.0], 2).Should().BeTrue();
    }

    [Fact]
    public void The_Judgement_Allocates_Nothing()
    {
        var receiver = new TrackedReceiver();
        TestBurst a = SuddenBench.Burst(StandardFormats.FirstContact, 2, 1);
        (float[] capture, int start) = SuddenBench.Capture(a, SuddenBench.SimClean, 1);
        SuddenBench.Receive(receiver, a, capture, start).Decoded.Should().BeTrue();
        bool sudden = false;
        Action work = () => sudden |= receiver.Sudden;
        AllocationMeasure.Minimum(work).Should().Be(0);
        sudden.Should().BeFalse();
    }

    [Fact]
    public void Measures_The_Old_Rule_And_The_New_At_Three_Formats()
    {
        // Gated: about 13 000 bursts. The table in the pull request and in docs/station-design.md is this test's output (SM_HOSTILE_OUT=<file> writes it to a file).
        Hostile.Gate();
        double[] cleanLevels = [0, 10, 20, 30, 40, SuddenBench.SimClean];
        double[] collideLevels = [10, 25, SuddenBench.SimClean];
        double[] fractions = [0.25, 0.5, 0.75];
        double[] weakLevels = [-1, -3];
        string Level(double l) => double.IsPositiveInfinity(l) ? "quiet" : Invariant($"+{l:0} dB");
        string Weak(double l) => Invariant($"{l:0} dB");
        string Share(IEnumerable<Trial> t, Func<Trial, bool> rule)
        {
            List<Trial> list = t.ToList();
            return $"{list.Count(rule)}/{list.Count}";
        }

        var cleanRows = new List<string>();
        var collideRows = new List<string>();
        var weakRows = new List<string>();
        foreach (int format in Formats)
        {
            double threshold = SuddenBench.Threshold(format);
            threshold.Should().Be(Thresholds[format], "the always-on tests put their noise against this figure");
            var jobs = new List<(string, double, double, double, int)>();
            foreach (double level in cleanLevels)
            {
                for (int k = 0; k < 500; k++)
                {
                    jobs.Add(("clean", level, 0, 0, k));
                }
            }

            foreach (double level in collideLevels)
            {
                foreach (double fraction in fractions)
                {
                    foreach (double rel in new[] { 6.0, -10.0 })
                    {
                        for (int k = 0; k < 50; k++)
                        {
                            jobs.Add(("collide", level, fraction, rel, k));
                        }
                    }
                }
            }

            foreach (double level in weakLevels)
            {
                for (int k = 0; k < 200; k++)
                {
                    jobs.Add(("weak", level, 0, 0, k));
                }
            }

            List<Trial> trials = Run(format, threshold, jobs);
            List<Trial> clean = trials.Where(t => t.Kind == "clean" && t.Decoded).ToList();
            cleanRows.Add(Invariant($"| {format} ({threshold:0.0} dB) | ") + string.Join(" | ", cleanLevels.Select(l => $"{Share(clean.Where(t => t.Level == l), t => t.Old)} -> {Share(clean.Where(t => t.Level == l), t => t.New)}")) + " |");
            foreach (double level in collideLevels)
            {
                foreach (double rel in new[] { 6.0, -10.0 })
                {
                    IEnumerable<Trial> Cell(double f) => trials.Where(t => t.Kind == "collide" && t.Level == level && t.RelativeDb == rel && t.Fraction == f);
                    collideRows.Add(Invariant($"| {format} | {Level(level)} | {rel:+0;-0} dB | ") + string.Join(" | ", fractions.Select(f => $"{Share(Cell(f), t => t.Old)} -> {Share(Cell(f), t => t.New)}")) + $" | {Cell(0.25).Concat(Cell(0.5)).Concat(Cell(0.75)).Count(t => t.Decoded)}/{Cell(0.25).Concat(Cell(0.5)).Concat(Cell(0.75)).Count()} |");
                }
            }

            List<Trial> weak = trials.Where(t => t.Kind == "weak" && !t.Decoded).ToList();
            weakRows.Add($"| {format} | " + string.Join(" | ", weakLevels.Select(l => $"{Share(weak.Where(t => t.Level == l), t => t.Old)} -> {Share(weak.Where(t => t.Level == l), t => t.New)}")) + " |");

            // What the receiver's rule must give: no clean burst judged sudden at any level, no burst too weak throughout, and a stronger burst landing part-way on a path 10 dB or more over the threshold nearly always.
            clean.Should().NotContain(t => t.New, $"format {format}");
            weak.Should().NotContain(t => t.New, $"format {format}");
            List<Trial> stronger = trials.Where(t => t.Kind == "collide" && t.RelativeDb > 0).ToList();
            stronger.Count(t => t.New).Should().BeGreaterThanOrEqualTo((int)(0.95 * stronger.Count), $"format {format}");
        }

        Hostile.Say("Clean bursts that decoded, judged sudden (prototype's rule -> receiver's), by signal to noise per carrier over the format's threshold (quiet: the sims' own link):");
        Hostile.Say("| Format (threshold) | " + string.Join(" | ", cleanLevels.Select(Level)) + " |");
        Hostile.Say("|---|" + string.Concat(cleanLevels.Select(_ => "---|")));
        cleanRows.ForEach(Hostile.Say);
        Hostile.Say(string.Empty);
        Hostile.Say("Another burst landing part-way through the payload and lasting to its end, judged sudden (prototype's rule -> receiver's), and how many of those bursts still decoded:");
        Hostile.Say("| Format | Path | Interferer | 1/4 | 1/2 | 3/4 | Decoded |");
        Hostile.Say("|---|---|---|---|---|---|---|");
        collideRows.ForEach(Hostile.Say);
        Hostile.Say(string.Empty);
        Hostile.Say("Bursts on a path too weak throughout that failed, judged sudden (prototype's rule -> receiver's), by signal to noise under the threshold:");
        Hostile.Say("| Format | " + string.Join(" | ", weakLevels.Select(Weak)) + " |");
        Hostile.Say("|---|" + string.Concat(weakLevels.Select(_ => "---|")));
        weakRows.ForEach(Hostile.Say);
    }
}
