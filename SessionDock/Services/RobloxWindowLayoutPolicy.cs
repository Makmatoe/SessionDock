namespace SessionDock.Services;

internal readonly record struct RobloxPixelSize(int Width, int Height)
{
    internal bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct RobloxPixelRect(
    int Left,
    int Top,
    int Width,
    int Height)
{
    internal int Right => checked(Left + Width);
    internal int Bottom => checked(Top + Height);
    internal bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        (long)Left + Width <= int.MaxValue &&
        (long)Top + Height <= int.MaxValue;
}

internal readonly record struct RobloxWindowFrameInsets(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    internal bool IsValid =>
        Left >= 0 && Top >= 0 && Right >= 0 && Bottom >= 0;
}

internal sealed record RobloxMonitor(
    string DeviceName,
    int Index,
    bool IsPrimary,
    RobloxPixelRect Bounds,
    RobloxPixelRect WorkArea,
    uint DpiX,
    uint DpiY)
{
    // The per-monitor device-interface path is stable across logical
    // \\.\DISPLAYn reassignment. It can be unavailable for virtual/remote
    // displays, so the legacy GDI name remains as a compatibility fallback.
    internal string? StableId { get; init; }
}

internal sealed record RobloxCascadeWindow(
    string Key,
    RobloxWindowFrameInsets FrameInsetsAt96Dpi,
    RobloxPixelSize ObservedMinimumOuterSizeAt96Dpi);

internal sealed record RobloxCascadeLayoutOptions(
    RobloxPixelSize TargetClientSizeAt96Dpi,
    RobloxPixelSize MinimumClientSizeAt96Dpi,
    int RevealXAt96Dpi,
    int RevealYAt96Dpi,
    int MarginAt96Dpi)
{
    internal string? PreferredMonitorDeviceName { get; init; }

    internal static RobloxCascadeLayoutOptions Default { get; } = new(
        new RobloxPixelSize(800, 600),
        new RobloxPixelSize(320, 240),
        RevealXAt96Dpi: 48,
        RevealYAt96Dpi: 48,
        MarginAt96Dpi: 16);
}

internal sealed record RobloxCascadePlacement(
    string Key,
    RobloxMonitor Monitor,
    RobloxPixelRect OuterBounds,
    int GroupIndex,
    int CascadeIndex,
    int ZOrderFromBottom);

internal sealed record RobloxCascadeLayoutPlan(
    bool Success,
    IReadOnlyList<RobloxCascadePlacement> Placements,
    int GroupCount,
    string? Error)
{
    internal bool RequiresGroupSwitch => GroupCount > 1;

    internal static RobloxCascadeLayoutPlan Failed(string error) =>
        new(false, [], 0, error);
}

internal sealed record RobloxNormalizedWindowBounds(
    string? PreferredMonitorDeviceName,
    int PreferredMonitorIndex,
    int LeftMillionths,
    int TopMillionths,
    int WidthMillionths,
    int HeightMillionths)
{
    internal string? PreferredMonitorStableId { get; init; }
}

internal sealed record RobloxSavedBoundsRestoreResult(
    bool Success,
    RobloxMonitor? Monitor,
    RobloxPixelRect Bounds,
    string? Error)
{
    internal static RobloxSavedBoundsRestoreResult Failed(string error) =>
        new(false, null, default, error);
}

internal static class RobloxWindowLayoutPolicy
{
    private const int NormalizedScale = 1_000_000;
    private const int MaximumDimension = 1_000_000;
    private const int MaximumWindowCount = 256;
    private const int MaximumMonitorStableIdLength = 512;

    internal static RobloxCascadeLayoutPlan CreateCascade(
        IReadOnlyList<RobloxCascadeWindow> windows,
        IReadOnlyList<RobloxMonitor> monitors,
        RobloxCascadeLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(options);

        if (windows.Count == 0)
            return new RobloxCascadeLayoutPlan(true, [], 0, null);
        if (windows.Count > MaximumWindowCount)
        {
            return RobloxCascadeLayoutPlan.Failed(
                $"A cascade can contain at most {MaximumWindowCount} windows.");
        }
        if (!TryValidateOptions(options, out var optionError))
            return RobloxCascadeLayoutPlan.Failed(optionError);

        var usableMonitors = monitors
            .Where(IsUsableMonitor)
            .OrderBy(monitor => string.Equals(
                monitor.DeviceName,
                options.PreferredMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(monitor => monitor.Index)
            .ThenBy(monitor => monitor.WorkArea.Left)
            .ThenBy(monitor => monitor.WorkArea.Top)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (usableMonitors.Length == 0)
            return RobloxCascadeLayoutPlan.Failed("No usable monitor work area is available.");

        foreach (var window in windows)
        {
            if (window is null ||
                string.IsNullOrWhiteSpace(window.Key) ||
                !window.FrameInsetsAt96Dpi.IsValid ||
                window.FrameInsetsAt96Dpi.Left > MaximumDimension ||
                window.FrameInsetsAt96Dpi.Top > MaximumDimension ||
                window.FrameInsetsAt96Dpi.Right > MaximumDimension ||
                window.FrameInsetsAt96Dpi.Bottom > MaximumDimension ||
                !(window.ObservedMinimumOuterSizeAt96Dpi == default ||
                  window.ObservedMinimumOuterSizeAt96Dpi.IsValid) ||
                window.ObservedMinimumOuterSizeAt96Dpi.Width > MaximumDimension ||
                window.ObservedMinimumOuterSizeAt96Dpi.Height > MaximumDimension)
            {
                return RobloxCascadeLayoutPlan.Failed(
                    "A window has invalid cascade geometry.");
            }
        }

        var placements = new List<RobloxCascadePlacement>(windows.Count);
        var nextWindow = 0;
        var groupIndex = 0;

        // One group is one visible diagonal across every available monitor. If
        // the configured/observed minimum prevents all windows from fitting,
        // another deterministic group reuses the monitors. The coordinator can
        // expose one group at a time instead of silently shrinking below minima.
        while (nextWindow < windows.Count)
        {
            var groupStart = nextWindow;
            foreach (var monitor in usableMonitors)
            {
                if (nextWindow >= windows.Count)
                    break;

                var capacity = FindPrefixCapacity(
                    windows,
                    nextWindow,
                    monitor,
                    options);
                if (capacity == 0)
                    continue;

                AddMonitorCascade(
                    windows,
                    nextWindow,
                    capacity,
                    monitor,
                    options,
                    groupIndex,
                    placements);
                nextWindow += capacity;
            }

            if (nextWindow == groupStart)
            {
                return RobloxCascadeLayoutPlan.Failed(
                    "The configured or Roblox-observed minimum window size does not fit on any monitor.");
            }

            groupIndex++;
        }

        return new RobloxCascadeLayoutPlan(
            true,
            placements,
            groupIndex,
            null);
    }

    internal static RobloxNormalizedWindowBounds? CaptureNormalizedBounds(
        RobloxPixelRect outerBounds,
        IReadOnlyList<RobloxMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (!IsSaneRect(outerBounds))
            return null;

        var monitor = SelectMonitorForBounds(outerBounds, monitors);
        if (monitor is null)
            return null;

        var fitted = FitToWorkArea(outerBounds, monitor.WorkArea);
        return new RobloxNormalizedWindowBounds(
            monitor.DeviceName,
            monitor.Index,
            Normalize(fitted.Left - monitor.WorkArea.Left, monitor.WorkArea.Width),
            Normalize(fitted.Top - monitor.WorkArea.Top, monitor.WorkArea.Height),
            Normalize(fitted.Width, monitor.WorkArea.Width),
            Normalize(fitted.Height, monitor.WorkArea.Height))
        {
            PreferredMonitorStableId = monitor.StableId
        };
    }

    internal static RobloxSavedBoundsRestoreResult RestoreNormalizedBounds(
        RobloxNormalizedWindowBounds saved,
        IReadOnlyList<RobloxMonitor> monitors,
        RobloxPixelSize minimumOuterSizeAt96Dpi)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(monitors);
        if (!minimumOuterSizeAt96Dpi.IsValid ||
            !IsValidNormalized(saved))
        {
            return RobloxSavedBoundsRestoreResult.Failed(
                "The saved window bounds are invalid.");
        }

        var monitor = SelectSavedMonitor(saved, monitors);
        if (monitor is null)
        {
            return RobloxSavedBoundsRestoreResult.Failed(
                "No usable monitor work area is available.");
        }

        var minimumWidth = Scale96(minimumOuterSizeAt96Dpi.Width, monitor.DpiX);
        var minimumHeight = Scale96(minimumOuterSizeAt96Dpi.Height, monitor.DpiY);
        if (minimumWidth > monitor.WorkArea.Width ||
            minimumHeight > monitor.WorkArea.Height)
        {
            return RobloxSavedBoundsRestoreResult.Failed(
                "The minimum window size does not fit on the selected monitor.");
        }

        var width = Math.Clamp(
            Denormalize(saved.WidthMillionths, monitor.WorkArea.Width),
            minimumWidth,
            monitor.WorkArea.Width);
        var height = Math.Clamp(
            Denormalize(saved.HeightMillionths, monitor.WorkArea.Height),
            minimumHeight,
            monitor.WorkArea.Height);
        var left = monitor.WorkArea.Left +
            Denormalize(saved.LeftMillionths, monitor.WorkArea.Width);
        var top = monitor.WorkArea.Top +
            Denormalize(saved.TopMillionths, monitor.WorkArea.Height);

        var restored = FitToWorkArea(
            new RobloxPixelRect(left, top, width, height),
            monitor.WorkArea);
        return new RobloxSavedBoundsRestoreResult(
            true,
            monitor,
            restored,
            null);
    }

    internal static RobloxMonitor? SelectSavedMonitor(
        RobloxNormalizedWindowBounds saved,
        IReadOnlyList<RobloxMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(monitors);
        var usable = monitors.Where(IsUsableMonitor).ToArray();

        if (!string.IsNullOrWhiteSpace(saved.PreferredMonitorStableId))
        {
            var stable = usable.FirstOrDefault(monitor => string.Equals(
                monitor.StableId,
                saved.PreferredMonitorStableId,
                StringComparison.OrdinalIgnoreCase));
            if (stable is not null)
                return stable;

            // A stable identity was captured, so a reused \\.\DISPLAYn name
            // must not be trusted as the same physical monitor.
            return usable.FirstOrDefault(monitor => monitor.IsPrimary) ??
                usable.OrderBy(monitor => monitor.Index).FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(saved.PreferredMonitorDeviceName))
        {
            var preferred = usable.FirstOrDefault(monitor => string.Equals(
                monitor.DeviceName,
                saved.PreferredMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;

            // A GDI display name is meaningful only while that named display
            // is connected. Reusing its old spatial ordinal can silently send
            // a saved primary-monitor window to a different secondary display
            // after docking, undocking, or changing monitor topology.
            return usable.FirstOrDefault(monitor => monitor.IsPrimary) ??
                usable.OrderBy(monitor => monitor.Index).FirstOrDefault();
        }

        // Placements written before monitor names were captured retain their
        // ordinal behavior for compatibility. Named placements never take
        // this branch.
        var ordinal = usable.FirstOrDefault(monitor =>
            monitor.Index == saved.PreferredMonitorIndex);
        if (ordinal is not null)
            return ordinal;

        return usable.FirstOrDefault(monitor => monitor.IsPrimary) ??
            usable.OrderBy(monitor => monitor.Index).FirstOrDefault();
    }

    private static void AddMonitorCascade(
        IReadOnlyList<RobloxCascadeWindow> windows,
        int start,
        int count,
        RobloxMonitor monitor,
        RobloxCascadeLayoutOptions options,
        int groupIndex,
        List<RobloxCascadePlacement> output)
    {
        var geometry = CalculateGroupGeometry(
            windows,
            start,
            count,
            monitor,
            options);
        var left = monitor.WorkArea.Left + geometry.MarginX;
        var top = monitor.WorkArea.Top + geometry.MarginY;

        for (var index = 0; index < count; index++)
        {
            var windowIndex = start + index;
            output.Add(new RobloxCascadePlacement(
                windows[windowIndex].Key,
                monitor,
                new RobloxPixelRect(
                    checked(left + index * geometry.StepX),
                    checked(top + index * geometry.StepY),
                    geometry.OuterWidth,
                    geometry.OuterHeight),
                groupIndex,
                index,
                windowIndex));
        }
    }

    private static int FindPrefixCapacity(
        IReadOnlyList<RobloxCascadeWindow> windows,
        int start,
        RobloxMonitor monitor,
        RobloxCascadeLayoutOptions options)
    {
        var capacity = 0;
        for (var count = 1; start + count <= windows.Count; count++)
        {
            var geometry = CalculateGroupGeometry(
                windows,
                start,
                count,
                monitor,
                options);
            if (!geometry.Fits)
                break;
            capacity = count;
        }

        return capacity;
    }

    private static CascadeGeometry CalculateGroupGeometry(
        IReadOnlyList<RobloxCascadeWindow> windows,
        int start,
        int count,
        RobloxMonitor monitor,
        RobloxCascadeLayoutOptions options)
    {
        var dpiX = monitor.DpiX;
        var dpiY = monitor.DpiY;
        var marginX = Scale96(options.MarginAt96Dpi, dpiX);
        var marginY = Scale96(options.MarginAt96Dpi, dpiY);

        var maximumHorizontalFrame = 0;
        var maximumVerticalFrame = 0;
        var targetOuterWidth = 0;
        var targetOuterHeight = 0;
        var minimumOuterWidth = 0;
        var minimumOuterHeight = 0;

        for (var index = 0; index < count; index++)
        {
            var window = windows[start + index];
            var frame = window.FrameInsetsAt96Dpi;
            var frameWidth = Scale96(frame.Left + frame.Right, dpiX);
            var frameHeight = Scale96(frame.Top + frame.Bottom, dpiY);
            maximumHorizontalFrame = Math.Max(
                maximumHorizontalFrame,
                Scale96(Math.Max(frame.Left, frame.Right), dpiX));
            maximumVerticalFrame = Math.Max(
                maximumVerticalFrame,
                Scale96(Math.Max(frame.Top, frame.Bottom), dpiY));

            targetOuterWidth = Math.Max(
                targetOuterWidth,
                Scale96(options.TargetClientSizeAt96Dpi.Width, dpiX) +
                frameWidth);
            targetOuterHeight = Math.Max(
                targetOuterHeight,
                Scale96(options.TargetClientSizeAt96Dpi.Height, dpiY) +
                frameHeight);
            minimumOuterWidth = Math.Max(
                minimumOuterWidth,
                Math.Max(
                    Scale96(options.MinimumClientSizeAt96Dpi.Width, dpiX) +
                    frameWidth,
                    window.ObservedMinimumOuterSizeAt96Dpi.IsValid
                        ? Scale96(
                            window.ObservedMinimumOuterSizeAt96Dpi.Width,
                            dpiX)
                        : 0));
            minimumOuterHeight = Math.Max(
                minimumOuterHeight,
                Math.Max(
                    Scale96(options.MinimumClientSizeAt96Dpi.Height, dpiY) +
                    frameHeight,
                    window.ObservedMinimumOuterSizeAt96Dpi.IsValid
                        ? Scale96(
                            window.ObservedMinimumOuterSizeAt96Dpi.Height,
                            dpiY)
                        : 0));
        }

        targetOuterWidth = Math.Max(targetOuterWidth, minimumOuterWidth);
        targetOuterHeight = Math.Max(targetOuterHeight, minimumOuterHeight);

        // Reveal values describe usable client-area pixels, not a fragile
        // percentage or a resize border. Including the larger opposing frame
        // inset keeps a human-sized client patch exposed after any window in
        // the diagonal is brought to the foreground.
        var stepX = checked(
            Scale96(options.RevealXAt96Dpi, dpiX) +
            maximumHorizontalFrame);
        var stepY = checked(
            Scale96(options.RevealYAt96Dpi, dpiY) +
            maximumVerticalFrame);
        var availableWidth = (long)monitor.WorkArea.Width -
            (2L * marginX) - ((long)(count - 1) * stepX);
        var availableHeight = (long)monitor.WorkArea.Height -
            (2L * marginY) - ((long)(count - 1) * stepY);
        var fits = availableWidth >= minimumOuterWidth &&
            availableHeight >= minimumOuterHeight;
        var outerWidth = fits
            ? (int)Math.Min(targetOuterWidth, availableWidth)
            : 0;
        var outerHeight = fits
            ? (int)Math.Min(targetOuterHeight, availableHeight)
            : 0;

        return new CascadeGeometry(
            fits,
            marginX,
            marginY,
            stepX,
            stepY,
            outerWidth,
            outerHeight);
    }

    private static RobloxMonitor? SelectMonitorForBounds(
        RobloxPixelRect bounds,
        IReadOnlyList<RobloxMonitor> monitors)
    {
        var selected = monitors
            .Where(IsUsableMonitor)
            .Select(monitor => new
            {
                Monitor = monitor,
                Intersection = IntersectionArea(bounds, monitor.WorkArea)
            })
            .OrderByDescending(item => item.Intersection)
            .ThenByDescending(item => item.Monitor.IsPrimary)
            .ThenBy(item => item.Monitor.Index)
            .FirstOrDefault();
        return selected is { Intersection: > 0 }
            ? selected.Monitor
            : null;
    }

    private static long IntersectionArea(
        RobloxPixelRect first,
        RobloxPixelRect second)
    {
        var width = Math.Max(
            0L,
            Math.Min((long)first.Right, second.Right) -
            Math.Max((long)first.Left, second.Left));
        var height = Math.Max(
            0L,
            Math.Min((long)first.Bottom, second.Bottom) -
            Math.Max((long)first.Top, second.Top));
        return width * height;
    }

    internal static RobloxPixelRect FitToWorkArea(
        RobloxPixelRect bounds,
        RobloxPixelRect workArea)
    {
        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        var maximumLeft = checked(workArea.Right - width);
        var maximumTop = checked(workArea.Bottom - height);
        return new RobloxPixelRect(
            Math.Clamp(bounds.Left, workArea.Left, maximumLeft),
            Math.Clamp(bounds.Top, workArea.Top, maximumTop),
            width,
            height);
    }

    private static int Normalize(int value, int extent) =>
        (int)Math.Clamp(
            ((long)value * NormalizedScale + extent / 2L) / extent,
            0,
            NormalizedScale);

    private static int Denormalize(int value, int extent) =>
        (int)(((long)value * extent + NormalizedScale / 2L) /
            NormalizedScale);

    private static int Scale96(int value, uint dpi)
    {
        var effectiveDpi = dpi is >= 48 and <= 960 ? dpi : 96;
        return checked((int)(((long)value * effectiveDpi + 48) / 96));
    }

    private static bool TryValidateOptions(
        RobloxCascadeLayoutOptions options,
        out string error)
    {
        if (!options.TargetClientSizeAt96Dpi.IsValid ||
            !options.MinimumClientSizeAt96Dpi.IsValid ||
            options.TargetClientSizeAt96Dpi.Width > MaximumDimension ||
            options.TargetClientSizeAt96Dpi.Height > MaximumDimension ||
            options.MinimumClientSizeAt96Dpi.Width > MaximumDimension ||
            options.MinimumClientSizeAt96Dpi.Height > MaximumDimension ||
            options.RevealXAt96Dpi <= 0 ||
            options.RevealYAt96Dpi <= 0 ||
            options.RevealXAt96Dpi > MaximumDimension ||
            options.RevealYAt96Dpi > MaximumDimension ||
            options.MarginAt96Dpi < 0 ||
            options.MarginAt96Dpi > MaximumDimension)
        {
            error = "The cascade settings contain invalid pixel dimensions.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidNormalized(RobloxNormalizedWindowBounds saved) =>
        saved.PreferredMonitorIndex >= 0 &&
        (string.IsNullOrWhiteSpace(saved.PreferredMonitorStableId) ||
         saved.PreferredMonitorStableId.Length <= MaximumMonitorStableIdLength &&
         !saved.PreferredMonitorStableId.Any(char.IsControl)) &&
        saved.LeftMillionths is >= 0 and <= NormalizedScale &&
        saved.TopMillionths is >= 0 and <= NormalizedScale &&
        saved.WidthMillionths is > 0 and <= NormalizedScale &&
        saved.HeightMillionths is > 0 and <= NormalizedScale;

    private static bool IsUsableMonitor(RobloxMonitor? monitor) =>
        monitor is not null &&
        !string.IsNullOrWhiteSpace(monitor.DeviceName) &&
        monitor.Index >= 0 &&
        IsSaneRect(monitor.Bounds) &&
        IsSaneRect(monitor.WorkArea) &&
        monitor.DpiX is >= 48 and <= 960 &&
        monitor.DpiY is >= 48 and <= 960;

    private static bool IsSaneRect(RobloxPixelRect rectangle) =>
        rectangle.IsValid &&
        rectangle.Width <= MaximumDimension &&
        rectangle.Height <= MaximumDimension &&
        Math.Abs((long)rectangle.Left) <= MaximumDimension &&
        Math.Abs((long)rectangle.Top) <= MaximumDimension;

    private readonly record struct CascadeGeometry(
        bool Fits,
        int MarginX,
        int MarginY,
        int StepX,
        int StepY,
        int OuterWidth,
        int OuterHeight);
}
