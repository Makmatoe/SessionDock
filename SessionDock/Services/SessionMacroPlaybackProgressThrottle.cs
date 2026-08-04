using System.Diagnostics;

namespace SessionDock.Services;

/// <summary>
/// Bounds transient playback presentation work. A short looping macro can
/// visit many clients hundreds of times without forcing WPF layout and UI
/// Automation updates for every focus transition.
/// </summary>
internal sealed class SessionMacroPlaybackProgressThrottle
{
    internal static TimeSpan DefaultInterval { get; } =
        TimeSpan.FromMilliseconds(250);

    private readonly Func<long> _timestampProvider;
    private readonly long _minimumIntervalTicks;
    private long _lastReportTimestamp;
    private bool _hasReported;

    internal SessionMacroPlaybackProgressThrottle(
        TimeSpan? interval = null,
        Func<long>? timestampProvider = null,
        long? timestampFrequency = null)
    {
        var effectiveInterval = interval ?? DefaultInterval;
        if (effectiveInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        var frequency = timestampFrequency ?? Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        _minimumIntervalTicks = Math.Max(
            1,
            checked((long)Math.Ceiling(
                effectiveInterval.TotalSeconds * frequency)));
    }

    internal bool TryAcquire()
    {
        var now = _timestampProvider();
        if (_hasReported &&
            now >= _lastReportTimestamp &&
            now - _lastReportTimestamp < _minimumIntervalTicks)
        {
            return false;
        }

        _lastReportTimestamp = now;
        _hasReported = true;
        return true;
    }

    internal void Reset()
    {
        _lastReportTimestamp = 0;
        _hasReported = false;
    }
}
