using SessionDock.ExactWheel;

namespace SessionDock.Services;

internal interface IRobloxProcessLifetimePin : IDisposable
{
    RobloxClientProcessIdentity Identity { get; }

    bool IsExitObservedAlive { get; }

    bool IsRetainedProcessAlive { get; }

    RobloxProcessVerificationStatus RevalidateIdentityAndToken(
        bool refreshExecutableTrust);
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

internal readonly record struct RobloxPlaybackTargetLeaseAcquisitionResult(
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
    private static readonly TimeSpan DefaultPlaybackIdentityRevalidationInterval =
        TimeSpan.FromSeconds(5);

    internal RobloxPlaybackTargetLeaseAcquisitionResult
        AcquirePlaybackTargetLease(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan? identityRevalidationInterval = null,
        RobloxExecutableTrustContext? trustContext = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return AcquirePlaybackTargetLease(
            [new RobloxPlaybackTarget(identity, windowHandle)],
            identityRevalidationInterval,
            trustContext);
    }

    internal RobloxPlaybackTargetLeaseAcquisitionResult
        AcquirePlaybackTargetLease(
        IReadOnlyList<RobloxPlaybackTarget> allowedTargets,
        TimeSpan? identityRevalidationInterval = null,
        RobloxExecutableTrustContext? trustContext = null)
    {
        ArgumentNullException.ThrowIfNull(allowedTargets);
        var interval = identityRevalidationInterval ??
            DefaultPlaybackIdentityRevalidationInterval;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(identityRevalidationInterval),
                "The playback identity revalidation interval must be from one tick through one minute.");
        }

        return RobloxPlaybackTargetLease.Acquire(
            _native,
            allowedTargets,
            interval,
            trustContext);
    }
}

internal sealed class RobloxPlaybackTargetLease : IDisposable
{
    private readonly object _sync = new();
    private readonly IRobloxWindowNativeAdapter _native;
    private readonly IReadOnlyDictionary<nint, PinnedTarget> _targetsByHandle;
    private readonly TimeSpan _identityRevalidationInterval;
    private DateTimeOffset _lastObservedUtc;
    private DateTimeOffset _nextLivenessSweepUtc;
    private RobloxPlaybackTargetLeaseFailure? _failure;
    private bool _pinsReleased;
    private bool _disposed;

    private RobloxPlaybackTargetLease(
        IRobloxWindowNativeAdapter native,
        IReadOnlyList<PinnedTarget> targets,
        TimeSpan identityRevalidationInterval)
    {
        _native = native;
        _targetsByHandle = targets.ToDictionary(target => target.Handle);
        _identityRevalidationInterval = identityRevalidationInterval;
        _lastObservedUtc = native.UtcNow;
        _nextLivenessSweepUtc =
            _lastObservedUtc + identityRevalidationInterval;
        foreach (var target in targets)
        {
            target.NextIdentityRevalidationUtc =
                _lastObservedUtc + identityRevalidationInterval;
        }
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
        TimeSpan identityRevalidationInterval,
        RobloxExecutableTrustContext? trustContext)
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
                var trustClaim = trustContext?.AcquireVerification(
                    target.Identity.ExecutablePath);
                if (trustClaim?.ExecutableTrustRejected == true)
                {
                    return FailAcquisition(
                        pinnedTargets,
                        VerificationFailure(
                            RobloxProcessVerificationStatus
                                .ExecutableNotTrusted,
                            duringAcquisition: true));
                }
                RobloxProcessVerificationStatus verification;
                IRobloxProcessLifetimePin? pin;
                using (trustClaim)
                {
                    verification = native.TryPinProcessLifetime(
                        target.Identity,
                        trustClaim?.ForceTrustRefresh ?? true,
                        trustClaim?.VerifyExecutableTrust ?? true,
                        trustClaim?.ExecutableHandle,
                        out pin);
                    trustClaim?.ReportVerification(verification);
                }
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
                if (!pin.IsRetainedProcessAlive)
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
                if (!pin.IsRetainedProcessAlive)
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
                    identityRevalidationInterval));
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

    // Window operations can reuse the exact process proof retained for input
    // playback without broadening dispatch authorization or rehashing the
    // executable. A mismatched caller is rejected without poisoning an
    // otherwise valid lease; a changed retained target remains a sticky
    // terminal lease failure.
    internal RobloxPlaybackTargetLeaseFailure? ValidateExactTarget(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        bool revalidateIdentityAndToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            if (_failure is not null)
                return _failure;
            if (!_targetsByHandle.TryGetValue(
                    windowHandle,
                    out var target) ||
                !RobloxClientProcessIdentityComparer.Instance.Equals(
                    target.Identity,
                    identity))
            {
                return new RobloxPlaybackTargetLeaseFailure(
                    RobloxPlaybackTargetLeaseFailureKind.IdentityRejected,
                    "The playback lease does not contain that exact Roblox process and window target.");
            }

            try
            {
                var now = _native.UtcNow;
                var clockRegressed = now < _lastObservedUtc;
                _lastObservedUtc = now;
                if (clockRegressed)
                {
                    ResetIdentityRevalidationDeadlinesLocked(now);
                    foreach (var retainedTarget in _targetsByHandle.Values)
                    {
                        if (!retainedTarget.Pin.IsExitObservedAlive)
                        {
                            _ = RejectLocked(
                                ProcessExitedFailure(),
                                out var failure);
                            return failure;
                        }
                    }

                    _nextLivenessSweepUtc =
                        now + _identityRevalidationInterval;
                }
                if (revalidateIdentityAndToken ||
                    clockRegressed ||
                    now >= target.NextIdentityRevalidationUtc)
                {
                    var verification = target.Pin
                        .RevalidateIdentityAndToken(
                            refreshExecutableTrust: false);
                    if (verification !=
                        RobloxProcessVerificationStatus.Verified)
                    {
                        _ = RejectLocked(
                            VerificationFailure(
                                verification,
                                duringAcquisition: false),
                            out var failure);
                        return failure;
                    }

                    target.NextIdentityRevalidationUtc =
                        now + _identityRevalidationInterval;
                }
                else if (!target.Pin.IsRetainedProcessAlive)
                {
                    _ = RejectLocked(ProcessExitedFailure(), out var failure);
                    return failure;
                }
                if (_native.GetWindowProcessId(target.Handle) !=
                    target.Identity.ProcessId)
                {
                    _ = RejectLocked(
                        WindowOwnershipFailure(),
                        out var failure);
                    return failure;
                }
                if (!_native.IsUsableTopLevelWindow(target.Handle))
                {
                    _ = RejectLocked(
                        WindowUnavailableFailure(),
                        out var failure);
                    return failure;
                }

                return null;
            }
            catch (Exception exception) when (
                IsExpectedNativeFailure(exception))
            {
                _ = RejectLocked(
                    new RobloxPlaybackTargetLeaseFailure(
                        RobloxPlaybackTargetLeaseFailureKind.WindowUnavailable,
                        "Windows could not revalidate the leased Roblox playback target."),
                    out var failure);
                return failure;
            }
        }
    }

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
                var clockRegressed = now < _lastObservedUtc;
                _lastObservedUtc = now;
                if (clockRegressed)
                    ResetIdentityRevalidationDeadlinesLocked(now);
                if (clockRegressed || now >= _nextLivenessSweepUtc)
                {
                    // Process-exit state is event-backed in production, so a
                    // complete session liveness sweep is only a small managed
                    // read per target. This preserves prompt failure when an
                    // inactive client exits without repeating expensive full
                    // identity and signer checks for every client.
                    foreach (var target in _targetsByHandle.Values)
                    {
                        if (!target.Pin.IsExitObservedAlive)
                        {
                            return RejectLocked(
                                ProcessExitedFailure(),
                                out failure);
                        }
                    }

                    _nextLivenessSweepUtc =
                        now + _identityRevalidationInterval;
                }
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
                if (!TryValidateDispatchTargetLocked(
                        foregroundTarget,
                        now,
                        clockRegressed,
                        out failure))
                    return false;

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
                    if (pointedTarget.Handle != foregroundTarget.Handle &&
                        !TryValidateDispatchTargetLocked(
                            pointedTarget,
                            now,
                            clockRegressed,
                            out failure))
                        return false;
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

    private bool TryValidateDispatchTargetLocked(
        PinnedTarget target,
        DateTimeOffset now,
        bool clockRegressed,
        out RobloxPlaybackTargetLeaseFailure? failure)
    {
        // A session can contain many clients, but one input event can affect
        // only its foreground and (for mouse input) pointed-at targets. Fully
        // revalidating every retained process on one dispatch caused an O(n)
        // pause once per interval. Keep a deadline per target and perform the
        // expensive path/token validation when that target is actually about
        // to receive input. Acquisition already force-validates every signer,
        // and the retained process handle prevents PID reuse while an inactive
        // target waits for its next use.
        if (clockRegressed || now >= target.NextIdentityRevalidationUtc)
        {
            var verification = target.Pin.RevalidateIdentityAndToken(
                refreshExecutableTrust: false);
            if (verification != RobloxProcessVerificationStatus.Verified)
            {
                return RejectLocked(
                    VerificationFailure(
                        verification,
                        duringAcquisition: false),
                    out failure);
            }
            if (!_native.IsUsableTopLevelWindow(target.Handle))
            {
                return RejectLocked(
                    WindowUnavailableFailure(),
                    out failure);
            }

            target.NextIdentityRevalidationUtc =
                now + _identityRevalidationInterval;
        }

        // These lightweight checks intentionally remain on every dispatch.
        // They keep the final pre-SendInput authorization exact without
        // imposing a full all-client identity scan on the playback thread.
        // The exit event is only an optimization. The retained kernel handle
        // is checked synchronously next to dispatch so thread-pool starvation
        // cannot leave a dead process looking alive long enough for PID/HWND
        // reuse to pass authorization.
        if (!target.Pin.IsRetainedProcessAlive)
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

        failure = null;
        return true;
    }

    private void ResetIdentityRevalidationDeadlinesLocked(
        DateTimeOffset now)
    {
        // A wall-clock rollback invalidates every per-target deadline, not
        // only the target that happened to expose the rollback. Otherwise an
        // inactive client's old future deadline could suppress revalidation
        // for the full rollback delta when that client becomes active later.
        foreach (var target in _targetsByHandle.Values)
            target.NextIdentityRevalidationUtc = now;
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

    private sealed class PinnedTarget(
        RobloxClientProcessIdentity identity,
        nint handle,
        IRobloxProcessLifetimePin pin)
    {
        internal RobloxClientProcessIdentity Identity { get; } = identity;

        internal nint Handle { get; } = handle;

        internal IRobloxProcessLifetimePin Pin { get; } = pin;

        internal DateTimeOffset NextIdentityRevalidationUtc { get; set; }
    }
}
