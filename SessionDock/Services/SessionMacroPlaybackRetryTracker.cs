using System.Diagnostics;

namespace SessionDock.Services;

/// <summary>
/// Prevents one unavailable client from imposing its full focus/trust cost on
/// every short macro cycle while healthy clients continue. State belongs to
/// one immutable runtime plan and is discarded when that context changes.
/// </summary>
internal sealed class SessionMacroPlaybackRetryTracker
{
    internal static TimeSpan InitialDelay { get; } =
        TimeSpan.FromMilliseconds(250);

    internal static TimeSpan MaximumDelay { get; } =
        TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, RetryState> _failures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _timestampProvider;
    private readonly long _frequency;

    internal SessionMacroPlaybackRetryTracker(
        Func<long>? timestampProvider = null,
        long? timestampFrequency = null)
    {
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        _frequency = timestampFrequency ?? Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_frequency);
    }

    internal bool CanAttempt(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        return !_failures.TryGetValue(targetKey, out var state) ||
            _timestampProvider() >= state.RetryAtTimestamp;
    }

    internal void ReportFailure(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        var failureCount = _failures.TryGetValue(targetKey, out var previous)
            ? Math.Min(previous.FailureCount + 1, 31)
            : 1;
        var multiplier = 1L << Math.Min(failureCount - 1, 20);
        var delayTicks = Math.Min(
            ToTimestampTicks(MaximumDelay),
            checked(ToTimestampTicks(InitialDelay) * multiplier));
        _failures[targetKey] = new RetryState(
            failureCount,
            checked(_timestampProvider() + delayTicks));
    }

    internal void ReportSuccess(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        _failures.Remove(targetKey);
    }

    private long ToTimestampTicks(TimeSpan duration) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                duration.TotalSeconds * _frequency)));

    private readonly record struct RetryState(
        int FailureCount,
        long RetryAtTimestamp);
}
