using System.Diagnostics;

namespace SessionDock.Services;

internal static class SessionMacroPlaybackLoop
{
    // ExactWheel uses the same floor for a repeated zero-duration timeline.
    // Keep the SessionDock-level cycle bounded too: a cycle can contain only
    // assignments that became unavailable after readiness was checked.
    internal static TimeSpan MinimumCycleDuration { get; } =
        TimeSpan.FromMilliseconds(10);

    internal static async Task RunUntilStoppedAsync(
        Func<CancellationToken, Task<bool>> playCycleAsync,
        CancellationToken cancellationToken)
    {
        await RunUntilStoppedAsync(
            playCycleAsync,
            Stopwatch.GetTimestamp,
            Stopwatch.GetElapsedTime,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);
    }

    internal static async Task RunUntilStoppedAsync(
        Func<CancellationToken, Task<bool>> playCycleAsync,
        Func<long> getTimestamp,
        Func<long, TimeSpan> getElapsedTime,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playCycleAsync);
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentNullException.ThrowIfNull(getElapsedTime);
        ArgumentNullException.ThrowIfNull(delayAsync);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycleStarted = getTimestamp();
            if (!await playCycleAsync(cancellationToken))
                return;

            // Enforce a minimum complete-cycle duration instead of adding a
            // fixed delay after real playback. Empty or failed-assignment
            // cycles remain bounded without slowing healthy short macros.
            var remaining = MinimumCycleDuration -
                getElapsedTime(cycleStarted);
            if (remaining > TimeSpan.Zero)
                await delayAsync(remaining, cancellationToken);
        }
    }
}
