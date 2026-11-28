using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// The simulated channel's three impairments (impulses, flutter, a steady path change), each shown to do what it says, and shown to be reproducible whatever the block size. These test the channel, not the station.
/// Counts are compared with the figures the model implies, to four standard deviations (a false failure about one run in 16 000 each).
/// </summary>
public class SimImpairmentTests
{
    private static ImpulseSource Run(ImpulseModel model, int seed, double seconds, int block = 4800, float[]? keep = null)
    {
        var source = new ImpulseSource(model, seed);
        var buffer = new float[block];
        long total = (long)(seconds * 48000);
        for (long at = 0; at < total; at += block)
        {
            int n = (int)Math.Min(block, total - at);
            Array.Clear(buffer);
            source.AddTo(buffer.AsSpan(0, n), at);
            if (keep is not null)
            {
                buffer.AsSpan(0, n).CopyTo(keep.AsSpan((int)at, n));
            }
        }

        return source;
    }

    [Theory]
    [InlineData(3.0, 1)]
    [InlineData(3.0, 2)]
    [InlineData(0.5, 3)]
    [InlineData(10.0, 4)]
    public void Impulse_Events_Arrive_At_The_Given_Mean_Rate_Within_Four_Standard_Deviations(double rate, int seed)
    {
        // Poisson: over 1000 s the count has mean rate * 1000 and standard deviation its square root.
        double mean = rate * 1000;
        double bound = 4 * Math.Sqrt(mean);
        ImpulseSource s = Run(new ImpulseModel(rate, 0), seed, 1000);
        s.Events.Should().BeInRange((int)(mean - bound), (int)(mean + bound + 1));
        s.Pulses.Count.Should().Be(s.Events, "with no trains every event is one impulse");
    }

    [Fact]
    public void Single_Impulses_Last_Between_0_2_And_5_Milliseconds_And_Average_2_6()
    {
        ImpulseSource s = Run(new ImpulseModel(3, 0), 7, 1000);
        double[] lengths = [.. s.Pulses.Select(p => (p.End - p.Start) / 48.0)];
        lengths.Min().Should().BeGreaterThanOrEqualTo(0.2 - 0.03);
        lengths.Max().Should().BeLessThanOrEqualTo(5.0 + 0.03);

        // Uniform on 0.2 to 5 ms: mean 2.6 ms, standard deviation 1.386 ms; over about 3000 impulses the mean is within 4 * 1.386 / sqrt(3000) = 0.10 ms.
        lengths.Average().Should().BeApproximately(2.6, 4 * 1.386 / Math.Sqrt(lengths.Length));
    }

    [Fact]
    public void An_Impulse_Has_The_Given_Level_Against_The_Reference_Signal()
    {
        const double levelDb = 10;
        var keep = new float[48000 * 600];
        ImpulseSource s = Run(new ImpulseModel(1, levelDb), 5, 600, keep: keep);

        // At 1 impulse a second the chance that two overlap is under half a percent, so the power over the samples that are inside an impulse is its level squared.
        double sum = 0;
        long count = 0;
        foreach (ImpulsePulse p in s.Pulses.Where(p => p.End < keep.Length))
        {
            for (long k = p.Start; k < p.End; k++)
            {
                sum += (double)keep[k] * keep[k];
                count++;
            }
        }

        double expected = SimChannel.ReferenceRms * Math.Pow(10, levelDb / 20);
        Math.Sqrt(sum / count).Should().BeApproximately(expected, expected * 0.03);
        keep.Where(v => v != 0).Count().Should().BeLessThanOrEqualTo((int)(count * 1.01), "nothing is added outside the impulses");
    }

    [Fact]
    public void A_Train_Is_A_Run_Of_Impulses_At_A_Fixed_Rate_Between_50_And_200_Hz_Lasting_0_1_To_1_Second()
    {
        ImpulseSource s = Run(new ImpulseModel(0.5, 0, 1.0), 11, 600);
        s.Pulses.Should().OnlyContain(p => p.Train >= 0);
        s.Events.Should().BeGreaterThan(200);

        // A train that began in the last second of the run is cut short by its end.
        IGrouping<int, ImpulsePulse>[] trains = [.. s.Pulses.GroupBy(p => p.Train).Where(g => g.First().Start < 598 * 48000L)];
        trains.Length.Should().BeGreaterThan(200);
        foreach (IGrouping<int, ImpulsePulse> t in trains)
        {
            ImpulsePulse[] p = [.. t];
            long[] gaps = [.. p.Zip(p.Skip(1), (a, b) => b.Start - a.Start)];
            p.Length.Should().BeInRange(5, 200);
            if (gaps.Length > 0)
            {
                (gaps.Max() - gaps.Min()).Should().BeLessThanOrEqualTo(1, "a fixed repetition rate, to the sample");
                (48000.0 / gaps.Average()).Should().BeInRange(49.5, 200.5);
                ((p[^1].Start - p[0].Start) / 48000.0).Should().BeLessThanOrEqualTo(1.0);
            }
        }

        // The fastest and slowest trains of 300 or so reach well into both ends of the ranges.
        trains.Max(t => t.Count()).Should().BeGreaterThan(100);
        trains.Min(t => t.Count()).Should().BeLessThan(20);
    }

    [Fact]
    public void The_Share_Of_Events_That_Are_Trains_Follows_The_Probability_Within_Four_Standard_Deviations()
    {
        ImpulseSource s = Run(new ImpulseModel(2, 0, 0.3), 13, 1000);
        int trains = s.Pulses.Where(p => p.Train >= 0).Select(p => p.Train).Distinct().Count();
        double n = s.Events;
        double bound = 4 * Math.Sqrt(n * 0.3 * 0.7);
        trains.Should().BeInRange((int)((n * 0.3) - bound), (int)((n * 0.3) + bound + 1));
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Impulse_Samples_Whatever_The_Block_Size_And_Another_Seed_Does_Not()
    {
        var model = new ImpulseModel(20, 6, 0.2);
        var a = new float[48000 * 20];
        var b = new float[a.Length];
        var c = new float[a.Length];
        var d = new float[a.Length];
        Run(model, 3, 20, 4800, a);
        Run(model, 3, 20, 777, b);
        Run(model, 3, 20, 1, c);
        Run(model, 4, 20, 4800, d);
        b.Should().Equal(a);
        c.Should().Equal(a);
        d.Should().NotEqual(a);
        a.Count(v => v != 0).Should().BeGreaterThan(1000);
    }

    // ---- flutter ----

    /// <summary>Two stations on a noiseless path with a flutter episode of 2 to 2 + <paramref name="seconds"/> s; a 1 kHz tone of amplitude 0.1 goes across for the whole of it. Returns the tone's amplitude at the receiver, <see cref="PerSecond"/> a second.</summary>
    // The envelope is measured every 4 samples (12 000 a second), so that the sampling does not blunt the bottom of a fast, deep fade.
    private const int Hop = 4;
    private const int PerSecond = 12000;

    private static double[] ToneEnvelope(FlutterModel model, double seconds, int block = 4800)
    {
        var rig = new SimRig(2, block, noise: 0.0);
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Flutter = model;
        rig.Nodes[1].RecordInput = true;
        int length = (int)((seconds + 4) * 48000);
        var tone = new float[length];
        for (int i = 0; i < length; i++)
        {
            tone[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        }

        rig.Channel.Interfere(rig.Nodes[0], tone, 0);
        rig.RunTo(seconds + 4);
        List<float> heard = rig.Nodes[1].Input;
        var envelope = new List<double>();
        const int window = 48;
        for (int at = 0; at + window <= heard.Count; at += Hop)
        {
            double re = 0, im = 0;
            for (int i = 0; i < window; i++)
            {
                double w = 2 * Math.PI * 1000 * (at + i) / 48000.0;
                re += heard[at + i] * Math.Cos(w);
                im += heard[at + i] * Math.Sin(w);
            }

            envelope.Add(2.0 / window * Math.Sqrt((re * re) + (im * im)));
        }

        return [.. envelope];
    }

    /// <summary>The fades between the envelope values <paramref name="from"/> and <paramref name="to"/> (in seconds): each lowest point of the envelope within 10 ms either side.</summary>
    private static int Minima(IReadOnlyList<double> e, double from, double to)
    {
        const int reach = PerSecond / 100;
        int count = 0;
        for (int i = Math.Max(reach, (int)(from * PerSecond)); i < Math.Min(e.Count - reach, (int)(to * PerSecond)); i++)
        {
            bool lowest = true;
            for (int j = i - reach; j <= i + reach && lowest; j++)
            {
                lowest = j == i || (j < i ? e[j] > e[i] : e[j] >= e[i]);
            }

            if (lowest)
            {
                count++;
            }
        }

        return count;
    }

    [Theory]
    [InlineData(0.5, 3.0)]
    [InlineData(0.9, 3.0)]
    [InlineData(0.5, 20.0)]
    [InlineData(0.9, 20.0)]
    public void A_Fixed_Rate_Flutter_Fades_To_The_Depth_And_At_The_Rate_Given(double strength, double hz)
    {
        double[] e = ToneEnvelope(new FlutterModel(strength, hz, false, 2, 12), 12);

        // The plateau, 3.5 to 12.5 s (the reflection takes a second to come up and to go away). 
        double[] plateau = e[(int)(3.5 * PerSecond)..(int)(12.5 * PerSecond)];
        double depth = 20 * Math.Log10(plateau.Max() / plateau.Min());
        double expected = 20 * Math.Log10((1 + strength) / (1 - strength));
        depth.Should().BeApproximately(expected, 0.5);
        Minima(e, 3.5, 12.5).Should().BeInRange((int)((hz * 9) - 2), (int)((hz * 9) + 2), "one fade a cycle of the rotating phase");

        // The mean power through the path is unchanged: the mean of the squared amplitude over whole cycles is 0.1^2.
        plateau.Average(v => v * v).Should().BeApproximately(0.01, 0.0006);
    }

    [Fact]
    public void A_Half_Strength_Reflection_Fades_About_9_5_dB_And_A_0_9_Reflection_About_26_dB()
    {
        double d5 = 20 * Math.Log10(1.5 / 0.5);
        double d9 = 20 * Math.Log10(1.9 / 0.1);
        d5.Should().BeApproximately(9.5, 0.1);
        d9.Should().BeApproximately(25.6, 0.1);
        var m5 = new FlutterModel(0.5, 3, false, 0, 100);
        var m9 = new FlutterModel(0.9, 3, false, 0, 100);
        double[] g5 = [.. Enumerable.Range(0, 48000 * 4).Select(i => m5.GainAt(10 * 48000L + i))];
        double[] g9 = [.. Enumerable.Range(0, 48000 * 4).Select(i => m9.GainAt(10 * 48000L + i))];
        (20 * Math.Log10(g5.Max() / g5.Min())).Should().BeApproximately(d5, 0.05);
        (20 * Math.Log10(g9.Max() / g9.Min())).Should().BeApproximately(d9, 0.1);
    }

    [Fact]
    public void A_Swept_Flutter_Slows_Through_Zero_And_Speeds_Up_Again_As_A_Crossing_Does()
    {
        // 10 Hz at the start, falling linearly to 0 half way and back up to 10 Hz over 20 s: 10 * 20 / 4 = 50 cycles in each half.
        double[] e = ToneEnvelope(new FlutterModel(0.9, 10, true, 2, 20), 20);
        int whole = Minima(e, 2, 22);
        whole.Should().BeInRange(94, 106, "fifty fades going down in rate and fifty coming back up");
        Minima(e, 2, 4).Should().BeGreaterThan(Minima(e, 11, 13) + 8, "fast at the start, near zero in the middle");
        Minima(e, 20, 22).Should().BeGreaterThan(Minima(e, 11, 13) + 8);
    }

    [Fact]
    public void Outside_The_Episode_The_Path_Is_Plain()
    {
        var m = new FlutterModel(0.9, 5, false, 10, 5);
        m.GainAt(0).Should().Be(1.0);
        m.GainAt(9 * 48000L).Should().Be(1.0);
        m.GainAt(15 * 48000L + 1).Should().Be(1.0);
        m.GainAt(10 * 48000L + 1).Should().BeApproximately(1.0, 1e-3, "it comes up smoothly");
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Audio_At_The_Receiver_Whatever_The_Block_Size_With_Impulses_And_Flutter_Together()
    {
        List<float> Heard(int block, int seed)
        {
            var rig = new SimRig(2, block, seed);
            rig.Nodes[1].Impulses = new ImpulseModel(30, 0, 0.2);
            rig.Nodes[0].Impulses = new ImpulseModel(10, -6, 0.0);
            rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Flutter = new FlutterModel(0.7, 7, true, 1, 6);
            rig.Nodes[1].RecordInput = true;
            var tone = new float[48000 * 8];
            for (int i = 0; i < tone.Length; i++)
            {
                tone[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 1500 * i / 48000.0));
            }

            rig.Channel.Interfere(rig.Nodes[0], tone, 24000);
            rig.RunTo(9);
            return rig.Nodes[1].Input;
        }

        List<float> a = Heard(4800, 5);
        a.Count.Should().Be(9 * 48000);
        Heard(1000, 5).Should().Equal(a);
        Heard(7777, 5).Should().Equal(a);
        Heard(4800, 6).Should().NotEqual(a);
    }

    // ---- a steady path change, and the ground truth ----

    [Fact]
    public void A_Step_In_A_Path_Changes_The_Gain_Of_The_Bursts_That_Go_After_It_Up_Or_Down_And_Is_Recorded()
    {
        var rig = new SimRig(2, noise: 0.0);
        rig.Channel.RecordDeliveries = true;
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        var tone = new float[4800];
        Array.Fill(tone, 0.1f);
        for (int k = 0; k < 4; k++)
        {
            rig.Channel.Interfere(a, tone, 48000L * (1 + (k * 10)));
        }

        rig.Channel.StepPath(a, b, 48000L * 15, -12);
        rig.Channel.StepPath(a, b, 48000L * 25, +12);
        rig.RunTo(45);
        double[] db = [.. b.Deliveries.Select(d => 20 * Math.Log10(d.Gain))];
        db.Length.Should().Be(4);
        db[0].Should().BeApproximately(0.0, 1e-6);
        db[1].Should().BeApproximately(0.0, 1e-6);
        db[2].Should().BeApproximately(-12.0, 1e-6);
        db[3].Should().BeApproximately(0.0, 1e-6);
        b.Deliveries.Should().OnlyContain(d => d.FlutterMin == 1.0 && d.FlutterMean == 1.0, "no flutter was asked for");
        a.Deliveries.Should().BeEmpty("nothing went the other way");
    }

    [Fact]
    public void Nothing_Is_Recorded_And_Nothing_Is_Added_By_Default()
    {
        var rig = new SimRig(2);
        var tone = new float[4800];
        Array.Fill(tone, 0.1f);
        rig.Channel.Interfere(rig.Nodes[0], tone, 48000);
        rig.Nodes[1].RecordInput = true;
        rig.RunTo(3);
        rig.Nodes[1].Deliveries.Should().BeEmpty();
        rig.Nodes[1].ImpulseStream.Should().BeNull();
        rig.Nodes[1].ImpulsePulses.Should().BeEmpty();
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Flutter.Should().BeNull();
    }

    [Fact]
    public void The_Reference_Level_Is_The_Level_Of_A_Burst_At_Gain_1()
    {
        foreach (int format in new[] { 192, 140, 100, 12, 0 })
        {
            TestBurst b = TestBurst.Data(2, 1, format, [TestBurst.Datagram(1, 100)]);
            double sum = 0;
            for (int i = b.Lead; i < b.Audio.Length; i++)
            {
                sum += (double)b.Audio[i] * b.Audio[i];
            }

            Math.Sqrt(sum / (b.Audio.Length - b.Lead)).Should().BeApproximately(SimChannel.ReferenceRms, SimChannel.ReferenceRms * 0.03, $"format {format}");
        }
    }
}
