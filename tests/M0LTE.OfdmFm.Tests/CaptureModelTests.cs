using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>The capture model of the simulated channel (step 8): what a receiver hears when two signals overlap. A model with assumed numbers, not a measurement; these tests pin that the code does what the model says.</summary>
public class CaptureModelTests
{
    private static float[] Tone(int length, double amplitude, double hz, double phase = 0) =>
        [.. Enumerable.Range(0, length).Select(i => (float)(amplitude * Math.Sin(phase + (2 * Math.PI * hz * i / 48000))))];

    private static SimNode Node(params (long Start, float[] Data)[] signals)
    {
        var node = new SimNode(new Station(1, new Random(1), runJobsInline: true).WithIdentity("M0TST"), "X", 0);
        foreach ((long start, float[] data) in signals)
        {
            node.Heard.Add(new Incoming { Start = start, Data = data, Level = CaptureModel.Levels(data) });
        }

        return node;
    }

    private static float[] Mix(SimNode node, CaptureModel model, long from, long to, int step)
    {
        node.CaptureRandom ??= new Random(5);
        var all = new float[to - from];
        for (long at = from; at < to; at += step)
        {
            int count = (int)Math.Min(step, to - at);
            SimChannel.MixWithCapture(node, all.AsSpan((int)(at - from), count), at, at + count, model);
        }

        return all;
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (float f in x)
        {
            s += (double)f * f;
        }

        return Math.Sqrt(s / x.Length);
    }

    [Fact]
    public void The_Defaults_Are_The_Stated_Assumptions()
    {
        var m = new CaptureModel();
        (m.MarginDb, m.SuppressionBaseDb, m.SuppressionSlope).Should().Be((3.0, 10.0, 1.0));
        m.Residual(3.0).Should().BeApproximately(Math.Pow(10, -10 / 20.0), 1e-12, "10 dB down at the margin");
        m.Residual(13.0).Should().BeApproximately(Math.Pow(10, -20 / 20.0), 1e-12, "a dB more of each dB of difference");
    }

    [Fact]
    public void One_Signal_On_Its_Own_Passes_Unchanged_And_Silence_Does_Not_Capture()
    {
        float[] signal = Tone(9600, 0.2, 1000);
        float[] silence = new float[9600];
        SimNode node = Node((0, signal), (0, silence));
        float[] heard = Mix(node, new CaptureModel(), 0, 9600, 4800);
        heard.Should().Equal(signal, "a signal over silence is the signal");
    }

    [Fact]
    public void A_Stronger_Signal_Captures_The_Receiver_And_The_Weaker_Is_Left_As_Noise_At_The_Level_The_Law_Gives()
    {
        float[] strong = Tone(48000, 0.2, 1000);
        float[] weak = Tone(48000, 0.02, 2300, 1.0);
        var model = new CaptureModel();
        SimNode node = Node((0, strong), (0, weak));
        float[] heard = Mix(node, model, 4800, 48000, 4800);
        float[] residual = [.. heard.Select((h, i) => h - strong[i + 4800])];

        // 20 dB apart: difference 20 dB, the weaker is 10 + (20 - 3) = 27 dB below its own level, and it is noise (not the tone it was).
        double expected = Rms(weak.AsSpan(4800)) * model.Residual(20.0);
        Rms(residual).Should().BeApproximately(expected, expected * 0.15);
        double toneInResidual = 0;
        for (int i = 0; i < residual.Length; i++)
        {
            toneInResidual += residual[i] * Math.Sin(1.0 + (2 * Math.PI * 2300 * (i + 4800) / 48000.0));
        }

        Math.Abs(toneInResidual / residual.Length).Should().BeLessThan(Rms(residual) * 0.05, "the weaker signal is gone, not attenuated");
    }

    [Fact]
    public void Two_Signals_Within_The_Margin_Are_Unusable_Noise_At_About_Their_Combined_Level()
    {
        float[] a = Tone(48000, 0.2, 1000);
        float[] b = Tone(48000, 0.2 * Math.Pow(10, -2 / 20.0), 2300, 1.0);
        SimNode node = Node((0, a), (0, b));
        float[] heard = Mix(node, new CaptureModel(), 4800, 48000, 4800);
        double combined = Math.Sqrt((0.2 * 0.2 / 2) + (b.Select(x => (double)x * x).Average()));
        Rms(heard).Should().BeApproximately(combined, combined * 0.1);
        double alike = 0;
        for (int i = 0; i < heard.Length; i++)
        {
            alike += heard[i] * a[i + 4800];
        }

        Math.Abs(alike / heard.Length).Should().BeLessThan(Rms(heard) * 0.2 * 0.1, "nothing of either survives");
    }

    [Fact]
    public void The_Margin_Moves_Where_The_Receiver_Is_Captured()
    {
        float[] strong = Tone(48000, 0.2, 1000);
        float[] weak = Tone(48000, 0.2 * Math.Pow(10, -4 / 20.0), 2300, 1.0);
        float Captured(double margin)
        {
            float[] heard = Mix(Node((0, strong), (0, weak)), new CaptureModel(margin), 4800, 48000, 4800);
            return (float)(Rms(heard) / Rms(strong.AsSpan(4800)));
        }

        Captured(3.0).Should().BeApproximately(1.0f, 0.05f, "4 dB apart is over a 3 dB margin: the strong signal");
        Captured(6.0).Should().BeGreaterThan(1.0f, "4 dB apart is under a 6 dB margin: noise at the combined level, which is louder");
        Math.Abs(Captured(6.0) - 1.0).Should().BeGreaterThan(0.1);
    }

    [Fact]
    public void The_Audio_Does_Not_Depend_On_How_The_Run_Is_Cut_Into_Blocks()
    {
        float[] strong = Tone(24000, 0.2, 1000);
        float[] weak = Tone(24000, 0.05, 2300, 1.0);
        float[] equal = Tone(24000, 0.19, 3100, 2.0);
        var model = new CaptureModel();
        float[] whole = Mix(Node((0, strong), (3000, weak), (12000, equal)), model, 0, 24000, 24000);
        foreach (int step in new[] { 1, 997, 4800 })
        {
            Mix(Node((0, strong), (3000, weak), (12000, equal)), model, 0, 24000, step).Should().Equal(whole, $"blocks of {step}");
        }
    }

    [Fact]
    public void A_Channel_With_A_Model_Gives_The_Same_Audio_As_The_Adder_Where_Nothing_Overlaps_And_Records_The_Level_Of_Each_Signal()
    {
        float[] signal = Tone(9600, 0.2, 1000);
        float[] levels = CaptureModel.Levels(signal);
        levels[^1].Should().BeApproximately(0.2f / (float)Math.Sqrt(2), 0.01f);
        levels[0].Should().BeApproximately(Math.Abs(signal[0]), 1e-6f);
    }
}
