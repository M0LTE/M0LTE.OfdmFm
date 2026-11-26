using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class BlockTrackerTests
{
    [Fact]
    public void The_Times_And_Delays_Of_Each_Vector_Match_The_Prototype_To_A_Picosecond()
    {
        var found = TrackedVectors.Cases.Value.Where(c => c.Tracker != null).ToArray();
        found.Length.Should().Be(3);
        double largest = 0;
        foreach (TrackedCase c in found)
        {
            TrackerVector v = c.Tracker!;
            var tracker = new BlockTracker(v.Reference.Length);
            var times = new double[tracker.MaxBlocks];
            var delays = new double[tracker.MaxBlocks];
            int m = tracker.Track(v.Capture, v.Reference, times, delays);
            m.Should().Be(v.Times.Length, c.Name);
            for (int i = 0; i < m; i++)
            {
                double t = Math.Abs(times[i] - v.Times[i]);
                double d = Math.Abs(delays[i] - v.Delays[i]);
                largest = Math.Max(largest, Math.Max(t, d));
                t.Should().BeLessThan(1e-12, $"{c.Name} block {i} time");
                d.Should().BeLessThan(1e-12, $"{c.Name} block {i} delay");
            }
        }

        largest.Should().BeLessThan(1e-12);
    }

    [Fact]
    public void A_Known_Delay_Is_Found_Relative_To_The_Mean()
    {
        // a burst-like reference (band-limited noise), the capture the same read late from a source read later by a ramp of 0 to 20 microseconds
        var rng = new Random(5);
        int n = 20000;
        var reference = new double[n];
        var samples = new float[n];
        var raw = new float[n + 200];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (float)(rng.NextDouble() - 0.5);
        }

        for (int i = 0; i < n; i++)
        {
            reference[i] = i < 1000 ? 0 : raw[i + 100];
        }

        var knots = new[] { 0.0, n / (double)BurstRenderer.SampleRate };
        var delays = new[] { 0.0, 20e-6 };
        var shifted = new double[n];
        // the capture reads the reference's source later by the ramp
        DelayResampler.Resample(raw, 100, n, knots, delays, 0, shifted);
        for (int i = 0; i < n; i++)
        {
            samples[i] = i < 1000 ? 0 : (float)shifted[i];
        }

        var tracker = new BlockTracker(n);
        var times = new double[tracker.MaxBlocks];
        var tau = new double[tracker.MaxBlocks];
        int m = tracker.Track(samples, reference, times, tau);
        m.Should().BeGreaterThan(40);
        // reading the source later makes the signal arrive earlier, so the delay falls by about the ramp's span
        tau[0].Should().BeGreaterThan(tau[m - 1] + 8e-6);
        tau.AsSpan(0, m).ToArray().Average().Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void A_Reference_With_No_Room_For_A_Block_Gives_Nothing()
    {
        var tracker = new BlockTracker(4096);
        var times = new double[tracker.MaxBlocks];
        var delays = new double[tracker.MaxBlocks];
        tracker.Track(new float[500], new double[500], times, delays).Should().Be(0);
    }

    [Fact]
    public void Skipping_Delays_That_Cannot_Win_Gives_Exactly_The_Exhaustive_Search_Result()
    {
        var cases = new List<(string Name, float[] Capture, double[] Reference)>();
        foreach (TrackedCase c in TrackedVectors.Cases.Value.Where(c => c.Tracker != null))
        {
            cases.Add((c.Name, c.Tracker!.Capture, c.Tracker.Reference));
        }

        // band-limited noise as a reference; the capture the reference read late by a ramp, at several noise levels, then pure noise, silence, and a capture with a not-a-number in it
        var rng = new Random(11);
        int n = 24000;
        var raw = new float[n + 200];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (float)(rng.NextDouble() - 0.5);
        }

        var reference = new double[n];
        for (int i = 0; i < n; i++)
        {
            reference[i] = i < 1000 ? 0 : raw[i + 100];
        }

        var shifted = new double[n];
        DelayResampler.Resample(raw, 100, n, [0.0, n / (double)BurstRenderer.SampleRate], [0.0, 400e-6], 0, shifted);
        foreach (double noise in new[] { 0.0, 0.01, 0.1, 0.5, 2.0, 20.0 })
        {
            var capture = new float[n];
            for (int i = 0; i < n; i++)
            {
                capture[i] = i < 1000 ? 0 : (float)(shifted[i] + (noise * (rng.NextDouble() - 0.5)));
            }

            cases.Add(($"ramp and noise {noise}", capture, reference));
        }

        var unrelated = new float[n];
        for (int i = 0; i < n; i++)
        {
            unrelated[i] = (float)(rng.NextDouble() - 0.5);
        }

        cases.Add(("unrelated noise", unrelated, reference));
        cases.Add(("silence", new float[n], reference));
        var broken = (float[])unrelated.Clone();
        broken[12000] = float.NaN;
        cases.Add(("a not-a-number", broken, reference));

        foreach ((string name, float[] capture, double[] reference2) in cases)
        {
            var times = new double[(reference2.Length / BlockTracker.Hop) + 1];
            var skipping = new BlockTracker(reference2.Length);
            var exhaustive = new BlockTracker(reference2.Length) { ExhaustiveGridSearch = true };
            var a = new double[times.Length];
            var b = new double[times.Length];
            int ma = skipping.Track(capture, reference2, times, a);
            int mb = exhaustive.Track(capture, reference2, times, b);
            ma.Should().Be(mb, name);
            for (int i = 0; i < ma; i++)
            {
                BitConverter.DoubleToInt64Bits(a[i]).Should().Be(BitConverter.DoubleToInt64Bits(b[i]), $"{name} block {i}");
            }

            skipping.ChunksSearched.Should().BeLessThanOrEqualTo(skipping.ChunksPossible, name);
        }
    }

    [Fact]
    public void Cosine_Is_Even_And_Sine_Odd_To_The_Last_Bit()
    {
        // the tracker takes the next round's rotation from the gain step's, relying on this of the runtime's functions
        var rng = new Random(3);
        for (int i = 0; i < 200000; i++)
        {
            double x = (rng.NextDouble() - 0.5) * (i % 3 == 0 ? 2000 : 40);
            BitConverter.DoubleToInt64Bits(Math.Cos(-x)).Should().Be(BitConverter.DoubleToInt64Bits(Math.Cos(x)));
            BitConverter.DoubleToInt64Bits(-Math.Sin(-x)).Should().Be(BitConverter.DoubleToInt64Bits(Math.Sin(x)));
        }
    }
}
