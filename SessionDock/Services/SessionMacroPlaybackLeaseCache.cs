using System.IO;
using SessionDock.ExactWheel;

namespace SessionDock.Services;

/// <summary>
/// Retains exact process/window playback proofs for one macro run. Successful
/// leases are reused across loop iterations; failed acquisitions are not
/// cached, so a client that is still starting can recover on the next pass.
/// </summary>
internal sealed class SessionMacroPlaybackLeaseCache : IDisposable
{
    private readonly Dictionary<ExactTargetKey, RobloxPlaybackTargetLease>
        _singleTargetLeases = new(ExactTargetKeyComparer.Instance);
    private readonly Dictionary<ExactTargetKey, string> _windowClasses =
        new(ExactTargetKeyComparer.Instance);
    private readonly List<TargetSetLease> _targetSetLeases = [];
    private readonly Func<nint, string> _captureWindowClass;
    private bool _disposed;

    internal SessionMacroPlaybackLeaseCache()
        : this(windowHandle =>
            ExactWheelDesktopCapture.CapturePlaybackWindowClass(
                windowHandle,
                requireForeground: true))
    {
    }

    internal SessionMacroPlaybackLeaseCache(
        Func<nint, string> captureWindowClass)
    {
        _captureWindowClass = captureWindowClass ??
            throw new ArgumentNullException(nameof(captureWindowClass));
    }

    internal RobloxPlaybackTargetLeaseAcquisitionResult GetOrAcquire(
        RobloxWindowService windowService,
        RobloxSessionLayoutWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(windowService);
        ArgumentNullException.ThrowIfNull(window);
        var key = new ExactTargetKey(window.Handle, window.Identity);
        if (_singleTargetLeases.TryGetValue(key, out var cached))
            return ResultFor(cached);

        var acquired = windowService.AcquirePlaybackTargetLease(
            window.Identity,
            window.Handle);
        if (acquired.Success && acquired.Lease is { } lease)
            _singleTargetLeases.Add(key, lease);
        return acquired;
    }

    internal RobloxPlaybackTargetLeaseAcquisitionResult GetOrAcquire(
        RobloxWindowService windowService,
        IReadOnlyList<RobloxSessionLayoutWindow> windows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(windowService);
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 1)
            return GetOrAcquire(windowService, windows[0]);

        foreach (var entry in _targetSetLeases)
        {
            if (entry.Matches(windows))
                return ResultFor(entry.Lease);
        }

        var targets = new RobloxPlaybackTarget[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            targets[index] = new RobloxPlaybackTarget(
                window.Identity,
                window.Handle);
        }

        var acquired = windowService.AcquirePlaybackTargetLease(targets);
        if (acquired.Success && acquired.Lease is { } lease)
        {
            _targetSetLeases.Add(new TargetSetLease(targets, lease));
        }
        return acquired;
    }

    internal int Count =>
        _singleTargetLeases.Count + _targetSetLeases.Count;

    internal string GetOrCaptureWindowClass(
        RobloxSessionLayoutWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        var key = new ExactTargetKey(window.Handle, window.Identity);
        if (!ContainsRetainedTarget(key))
        {
            throw new InvalidOperationException(
                "An exact retained playback lease is required before the window class can be cached.");
        }
        if (_windowClasses.TryGetValue(key, out var cached))
            return cached;

        var captured = _captureWindowClass(window.Handle);
        if (string.IsNullOrWhiteSpace(captured) ||
            captured.Length > ExactWheelLimits.MaximumWindowClassUtf16Units)
        {
            throw new InvalidDataException(
                "The retained playback window returned an invalid class.");
        }
        _windowClasses.Add(key, captured);
        return captured;
    }

    internal int CachedWindowClassCount => _windowClasses.Count;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lease in _singleTargetLeases.Values)
            lease.Dispose();
        foreach (var entry in _targetSetLeases)
            entry.Lease.Dispose();
        _singleTargetLeases.Clear();
        _windowClasses.Clear();
        _targetSetLeases.Clear();
    }

    private bool ContainsRetainedTarget(ExactTargetKey key)
    {
        if (_singleTargetLeases.TryGetValue(key, out var singleLease) &&
            singleLease.Failure is null)
        {
            return true;
        }
        foreach (var entry in _targetSetLeases)
        {
            if (entry.Lease.Failure is null && entry.Contains(key))
                return true;
        }
        return false;
    }

    private static RobloxPlaybackTargetLeaseAcquisitionResult ResultFor(
        RobloxPlaybackTargetLease cached) =>
        cached.Failure is { } failure
            ? RobloxPlaybackTargetLeaseAcquisitionResult.Failed(failure)
            : RobloxPlaybackTargetLeaseAcquisitionResult.Succeeded(cached);

    private readonly record struct ExactTargetKey(
        nint Handle,
        RobloxClientProcessIdentity Identity);

    private sealed class ExactTargetKeyComparer :
        IEqualityComparer<ExactTargetKey>
    {
        internal static ExactTargetKeyComparer Instance { get; } = new();

        public bool Equals(ExactTargetKey left, ExactTargetKey right) =>
            left.Handle == right.Handle &&
            RobloxClientProcessIdentityComparer.Instance.Equals(
                left.Identity,
                right.Identity);

        public int GetHashCode(ExactTargetKey key) => HashCode.Combine(
            key.Handle,
            RobloxClientProcessIdentityComparer.Instance.GetHashCode(
                key.Identity));
    }

    private sealed class TargetSetLease
    {
        private readonly Dictionary<nint, RobloxClientProcessIdentity>
            _identitiesByHandle;

        internal TargetSetLease(
            IReadOnlyList<RobloxPlaybackTarget> targets,
            RobloxPlaybackTargetLease lease)
        {
            Lease = lease;
            _identitiesByHandle = new Dictionary<
                nint,
                RobloxClientProcessIdentity>(targets.Count);
            foreach (var target in targets)
            {
                _identitiesByHandle.Add(
                    target.Handle,
                    target.Identity);
            }
        }

        internal RobloxPlaybackTargetLease Lease { get; }

        internal bool Contains(ExactTargetKey key) =>
            _identitiesByHandle.TryGetValue(key.Handle, out var identity) &&
            RobloxClientProcessIdentityComparer.Instance.Equals(
                identity,
                key.Identity);

        internal bool Matches(
            IReadOnlyList<RobloxSessionLayoutWindow> windows)
        {
            if (windows.Count != _identitiesByHandle.Count)
                return false;
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                if (!_identitiesByHandle.TryGetValue(
                        window.Handle,
                        out var identity) ||
                    !RobloxClientProcessIdentityComparer.Instance.Equals(
                        identity,
                        window.Identity))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
