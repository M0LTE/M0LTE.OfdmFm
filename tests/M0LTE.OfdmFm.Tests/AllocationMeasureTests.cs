using AwesomeAssertions;

namespace M0LTE.OfdmFm.Tests;

public class AllocationMeasureTests
{
    private static object? sink;

    [Fact]
    public void Work_That_Allocates_Every_Time_Reads_Above_Zero()
    {
        Action work = () => sink = new byte[64];
        AllocationMeasure.Minimum(work).Should().BeGreaterThan(0);
        AllocationMeasure.Once(work).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Work_That_Allocates_Only_The_First_Time_Reads_Zero()
    {
        bool built = false;
        Action work = () =>
        {
            if (!built)
            {
                built = true;
                sink = new byte[64];
            }
        };
        AllocationMeasure.Minimum(work).Should().Be(0);
    }

    [Fact]
    public void Allocation_Free_Work_Reads_Zero_Every_Round_While_Other_Threads_Allocate_Hard()
    {
        const int rounds = 3000;
        bool stop = false;
        var noise = new Thread[4];
        for (int t = 0; t < noise.Length; t++)
        {
            int id = t;
            noise[t] = new Thread(() =>
            {
                // Small, large and some long-lived allocations; the list is trimmed so memory stays bounded.
                var keep = new List<object>();
                var rnd = new Random(id);
                long n = 0;
                while (!Volatile.Read(ref stop))
                {
                    object o = n % 500 == 0 ? new float[100_000 + rnd.Next(500_000)] : new byte[200 + rnd.Next(4000)];
                    if (rnd.Next(4) == 0)
                    {
                        keep.Add(o);
                    }

                    if (keep.Count > 4000)
                    {
                        keep.RemoveRange(0, 2000);
                    }

                    n++;
                }
            });
            noise[t].Start();
        }

        int rawNonZero = 0;
        try
        {
            double[] data = new double[4096];
            double s = 0;
            Action work = () =>
            {
                for (int k = 0; k < 4; k++)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = Math.Sin(i + s);
                        s += data[i];
                    }
                }
            };

            for (int round = 0; round < rounds; round++)
            {
                if (AllocationMeasure.Once(work) != 0)
                {
                    rawNonZero++;
                }

                AllocationMeasure.Minimum(work).Should().Be(0, $"round {round}");
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (Thread t in noise)
            {
                t.Join();
            }
        }

        Console.WriteLine($"raw single readings that were not zero: {rawNonZero} of {rounds}");
    }
}
