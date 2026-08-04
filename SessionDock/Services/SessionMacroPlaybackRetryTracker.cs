using System.Diagnostics;

namespace SessionDock.Services;

internal enum SessionMacroPlaybackRetryDisposition
{
    Transient,
    Terminal
}

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

    // Retry a bounded cohort at once. Large batches otherwise make every
    // unavailable client repeat the same focus and lease work in one burst.
    internal static int RetryWaveSize { get; } = 8;

    internal static TimeSpan RetryWaveSpacing { get; } =
        TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<string, RetryState> _failures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, int> _retryWaveOccupancies = [];
    private readonly Func<long> _timestampProvider;
    private readonly long _frequency;
    private long _admissionWindowStarted = long.MinValue;
    private int _admissionsInWindow;

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
        if (!_failures.TryGetValue(targetKey, out var state))
            return true;
        if (state.IsTerminal || state.AdmissionPending)
            return false;

        var now = _timestampProvider();
        if (now < state.RetryAtTimestamp || !TryReserveAdmission(now))
            return false;

        // A granted retry must be completed with ReportFailure or
        // ReportSuccess before it can be admitted again. Besides preventing
        // accidental duplicate work, this lets a late outer loop release at
        // most one bounded cohort instead of treating every overdue wave as
        // immediately runnable.
        _failures[targetKey] = state with { AdmissionPending = true };
        return true;
    }

    internal void ReportFailure(string targetKey)
    {
        ReportFailure(
            targetKey,
            SessionMacroPlaybackRetryDisposition.Transient);
    }

    internal void ReportFailure(
        string targetKey,
        SessionMacroPlaybackRetryDisposition disposition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        var hasPrevious = _failures.TryGetValue(targetKey, out var previous);
        var failureCount = hasPrevious
            ? Math.Min(previous.FailureCount + 1, 31)
            : 1;
        if (hasPrevious)
            RemoveRetryWaveReservation(previous);
        if (disposition == SessionMacroPlaybackRetryDisposition.Terminal)
        {
            _failures[targetKey] = new RetryState(
                failureCount,
                RetryAtTimestamp: long.MaxValue,
                IsTerminal: true,
                AdmissionPending: false);
            return;
        }

        var multiplier = 1L << Math.Min(failureCount - 1, 20);
        var delayTicks = Math.Min(
            ToTimestampTicks(MaximumDelay),
            checked(ToTimestampTicks(InitialDelay) * multiplier));
        var now = _timestampProvider();
        var retryAtTimestamp = AddTimestampTicks(
            now,
            delayTicks);
        retryAtTimestamp = PlaceInRetryWave(retryAtTimestamp);
        _failures[targetKey] = new RetryState(
            failureCount,
            retryAtTimestamp,
            IsTerminal: false,
            AdmissionPending: false);
    }

    internal void ReportSuccess(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        if (_failures.Remove(targetKey, out var previous))
            RemoveRetryWaveReservation(previous);
    }

    /// <summary>
    /// Returns the remaining delay until at least one transient target can be
    /// retried. A null result means every tracked failure is terminal (or the
    /// tracker is empty), so waiting cannot make progress.
    /// </summary>
    internal TimeSpan? GetDelayUntilNextAttempt()
    {
        var now = _timestampProvider();
        var earliest = long.MaxValue;
        var foundTransientFailure = false;
        foreach (var state in _failures.Values)
        {
            if (state.IsTerminal || state.AdmissionPending)
                continue;
            foundTransientFailure = true;
            earliest = Math.Min(earliest, state.RetryAtTimestamp);
        }

        if (!foundTransientFailure)
            return null;
        if (earliest <= now)
        {
            var admissionDelay = GetAdmissionDelay(now);
            return admissionDelay > TimeSpan.Zero
                ? admissionDelay
                : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(
            (double)(earliest - now) / _frequency);
    }

    private bool TryReserveAdmission(long now)
    {
        var spacingTicks = ToTimestampTicks(RetryWaveSpacing);
        if (_admissionWindowStarted == long.MinValue ||
            now < _admissionWindowStarted ||
            now >= AddTimestampTicks(
                _admissionWindowStarted,
                spacingTicks))
        {
            _admissionWindowStarted = now;
            _admissionsInWindow = 0;
        }

        if (_admissionsInWindow >= RetryWaveSize)
            return false;
        _admissionsInWindow++;
        return true;
    }

    private TimeSpan GetAdmissionDelay(long now)
    {
        if (_admissionWindowStarted == long.MinValue ||
            _admissionsInWindow < RetryWaveSize ||
            now < _admissionWindowStarted)
        {
            return TimeSpan.Zero;
        }

        var nextWindow = AddTimestampTicks(
            _admissionWindowStarted,
            ToTimestampTicks(RetryWaveSpacing));
        if (now >= nextWindow)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(
            (double)(nextWindow - now) / _frequency);
    }

    private long PlaceInRetryWave(long requestedTimestamp)
    {
        var spacingTicks = ToTimestampTicks(RetryWaveSpacing);
        var retryWave = RoundUpToWave(
            requestedTimestamp,
            spacingTicks);
        while (_retryWaveOccupancies.TryGetValue(
                   retryWave,
                   out var occupancy) &&
               occupancy >= RetryWaveSize)
        {
            var nextWave = AddTimestampTicks(retryWave, spacingTicks);
            if (nextWave == retryWave)
                break;
            retryWave = nextWave;
        }

        _retryWaveOccupancies.TryGetValue(retryWave, out var reserved);
        _retryWaveOccupancies[retryWave] = reserved + 1;
        return retryWave;
    }

    private void RemoveRetryWaveReservation(RetryState state)
    {
        if (state.IsTerminal ||
            !_retryWaveOccupancies.TryGetValue(
                state.RetryAtTimestamp,
                out var occupancy))
        {
            return;
        }

        if (occupancy <= 1)
            _retryWaveOccupancies.Remove(state.RetryAtTimestamp);
        else
            _retryWaveOccupancies[state.RetryAtTimestamp] = occupancy - 1;
    }

    private static long RoundUpToWave(long timestamp, long spacingTicks)
    {
        if (timestamp == long.MaxValue)
            return timestamp;

        var remainder = timestamp % spacingTicks;
        return remainder == 0
            ? timestamp
            : AddTimestampTicks(timestamp, spacingTicks - remainder);
    }

    private static long AddTimestampTicks(long timestamp, long ticks) =>
        timestamp > long.MaxValue - ticks
            ? long.MaxValue
            : timestamp + ticks;

    private long ToTimestampTicks(TimeSpan duration) =>
        Math.Max(
            1,
            checked((long)Math.Ceiling(
                duration.TotalSeconds * _frequency)));

    private readonly record struct RetryState(
        int FailureCount,
        long RetryAtTimestamp,
        bool IsTerminal,
        bool AdmissionPending);
}
