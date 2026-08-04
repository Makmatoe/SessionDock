using System.Globalization;
using System.Text;

namespace SessionDock.Services;

/// <summary>
/// Retains exact process/window playback proofs for one macro run. Successful
/// leases are reused across loop iterations; failed acquisitions are not
/// cached, so a client that is still starting can recover on the next pass.
/// </summary>
internal sealed class SessionMacroPlaybackLeaseCache : IDisposable
{
    private readonly Dictionary<string, RobloxPlaybackTargetLease> _leases =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal RobloxPlaybackTargetLeaseAcquisitionResult GetOrAcquire(
        RobloxWindowService windowService,
        RobloxSessionLayoutWindow window) =>
        GetOrAcquire(
            windowService,
            [new RobloxPlaybackTarget(window.Identity, window.Handle)]);

    internal RobloxPlaybackTargetLeaseAcquisitionResult GetOrAcquire(
        RobloxWindowService windowService,
        IReadOnlyList<RobloxSessionLayoutWindow> windows) =>
        GetOrAcquire(
            windowService,
            windows.Select(window => new RobloxPlaybackTarget(
                window.Identity,
                window.Handle)).ToArray());

    internal int Count => _leases.Count;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lease in _leases.Values)
            lease.Dispose();
        _leases.Clear();
    }

    private RobloxPlaybackTargetLeaseAcquisitionResult GetOrAcquire(
        RobloxWindowService windowService,
        IReadOnlyList<RobloxPlaybackTarget> targets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(windowService);
        ArgumentNullException.ThrowIfNull(targets);
        var key = CreateTargetSetIdentity(targets);
        if (_leases.TryGetValue(key, out var cached))
        {
            return cached.Failure is { } failure
                ? RobloxPlaybackTargetLeaseAcquisitionResult.Failed(failure)
                : RobloxPlaybackTargetLeaseAcquisitionResult.Succeeded(cached);
        }

        var acquired = windowService.AcquirePlaybackTargetLease(targets);
        if (acquired.Success && acquired.Lease is { } lease)
            _leases.Add(key, lease);
        return acquired;
    }

    private static string CreateTargetSetIdentity(
        IReadOnlyList<RobloxPlaybackTarget> targets)
    {
        var identity = new StringBuilder(targets.Count * 128);
        foreach (var target in targets
                     .OrderBy(target => target.Handle.ToInt64())
                     .ThenBy(target => target.Identity.ProcessId))
        {
            Append(identity, target.Handle.ToInt64());
            Append(identity, target.Identity.ProcessId);
            Append(identity, target.Identity.StartTimeUtc.Ticks);
            var path = target.Identity.ExecutablePath;
            Append(identity, path.Length);
            identity.Append(path).Append('\u001e');
        }
        return identity.ToString();

        static void Append(StringBuilder builder, long value) =>
            builder.Append(value.ToString(CultureInfo.InvariantCulture))
                .Append('\u001f');
    }
}
