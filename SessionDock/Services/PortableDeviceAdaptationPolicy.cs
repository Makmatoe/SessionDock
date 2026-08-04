using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal enum PortableMacroAdaptationStatus
{
    AdaptsAtPlayback,
    Compatible,
    Incompatible
}

internal enum PortableDeviceAdaptationReason
{
    SourceTopologyInvalid,
    CurrentTopologyInvalid,
    MonitorCountMismatch,
    VirtualAspectRatioMismatch,
    MonitorArrangementMismatch
}

internal sealed record PortableMacroAdaptationResult(
    PortableMacroAdaptationStatus Status,
    IReadOnlyList<PortableDeviceAdaptationReason> Reasons)
{
    internal bool CanAssign =>
        Status != PortableMacroAdaptationStatus.Incompatible;
}

internal static class PortableDeviceAdaptationPolicy
{
    // Layouts that differ only by ordinary taskbar, scaling, or rounding
    // changes should remain assignable. Materially different display shapes
    // are rejected so an imported whole-layout macro is not silently guessed.
    internal const double VirtualAspectRatioTolerance = 0.05;
    internal const double NormalizedMonitorArrangementTolerance = 0.05;

    private static PortableMacroAdaptationResult ClientMacroResult { get; } =
        new(
            PortableMacroAdaptationStatus.AdaptsAtPlayback,
            Array.Empty<PortableDeviceAdaptationReason>());

    internal static PortableMacroAdaptationResult ForClientMacro() =>
        ClientMacroResult;

    internal static PortableMacroAdaptationResult ForWholeLayoutMacro(
        ExactWheelDisplayTopology source,
        ExactWheelDisplayTopology current)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(current);

        var reasons = new List<PortableDeviceAdaptationReason>();
        var sourceIsSane = IsSaneTopology(source);
        var currentIsSane = IsSaneTopology(current);
        if (!sourceIsSane)
        {
            reasons.Add(
                PortableDeviceAdaptationReason.SourceTopologyInvalid);
        }
        if (!currentIsSane)
        {
            reasons.Add(
                PortableDeviceAdaptationReason.CurrentTopologyInvalid);
        }
        if (!sourceIsSane || !currentIsSane)
            return Incompatible(reasons);

        if (source.Monitors.Count != current.Monitors.Count)
        {
            reasons.Add(
                PortableDeviceAdaptationReason.MonitorCountMismatch);
        }
        if (RelativeAspectRatioDifference(source, current) >
            VirtualAspectRatioTolerance)
        {
            reasons.Add(
                PortableDeviceAdaptationReason.VirtualAspectRatioMismatch);
        }
        if (source.Monitors.Count == current.Monitors.Count &&
            !HasEquivalentNormalizedMonitorArrangement(source, current))
        {
            reasons.Add(
                PortableDeviceAdaptationReason.MonitorArrangementMismatch);
        }

        return reasons.Count == 0
            ? new PortableMacroAdaptationResult(
                PortableMacroAdaptationStatus.Compatible,
                Array.Empty<PortableDeviceAdaptationReason>())
            : Incompatible(reasons);
    }

    internal static bool TrySanitizePlacement(
        NormalizedClientWindowPlacement? source,
        out NormalizedClientWindowPlacement? portable)
    {
        portable = null;
        if (source is null ||
            source.MonitorIndex is < 0 or >=
                ExactWheelLimits.MaximumMonitorCount ||
            !IsNormalizedRectangle(source))
        {
            return false;
        }

        portable = new NormalizedClientWindowPlacement
        {
            MonitorStableId = null,
            MonitorDeviceName = null,
            MonitorIndex = source.MonitorIndex,
            Left = source.Left,
            Top = source.Top,
            Width = source.Width,
            Height = source.Height
        };
        return true;
    }

    private static PortableMacroAdaptationResult Incompatible(
        IEnumerable<PortableDeviceAdaptationReason> reasons) =>
        new(
            PortableMacroAdaptationStatus.Incompatible,
            Array.AsReadOnly(reasons.Distinct().ToArray()));

    private static bool IsNormalizedRectangle(
        NormalizedClientWindowPlacement placement) =>
        double.IsFinite(placement.Left) &&
        double.IsFinite(placement.Top) &&
        double.IsFinite(placement.Width) &&
        double.IsFinite(placement.Height) &&
        placement.Left >= 0 &&
        placement.Top >= 0 &&
        placement.Width is > 0 and <= 1 &&
        placement.Height is > 0 and <= 1 &&
        placement.Left <= 1 - placement.Width &&
        placement.Top <= 1 - placement.Height;

    private static bool IsSaneTopology(ExactWheelDisplayTopology topology)
    {
        if (topology.VirtualWidth <= 0 ||
            topology.VirtualHeight <= 0 ||
            topology.VirtualWidth > ExactWheelLimits.MaximumVirtualExtent ||
            topology.VirtualHeight > ExactWheelLimits.MaximumVirtualExtent ||
            topology.Monitors.Count is 0 or >
                ExactWheelLimits.MaximumMonitorCount)
        {
            return false;
        }

        var virtualRight = (long)topology.VirtualLeft +
            topology.VirtualWidth;
        var virtualBottom = (long)topology.VirtualTop +
            topology.VirtualHeight;
        if (virtualRight is < int.MinValue or > int.MaxValue ||
            virtualBottom is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        foreach (var monitor in topology.Monitors)
        {
            var bounds = monitor.Bounds;
            if (bounds.Right <= bounds.Left ||
                bounds.Bottom <= bounds.Top ||
                bounds.Left < topology.VirtualLeft ||
                bounds.Top < topology.VirtualTop ||
                bounds.Right > virtualRight ||
                bounds.Bottom > virtualBottom ||
                monitor.DpiX is 0 or > ExactWheelLimits.MaximumPlausibleDpi ||
                monitor.DpiY is 0 or > ExactWheelLimits.MaximumPlausibleDpi)
            {
                return false;
            }
        }

        return true;
    }

    private static double RelativeAspectRatioDifference(
        ExactWheelDisplayTopology source,
        ExactWheelDisplayTopology current)
    {
        var sourceCross = (double)source.VirtualWidth *
            current.VirtualHeight;
        var currentCross = (double)current.VirtualWidth *
            source.VirtualHeight;
        return Math.Abs(sourceCross - currentCross) /
            Math.Max(sourceCross, currentCross);
    }

    private static bool HasEquivalentNormalizedMonitorArrangement(
        ExactWheelDisplayTopology source,
        ExactWheelDisplayTopology current)
    {
        var sourceMonitors = NormalizeAndOrderMonitors(source);
        var currentMonitors = NormalizeAndOrderMonitors(current);
        for (var index = 0; index < sourceMonitors.Length; index++)
        {
            var first = sourceMonitors[index];
            var second = currentMonitors[index];
            if (Math.Abs(first.Left - second.Left) >
                    NormalizedMonitorArrangementTolerance ||
                Math.Abs(first.Top - second.Top) >
                    NormalizedMonitorArrangementTolerance ||
                Math.Abs(first.Right - second.Right) >
                    NormalizedMonitorArrangementTolerance ||
                Math.Abs(first.Bottom - second.Bottom) >
                    NormalizedMonitorArrangementTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static NormalizedMonitor[] NormalizeAndOrderMonitors(
        ExactWheelDisplayTopology topology) =>
        topology.Monitors
            .Select(monitor => new NormalizedMonitor(
                ((long)monitor.Bounds.Left - topology.VirtualLeft) /
                    (double)topology.VirtualWidth,
                ((long)monitor.Bounds.Top - topology.VirtualTop) /
                    (double)topology.VirtualHeight,
                ((long)monitor.Bounds.Right - topology.VirtualLeft) /
                    (double)topology.VirtualWidth,
                ((long)monitor.Bounds.Bottom - topology.VirtualTop) /
                    (double)topology.VirtualHeight))
            .OrderBy(monitor => monitor.Left)
            .ThenBy(monitor => monitor.Top)
            .ThenBy(monitor => monitor.Right)
            .ThenBy(monitor => monitor.Bottom)
            .ToArray();

    private readonly record struct NormalizedMonitor(
        double Left,
        double Top,
        double Right,
        double Bottom);
}
