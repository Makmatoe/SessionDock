using System.Windows;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class WindowPlacementPolicy
{
    internal const int MaximumMonitorDeviceNameLength = 128;
    private const double MaximumDimension = 32_768;
    private const double MaximumOffsetMagnitude = 1_000_000;
    private const double MinimumStoredHeight = 240;
    private const double MinimumStoredWidth = 320;

    internal static WindowPlacementSettings? Normalize(
        WindowPlacementSettings? placement)
    {
        if (placement is null ||
            !IsFiniteInRange(
                placement.Width,
                MinimumStoredWidth,
                MaximumDimension) ||
            !IsFiniteInRange(
                placement.Height,
                MinimumStoredHeight,
                MaximumDimension) ||
            !IsFiniteInRange(
                placement.OffsetX,
                -MaximumOffsetMagnitude,
                MaximumOffsetMagnitude) ||
            !IsFiniteInRange(
                placement.OffsetY,
                -MaximumOffsetMagnitude,
                MaximumOffsetMagnitude))
        {
            return null;
        }

        return new WindowPlacementSettings
        {
            MonitorDeviceName = NormalizeMonitorDeviceName(
                placement.MonitorDeviceName),
            OffsetX = placement.OffsetX,
            OffsetY = placement.OffsetY,
            Width = placement.Width,
            Height = placement.Height,
            IsMaximized = placement.IsMaximized
        };
    }

    internal static Rect? CalculateRestoredBounds(
        Rect workArea,
        WindowPlacementSettings? placement,
        double minimumWidth,
        double minimumHeight)
    {
        var normalized = Normalize(placement);
        if (normalized is null ||
            workArea.IsEmpty ||
            !IsFinitePositive(workArea.Width) ||
            !IsFinitePositive(workArea.Height) ||
            !double.IsFinite(workArea.Left) ||
            !double.IsFinite(workArea.Top))
        {
            return null;
        }

        const double margin = 16;
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        var effectiveMinimumWidth = IsFinitePositive(minimumWidth)
            ? Math.Min(minimumWidth, availableWidth)
            : 1;
        var effectiveMinimumHeight = IsFinitePositive(minimumHeight)
            ? Math.Min(minimumHeight, availableHeight)
            : 1;
        var width = Math.Clamp(
            normalized.Width,
            effectiveMinimumWidth,
            availableWidth);
        var height = Math.Clamp(
            normalized.Height,
            effectiveMinimumHeight,
            availableHeight);

        return WindowLayoutService.CalculateFittedBounds(
            workArea,
            new Rect(
                workArea.Left + normalized.OffsetX,
                workArea.Top + normalized.OffsetY,
                width,
                height));
    }

    internal static bool AreEquivalent(
        WindowPlacementSettings? first,
        WindowPlacementSettings? second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first is null || second is null)
            return false;

        return string.Equals(
                   first.MonitorDeviceName,
                   second.MonitorDeviceName,
                   StringComparison.Ordinal) &&
               first.OffsetX.Equals(second.OffsetX) &&
               first.OffsetY.Equals(second.OffsetY) &&
               first.Width.Equals(second.Width) &&
               first.Height.Equals(second.Height) &&
               first.IsMaximized == second.IsMaximized;
    }

    private static string? NormalizeMonitorDeviceName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > MaximumMonitorDeviceNameLength ||
               normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private static bool IsFiniteInRange(
        double value,
        double minimum,
        double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;
}
