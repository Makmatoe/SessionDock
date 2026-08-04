using System.IO;
using SessionDock.ExactWheel;

namespace SessionDock.Services;

/// <summary>
/// Retains exact process/window playback proofs for one macro run. Successful
/// leases are reused across loop iterations. Failed acquisitions are not
/// cached, and a retained lease with a sticky failure is evicted before the
/// next acquisition so a transient target can recover on a later retry.
/// </summary>
internal sealed class SessionMacroPlaybackLeaseCache : IDisposable
{
    private readonly Dictionary<ExactTargetKey, RobloxPlaybackTargetLease>
        _singleTargetLeases = new(ExactTargetKeyComparer.Instance);
    private readonly Dictionary<ExactTargetKey, string> _windowClasses =
        new(ExactTargetKeyComparer.Instance);
    private readonly List<TargetSetLease> _targetSetLeases = [];
    private readonly Func<nint, string> _captureWindowClass;
    private readonly RobloxExecutableTrustContext _trustContext;
    private bool _disposed;

    internal SessionMacroPlaybackLeaseCache()
        : this(windowHandle =>
            ExactWheelDesktopCapture.CapturePlaybackWindowClass(
                windowHandle,
                requireForeground: true),
            new RobloxExecutableTrustContext())
    {
    }

    internal SessionMacroPlaybackLeaseCache(
        Func<nint, string> captureWindowClass)
        : this(captureWindowClass, new RobloxExecutableTrustContext())
    {
    }

    internal SessionMacroPlaybackLeaseCache(
        Func<nint, string> captureWindowClass,
        RobloxExecutableTrustContext trustContext)
    {
        _captureWindowClass = captureWindowClass ??
            throw new ArgumentNullException(nameof(captureWindowClass));
        _trustContext = trustContext ??
            throw new ArgumentNullException(nameof(trustContext));
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
        {
            if (cached.Failure is null)
            {
                return RobloxPlaybackTargetLeaseAcquisitionResult.Succeeded(
                    cached);
            }

            _singleTargetLeases.Remove(key);
            _windowClasses.Remove(key);
            cached.Dispose();
        }

        var acquired = windowService.AcquirePlaybackTargetLease(
            window.Identity,
            window.Handle,
            trustContext: _trustContext);
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

        for (var index = 0; index < _targetSetLeases.Count; index++)
        {
            var entry = _targetSetLeases[index];
            if (entry.Matches(windows))
            {
                if (entry.Lease.Failure is null)
                {
                    return RobloxPlaybackTargetLeaseAcquisitionResult
                        .Succeeded(entry.Lease);
                }

                _targetSetLeases.RemoveAt(index);
                entry.RemoveCachedWindowClasses(_windowClasses);
                entry.Lease.Dispose();
                break;
            }
        }

        var targets = new RobloxPlaybackTarget[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            targets[index] = new RobloxPlaybackTarget(
                window.Identity,
                window.Handle);
        }

        var acquired = windowService.AcquirePlaybackTargetLease(
            targets,
            trustContext: _trustContext);
        if (acquired.Success && acquired.Lease is { } lease)
        {
            _targetSetLeases.Add(new TargetSetLease(
                windows,
                targets,
                lease));
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
        _trustContext.Dispose();
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
        private readonly IReadOnlyList<RobloxSessionLayoutWindow>
            _sourceWindows;
        private readonly Dictionary<nint, RobloxClientProcessIdentity>
            _identitiesByHandle;

        internal TargetSetLease(
            IReadOnlyList<RobloxSessionLayoutWindow> sourceWindows,
            IReadOnlyList<RobloxPlaybackTarget> targets,
            RobloxPlaybackTargetLease lease)
        {
            _sourceWindows = sourceWindows;
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

        internal void RemoveCachedWindowClasses(
            IDictionary<ExactTargetKey, string> windowClasses)
        {
            foreach (var (handle, identity) in _identitiesByHandle)
            {
                windowClasses.Remove(new ExactTargetKey(handle, identity));
            }
        }

        internal bool Matches(
            IReadOnlyList<RobloxSessionLayoutWindow> windows)
        {
            // RuntimeMacroPlan owns one immutable window snapshot for the
            // complete run. Its repeated whole-layout lookup can therefore
            // bypass an otherwise linear set comparison safely.
            if (ReferenceEquals(_sourceWindows, windows))
                return true;
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
