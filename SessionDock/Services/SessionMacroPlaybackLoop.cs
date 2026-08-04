namespace SessionDock.Services;

internal static class SessionMacroPlaybackLoop
{
    // ExactWheel uses the same floor for a repeated zero-duration timeline.
    // Keep the SessionDock-level cycle bounded too: a cycle can contain only
    // assignments that became unavailable after readiness was checked.
    internal static TimeSpan MinimumInterCycleDelay { get; } =
        TimeSpan.FromMilliseconds(10);

    internal static async Task RunUntilStoppedAsync(
        Func<CancellationToken, Task<bool>> playCycleAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playCycleAsync);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await playCycleAsync(cancellationToken))
                return;

            // This delay is part of the cycle boundary rather than individual
            // recordings, so every assignment still keeps its recorded
            // per-pass timing and selected playback rate.
            await Task.Delay(
                MinimumInterCycleDelay,
                cancellationToken);
        }
    }
}
