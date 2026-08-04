namespace SessionDock.Tests;

internal static class AllocationMeasurement
{
    internal static long MinimumAllocatedBytes(
        Action action,
        int attempts = 3)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (attempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempts));

        // Tiered JIT/PGO can publish one-time runtime bookkeeping on the test
        // thread after an ordinary warmup loop. Multiple complete samples
        // still fail for a real steady-state allocation while filtering that
        // unrelated one-time test-host noise.
        action();
        var minimum = long.MaxValue;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            minimum = Math.Min(
                minimum,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return minimum;
    }
}
