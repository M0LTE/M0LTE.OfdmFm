namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Measures how many bytes a piece of work allocates on the calling thread, soundly enough to assert "allocates nothing".
/// A single before-and-after reading of <see cref="GC.GetAllocatedBytesForCurrentThread"/> is not sound: when other threads
/// are busy the runtime can take back a thread's part-used allocation buffer and the thread's counter rises by the unused
/// part (never more than 8 KB), though it allocated nothing. Work that really allocates does so on every attempt; a
/// phantom rise comes from elsewhere and is rare. So the work is run up to <see cref="Attempts"/> times and the smallest
/// difference returned: it is zero exactly when the work can run without allocating. The work must be equivalent each time
/// it runs, and the delegate must be created before the call (invoking one does not allocate).
/// </summary>
internal static class AllocationMeasure
{
    /// <summary>The most times the work is run to get one clean reading.</summary>
    public const int Attempts = 5;

    /// <summary>The smallest number of bytes the work allocated over at most <see cref="Attempts"/> runs; stops at the first run that allocated nothing.</summary>
    public static long Minimum(Action work)
    {
        long smallest = long.MaxValue;
        for (int attempt = 0; attempt < Attempts && smallest != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            work();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < smallest)
            {
                smallest = allocated;
            }
        }

        return smallest;
    }

    /// <summary>One raw reading, for work whose first use is expected to allocate (a phantom rise can only add to that).</summary>
    public static long Once(Action work)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
