using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.Services;

internal enum RobloxProcessVerificationStatus
{
    Verified,
    NotFound,
    Exited,
    StartTimeMismatch,
    ExecutablePathMismatch,
    ExecutableNotTrusted,
    WrongUserOrSession,
    Unavailable
}

internal enum RobloxWindowOperationStatus
{
    Success,
    ProcessUnavailable,
    IdentityRejected,
    WindowUnavailable,
    Fullscreen,
    RestoreFailed,
    GeometryUnavailable,
    MoveFailed,
    FocusDenied,
    TimedOut
}

internal sealed record RobloxWindowSnapshot(
    RobloxClientProcessIdentity Identity,
    nint Handle,
    RobloxPixelRect OuterBounds,
    RobloxPixelRect ClientBounds,
    bool IsMinimized,
    bool IsMaximized);

internal sealed record RobloxWindowOperationResult(
    RobloxWindowOperationStatus Status,
    RobloxWindowSnapshot? Window,
    RobloxPixelRect RequestedBounds,
    bool WasClamped,
    string? Error)
{
    internal bool Success => Status == RobloxWindowOperationStatus.Success;

    internal static RobloxWindowOperationResult Failed(
        RobloxWindowOperationStatus status,
        string error) =>
        new(status, null, default, false, error);

    internal static RobloxWindowOperationResult Succeeded(
        RobloxWindowSnapshot window,
        RobloxPixelRect requestedBounds = default) =>
        new(
            RobloxWindowOperationStatus.Success,
            window,
            requestedBounds,
            requestedBounds.IsValid && requestedBounds != window.OuterBounds,
            null);
}

internal sealed record RobloxWindowZOrderTarget(
    string Key,
    RobloxClientProcessIdentity Identity,
    nint Handle);

internal readonly record struct RobloxWindowZOrderPlacement(
    nint Handle,
    nint InsertAfter);

internal sealed record RobloxWindowZOrderResult(
    bool Success,
    string? Error);

internal interface IRobloxWindowNativeAdapter
{
    DateTimeOffset UtcNow { get; }

    RobloxProcessVerificationStatus VerifyProcess(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle);

    RobloxProcessVerificationStatus TryPinProcessLifetime(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle,
        out IRobloxProcessLifetimePin? lifetimePin)
    {
        lifetimePin = null;
        return RobloxProcessVerificationStatus.Unavailable;
    }

    IReadOnlyList<nint> EnumerateTopLevelWindows(int processId);

    IReadOnlyList<nint> EnumerateTopLevelWindowsInZOrder();

    bool IsUsableTopLevelWindow(nint windowHandle);

    int GetWindowProcessId(nint windowHandle);

    bool IsMinimized(nint windowHandle);

    bool IsMaximized(nint windowHandle);

    bool IsFullscreen(nint windowHandle);

    bool TryRestore(nint windowHandle);

    bool TryGetGeometry(
        nint windowHandle,
        out RobloxPixelRect outerBounds,
        out RobloxPixelRect clientBounds);

    bool TrySetBounds(nint windowHandle, RobloxPixelRect outerBounds);

    bool IsTopmost(nint windowHandle);

    bool TryDemoteTopmostWithoutActivation(nint windowHandle);

    bool TryApplyZOrderWithoutActivation(
        IReadOnlyList<RobloxWindowZOrderPlacement> placements);

    bool TrySetForeground(nint windowHandle);

    nint GetForegroundWindow();

    nint GetRootWindowAtPoint(int x, int y);

    IReadOnlyList<RobloxMonitor> GetMonitors();

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed partial class RobloxWindowService
{
    private static readonly TimeSpan DefaultWindowTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultFocusTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRealizeTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProcessRevalidationInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WindowReadinessStability =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RealizedBoundsStability =
        TimeSpan.FromMilliseconds(500);
    private const int MinimumStableReads = 3;
    private const int MaximumPositionReapplyAttempts = 1;

    private readonly IRobloxWindowNativeAdapter _native;
    private readonly TimeSpan _pollInterval;

    internal RobloxWindowService()
        : this(new Win32RobloxWindowNativeAdapter(), DefaultPollInterval)
    {
    }

    internal RobloxWindowService(
        IRobloxWindowNativeAdapter native,
        TimeSpan? pollInterval = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval <= TimeSpan.Zero ||
            _pollInterval > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                "The window poll interval must be from one tick through one second.");
        }
    }

    internal IReadOnlyList<RobloxMonitor> GetMonitors() =>
        _native.GetMonitors();

    internal Task<RobloxWindowOperationResult> WaitForWindowAsync(
        RobloxClientProcessIdentity identity,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        WaitForWindowAsync(
            identity,
            timeout,
            trustContext: null,
            cancellationToken);

    internal async Task<RobloxWindowOperationResult> WaitForWindowAsync(
        RobloxClientProcessIdentity identity,
        TimeSpan? timeout,
        RobloxExecutableTrustContext? trustContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var effectiveTimeout = ValidateTimeout(
            timeout ?? DefaultWindowTimeout,
            nameof(timeout));
        var trustClaim = trustContext is null
            ? null
            : await trustContext.AcquireVerificationAsync(
                identity.ExecutablePath,
                cancellationToken);
        if (trustClaim?.ExecutableTrustRejected == true)
            return VerificationFailure(
                RobloxProcessVerificationStatus.ExecutableNotTrusted);
        RobloxProcessVerificationStatus preliminary;
        IRobloxProcessLifetimePin? lifetimePin;
        using (trustClaim)
        {
            preliminary = _native.TryPinProcessLifetime(
                identity,
                trustClaim?.ForceTrustRefresh ?? true,
                trustClaim?.VerifyExecutableTrust ?? true,
                trustClaim?.ExecutableHandle,
                out lifetimePin);
            trustClaim?.ReportVerification(preliminary);
        }
        if (preliminary != RobloxProcessVerificationStatus.Verified ||
            lifetimePin is null)
        {
            lifetimePin?.Dispose();
            return VerificationFailure(preliminary);
        }
        using (lifetimePin)
        {
            if (!RobloxClientProcessIdentityComparer.Instance.Equals(
                    lifetimePin.Identity,
                    identity))
            {
                return VerificationFailure(
                    RobloxProcessVerificationStatus.StartTimeMismatch);
            }

            var deadline = _native.UtcNow + effectiveTimeout;
            var requiredStability = GetRequiredStability(
                WindowReadinessStability,
                effectiveTimeout);
            var sawFullscreen = false;
            var sawAmbiguousMainWindow = false;
            CandidateWindow? stableCandidate = null;
            var stableSince = _native.UtcNow;
            var lastProcessVerificationUtc = stableSince;
            var nextProcessVerificationUtc =
                stableSince + ProcessRevalidationInterval;
            var stableReads = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _native.UtcNow;
                if (!lifetimePin.IsRetainedProcessAlive)
                {
                    return VerificationFailure(
                        RobloxProcessVerificationStatus.Exited);
                }
                if (now < lastProcessVerificationUtc ||
                    now >= nextProcessVerificationUtc)
                {
                    var currentVerification = lifetimePin
                        .RevalidateIdentityAndToken(
                            refreshExecutableTrust: false);
                    if (currentVerification !=
                        RobloxProcessVerificationStatus.Verified)
                    {
                        return VerificationFailure(currentVerification);
                    }

                    lastProcessVerificationUtc = now;
                    nextProcessVerificationUtc =
                        now + ProcessRevalidationInterval;
                }

                var candidates = _native
                    .EnumerateTopLevelWindows(identity.ProcessId)
                    .Where(window =>
                        window != nint.Zero &&
                        _native.GetWindowProcessId(window) ==
                            identity.ProcessId &&
                        _native.IsUsableTopLevelWindow(window))
                    .Select(window =>
                    {
                        var hasGeometry = _native.TryGetGeometry(
                            window,
                            out var outerBounds,
                            out var clientBounds);
                        return new CandidateWindow(
                            window,
                            hasGeometry,
                            outerBounds,
                            clientBounds,
                            _native.IsFullscreen(window),
                            _native.IsMinimized(window),
                            _native.IsMaximized(window));
                    })
                    .Where(candidate =>
                        candidate.HasGeometry &&
                        !candidate.IsMinimized)
                    .ToArray();

                sawFullscreen |= candidates.Any(candidate =>
                    candidate.IsFullscreen);
                var viable = candidates
                    .Where(item => !item.IsFullscreen)
                    .OrderByDescending(item => Area(item.OuterBounds))
                    .ToArray();
                CandidateWindow? candidate = null;
                if (viable.Length > 0)
                {
                    var largestArea = Area(viable[0].OuterBounds);
                    var equallyViable = viable
                        .TakeWhile(item =>
                            Area(item.OuterBounds) == largestArea)
                        .ToArray();
                    if (equallyViable.Length == 1)
                    {
                        candidate = equallyViable[0];
                    }
                    else
                    {
                        sawAmbiguousMainWindow = true;
                    }
                }

                if (candidate is null)
                {
                    stableCandidate = null;
                    stableReads = 0;
                }
                else if (stableCandidate is not null &&
                         HasSameUsableGeometry(stableCandidate, candidate))
                {
                    stableReads++;
                }
                else
                {
                    stableCandidate = candidate;
                    stableSince = _native.UtcNow;
                    stableReads = 1;
                }

                if (candidate is not null &&
                    stableReads >= MinimumStableReads &&
                    _native.UtcNow - stableSince >= requiredStability)
                {
                    var finalVerification = lifetimePin
                        .RevalidateIdentityAndToken(
                            refreshExecutableTrust: false);
                    if (finalVerification !=
                        RobloxProcessVerificationStatus.Verified)
                    {
                        return VerificationFailure(finalVerification);
                    }
                    if (_native.GetWindowProcessId(candidate.Handle) !=
                        identity.ProcessId)
                    {
                        return RobloxWindowOperationResult.Failed(
                            RobloxWindowOperationStatus.IdentityRejected,
                            "The Roblox window changed process ownership before it could be used.");
                    }

                    if (!_native.IsUsableTopLevelWindow(candidate.Handle) ||
                        _native.IsMinimized(candidate.Handle) ||
                        _native.IsFullscreen(candidate.Handle) ||
                        !_native.TryGetGeometry(
                            candidate.Handle,
                            out var finalOuterBounds,
                            out var finalClientBounds) ||
                        finalOuterBounds != candidate.OuterBounds ||
                        finalClientBounds != candidate.ClientBounds)
                    {
                        stableCandidate = null;
                        stableReads = 0;
                        continue;
                    }

                    return RobloxWindowOperationResult.Succeeded(
                        CreateSnapshot(identity, candidate));
                }

                if (_native.UtcNow >= deadline)
                    break;
                await _native.DelayAsync(_pollInterval, cancellationToken);
            }
            while (_native.UtcNow <= deadline);

            return sawFullscreen
                ? RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.Fullscreen,
                    "Roblox is fullscreen. Switch it to windowed mode before arranging it.")
                : sawAmbiguousMainWindow
                    ? RobloxWindowOperationResult.Failed(
                        RobloxWindowOperationStatus.TimedOut,
                        "Multiple equally viable Roblox windows remained visible, so SessionDock could not identify one main window safely.")
                : RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.TimedOut,
                    "One stable, visible Roblox main window did not become available in time.");
        }
    }

    internal Task<RobloxWindowOperationResult> CaptureAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(
            identity,
            windowHandle,
            trustContext: null,
            cancellationToken);

    internal async Task<RobloxWindowOperationResult> CaptureAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        RobloxExecutableTrustContext? trustContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var trustClaim = trustContext is null
            ? null
            : await trustContext.AcquireVerificationAsync(
                identity.ExecutablePath,
                cancellationToken);
        if (trustClaim?.ExecutableTrustRejected == true)
            return VerificationFailure(
                RobloxProcessVerificationStatus.ExecutableNotTrusted);
        RobloxWindowOperationResult? validated;
        using (trustClaim)
        {
            validated = ValidateWindow(
                identity,
                windowHandle,
                trustClaim?.ForceTrustRefresh ?? true,
                trustClaim?.VerifyExecutableTrust ?? true,
                trustClaim?.ExecutableHandle,
                out var verification);
            trustClaim?.ReportVerification(verification);
        }
        if (validated is not null)
            return validated;
        if (!_native.TryGetGeometry(
                windowHandle,
                out var outerBounds,
                out var clientBounds))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.GeometryUnavailable,
                "The Roblox window bounds could not be read.");
        }

        await Task.CompletedTask;
        return RobloxWindowOperationResult.Succeeded(new RobloxWindowSnapshot(
            identity,
            windowHandle,
            outerBounds,
            clientBounds,
            _native.IsMinimized(windowHandle),
            _native.IsMaximized(windowHandle)));
    }

    internal Task<RobloxWindowOperationResult> SetBoundsAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        RobloxPixelRect requestedOuterBounds,
        TimeSpan? realizeTimeout = null,
        CancellationToken cancellationToken = default) =>
        SetBoundsAsync(
            identity,
            windowHandle,
            requestedOuterBounds,
            realizeTimeout,
            trustContext: null,
            cancellationToken);

    internal async Task<RobloxWindowOperationResult> SetBoundsAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        RobloxPixelRect requestedOuterBounds,
        TimeSpan? realizeTimeout,
        RobloxExecutableTrustContext? trustContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!requestedOuterBounds.IsValid)
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.MoveFailed,
                "The requested Roblox window bounds are invalid.");
        }

        var effectiveTimeout = ValidateTimeout(
            realizeTimeout ?? DefaultRealizeTimeout,
            nameof(realizeTimeout));
        var trustClaim = trustContext is null
            ? null
            : await trustContext.AcquireVerificationAsync(
                identity.ExecutablePath,
                cancellationToken);
        if (trustClaim?.ExecutableTrustRejected == true)
            return VerificationFailure(
                RobloxProcessVerificationStatus.ExecutableNotTrusted);
        RobloxWindowOperationResult? validated;
        using (trustClaim)
        {
            validated = ValidateWindow(
                identity,
                windowHandle,
                trustClaim?.ForceTrustRefresh ?? true,
                trustClaim?.VerifyExecutableTrust ?? true,
                trustClaim?.ExecutableHandle,
                out var verification);
            trustClaim?.ReportVerification(verification);
        }
        if (validated is not null)
            return validated;
        var restored = await RestoreIfNeededAsync(
            identity,
            windowHandle,
            effectiveTimeout,
            cancellationToken);
        if (restored is not null)
            return restored;
        if (_native.IsFullscreen(windowHandle))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.Fullscreen,
                "Roblox is fullscreen. Switch it to windowed mode before arranging it.");
        }
        if (!_native.TrySetBounds(windowHandle, requestedOuterBounds))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.MoveFailed,
                "Windows did not accept the Roblox window position.");
        }

        var deadline = _native.UtcNow + effectiveTimeout;
        var requiredStability = GetRequiredStability(
            RealizedBoundsStability,
            effectiveTimeout);
        RobloxWindowSnapshot? lastSnapshot = null;
        var stableSince = _native.UtcNow;
        var stableReads = 0;
        var positionReapplyAttempts = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repeatedValidation = ValidateWindowHandle(
                identity,
                windowHandle);
            if (repeatedValidation is not null)
                return repeatedValidation;
            if (_native.TryGetGeometry(
                    windowHandle,
                    out var outerBounds,
                    out var clientBounds))
            {
                var snapshot = new RobloxWindowSnapshot(
                    identity,
                    windowHandle,
                    outerBounds,
                    clientBounds,
                    _native.IsMinimized(windowHandle),
                    _native.IsMaximized(windowHandle));
                stableReads = lastSnapshot?.OuterBounds == snapshot.OuterBounds &&
                    lastSnapshot.ClientBounds == snapshot.ClientBounds
                    ? stableReads + 1
                    : 1;
                if (stableReads == 1)
                    stableSince = _native.UtcNow;
                lastSnapshot = snapshot;
                if (stableReads >= MinimumStableReads &&
                    _native.UtcNow - stableSince >= requiredStability)
                {
                    if ((snapshot.OuterBounds.Left != requestedOuterBounds.Left ||
                         snapshot.OuterBounds.Top != requestedOuterBounds.Top) &&
                        positionReapplyAttempts < MaximumPositionReapplyAttempts &&
                        _native.UtcNow < deadline)
                    {
                        if (!_native.TrySetBounds(
                                windowHandle,
                                requestedOuterBounds))
                        {
                            return RobloxWindowOperationResult.Failed(
                                RobloxWindowOperationStatus.MoveFailed,
                                "Windows did not accept the Roblox window position after startup moved it.");
                        }

                        positionReapplyAttempts++;
                        lastSnapshot = null;
                        stableReads = 0;
                        stableSince = _native.UtcNow;
                    }
                    else if (snapshot.OuterBounds.Left == requestedOuterBounds.Left &&
                             snapshot.OuterBounds.Top == requestedOuterBounds.Top)
                    {
                        var finalValidation = ValidateWindow(
                            identity,
                            windowHandle,
                            forceTrustRefresh: false);
                        if (finalValidation is not null)
                            return finalValidation;
                        return RobloxWindowOperationResult.Succeeded(
                            snapshot,
                            requestedOuterBounds);
                    }
                    else
                    {
                        return RobloxWindowOperationResult.Failed(
                            RobloxWindowOperationStatus.MoveFailed,
                            "The Roblox window did not remain at the requested monitor position.");
                    }
                }
            }
            else
            {
                lastSnapshot = null;
                stableReads = 0;
                stableSince = _native.UtcNow;
            }

            if (_native.UtcNow >= deadline)
                break;
            await _native.DelayAsync(_pollInterval, cancellationToken);
        }
        while (_native.UtcNow <= deadline);

        return lastSnapshot is null
            ? RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.GeometryUnavailable,
                "The realized Roblox window bounds could not be read.")
            : RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.TimedOut,
                "The realized Roblox window bounds did not remain stable long enough.");
    }

    internal async Task<RobloxWindowOperationResult> FocusAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var effectiveTimeout = ValidateTimeout(
            timeout ?? DefaultFocusTimeout,
            nameof(timeout));
        var validated = ValidateWindow(
            identity,
            windowHandle,
            forceTrustRefresh: true);
        if (validated is not null)
            return validated;
        var restored = await RestoreIfNeededAsync(
            identity,
            windowHandle,
            effectiveTimeout,
            cancellationToken);
        if (restored is not null)
            return restored;
        if (_native.IsFullscreen(windowHandle))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.Fullscreen,
                "Roblox is fullscreen. Switch it to windowed mode before focusing it.");
        }

        // Deliberately use only the documented foreground request. SessionDock
        // never attaches another thread's input queue or injects an Alt key to
        // bypass Windows foreground-lock policy.
        _ = _native.TrySetForeground(windowHandle);
        var deadline = _native.UtcNow + effectiveTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repeatedValidation = ValidateWindowHandle(
                identity,
                windowHandle);
            if (repeatedValidation is not null)
                return repeatedValidation;

            var foreground = _native.GetForegroundWindow();
            if (foreground == windowHandle &&
                _native.GetWindowProcessId(foreground) == identity.ProcessId)
            {
                var finalVerification = _native.VerifyProcess(
                    identity,
                    forceTrustRefresh: true,
                    verifyExecutableTrust: true,
                    executableTrustHandle: null);
                if (finalVerification != RobloxProcessVerificationStatus.Verified)
                    return VerificationFailure(finalVerification);
                if (!_native.TryGetGeometry(
                        windowHandle,
                        out var outerBounds,
                        out var clientBounds))
                {
                    return RobloxWindowOperationResult.Failed(
                        RobloxWindowOperationStatus.GeometryUnavailable,
                        "The focused Roblox window bounds could not be read.");
                }

                return RobloxWindowOperationResult.Succeeded(
                    new RobloxWindowSnapshot(
                        identity,
                        windowHandle,
                        outerBounds,
                        clientBounds,
                        _native.IsMinimized(windowHandle),
                        _native.IsMaximized(windowHandle)));
            }

            if (_native.UtcNow >= deadline)
                break;
            await _native.DelayAsync(_pollInterval, cancellationToken);
        }
        while (_native.UtcNow <= deadline);

        return RobloxWindowOperationResult.Failed(
            RobloxWindowOperationStatus.FocusDenied,
            "Windows denied the foreground request. Click the visible Roblox reveal area and try again.");
    }

    internal Task<RobloxWindowOperationResult> FocusAsync(
        RobloxPlaybackTargetLease playbackLease,
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        FocusAsync(
            playbackLease,
            identity,
            windowHandle,
            timeout,
            canProgrammaticallyActivate: null,
            cancellationToken);

    internal async Task<RobloxWindowOperationResult> FocusAsync(
        RobloxPlaybackTargetLease playbackLease,
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan? timeout,
        Func<bool>? canProgrammaticallyActivate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playbackLease);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveTimeout = ValidateTimeout(
            timeout ?? DefaultFocusTimeout,
            nameof(timeout));
        var leaseFailure = playbackLease.ValidateExactTarget(
            identity,
            windowHandle,
            revalidateIdentityAndToken: false);
        if (leaseFailure is not null)
            return LeaseValidationFailure(leaseFailure);

        if (_native.IsMinimized(windowHandle) ||
            _native.IsMaximized(windowHandle))
        {
            if (!CanProgrammaticallyActivate(canProgrammaticallyActivate))
                return PhysicalInterventionFocusFailure();
            if (!_native.TryRestore(windowHandle))
            {
                return RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.RestoreFailed,
                    "Windows did not restore the Roblox window.");
            }

            var restoreDeadline = _native.UtcNow + effectiveTimeout;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                leaseFailure = playbackLease.ValidateExactTarget(
                    identity,
                    windowHandle,
                    revalidateIdentityAndToken: false);
                if (leaseFailure is not null)
                    return LeaseValidationFailure(leaseFailure);
                if (!_native.IsMinimized(windowHandle) &&
                    !_native.IsMaximized(windowHandle))
                {
                    break;
                }

                if (_native.UtcNow >= restoreDeadline)
                {
                    return RobloxWindowOperationResult.Failed(
                        RobloxWindowOperationStatus.RestoreFailed,
                        "The Roblox window did not finish restoring in time.");
                }
                await _native.DelayAsync(_pollInterval, cancellationToken);
            }
            while (_native.UtcNow <= restoreDeadline);
        }

        if (_native.IsFullscreen(windowHandle))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.Fullscreen,
                "Roblox is fullscreen. Switch it to windowed mode before focusing it.");
        }

        leaseFailure = playbackLease.ValidateExactTarget(
            identity,
            windowHandle,
            revalidateIdentityAndToken: false);
        if (leaseFailure is not null)
            return LeaseValidationFailure(leaseFailure);

        // The lease already force-validated the executable and retained the
        // exact kernel process object. Focus therefore needs only the pinned
        // liveness/identity proof plus live HWND ownership checks; it must not
        // hash and WinVerifyTrust the same executable again.
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanProgrammaticallyActivate(canProgrammaticallyActivate))
            return PhysicalInterventionFocusFailure();
        _ = _native.TrySetForeground(windowHandle);
        var deadline = _native.UtcNow + effectiveTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var foreground = _native.GetForegroundWindow();
            if (foreground == windowHandle &&
                _native.GetWindowProcessId(foreground) == identity.ProcessId)
            {
                // The retained lease schedules start-time, path, user,
                // session, and token checks per exact target. Honor that
                // deadline here rather than forcing the same expensive check
                // on every short macro cycle. Exact liveness and HWND
                // ownership remain live-checked on every call and dispatch.
                leaseFailure = playbackLease.ValidateExactTarget(
                    identity,
                    windowHandle,
                    revalidateIdentityAndToken: false);
                if (leaseFailure is not null)
                    return LeaseValidationFailure(leaseFailure);
                if (!_native.TryGetGeometry(
                        windowHandle,
                        out var outerBounds,
                        out var clientBounds))
                {
                    return RobloxWindowOperationResult.Failed(
                        RobloxWindowOperationStatus.GeometryUnavailable,
                        "The focused Roblox window bounds could not be read.");
                }

                return RobloxWindowOperationResult.Succeeded(
                    new RobloxWindowSnapshot(
                        identity,
                        windowHandle,
                        outerBounds,
                        clientBounds,
                        _native.IsMinimized(windowHandle),
                        _native.IsMaximized(windowHandle)));
            }

            // A successful foreground transition takes the stronger final
            // retained-identity path above. While Windows is still deciding,
            // keep checking the exact retained process/HWND before waiting so
            // a changed target fails closed without duplicating the success
            // path's native checks.
            leaseFailure = playbackLease.ValidateExactTarget(
                identity,
                windowHandle,
                revalidateIdentityAndToken: false);
            if (leaseFailure is not null)
                return LeaseValidationFailure(leaseFailure);

            if (_native.UtcNow >= deadline)
                break;
            await _native.DelayAsync(_pollInterval, cancellationToken);
        }
        while (_native.UtcNow <= deadline);

        return RobloxWindowOperationResult.Failed(
            RobloxWindowOperationStatus.FocusDenied,
            "Windows denied the foreground request. Click the visible Roblox reveal area and try again.");
    }

    private static bool CanProgrammaticallyActivate(Func<bool>? guard)
    {
        if (guard is null)
            return true;
        try
        {
            return guard();
        }
        catch (Exception)
        {
            // A lost intervention monitor or concurrently disposed playback
            // session must fail closed before calling a foreground API.
            return false;
        }
    }

    private static RobloxWindowOperationResult
        PhysicalInterventionFocusFailure() =>
        RobloxWindowOperationResult.Failed(
            RobloxWindowOperationStatus.FocusDenied,
            "Focus stayed paused after physical input. Release the input and click an allowed Roblox window to resume.");

    internal Task<RobloxWindowZOrderResult> ApplyZOrderAsync(
        RobloxCascadeLayoutPlan plan,
        IReadOnlyList<RobloxWindowZOrderTarget> targets,
        CancellationToken cancellationToken = default) =>
        ApplyZOrderAsync(
            plan,
            targets,
            trustContext: null,
            cancellationToken);

    internal Task<RobloxWindowZOrderResult> ApplyZOrderAsync(
        RobloxCascadeLayoutPlan plan,
        IReadOnlyList<RobloxWindowZOrderTarget> targets,
        RobloxExecutableTrustContext? trustContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targets);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.Success)
        {
            return Task.FromResult(new RobloxWindowZOrderResult(
                false,
                "A failed cascade plan cannot be applied."));
        }

        var targetsByKey = targets
            .Where(target =>
                target is not null &&
                !string.IsNullOrWhiteSpace(target.Key))
            .GroupBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        if (targetsByKey.Count != targets.Count ||
            targetsByKey.Values.Any(group => group.Length != 1) ||
            targets
                .Where(target => target is not null)
                .Select(target => target.Handle)
                .Distinct()
                .Count() != targets.Count ||
            plan.Placements.Count != targets.Count ||
            plan.Placements
                .Select(placement => placement.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != plan.Placements.Count)
        {
            return Task.FromResult(new RobloxWindowZOrderResult(
                false,
                "The cascade window mapping is incomplete or ambiguous."));
        }

        var orderedTargets = new List<RobloxWindowZOrderTarget>(targets.Count);
        foreach (var placement in plan.Placements
                     .OrderBy(item => item.ZOrderFromBottom))
        {
            if (!targetsByKey.TryGetValue(placement.Key, out var matches))
            {
                return Task.FromResult(new RobloxWindowZOrderResult(
                    false,
                    "The cascade window mapping is incomplete or ambiguous."));
            }

            var target = matches[0];
            var trustClaim = trustContext?.AcquireVerification(
                target.Identity.ExecutablePath,
                cancellationToken);
            if (trustClaim?.ExecutableTrustRejected == true)
            {
                return Task.FromResult(new RobloxWindowZOrderResult(
                    false,
                    VerificationFailure(
                        RobloxProcessVerificationStatus
                            .ExecutableNotTrusted).Error));
            }
            RobloxWindowOperationResult? validated;
            using (trustClaim)
            {
                validated = ValidateWindow(
                    target.Identity,
                    target.Handle,
                    trustClaim?.ForceTrustRefresh ?? true,
                    trustClaim?.VerifyExecutableTrust ?? true,
                    trustClaim?.ExecutableHandle,
                    out var verification);
                trustClaim?.ReportVerification(verification);
            }
            if (validated is not null)
            {
                return Task.FromResult(new RobloxWindowZOrderResult(
                    false,
                    validated.Error));
            }
            orderedTargets.Add(target);
        }

        // Older SessionDock builds accidentally passed HWND_TOPMOST while
        // arranging clients. Recover those windows first and put them behind
        // normal application windows instead of preserving a sticky global
        // always-on-top state.
        foreach (var target in orderedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_native.IsTopmost(target.Handle) &&
                !_native.TryDemoteTopmostWithoutActivation(target.Handle))
            {
                return Task.FromResult(new RobloxWindowZOrderResult(
                    false,
                    "Windows did not remove an old Roblox always-on-top state."));
            }
        }

        var currentZOrder = _native.EnumerateTopLevelWindowsInZOrder();
        var targetHandles = orderedTargets
            .Select(target => target.Handle)
            .ToHashSet();
        var targetSlots = currentZOrder
            .Select((handle, index) => (handle, index))
            .Where(item => targetHandles.Contains(item.handle))
            .Select(item => item.index)
            .ToArray();
        if (targetHandles.Count != orderedTargets.Count ||
            targetSlots.Length != orderedTargets.Count)
        {
            return Task.FromResult(new RobloxWindowZOrderResult(
                false,
                "The Roblox windows disappeared before their cascade order could be applied."));
        }

        // Keep every existing Roblox z-order slot in place and only exchange
        // which verified Roblox HWND occupies each slot. This establishes the
        // requested relative staircase order without raising the group above
        // unrelated applications. The native adapter applies these insertions
        // as one non-activating deferred operation.
        var desiredTopToBottom = orderedTargets
            .AsEnumerable()
            .Reverse()
            .ToArray();
        var finalZOrder = currentZOrder.ToArray();
        for (var index = 0; index < targetSlots.Length; index++)
            finalZOrder[targetSlots[index]] = desiredTopToBottom[index].Handle;

        if (targetSlots.Length > 1)
        {
            var placements = targetSlots
                .Select(slot => new RobloxWindowZOrderPlacement(
                    finalZOrder[slot],
                    slot == 0 ? nint.Zero : finalZOrder[slot - 1]))
                .ToArray();
            if (!_native.TryApplyZOrderWithoutActivation(placements))
            {
                return Task.FromResult(new RobloxWindowZOrderResult(
                    false,
                    "Windows did not accept the Roblox cascade order."));
            }
        }

        return Task.FromResult(new RobloxWindowZOrderResult(true, null));
    }

    private async Task<RobloxWindowOperationResult?> RestoreIfNeededAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_native.IsMinimized(windowHandle) &&
            !_native.IsMaximized(windowHandle))
        {
            return null;
        }
        if (!_native.TryRestore(windowHandle))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.RestoreFailed,
                "Windows did not restore the Roblox window.");
        }

        var deadline = _native.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validated = ValidateWindowHandle(identity, windowHandle);
            if (validated is not null)
                return validated;
            if (!_native.IsMinimized(windowHandle) &&
                !_native.IsMaximized(windowHandle))
            {
                return null;
            }

            if (_native.UtcNow >= deadline)
                break;
            await _native.DelayAsync(_pollInterval, cancellationToken);
        }
        while (_native.UtcNow <= deadline);

        return RobloxWindowOperationResult.Failed(
            RobloxWindowOperationStatus.RestoreFailed,
            "The Roblox window did not finish restoring in time.");
    }

    private RobloxWindowOperationResult? ValidateWindow(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        bool forceTrustRefresh = false)
    {
        return ValidateWindow(
            identity,
            windowHandle,
            forceTrustRefresh,
            verifyExecutableTrust: true,
            executableTrustHandle: null,
            out _);
    }

    private RobloxWindowOperationResult? ValidateWindow(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle,
        out RobloxProcessVerificationStatus verification)
    {
        verification = _native.VerifyProcess(
            identity,
            forceTrustRefresh,
            verifyExecutableTrust,
            executableTrustHandle);
        if (verification != RobloxProcessVerificationStatus.Verified)
            return VerificationFailure(verification);
        return ValidateWindowHandle(identity, windowHandle);
    }

    private RobloxWindowOperationResult? ValidateWindowHandle(
        RobloxClientProcessIdentity identity,
        nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            _native.GetWindowProcessId(windowHandle) != identity.ProcessId)
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.IdentityRejected,
                "The window does not belong to the exact launched Roblox process.");
        }
        if (!_native.IsUsableTopLevelWindow(windowHandle))
        {
            return RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.WindowUnavailable,
                "The Roblox window is no longer visible and usable.");
        }

        return null;
    }

    private static RobloxWindowOperationResult LeaseValidationFailure(
        RobloxPlaybackTargetLeaseFailure failure) =>
        failure.Kind switch
        {
            RobloxPlaybackTargetLeaseFailureKind.InvalidTargetSet or
                RobloxPlaybackTargetLeaseFailureKind.ConflictingTargetSet or
                RobloxPlaybackTargetLeaseFailureKind.IdentityRejected or
                RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged =>
                RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.IdentityRejected,
                    failure.Error),
            RobloxPlaybackTargetLeaseFailureKind.ProcessUnavailable or
                RobloxPlaybackTargetLeaseFailureKind.ProcessExited or
                RobloxPlaybackTargetLeaseFailureKind.Disposed =>
                RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.ProcessUnavailable,
                    failure.Error),
            RobloxPlaybackTargetLeaseFailureKind.WindowUnavailable =>
                RobloxWindowOperationResult.Failed(
                    RobloxWindowOperationStatus.WindowUnavailable,
                    failure.Error),
            _ => RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.FocusDenied,
                failure.Error)
        };

    private static RobloxWindowOperationResult VerificationFailure(
        RobloxProcessVerificationStatus status) =>
        status is RobloxProcessVerificationStatus.NotFound or
            RobloxProcessVerificationStatus.Exited or
            RobloxProcessVerificationStatus.Unavailable
            ? RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.ProcessUnavailable,
                "The exact launched Roblox process is no longer available.")
            : RobloxWindowOperationResult.Failed(
                RobloxWindowOperationStatus.IdentityRejected,
                "The Roblox process identity, executable trust, user, or Windows session no longer matches.");

    private static RobloxWindowSnapshot CreateSnapshot(
        RobloxClientProcessIdentity identity,
        CandidateWindow candidate) =>
        new(
            identity,
            candidate.Handle,
            candidate.OuterBounds,
            candidate.ClientBounds,
            candidate.IsMinimized,
            candidate.IsMaximized);

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                name,
                "The window operation timeout must be from one tick through one minute.");
        }

        return timeout;
    }

    private static TimeSpan GetRequiredStability(
        TimeSpan preferred,
        TimeSpan timeout) =>
        TimeSpan.FromTicks(Math.Max(
            1,
            Math.Min(preferred.Ticks, timeout.Ticks / 2)));

    private static bool HasSameUsableGeometry(
        CandidateWindow first,
        CandidateWindow second) =>
        first.Handle == second.Handle &&
        first.OuterBounds == second.OuterBounds &&
        first.ClientBounds == second.ClientBounds &&
        first.IsMaximized == second.IsMaximized;

    private static long Area(RobloxPixelRect bounds) =>
        (long)bounds.Width * bounds.Height;

    private sealed record CandidateWindow(
        nint Handle,
        bool HasGeometry,
        RobloxPixelRect OuterBounds,
        RobloxPixelRect ClientBounds,
        bool IsFullscreen,
        bool IsMinimized,
        bool IsMaximized);
}

internal sealed class Win32RobloxWindowNativeAdapter :
    IRobloxWindowNativeAdapter
{
    private static readonly TimeSpan WindowEnumerationSnapshotLifetime =
        TimeSpan.FromMilliseconds(75);
    private const uint WaitTimeout = 258;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorPrimaryFlag = 1;
    private const uint DisplayDeviceActive = 1;
    private const uint GetDeviceInterfaceName = 1;
    private const uint MaximumMonitorDevicesPerAdapter = 64;
    private const int DwmCloakedAttribute = 14;
    private const int GetWindowOwner = 4;
    private const uint GetAncestorRoot = 2;
    private const int WindowStyleIndex = -16;
    private const int WindowExtendedStyleIndex = -20;
    private const long WindowStylePopup = 0x80000000L;
    private const long WindowExtendedStyleTopmost = 0x00000008L;
    private const int ShowNormalNoActivate = 4;
    private static readonly nint WindowBottom = new(1);
    private static readonly nint WindowNotTopmost = new(-2);
    private const uint SetWindowFlags =
        0x0004 | // SWP_NOZORDER
        0x0010 | // SWP_NOACTIVATE
        0x0200 | // SWP_NOOWNERZORDER
        0x4000;  // SWP_ASYNCWINDOWPOS
    private const uint SetZOrderFlags =
        0x0001 | // SWP_NOSIZE
        0x0002 | // SWP_NOMOVE
        0x0010 | // SWP_NOACTIVATE
        0x0200;  // SWP_NOOWNERZORDER

    private readonly object _windowEnumerationGate = new();
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<IReadOnlyDictionary<int, IReadOnlyList<nint>>>
        _enumerateWindowsByProcess;
    private IReadOnlyDictionary<int, IReadOnlyList<nint>>
        _windowEnumerationSnapshot =
            new Dictionary<int, IReadOnlyList<nint>>();
    private DateTimeOffset _windowEnumerationSnapshotUtc =
        DateTimeOffset.MinValue;
    private DateTimeOffset _windowEnumerationSnapshotExpiresUtc =
        DateTimeOffset.MinValue;

    internal Win32RobloxWindowNativeAdapter()
        : this(
            () => DateTimeOffset.UtcNow,
            EnumerateWindowsByProcess)
    {
    }

    internal Win32RobloxWindowNativeAdapter(
        Func<DateTimeOffset> getUtcNow,
        Func<IReadOnlyDictionary<int, IReadOnlyList<nint>>>
            enumerateWindowsByProcess)
    {
        _getUtcNow = getUtcNow ??
            throw new ArgumentNullException(nameof(getUtcNow));
        _enumerateWindowsByProcess = enumerateWindowsByProcess ??
            throw new ArgumentNullException(nameof(enumerateWindowsByProcess));
    }

    public DateTimeOffset UtcNow => _getUtcNow();

    public RobloxProcessVerificationStatus VerifyProcess(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return RobloxProcessVerificationStatus.NotFound;
        }

        using (process)
        {
            return VerifyPinnedProcess(
                process,
                identity,
                forceTrustRefresh,
                verifyExecutableTrust,
                executableTrustHandle);
        }
    }

    public RobloxProcessVerificationStatus TryPinProcessLifetime(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle,
        out IRobloxProcessLifetimePin? lifetimePin)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lifetimePin = null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);

            // Force the Process instance to open and retain its kernel process
            // handle before identity verification. The retained handle keeps
            // referring to this process object even after its PID is recycled.
            _ = process.SafeHandle;
            var verification = VerifyPinnedProcess(
                process,
                identity,
                forceTrustRefresh,
                verifyExecutableTrust,
                executableTrustHandle);
            if (verification != RobloxProcessVerificationStatus.Verified)
                return verification;

            lifetimePin = new Win32RobloxProcessLifetimePin(process, identity);
            process = null;
            return RobloxProcessVerificationStatus.Verified;
        }
        catch (ArgumentException)
        {
            return RobloxProcessVerificationStatus.NotFound;
        }
        catch (Exception exception) when (
            IsExpectedProcessAccessFailure(exception))
        {
            return RobloxProcessVerificationStatus.Unavailable;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static RobloxProcessVerificationStatus VerifyPinnedProcess(
        Process process,
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
        bool verifyExecutableTrust,
        SafeFileHandle? executableTrustHandle)
    {
        try
        {
            if (process.HasExited)
                return RobloxProcessVerificationStatus.Exited;
            if (process.StartTime.ToUniversalTime() != identity.StartTimeUtc)
                return RobloxProcessVerificationStatus.StartTimeMismatch;

            var executablePath = process.MainModule?.FileName;
            if (executablePath is null ||
                !Path.GetFullPath(executablePath).Equals(
                    Path.GetFullPath(identity.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return RobloxProcessVerificationStatus.ExecutablePathMismatch;
            }
            if (verifyExecutableTrust &&
                !(executableTrustHandle is not null
                    ? RobloxExecutableTrust.IsTrustedPlayerPath(
                        executablePath,
                        executableTrustHandle,
                        forceTrustRefresh)
                    : RobloxExecutableTrust.IsTrustedPlayerPath(
                        executablePath,
                        forceTrustRefresh)))
            {
                return RobloxProcessVerificationStatus.ExecutableNotTrusted;
            }
            return WindowsProcessSecurity
                .IsOwnedStandardUserProcessInCurrentSession(process)
                ? RobloxProcessVerificationStatus.Verified
                : RobloxProcessVerificationStatus.WrongUserOrSession;
        }
        catch (Exception exception) when (
            IsExpectedProcessAccessFailure(exception))
        {
            return RobloxProcessVerificationStatus.Unavailable;
        }
    }

    private static bool IsExpectedProcessAccessFailure(Exception exception) =>
        exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException or ArgumentException or
            UnauthorizedAccessException;

    private sealed class Win32RobloxProcessLifetimePin :
        IRobloxProcessLifetimePin
    {
        private readonly object _sync = new();
        private readonly RobloxClientProcessIdentity _identity;
        private Process? _process;
        private volatile bool _isAlive;

        internal Win32RobloxProcessLifetimePin(
            Process process,
            RobloxClientProcessIdentity identity)
        {
            _process = process;
            _identity = identity;
            process.Exited += Process_Exited;
            process.EnableRaisingEvents = true;
            _isAlive = !process.HasExited;
        }

        public RobloxClientProcessIdentity Identity => _identity;

        // The Process.Exited callback owns the hot-path liveness state. An
        // authorization check can read it without taking the process lock or
        // issuing GetExitCodeProcess for every recorded input event.
        public bool IsExitObservedAlive => _isAlive;

        public bool IsRetainedProcessAlive
        {
            get
            {
                lock (_sync)
                {
                    if (!_isAlive || _process is not { } process)
                        return false;
                    try
                    {
                        var waitResult = WaitForSingleObject(
                            process.SafeHandle.DangerousGetHandle(),
                            milliseconds: 0);
                        if (waitResult == WaitTimeout)
                            return true;

                        // WAIT_OBJECT_0 means the exact retained process has
                        // exited. WAIT_FAILED is also fail-closed; neither may
                        // authorize a potentially reused PID/HWND pair.
                        _isAlive = false;
                        return false;
                    }
                    catch (Exception exception) when (
                        IsExpectedProcessAccessFailure(exception))
                    {
                        _isAlive = false;
                        return false;
                    }
                }
            }
        }

        public RobloxProcessVerificationStatus RevalidateIdentityAndToken(
            bool refreshExecutableTrust)
        {
            lock (_sync)
            {
                if (_process is null)
                    return RobloxProcessVerificationStatus.Unavailable;
                if (!IsRetainedProcessAlive)
                    return RobloxProcessVerificationStatus.Exited;
                var verification = VerifyPinnedProcess(
                    _process,
                    _identity,
                    refreshExecutableTrust,
                    // Acquisition already verified the executable signer
                    // before this exact kernel process handle was retained.
                    // Rehashing the large Roblox executable on every playback
                    // refresh stalls input without adding protection against
                    // PID reuse. A caller requesting a forced refresh still
                    // gets a complete file/signature verification.
                    verifyExecutableTrust: refreshExecutableTrust,
                    executableTrustHandle: null);
                if (verification == RobloxProcessVerificationStatus.Exited)
                    _isAlive = false;
                return verification;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_process is not { } process)
                    return;
                _process = null;
                _isAlive = false;
                process.Exited -= Process_Exited;
                process.Dispose();
            }
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            lock (_sync)
                _isAlive = false;
        }
    }

    public IReadOnlyList<nint> EnumerateTopLevelWindows(int processId)
    {
        lock (_windowEnumerationGate)
        {
            var now = _getUtcNow();
            if (now < _windowEnumerationSnapshotUtc ||
                now >= _windowEnumerationSnapshotExpiresUtc)
            {
                _windowEnumerationSnapshot =
                    _enumerateWindowsByProcess();
                _windowEnumerationSnapshotUtc = now;
                _windowEnumerationSnapshotExpiresUtc =
                    now + WindowEnumerationSnapshotLifetime;
            }

            return _windowEnumerationSnapshot.TryGetValue(
                processId,
                out var windows)
                ? windows
                : Array.Empty<nint>();
        }
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<nint>>
        EnumerateWindowsByProcess()
    {
        var windowsByProcess = new Dictionary<int, List<nint>>();
        _ = EnumWindows(
            (window, _) =>
            {
                var threadId = GetWindowThreadProcessId(
                    window,
                    out var processId);
                if (threadId == 0 ||
                    processId == 0 ||
                    processId > int.MaxValue)
                    return true;
                var processIdValue = (int)processId;
                if (!windowsByProcess.TryGetValue(
                        processIdValue,
                        out var windows))
                {
                    windows = [];
                    windowsByProcess.Add(processIdValue, windows);
                }
                windows.Add(window);
                return true;
            },
            nint.Zero);
        return windowsByProcess.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<nint>)pair.Value.ToArray());
    }

    public IReadOnlyList<nint> EnumerateTopLevelWindowsInZOrder()
    {
        var windows = new List<nint>();
        _ = EnumWindows(
            (window, _) =>
            {
                windows.Add(window);
                return true;
            },
            nint.Zero);
        return windows;
    }

    public bool IsUsableTopLevelWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            !IsWindow(windowHandle) ||
            !IsWindowVisible(windowHandle) ||
            GetAncestor(windowHandle, GetAncestorRoot) != windowHandle ||
            GetWindow(windowHandle, GetWindowOwner) != nint.Zero)
        {
            return false;
        }

        var cloaked = 0;
        return DwmGetWindowAttribute(
                   windowHandle,
                   DwmCloakedAttribute,
                   out cloaked,
                   sizeof(int)) != 0 ||
               cloaked == 0;
    }

    public int GetWindowProcessId(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId <= int.MaxValue ? (int)processId : 0;
    }

    public bool IsMinimized(nint windowHandle) =>
        IsIconic(windowHandle);

    public bool IsMaximized(nint windowHandle) =>
        IsZoomed(windowHandle);

    public bool IsFullscreen(nint windowHandle)
    {
        if (!TryGetNativeWindowRect(windowHandle, out var windowBounds))
            return false;
        var monitorHandle = MonitorFromWindow(
            windowHandle,
            MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (monitorHandle == nint.Zero ||
            !GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        var coversMonitor = Math.Abs(
                (long)windowBounds.Left - monitorInfo.MonitorArea.Left) <= 2 &&
            Math.Abs((long)windowBounds.Top - monitorInfo.MonitorArea.Top) <= 2 &&
            Math.Abs((long)windowBounds.Right - monitorInfo.MonitorArea.Right) <= 2 &&
            Math.Abs((long)windowBounds.Bottom - monitorInfo.MonitorArea.Bottom) <= 2;
        var style = GetWindowLongPtr(windowHandle, WindowStyleIndex).ToInt64();
        return coversMonitor && (style & WindowStylePopup) != 0;
    }

    public bool TryRestore(nint windowHandle) =>
        ShowWindowAsync(windowHandle, ShowNormalNoActivate);

    public bool TryGetGeometry(
        nint windowHandle,
        out RobloxPixelRect outerBounds,
        out RobloxPixelRect clientBounds)
    {
        outerBounds = default;
        clientBounds = default;
        if (!TryGetNativeWindowRect(windowHandle, out var outer) ||
            !GetClientRect(windowHandle, out var client))
        {
            return false;
        }

        var topLeft = new NativePoint(client.Left, client.Top);
        var bottomRight = new NativePoint(client.Right, client.Bottom);
        if (!ClientToScreen(windowHandle, ref topLeft) ||
            !ClientToScreen(windowHandle, ref bottomRight))
        {
            return false;
        }

        return TryConvertRect(outer, out outerBounds) &&
            TryConvertRect(
                new NativeRect(
                    topLeft.X,
                    topLeft.Y,
                    bottomRight.X,
                    bottomRight.Y),
                out clientBounds);
    }

    public bool TrySetBounds(
        nint windowHandle,
        RobloxPixelRect outerBounds) =>
        SetWindowPos(
            windowHandle,
            nint.Zero,
            outerBounds.Left,
            outerBounds.Top,
            outerBounds.Width,
            outerBounds.Height,
            SetWindowFlags);

    public bool IsTopmost(nint windowHandle) =>
        (GetWindowLongPtr(windowHandle, WindowExtendedStyleIndex).ToInt64() &
            WindowExtendedStyleTopmost) != 0;

    public bool TryDemoteTopmostWithoutActivation(nint windowHandle) =>
        SetWindowPos(
            windowHandle,
            WindowNotTopmost,
            0,
            0,
            0,
            0,
            SetZOrderFlags) &&
        SetWindowPos(
            windowHandle,
            WindowBottom,
            0,
            0,
            0,
            0,
            SetZOrderFlags);

    public bool TryApplyZOrderWithoutActivation(
        IReadOnlyList<RobloxWindowZOrderPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count == 0)
            return true;
        if (placements.Any(placement =>
                placement.Handle == nint.Zero ||
                placement.Handle == placement.InsertAfter))
        {
            return false;
        }

        var deferred = BeginDeferWindowPos(placements.Count);
        if (deferred == nint.Zero)
            return false;
        foreach (var placement in placements)
        {
            deferred = DeferWindowPos(
                deferred,
                placement.Handle,
                placement.InsertAfter,
                0,
                0,
                0,
                0,
                SetZOrderFlags);
            if (deferred == nint.Zero)
                return false;
        }

        return EndDeferWindowPos(deferred);
    }

    public bool TrySetForeground(nint windowHandle) =>
        SetForegroundWindow(windowHandle);

    public nint GetForegroundWindow() =>
        NativeGetForegroundWindow();

    public nint GetRootWindowAtPoint(int x, int y)
    {
        var hit = WindowFromPoint(new NativePoint(x, y));
        return hit == nint.Zero
            ? nint.Zero
            : GetAncestor(hit, GetAncestorRoot);
    }

    public IReadOnlyList<RobloxMonitor> GetMonitors()
    {
        var discovered = new List<DiscoveredMonitor>();
        _ = EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (monitorHandle, _, _, _) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                    DeviceName = string.Empty
                };
                if (!GetMonitorInfo(monitorHandle, ref info) ||
                    !TryConvertRect(info.MonitorArea, out var bounds) ||
                    !TryConvertRect(info.WorkArea, out var workArea))
                {
                    return true;
                }

                GetMonitorDpi(monitorHandle, out var dpiX, out var dpiY);
                discovered.Add(new DiscoveredMonitor(
                    info.DeviceName,
                    TryGetMonitorStableId(info.DeviceName),
                    (info.Flags & MonitorPrimaryFlag) != 0,
                    bounds,
                    workArea,
                    dpiX,
                    dpiY));
                return true;
            },
            nint.Zero);

        return discovered
            .OrderBy(monitor => monitor.WorkArea.Left)
            .ThenBy(monitor => monitor.WorkArea.Top)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select((monitor, index) => new RobloxMonitor(
                monitor.DeviceName,
                index,
                monitor.IsPrimary,
                monitor.Bounds,
                monitor.WorkArea,
                monitor.DpiX,
                monitor.DpiY)
            {
                StableId = monitor.StableId
            })
            .ToArray();
    }

    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private static bool TryGetNativeWindowRect(
        nint windowHandle,
        out NativeRect bounds) =>
        GetWindowRect(windowHandle, out bounds) &&
        bounds.Right > bounds.Left &&
        bounds.Bottom > bounds.Top;

    private static bool TryConvertRect(
        NativeRect source,
        out RobloxPixelRect result)
    {
        var width = (long)source.Right - source.Left;
        var height = (long)source.Bottom - source.Top;
        if (width <= 0 || width > int.MaxValue ||
            height <= 0 || height > int.MaxValue)
        {
            result = default;
            return false;
        }

        result = new RobloxPixelRect(
            source.Left,
            source.Top,
            (int)width,
            (int)height);
        return result.IsValid;
    }

    private static void GetMonitorDpi(
        nint monitorHandle,
        out uint dpiX,
        out uint dpiY)
    {
        dpiX = 96;
        dpiY = 96;
        try
        {
            if (GetDpiForMonitor(
                    monitorHandle,
                    0,
                    out var reportedX,
                    out var reportedY) == 0 &&
                reportedX is >= 48 and <= 960 &&
                reportedY is >= 48 and <= 960)
            {
                dpiX = reportedX;
                dpiY = reportedY;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows without shcore uses a conservative 96 DPI fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // Windows without GetDpiForMonitor uses the same fallback.
        }
    }

    private static string? TryGetMonitorStableId(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        var candidates = new List<string>();
        for (uint index = 0; index < MaximumMonitorDevicesPerAdapter; index++)
        {
            var device = new DisplayDevice
            {
                Size = (uint)Marshal.SizeOf<DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty
            };
            if (!EnumDisplayDevices(
                    deviceName,
                    index,
                    ref device,
                    GetDeviceInterfaceName))
            {
                break;
            }

            var stableId = device.DeviceId?.Trim();
            if ((device.StateFlags & DisplayDeviceActive) != 0 &&
                !string.IsNullOrWhiteSpace(stableId) &&
                !stableId.Any(char.IsControl))
            {
                candidates.Add(stableId);
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private sealed record DiscoveredMonitor(
        string DeviceName,
        string? StableId,
        bool IsPrimary,
        RobloxPixelRect Bounds,
        RobloxPixelRect WorkArea,
        uint DpiX,
        uint DpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(
        int left,
        int top,
        int right,
        int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal uint Size;
        internal NativeRect MonitorArea;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;

        internal uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    private delegate bool MonitorEnumerationCallback(
        nint monitor,
        nint deviceContext,
        nint monitorBounds,
        nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        nint handle,
        uint milliseconds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumerationCallback callback,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? deviceName,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitorHandle,
        ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint windowHandle,
        out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(
        nint windowHandle,
        out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(
        nint windowHandle,
        ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(
        nint windowHandle,
        int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint BeginDeferWindowPos(int windowCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint DeferWindowPos(
        nint deferredWindowPosition,
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(
        nint deferredWindowPosition);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        nint windowHandle,
        int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint windowHandle,
        int attribute,
        out int attributeValue,
        int attributeSize);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitorHandle,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
