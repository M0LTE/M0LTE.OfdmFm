// The interleaver and the payload code keep small shared caches, and several tests check that a repeat
// call allocates nothing or that the cache is bounded. Another test class using the caches meanwhile evicts
// the entry such a test relies on, so the classes run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
