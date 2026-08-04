using SessionDock.ExactWheel;

namespace SessionDock.Services;

internal interface IRobloxProcessLifetimePin : IDisposable
{
    RobloxClientProcessIdentity Identity { get; }

    bool IsAlive { get; }

    RobloxProcessVerificationStatus VerifyIdentity(bool forceTrustRefresh);
}

internal sealed record RobloxPlaybackTarget(
    RobloxClientProcessIdentity Identity,
    nint Handle);

internal enum RobloxPlaybackTargetLeaseFailureKind
{
    InvalidTargetSet,
    ConflictingTargetSet,
    ProcessUnavailable,
    ProcessExited,
    IdentityRejected,
    WindowOwnershipChanged,
    WindowUnavailable,
    ForegroundMismatch,
    PointerTargetMismatch,
    Disposed
}

internal sealed record RobloxPlaybackTargetLeaseFailure(
    RobloxPlaybackTargetLeaseFailureKind Kind,
    string Error);

internal sealed record RobloxPlaybackTargetLeaseAcquisitionResult(
    RobloxPlaybackTargetLease? Lease,
    RobloxPlaybackTargetLeaseFailure? Failure)
{
    internal bool Success => Lease is not null && Failure is null;

    internal static RobloxPlaybackTargetLeaseAcquisitionResult Succeeded(
        RobloxPlaybackTargetLease lease) =>
        new(lease, null);

    internal static RobloxPlaybackTargetLeaseAcquisitionResult Failed(
        RobloxPlaybackTargetLeaseFailure failure) =>
        new(null, failure);
}

internal sealed partial class RobloxWindowService
{
    private static readonly TimeSpan DefaultPlaybackFullVerificationInterval =
        TimeSpan.FromSeconds(1);

    internal RobloxPlaybackTargetLeaseAcquisitionResult
        AcquirePlaybackTargetLease(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan? fullVerificationInterval = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return AcquirePlaybackTargetLease(
            [new RobloxPlaybackTarget(identity, windowHandle)],
            fullVerificationInterval);
    }

    internal RobloxPlaybackTargetLeaseAcquisitionResult
        AcquirePlaybackTargetLease(
        IReadOnlyList<RobloxPlaybackTarget> allowedTargets,
        TimeSpan? fullVerificationInterval = null)
    {
        ArgumentNullException.ThrowIfNull(allowedTargets);
        var interval = fullVerificationInterval ??
            DefaultPlaybackFullVerificationInterval;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullVerificationInterval),
                "The playback identity refresh interval must be from one tick through one minute.");
        }

        return RobloxPlaybackTargetLease.Acquire(
            _native,
            allowedTargets,
            interval);
    }
}

internal sealed class RobloxPlaybackTargetLease : IDisposable
{
    private readonly object _sync = new();
    private readonly IRobloxWindowNativeAdapter _native;
    private readonly IReadOnlyDictionary<nint, PinnedTarget> _targetsByHandle;
    private readonly TimeSpan _fullVerificationInterval;
    private DateTimeOffset _lastObservedUtc;
    private DateTimeOffset _nextFullVerificationUtc;
    private RobloxPlaybackTargetLeaseFailure? _failure;
    private bool _pinsReleased;
    private bool _disposed;

    private RobloxPlaybackTargetLease(
        IRobloxWindowNativeAdapter native,
        IReadOnlyList<PinnedTarget> targets,
        TimeSpan fullVerificationInterval)
    {
        _native = native;
        _targetsByHandle = targets.ToDictionary(target => target.Handle);
        _fullVerificationInterval = fullVerificationInterval;
        _lastObservedUtc = native.UtcNow;
        _nextFullVerificationUtc =
            _lastObservedUtc + fullVerificationInterval;
    }

    internal int AllowedTargetCount => _targetsByHandle.Count;

    internal RobloxPlaybackTargetLeaseFailure? Failure
    {
        get
        {
            lock (_sync)
                return _failure;
        }
    }

    internal static RobloxPlaybackTargetLeaseAcquisitionResult Acquire(
        IRobloxWindowNativeAdapter native,
        IReadOnlyList<RobloxPlaybackTarget> allowedTargets,
        TimeSpan fullVerificationInterval)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(allowedTargets);
        var targetValidation = ValidateTargetSet(allowedTargets);
        if (targetValidation is not null)
        {
            return RobloxPlaybackTargetLeaseAcquisitionResult.Failed(
                targetValidation);
        }

        var pinnedTargets = new List<PinnedTarget>(allowedTargets.Count);
        try
        {
            foreach (var target in allowedTargets)
            {
                var verification = native.TryPinProcessLifetime(
                    target.Identity,
                    forceTrustRefresh: true,
                    out var pin);
                if (verification != RobloxProcessVerificationStatus.Verified ||
                    pin is null)
                {
                    pin?.Dispose();
                    return FailAcquisition(
                        pinnedTargets,
                        VerificationFailure(
                            verification,
                            duringAcquisition: true));
                }
                if (!RobloxClientProcessIdentityComparer.Instance.Equals(
                        pin.Identity,
                        target.Identity))
                {
                    pin.Dispose();
                    return FailAcquisition(
                        pinnedTargets,
                        new RobloxPlaybackTargetLeaseFailure(
                            RobloxPlaybackTargetLeaseFailureKind.IdentityRejected,
                            "Windows retained a process that did not match the requested stable Roblox identity."));
                }

                var pinnedTarget = new PinnedTarget(
                    target.Identity,
                    target.Handle,
                    pin);
                pinnedTargets.Add(pinnedTarget);
                if (!pin.IsAlive)
                {
                    return FailAcquisition(
                        pinnedTargets,
                        ProcessExitedFailure());
                }
                if (native.GetWindowProcessId(target.Handle) !=
                    target.Identity.ProcessId)
                {
                    return FailAcquisition(
                        pinnedTargets,
                        WindowOwnershipFailure());
                }
                if (!native.IsUsableTopLevelWindow(target.Handle))
                {
                    return FailAcquisition(
                        pinnedTargets,
                        WindowUnavailableFailure());
                }
                if (!pin.IsAlive)
                {
                    return FailAcquisition(
                        pinnedTargets,
                        ProcessExitedFailure());
                }
            }

            return RobloxPlaybackTargetLeaseAcquisitionResult.Succeeded(
                new RobloxPlaybackTargetLease(
                    native,
                    pinnedTargets,
                    fullVerificationInterval));
        }
        catch (Exception exception) when (IsExpectedNativeFailure(exception))
        {
            return FailAcquisition(
                pinnedTargets,
                new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.ProcessUnavailable,
                    "Windows could not securely retain and validate the Roblox playback targets."));
        }
    }

    internal bool IsDispatchAuthorized() =>
        TryAuthorizeDispatch(out _);

    internal bool IsDispatchAuthorized(ExactWheelInputEvent inputEvent) =>
        TryAuthorizeDispatch(inputEvent, out _);

    internal ExactWheelDispatchAuthorization GetDispatchAuthorization()
    {
        if (TryAuthorizeDispatch(out var failure))
            return ExactWheelDispatchAuthorization.Authorized;
        return ToDispatchAuthorization(failure);
    }

    internal ExactWheelDispatchAuthorization GetDispatchAuthorization(
        ExactWheelInputEvent inputEvent)
    {
        if (TryAuthorizeDispatch(inputEvent, out var failure))
            return ExactWheelDispatchAuthorization.Authorized;
        return ToDispatchAuthorization(failure);
    }

    internal bool TryAuthorizeDispatch(
        out RobloxPlaybackTargetLeaseFailure? failure) =>
        TryAuthorizeDispatch(inputEvent: null, out failure);

    internal bool TryAuthorizeDispatch(
        ExactWheelInputEvent inputEvent,
        out RobloxPlaybackTargetLeaseFailure? failure) =>
        TryAuthorizeDispatch((ExactWheelInputEvent?)inputEvent, out failure);

    private bool TryAuthorizeDispatch(
        ExactWheelInputEvent? inputEvent,
        out RobloxPlaybackTargetLeaseFailure? failure)
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                failure = _failure;
                return false;
            }

            try
            {
                var now = _native.UtcNow;
                if (now < _lastObservedUtc || now >= _nextFullVerificationUtc)
                {
                    foreach (var target in _targetsByHandle.Values)
                    {
                        var verification = target.Pin.VerifyIdentity(
                            forceTrustRefresh: false);
                        if (verification !=
                            RobloxProcessVerificationStatus.Verified)
                        {
                            return RejectLocked(
                                VerificationFailure(
                                    verification,
                                    duringAcquisition: false),
                                out failure);
                        }
                        if (!target.Pin.IsAlive)
                        {
                            return RejectLocked(
                                ProcessExitedFailure(),
                                out failure);
                        }
                        if (_native.GetWindowProcessId(target.Handle) !=
                            target.Identity.ProcessId)
                        {
                            return RejectLocked(
                                WindowOwnershipFailure(),
                                out failure);
                        }
                        if (!_native.IsUsableTopLevelWindow(target.Handle))
                        {
                            return RejectLocked(
                                WindowUnavailableFailure(),
                                out failure);
                        }
                    }

                    _nextFullVerificationUtc =
                        now + _fullVerificationInterval;
                }

                _lastObservedUtc = now;
                var foreground = _native.GetForegroundWindow();
                if (!_targetsByHandle.TryGetValue(
                        foreground,
                        out var foregroundTarget))
                {
                    // Foreground activation is not atomic. While a recorded
                    // click moves focus between two leased Roblox windows,
                    // Windows can briefly report the shell or no foreground
                    // window at all. Keep the verified process pins alive so
                    // whole-layout playback can wait without injecting until
                    // an allowed HWND returns, cancellation is requested, or
                    // a terminal lease failure occurs. No input is authorized
                    // while the mismatch exists.
                    failure = new RobloxPlaybackTargetLeaseFailure(
                        RobloxPlaybackTargetLeaseFailureKind.ForegroundMismatch,
                        "Playback paused because no leased Roblox window is in the foreground.");
                    return false;
                }
                if (!foregroundTarget.Pin.IsAlive)
                {
                    return RejectLocked(
                        ProcessExitedFailure(),
                        out failure);
                }
                if (_native.GetWindowProcessId(foregroundTarget.Handle) !=
                    foregroundTarget.Identity.ProcessId)
                {
                    return RejectLocked(
                        WindowOwnershipFailure(),
                        out failure);
                }

                if (inputEvent is { } candidate && candidate.IsMouseEvent)
                {
                    var pointedRoot = _native.GetRootWindowAtPoint(
                        candidate.X,
                        candidate.Y);
                    if (!_targetsByHandle.TryGetValue(
                            pointedRoot,
                            out var pointedTarget))
                    {
                        failure = new RobloxPlaybackTargetLeaseFailure(
                            RobloxPlaybackTargetLeaseFailureKind
                                .PointerTargetMismatch,
                            "Playback paused because the recorded pointer location is not over a leased Roblox window.");
                        return false;
                    }
                    if (!pointedTarget.Pin.IsAlive)
                    {
                        return RejectLocked(
                            ProcessExitedFailure(),
                            out failure);
                    }
                    if (_native.GetWindowProcessId(pointedTarget.Handle) !=
                        pointedTarget.Identity.ProcessId)
                    {
                        return RejectLocked(
                            WindowOwnershipFailure(),
                            out failure);
                    }
                }

                failure = null;
                return true;
            }
            catch (Exception exception) when (IsExpectedNativeFailure(exception))
            {
                return RejectLocked(
                    new RobloxPlaybackTargetLeaseFailure(
                        RobloxPlaybackTargetLeaseFailureKind.WindowUnavailable,
                        "Windows could not revalidate the leased Roblox playback targets."),
                    out failure);
            }
        }
    }

    private static ExactWheelDispatchAuthorization ToDispatchAuthorization(
        RobloxPlaybackTargetLeaseFailure? failure) =>
        failure?.Kind is
            RobloxPlaybackTargetLeaseFailureKind.ForegroundMismatch or
            RobloxPlaybackTargetLeaseFailureKind.PointerTargetMismatch
            ? ExactWheelDispatchAuthorization.TemporarilyUnavailable
            : ExactWheelDispatchAuthorization.Denied;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _failure ??= new RobloxPlaybackTargetLeaseFailure(
                RobloxPlaybackTargetLeaseFailureKind.Disposed,
                "The Roblox playback target lease has ended.");
            ReleasePinsLocked();
        }
    }

    private static RobloxPlaybackTargetLeaseFailure? ValidateTargetSet(
        IReadOnlyList<RobloxPlaybackTarget> allowedTargets)
    {
        if (allowedTargets.Count == 0)
        {
            return new RobloxPlaybackTargetLeaseFailure(
                RobloxPlaybackTargetLeaseFailureKind.InvalidTargetSet,
                "At least one exact Roblox playback target is required.");
        }

        var processIds = new HashSet<int>();
        var handles = new HashSet<nint>();
        foreach (var target in allowedTargets)
        {
            if (target is null ||
                target.Identity is null ||
                target.Identity.ProcessId <= 0 ||
                target.Identity.StartTimeUtc == default ||
                target.Identity.StartTimeUtc.Kind != DateTimeKind.Utc ||
                string.IsNullOrWhiteSpace(target.Identity.ExecutablePath) ||
                target.Handle == nint.Zero)
            {
                return new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.InvalidTargetSet,
                    "Every playback target requires a stable process identity and exact nonzero window handle.");
            }
            if (!processIds.Add(target.Identity.ProcessId) ||
                !handles.Add(target.Handle))
            {
                return new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.ConflictingTargetSet,
                    "A playback lease cannot map one process or window handle to multiple targets.");
            }
        }

        return null;
    }

    private static RobloxPlaybackTargetLeaseAcquisitionResult FailAcquisition(
        IReadOnlyList<PinnedTarget> targets,
        RobloxPlaybackTargetLeaseFailure failure)
    {
        foreach (var target in targets)
            target.Pin.Dispose();
        return RobloxPlaybackTargetLeaseAcquisitionResult.Failed(failure);
    }

    private bool RejectLocked(
        RobloxPlaybackTargetLeaseFailure failure,
        out RobloxPlaybackTargetLeaseFailure? reportedFailure)
    {
        _failure ??= failure;
        ReleasePinsLocked();
        reportedFailure = _failure;
        return false;
    }

    private void ReleasePinsLocked()
    {
        if (_pinsReleased)
            return;
        _pinsReleased = true;
        foreach (var target in _targetsByHandle.Values)
            target.Pin.Dispose();
    }

    private static RobloxPlaybackTargetLeaseFailure VerificationFailure(
        RobloxProcessVerificationStatus verification,
        bool duringAcquisition) =>
        verification switch
        {
            RobloxProcessVerificationStatus.NotFound when duringAcquisition =>
                new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.ProcessUnavailable,
                    "The exact Roblox process was unavailable while playback targets were acquired."),
            RobloxProcessVerificationStatus.NotFound or
                RobloxProcessVerificationStatus.Exited =>
                ProcessExitedFailure(),
            RobloxProcessVerificationStatus.Unavailable =>
                new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.ProcessUnavailable,
                    "Windows could not revalidate the retained Roblox process."),
            _ => new RobloxPlaybackTargetLeaseFailure(
                RobloxPlaybackTargetLeaseFailureKind.IdentityRejected,
                "The retained Roblox process no longer matches its stable verified identity.")
        };

    private static RobloxPlaybackTargetLeaseFailure ProcessExitedFailure() =>
        new(
            RobloxPlaybackTargetLeaseFailureKind.ProcessExited,
            "The original Roblox process exited, so playback was stopped before its PID or HWND could be reused.");

    private static RobloxPlaybackTargetLeaseFailure WindowOwnershipFailure() =>
        new(
            RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged,
            "A leased Roblox window no longer belongs to its original process.");

    private static RobloxPlaybackTargetLeaseFailure WindowUnavailableFailure() =>
        new(
            RobloxPlaybackTargetLeaseFailureKind.WindowUnavailable,
            "A leased Roblox window is no longer a visible, usable top-level window.");

    private static bool IsExpectedNativeFailure(Exception exception) =>
        exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException or ArgumentException or
            UnauthorizedAccessException;

    private sealed record PinnedTarget(
        RobloxClientProcessIdentity Identity,
        nint Handle,
        IRobloxProcessLifetimePin Pin);
}
