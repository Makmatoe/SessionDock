using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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
        bool forceTrustRefresh);

    RobloxProcessVerificationStatus TryPinProcessLifetime(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
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

    internal async Task<RobloxWindowOperationResult> WaitForWindowAsync(
        RobloxClientProcessIdentity identity,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var effectiveTimeout = ValidateTimeout(
            timeout ?? DefaultWindowTimeout,
            nameof(timeout));
        var preliminary = _native.VerifyProcess(
            identity,
            forceTrustRefresh: false);
        if (preliminary != RobloxProcessVerificationStatus.Verified)
            return VerificationFailure(preliminary);

        var deadline = _native.UtcNow + effectiveTimeout;
        var requiredStability = GetRequiredStability(
            WindowReadinessStability,
            effectiveTimeout);
        var sawFullscreen = false;
        var sawAmbiguousMainWindow = false;
        CandidateWindow? stableCandidate = null;
        var stableSince = _native.UtcNow;
        var stableReads = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentVerification = _native.VerifyProcess(
                identity,
                forceTrustRefresh: false);
            if (currentVerification != RobloxProcessVerificationStatus.Verified)
                return VerificationFailure(currentVerification);

            var candidates = _native.EnumerateTopLevelWindows(identity.ProcessId)
                .Where(window =>
                    window != nint.Zero &&
                    _native.GetWindowProcessId(window) == identity.ProcessId &&
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

            sawFullscreen |= candidates.Any(candidate => candidate.IsFullscreen);
            var viable = candidates
                .Where(item => !item.IsFullscreen)
                .OrderByDescending(item => Area(item.OuterBounds))
                .ToArray();
            CandidateWindow? candidate = null;
            if (viable.Length > 0)
            {
                var largestArea = Area(viable[0].OuterBounds);
                var equallyViable = viable
                    .TakeWhile(item => Area(item.OuterBounds) == largestArea)
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
                var finalVerification = _native.VerifyProcess(
                    identity,
                    forceTrustRefresh: true);
                if (finalVerification != RobloxProcessVerificationStatus.Verified)
                    return VerificationFailure(finalVerification);
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

    internal async Task<RobloxWindowOperationResult> CaptureAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var validated = ValidateWindow(
            identity,
            windowHandle,
            forceTrustRefresh: true);
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

    internal async Task<RobloxWindowOperationResult> SetBoundsAsync(
        RobloxClientProcessIdentity identity,
        nint windowHandle,
        RobloxPixelRect requestedOuterBounds,
        TimeSpan? realizeTimeout = null,
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
            var repeatedValidation = ValidateWindow(identity, windowHandle);
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
            var repeatedValidation = ValidateWindow(identity, windowHandle);
            if (repeatedValidation is not null)
                return repeatedValidation;

            var foreground = _native.GetForegroundWindow();
            if (foreground == windowHandle &&
                _native.GetWindowProcessId(foreground) == identity.ProcessId)
            {
                var finalVerification = _native.VerifyProcess(
                    identity,
                    forceTrustRefresh: true);
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

    internal Task<RobloxWindowZOrderResult> ApplyZOrderAsync(
        RobloxCascadeLayoutPlan plan,
        IReadOnlyList<RobloxWindowZOrderTarget> targets,
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
            var validated = ValidateWindow(
                target.Identity,
                target.Handle,
                forceTrustRefresh: true);
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
            var validated = ValidateWindow(identity, windowHandle);
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
        var verification = _native.VerifyProcess(
            identity,
            forceTrustRefresh);
        if (verification != RobloxProcessVerificationStatus.Verified)
            return VerificationFailure(verification);
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

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public RobloxProcessVerificationStatus VerifyProcess(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh)
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
            return VerifyPinnedProcess(process, identity, forceTrustRefresh);
    }

    public RobloxProcessVerificationStatus TryPinProcessLifetime(
        RobloxClientProcessIdentity identity,
        bool forceTrustRefresh,
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
                forceTrustRefresh);
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
        bool forceTrustRefresh)
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
            if (!RobloxExecutableTrust.IsTrustedPlayerPath(
                    executablePath,
                    forceTrustRefresh))
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

        internal Win32RobloxProcessLifetimePin(
            Process process,
            RobloxClientProcessIdentity identity)
        {
            _process = process;
            _identity = identity;
        }

        public RobloxClientProcessIdentity Identity => _identity;

        public bool IsAlive
        {
            get
            {
                lock (_sync)
                {
                    try
                    {
                        return _process is not null && !_process.HasExited;
                    }
                    catch (Exception exception) when (
                        IsExpectedProcessAccessFailure(exception))
                    {
                        return false;
                    }
                }
            }
        }

        public RobloxProcessVerificationStatus VerifyIdentity(
            bool forceTrustRefresh)
        {
            lock (_sync)
            {
                return _process is null
                    ? RobloxProcessVerificationStatus.Unavailable
                    : VerifyPinnedProcess(
                        _process,
                        _identity,
                        forceTrustRefresh);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _process?.Dispose();
                _process = null;
            }
        }
    }

    public IReadOnlyList<nint> EnumerateTopLevelWindows(int processId)
    {
        var windows = new List<nint>();
        _ = EnumWindows(
            (window, _) =>
            {
                if (GetWindowProcessId(window) == processId)
                    windows.Add(window);
                return true;
            },
            nint.Zero);
        return windows;
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
