using System.Diagnostics;

namespace SessionDock.Services;

internal sealed record RobloxServerObservation(
    DateTimeOffset Timestamp,
    long PlaceId,
    long UserId,
    string ServerJobId);

internal sealed class RobloxServerTrackingSnapshot
{
    internal static RobloxServerTrackingSnapshot Empty { get; } =
        new([]);

    private readonly Dictionary<RobloxServerObservationKey,
        RobloxServerObservation> _latestByUserAndPlace;

    internal RobloxServerTrackingSnapshot(
        IReadOnlyList<RobloxServerObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        _latestByUserAndPlace = new Dictionary<
            RobloxServerObservationKey,
            RobloxServerObservation>(observations.Count);
        foreach (var observation in observations)
        {
            var key = new RobloxServerObservationKey(
                observation.UserId,
                observation.PlaceId);
            if (!_latestByUserAndPlace.TryGetValue(key, out var existing) ||
                observation.Timestamp >= existing.Timestamp)
            {
                _latestByUserAndPlace[key] = observation;
            }
        }
    }

    internal string? FindJoinedServer(
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset earliestTimestamp) =>
        _latestByUserAndPlace.TryGetValue(
            new RobloxServerObservationKey(
                expectedUserId,
                expectedPlaceId),
            out var observation) &&
        observation.Timestamp >= earliestTimestamp
            ? observation.ServerJobId
            : null;

    private readonly record struct RobloxServerObservationKey(
        long UserId,
        long PlaceId);
}

/// <summary>
/// Coalesces the optional log scan used by every client in a batch. One
/// tracker instance serves the whole app, so callers arriving during a scan
/// share that work and callers within one polling interval reuse its result.
/// </summary>
internal sealed class RobloxServerTrackingScanCoordinator
{
    internal static TimeSpan SnapshotReuseDuration { get; } =
        TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly Func<RobloxServerTrackingSnapshot> _capture;
    private readonly Func<long> _timestampProvider;
    private readonly Func<long, TimeSpan> _getElapsedTime;
    private Task<RobloxServerTrackingSnapshot>? _activeScan;
    private RobloxServerTrackingSnapshot? _cachedSnapshot;
    private long _cachedAtTimestamp;

    internal RobloxServerTrackingScanCoordinator(
        Func<RobloxServerTrackingSnapshot> capture)
        : this(
            capture,
            Stopwatch.GetTimestamp,
            Stopwatch.GetElapsedTime)
    {
    }

    internal RobloxServerTrackingScanCoordinator(
        Func<RobloxServerTrackingSnapshot> capture,
        Func<long> timestampProvider,
        Func<long, TimeSpan> getElapsedTime)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _timestampProvider = timestampProvider ??
            throw new ArgumentNullException(nameof(timestampProvider));
        _getElapsedTime = getElapsedTime ??
            throw new ArgumentNullException(nameof(getElapsedTime));
    }

    internal async Task<RobloxServerTrackingSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        Task<RobloxServerTrackingSnapshot> scan;
        lock (_sync)
        {
            if (_cachedSnapshot is not null &&
                _getElapsedTime(_cachedAtTimestamp) < SnapshotReuseDuration)
            {
                return _cachedSnapshot;
            }

            scan = _activeScan ??= Task.Run(_capture);
        }

        RobloxServerTrackingSnapshot snapshot;
        try
        {
            snapshot = await scan.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // One caller does not own or cancel the app-wide shared scan.
            throw;
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeScan, scan) && scan.IsCompleted)
                    _activeScan = null;
            }
            throw;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_activeScan, scan))
            {
                _cachedSnapshot = snapshot;
                _cachedAtTimestamp = _timestampProvider();
                _activeScan = null;
            }
            return _cachedSnapshot ?? snapshot;
        }
    }
}
