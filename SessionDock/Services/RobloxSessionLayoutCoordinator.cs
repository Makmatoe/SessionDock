using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record RobloxSessionLayoutWindow(
    string Key,
    RobloxClientProcessIdentity Identity,
    nint Handle);

internal enum RobloxSessionLayoutStage
{
    InputValidation,
    InitialCapture,
    Planning,
    Move,
    FinalCapture,
    Normalize
}

internal sealed record RobloxSessionLayoutItemResult(
    string Key,
    RobloxClientProcessIdentity Identity,
    nint Handle,
    bool Success,
    RobloxSessionLayoutStage? FailureStage,
    RobloxWindowOperationStatus? OperationStatus,
    RobloxPixelRect RequestedBounds,
    RobloxPixelRect RealizedBounds,
    bool WasClamped,
    int GroupIndex,
    int CascadeIndex,
    int ZOrderFromBottom,
    NormalizedClientWindowPlacement? Placement,
    string? Error);

internal sealed record RobloxSessionLayoutResult(
    IReadOnlyList<RobloxSessionLayoutItemResult> Items,
    int GroupCount,
    bool ZOrderRequested,
    bool ZOrderApplied,
    string? GlobalError,
    string? ZOrderError)
{
    internal bool Success =>
        GlobalError is null &&
        Items.All(item => item.Success) &&
        (!ZOrderRequested || ZOrderApplied);

    internal bool HasPartialFailures =>
        Items.Any(item => item.Success) &&
        (Items.Any(item => !item.Success) ||
         (ZOrderRequested && !ZOrderApplied));
}

/// <summary>
/// Coordinates only verified Roblox identities and HWNDs. Window discovery,
/// identity checks, foreground policy, and native mutations remain in
/// <see cref="RobloxWindowService"/>; geometry decisions remain pure in
/// <see cref="RobloxWindowLayoutPolicy"/>.
/// </summary>
internal sealed class RobloxSessionLayoutCoordinator
{
    private const int NormalizedScale = 1_000_000;
    private const int MaximumLogicalDimension = 1_000_000;

    private readonly RobloxWindowService _windows;
    private readonly Func<RobloxExecutableTrustContext> _createTrustContext;

    internal RobloxSessionLayoutCoordinator()
        : this(new RobloxWindowService())
    {
    }

    internal RobloxSessionLayoutCoordinator(RobloxWindowService windows)
        : this(
            windows,
            static () => new RobloxExecutableTrustContext())
    {
    }

    internal RobloxSessionLayoutCoordinator(
        RobloxWindowService windows,
        Func<RobloxExecutableTrustContext> createTrustContext)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _createTrustContext = createTrustContext ??
            throw new ArgumentNullException(nameof(createTrustContext));
    }

    internal async Task<RobloxSessionLayoutResult> ArrangeAsync(
        IReadOnlyList<RobloxSessionLayoutWindow> orderedWindows,
        TemplatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWindows);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!TryValidateWindows(orderedWindows, out var inputError))
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.InputValidation,
                inputError);
        }
        if (!TryCreateOptions(preferences, out var options, out var optionError))
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.InputValidation,
                optionError);
        }
        if (orderedWindows.Count == 0)
            return EmptyResult();

        var monitors = _windows.GetMonitors();
        if (monitors.Count == 0)
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.Planning,
                "No usable monitor work area is available.");
        }

        var results = new Dictionary<string, RobloxSessionLayoutItemResult>(
            StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedWindow>(orderedWindows.Count);
        using var trustContext = CreateTrustContext();
        foreach (var window in orderedWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = await _windows.CaptureAsync(
                window.Identity,
                window.Handle,
                trustContext,
                cancellationToken);
            if (!captured.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InitialCapture,
                    captured.Status,
                    captured.Error ?? "The Roblox window could not be captured.");
                continue;
            }

            if (!TryGetFrameAt96Dpi(
                    captured.Window!,
                    monitors,
                    out var frame,
                    out var frameError))
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Planning,
                    RobloxWindowOperationStatus.GeometryUnavailable,
                    frameError);
                continue;
            }

            prepared.Add(new PreparedWindow(window, frame));
        }

        if (prepared.Count == 0)
        {
            return BuildResult(
                orderedWindows,
                results,
                groupCount: 0,
                zOrderRequested: false,
                zOrderApplied: false,
                globalError: "No verified Roblox window could be arranged.",
                zOrderError: null);
        }

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            prepared
                .Select(item => new RobloxCascadeWindow(
                    item.Window.Key,
                    item.FrameAt96Dpi,
                    default))
                .ToArray(),
            monitors,
            options);
        if (!plan.Success)
        {
            foreach (var item in prepared)
            {
                results[item.Window.Key] = FailedItem(
                    item.Window,
                    RobloxSessionLayoutStage.Planning,
                    operationStatus: null,
                    plan.Error ?? "The Roblox cascade could not be planned.");
            }

            return BuildResult(
                orderedWindows,
                results,
                groupCount: 0,
                zOrderRequested: false,
                zOrderApplied: false,
                globalError: plan.Error,
                zOrderError: null);
        }

        var windowsByKey = prepared.ToDictionary(
            item => item.Window.Key,
            item => item.Window,
            StringComparer.OrdinalIgnoreCase);
        foreach (var placement in plan.Placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = windowsByKey[placement.Key];
            var moved = await _windows.SetBoundsAsync(
                window.Identity,
                window.Handle,
                placement.OuterBounds,
                realizeTimeout: null,
                trustContext,
                cancellationToken);
            if (!moved.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Move,
                    moved.Status,
                    moved.Error ?? "The Roblox window could not be moved.",
                    placement);
                continue;
            }

            // Capture again after every requested move. Roblox can apply its
            // own minimum size or asynchronous geometry clamp after
            // SetWindowPos returns, and the saved template must contain those
            // realized pixels rather than the optimistic requested rectangle.
            var realized = await _windows.CaptureAsync(
                window.Identity,
                window.Handle,
                trustContext,
                cancellationToken);
            if (!realized.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.FinalCapture,
                    realized.Status,
                    realized.Error ??
                        "The realized Roblox window bounds could not be read.",
                    placement,
                    moved.Window?.OuterBounds ?? default,
                    moved.WasClamped);
                continue;
            }

            var normalized = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
                realized.Window!.OuterBounds,
                monitors);
            if (normalized is null)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Normalize,
                    RobloxWindowOperationStatus.GeometryUnavailable,
                    "The realized Roblox bounds could not be normalized.",
                    placement,
                    realized.Window.OuterBounds,
                    moved.WasClamped);
                continue;
            }

            results[window.Key] = SucceededItem(
                window,
                placement,
                realized.Window.OuterBounds,
                moved.WasClamped ||
                    realized.Window.OuterBounds != placement.OuterBounds,
                ToModelPlacement(normalized));
        }

        var zOrder = await ApplySuccessfulZOrderAsync(
            plan,
            orderedWindows,
            results,
            trustContext,
            cancellationToken);
        return BuildResult(
            orderedWindows,
            results,
            plan.GroupCount,
            zOrder.Requested,
            zOrder.Applied,
            globalError: null,
            zOrder.Error);
    }

    internal async Task<RobloxSessionLayoutResult> CapturePlacementsAsync(
        IReadOnlyList<RobloxSessionLayoutWindow> orderedWindows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWindows);
        if (!TryValidateWindows(orderedWindows, out var inputError))
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.InputValidation,
                inputError);
        }
        if (orderedWindows.Count == 0)
            return EmptyResult();

        var monitors = _windows.GetMonitors();
        if (monitors.Count == 0)
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.Normalize,
                "No usable monitor work area is available.");
        }

        var results = new Dictionary<string, RobloxSessionLayoutItemResult>(
            StringComparer.OrdinalIgnoreCase);
        using var trustContext = CreateTrustContext();
        foreach (var window in orderedWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = await _windows.CaptureAsync(
                window.Identity,
                window.Handle,
                trustContext,
                cancellationToken);
            if (!captured.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InitialCapture,
                    captured.Status,
                    captured.Error ?? "The Roblox window could not be captured.");
                continue;
            }

            if (captured.Window!.IsMinimized)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InitialCapture,
                    RobloxWindowOperationStatus.WindowUnavailable,
                    "Restore the Roblox window before saving its placement.",
                    realizedBounds: captured.Window.OuterBounds);
                continue;
            }

            var normalized = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
                captured.Window.OuterBounds,
                monitors);
            if (normalized is null)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Normalize,
                    RobloxWindowOperationStatus.GeometryUnavailable,
                    "The Roblox window bounds could not be normalized.",
                    realizedBounds: captured.Window.OuterBounds);
                continue;
            }

            results[window.Key] = new RobloxSessionLayoutItemResult(
                window.Key,
                window.Identity,
                window.Handle,
                true,
                null,
                RobloxWindowOperationStatus.Success,
                default,
                captured.Window.OuterBounds,
                false,
                -1,
                -1,
                -1,
                ToModelPlacement(normalized),
                null);
        }

        return BuildResult(
            orderedWindows,
            results,
            groupCount: 0,
            zOrderRequested: false,
            zOrderApplied: false,
            globalError: null,
            zOrderError: null);
    }

    internal async Task<RobloxSessionLayoutResult> RestorePlacementsAsync(
        IReadOnlyList<RobloxSessionLayoutWindow> orderedWindows,
        IReadOnlyDictionary<string, NormalizedClientWindowPlacement> placements,
        TemplatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWindows);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!TryValidateWindows(orderedWindows, out var inputError))
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.InputValidation,
                inputError);
        }
        if (!TryCreateOptions(preferences, out var options, out var optionError))
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.InputValidation,
                optionError);
        }
        if (orderedWindows.Count == 0)
            return EmptyResult();

        var placementLookup = placements
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Value).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var monitors = _windows.GetMonitors();
        if (monitors.Count == 0)
        {
            return FailedForAll(
                orderedWindows,
                RobloxSessionLayoutStage.Planning,
                "No usable monitor work area is available.");
        }

        var results = new Dictionary<string, RobloxSessionLayoutItemResult>(
            StringComparer.OrdinalIgnoreCase);
        var restoredPlacements = new List<RobloxCascadePlacement>();
        using var trustContext = CreateTrustContext();
        for (var index = 0; index < orderedWindows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = orderedWindows[index];
            if (!placementLookup.TryGetValue(window.Key, out var matches) ||
                matches.Length != 1 ||
                matches[0] is null)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InputValidation,
                    operationStatus: null,
                    "The saved placement is missing or ambiguous.");
                continue;
            }
            if (!TryToPolicyPlacement(
                    matches[0],
                    out var saved,
                    out var placementError))
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InputValidation,
                    operationStatus: null,
                    placementError ?? "The saved placement is invalid.");
                continue;
            }

            var captured = await _windows.CaptureAsync(
                window.Identity,
                window.Handle,
                trustContext,
                cancellationToken);
            if (!captured.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.InitialCapture,
                    captured.Status,
                    captured.Error ?? "The Roblox window could not be captured.");
                continue;
            }
            if (!TryGetFrameAt96Dpi(
                    captured.Window!,
                    monitors,
                    out var frame,
                    out var frameError))
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Planning,
                    RobloxWindowOperationStatus.GeometryUnavailable,
                    frameError);
                continue;
            }

            var minimumOuter = new RobloxPixelSize(
                checked(options.MinimumClientSizeAt96Dpi.Width +
                    frame.Left + frame.Right),
                checked(options.MinimumClientSizeAt96Dpi.Height +
                    frame.Top + frame.Bottom));
            var restored = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
                saved,
                monitors,
                minimumOuter);
            if (!restored.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Planning,
                    operationStatus: null,
                    restored.Error ?? "The saved placement could not be restored.");
                continue;
            }

            var planned = new RobloxCascadePlacement(
                window.Key,
                restored.Monitor!,
                restored.Bounds,
                GroupIndex: 0,
                CascadeIndex: index,
                ZOrderFromBottom: index);
            var moved = await _windows.SetBoundsAsync(
                window.Identity,
                window.Handle,
                restored.Bounds,
                realizeTimeout: null,
                trustContext,
                cancellationToken);
            if (!moved.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Move,
                    moved.Status,
                    moved.Error ?? "The Roblox window could not be restored.",
                    planned);
                continue;
            }

            var realized = await _windows.CaptureAsync(
                window.Identity,
                window.Handle,
                trustContext,
                cancellationToken);
            if (!realized.Success)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.FinalCapture,
                    realized.Status,
                    realized.Error ??
                        "The restored Roblox bounds could not be read.",
                    planned,
                    moved.Window?.OuterBounds ?? default,
                    moved.WasClamped);
                continue;
            }

            var finalWindow = realized.Window!;
            var safetyWasClamped = false;
            var safeRealizedBounds = RobloxWindowLayoutPolicy.FitToWorkArea(
                finalWindow.OuterBounds,
                restored.Monitor!.WorkArea);
            if (safeRealizedBounds != finalWindow.OuterBounds)
            {
                // Roblox can enforce a larger realized minimum than Windows was
                // asked for. Reposition that actual size so its right/bottom edge
                // remains in the chosen work area instead of accepting overflow.
                var refitted = await _windows.SetBoundsAsync(
                    window.Identity,
                    window.Handle,
                    safeRealizedBounds,
                    realizeTimeout: null,
                    trustContext,
                    cancellationToken);
                if (!refitted.Success)
                {
                    results[window.Key] = FailedItem(
                        window,
                        RobloxSessionLayoutStage.Move,
                        refitted.Status,
                        refitted.Error ??
                            "The realized Roblox window could not be clamped to the monitor work area.",
                        planned,
                        finalWindow.OuterBounds,
                        wasClamped: true);
                    continue;
                }

                finalWindow = refitted.Window!;
                safetyWasClamped = true;
                if (RobloxWindowLayoutPolicy.FitToWorkArea(
                        finalWindow.OuterBounds,
                        restored.Monitor.WorkArea) != finalWindow.OuterBounds)
                {
                    results[window.Key] = FailedItem(
                        window,
                        RobloxSessionLayoutStage.Move,
                        RobloxWindowOperationStatus.MoveFailed,
                        "Roblox enforced a window size larger than the selected monitor work area.",
                        planned,
                        finalWindow.OuterBounds,
                        wasClamped: true);
                    continue;
                }
            }

            var finalNormalized =
                RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
                    finalWindow.OuterBounds,
                    monitors);
            if (finalNormalized is null)
            {
                results[window.Key] = FailedItem(
                    window,
                    RobloxSessionLayoutStage.Normalize,
                    RobloxWindowOperationStatus.GeometryUnavailable,
                    "The restored Roblox bounds could not be normalized.",
                    planned,
                    finalWindow.OuterBounds,
                    moved.WasClamped || safetyWasClamped);
                continue;
            }

            results[window.Key] = SucceededItem(
                window,
                planned,
                finalWindow.OuterBounds,
                moved.WasClamped ||
                    safetyWasClamped ||
                    finalWindow.OuterBounds != planned.OuterBounds,
                ToModelPlacement(finalNormalized));
            restoredPlacements.Add(planned);
        }

        var restorePlan = new RobloxCascadeLayoutPlan(
            true,
            restoredPlacements,
            restoredPlacements.Count == 0 ? 0 : 1,
            null);
        var zOrder = await ApplySuccessfulZOrderAsync(
            restorePlan,
            orderedWindows,
            results,
            trustContext,
            cancellationToken);
        return BuildResult(
            orderedWindows,
            results,
            restorePlan.GroupCount,
            zOrder.Requested,
            zOrder.Applied,
            globalError: null,
            zOrder.Error);
    }

    private RobloxExecutableTrustContext CreateTrustContext() =>
        _createTrustContext() ?? throw new InvalidOperationException(
            "The executable trust context factory returned null.");

    private async Task<ZOrderOutcome> ApplySuccessfulZOrderAsync(
        RobloxCascadeLayoutPlan plan,
        IReadOnlyList<RobloxSessionLayoutWindow> orderedWindows,
        Dictionary<string, RobloxSessionLayoutItemResult> results,
        RobloxExecutableTrustContext trustContext,
        CancellationToken cancellationToken)
    {
        var successfulKeys = results
            .Where(item => item.Value.Success)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (successfulKeys.Count == 0)
            return new ZOrderOutcome(false, false, null);

        var filteredPlacements = plan.Placements
            .Where(item => successfulKeys.Contains(item.Key))
            .ToArray();
        var filteredPlan = new RobloxCascadeLayoutPlan(
            true,
            filteredPlacements,
            plan.GroupCount,
            null);
        var targets = orderedWindows
            .Where(window => successfulKeys.Contains(window.Key))
            .Select(window => new RobloxWindowZOrderTarget(
                window.Key,
                window.Identity,
                window.Handle))
            .ToArray();
        var applied = await _windows.ApplyZOrderAsync(
            filteredPlan,
            targets,
            trustContext,
            cancellationToken);
        return new ZOrderOutcome(true, applied.Success, applied.Error);
    }

    private static bool TryGetFrameAt96Dpi(
        RobloxWindowSnapshot snapshot,
        IReadOnlyList<RobloxMonitor> monitors,
        out RobloxWindowFrameInsets frame,
        out string error)
    {
        frame = default;
        var normalized = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
            snapshot.OuterBounds,
            monitors);
        var monitor = normalized is null
            ? null
            : RobloxWindowLayoutPolicy.SelectSavedMonitor(
                normalized,
                monitors);
        if (monitor is null)
        {
            error = "The Roblox window is not associated with a usable monitor.";
            return false;
        }

        var left = Math.Max(
            0L,
            (long)snapshot.ClientBounds.Left - snapshot.OuterBounds.Left);
        var top = Math.Max(
            0L,
            (long)snapshot.ClientBounds.Top - snapshot.OuterBounds.Top);
        var right = Math.Max(
            0L,
            (long)snapshot.OuterBounds.Right - snapshot.ClientBounds.Right);
        var bottom = Math.Max(
            0L,
            (long)snapshot.OuterBounds.Bottom - snapshot.ClientBounds.Bottom);
        if (left > MaximumLogicalDimension ||
            top > MaximumLogicalDimension ||
            right > MaximumLogicalDimension ||
            bottom > MaximumLogicalDimension)
        {
            error = "The Roblox window frame geometry is invalid.";
            return false;
        }

        frame = new RobloxWindowFrameInsets(
            To96Dpi((int)left, monitor.DpiX),
            To96Dpi((int)top, monitor.DpiY),
            To96Dpi((int)right, monitor.DpiX),
            To96Dpi((int)bottom, monitor.DpiY));
        error = string.Empty;
        return true;
    }

    private static bool TryCreateOptions(
        TemplatePreferences preferences,
        out RobloxCascadeLayoutOptions options,
        out string error)
    {
        options = RobloxCascadeLayoutOptions.Default;
        if (!TryRoundLogicalPixel(preferences.TargetWidth, out var targetWidth) ||
            !TryRoundLogicalPixel(preferences.TargetHeight, out var targetHeight) ||
            !TryRoundLogicalPixel(preferences.MinimumWidth, out var minimumWidth) ||
            !TryRoundLogicalPixel(preferences.MinimumHeight, out var minimumHeight) ||
            !TryRoundLogicalPixel(preferences.RevealX, out var revealX) ||
            !TryRoundLogicalPixel(preferences.RevealY, out var revealY))
        {
            error = "The template window dimensions must be finite positive logical pixels.";
            return false;
        }

        options = new RobloxCascadeLayoutOptions(
            new RobloxPixelSize(targetWidth, targetHeight),
            new RobloxPixelSize(minimumWidth, minimumHeight),
            revealX,
            revealY,
            RobloxCascadeLayoutOptions.Default.MarginAt96Dpi)
        {
            PreferredMonitorDeviceName =
                string.IsNullOrWhiteSpace(preferences.PreferredMonitorDeviceName)
                    ? null
                    : preferences.PreferredMonitorDeviceName.Trim()
        };
        error = string.Empty;
        return true;
    }

    private static bool TryRoundLogicalPixel(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) ||
            value <= 0 || value > MaximumLogicalDimension)
        {
            return false;
        }

        result = checked((int)Math.Round(
            value,
            MidpointRounding.AwayFromZero));
        return result > 0;
    }

    private static bool TryToPolicyPlacement(
        NormalizedClientWindowPlacement placement,
        out RobloxNormalizedWindowBounds result,
        out string? error)
    {
        result = default!;
        error = null;
        if (!double.IsFinite(placement.Left) ||
            !double.IsFinite(placement.Top) ||
            !double.IsFinite(placement.Width) ||
            !double.IsFinite(placement.Height) ||
            placement.Width <= 0 || placement.Height <= 0)
        {
            error = "The saved placement contains invalid normalized bounds.";
            return false;
        }

        var width = Math.Clamp(placement.Width, 0.000001, 1);
        var height = Math.Clamp(placement.Height, 0.000001, 1);
        var left = Math.Clamp(placement.Left, 0, 1 - width);
        var top = Math.Clamp(placement.Top, 0, 1 - height);
        var monitorStableId = placement.MonitorStableId?.Trim();
        if (placement.MonitorStableId is not null &&
            (string.IsNullOrEmpty(monitorStableId) ||
             monitorStableId.Length >
                 SessionTemplatePolicy.MaximumMonitorStableIdLength ||
             monitorStableId.Any(char.IsControl)))
        {
            error = "The saved placement contains an invalid monitor identity.";
            return false;
        }

        result = new RobloxNormalizedWindowBounds(
            string.IsNullOrWhiteSpace(placement.MonitorDeviceName)
                ? null
                : placement.MonitorDeviceName.Trim(),
            Math.Max(0, placement.MonitorIndex),
            ToMillionths(left),
            ToMillionths(top),
            ToMillionths(width),
            ToMillionths(height))
        {
            PreferredMonitorStableId = monitorStableId
        };
        return true;
    }

    private static NormalizedClientWindowPlacement ToModelPlacement(
        RobloxNormalizedWindowBounds placement) => new()
        {
            MonitorStableId = placement.PreferredMonitorStableId,
            MonitorDeviceName = placement.PreferredMonitorDeviceName,
            MonitorIndex = placement.PreferredMonitorIndex,
            Left = placement.LeftMillionths / (double)NormalizedScale,
            Top = placement.TopMillionths / (double)NormalizedScale,
            Width = placement.WidthMillionths / (double)NormalizedScale,
            Height = placement.HeightMillionths / (double)NormalizedScale
        };

    private static int ToMillionths(double value) =>
        (int)Math.Clamp(
            Math.Round(
                value * NormalizedScale,
                MidpointRounding.AwayFromZero),
            0,
            NormalizedScale);

    private static int To96Dpi(int physicalPixels, uint dpi)
    {
        var effectiveDpi = dpi is >= 48 and <= 960 ? dpi : 96;
        return checked((int)(((long)physicalPixels * 96 +
            effectiveDpi / 2L) / effectiveDpi));
    }

    private static bool TryValidateWindows(
        IReadOnlyList<RobloxSessionLayoutWindow> windows,
        out string error)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (window is null ||
                string.IsNullOrWhiteSpace(window.Key) ||
                window.Identity is null ||
                window.Handle == nint.Zero ||
                !keys.Add(window.Key))
            {
                error =
                    "Every Roblox layout window needs a unique key, exact identity, and nonzero HWND.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static RobloxSessionLayoutItemResult SucceededItem(
        RobloxSessionLayoutWindow window,
        RobloxCascadePlacement placement,
        RobloxPixelRect realizedBounds,
        bool wasClamped,
        NormalizedClientWindowPlacement normalized) =>
        new(
            window.Key,
            window.Identity,
            window.Handle,
            true,
            null,
            RobloxWindowOperationStatus.Success,
            placement.OuterBounds,
            realizedBounds,
            wasClamped,
            placement.GroupIndex,
            placement.CascadeIndex,
            placement.ZOrderFromBottom,
            normalized,
            null);

    private static RobloxSessionLayoutItemResult FailedItem(
        RobloxSessionLayoutWindow window,
        RobloxSessionLayoutStage stage,
        RobloxWindowOperationStatus? operationStatus,
        string error,
        RobloxCascadePlacement? placement = null,
        RobloxPixelRect realizedBounds = default,
        bool wasClamped = false) =>
        new(
            window.Key,
            window.Identity,
            window.Handle,
            false,
            stage,
            operationStatus,
            placement?.OuterBounds ?? default,
            realizedBounds,
            wasClamped,
            placement?.GroupIndex ?? -1,
            placement?.CascadeIndex ?? -1,
            placement?.ZOrderFromBottom ?? -1,
            null,
            error);

    private static RobloxSessionLayoutResult FailedForAll(
        IReadOnlyList<RobloxSessionLayoutWindow> windows,
        RobloxSessionLayoutStage stage,
        string error) =>
        new(
            windows
                .Where(window => window is not null)
                .Select(window => FailedItem(
                    window,
                    stage,
                    operationStatus: null,
                    error))
                .ToArray(),
            0,
            false,
            false,
            error,
            null);

    private static RobloxSessionLayoutResult BuildResult(
        IReadOnlyList<RobloxSessionLayoutWindow> orderedWindows,
        Dictionary<string, RobloxSessionLayoutItemResult> results,
        int groupCount,
        bool zOrderRequested,
        bool zOrderApplied,
        string? globalError,
        string? zOrderError) =>
        new(
            orderedWindows
                .Where(window => results.ContainsKey(window.Key))
                .Select(window => results[window.Key])
                .ToArray(),
            groupCount,
            zOrderRequested,
            zOrderApplied,
            globalError,
            zOrderError);

    private static RobloxSessionLayoutResult EmptyResult() =>
        new([], 0, false, false, null, null);

    private sealed record PreparedWindow(
        RobloxSessionLayoutWindow Window,
        RobloxWindowFrameInsets FrameAt96Dpi);

    private readonly record struct ZOrderOutcome(
        bool Requested,
        bool Applied,
        string? Error);
}
