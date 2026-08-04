using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PortableDeviceAdaptationPolicyTests
{
    [Fact]
    public void ForClientMacro_ExplicitlyDefersAdaptationToPlayback()
    {
        var result = PortableDeviceAdaptationPolicy.ForClientMacro();

        Assert.Equal(
            PortableMacroAdaptationStatus.AdaptsAtPlayback,
            result.Status);
        Assert.True(result.CanAssign);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_FourKTo1080pSingleMonitor_IsCompatible()
    {
        var source = Display(
            0,
            0,
            3840,
            2160,
            new ExactWheelRect(0, 0, 3840, 2160),
            dpi: 192);
        var current = Display(
            0,
            0,
            1920,
            1080,
            new ExactWheelRect(0, 0, 1920, 1080),
            dpi: 96);

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(PortableMacroAdaptationStatus.Compatible, result.Status);
        Assert.True(result.CanAssign);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_AspectMismatch_IsIncompatible()
    {
        var source = Display(
            0,
            0,
            1920,
            1080,
            new ExactWheelRect(0, 0, 1920, 1080));
        var current = Display(
            0,
            0,
            1600,
            1200,
            new ExactWheelRect(0, 0, 1600, 1200));

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(
            PortableMacroAdaptationStatus.Incompatible,
            result.Status);
        Assert.False(result.CanAssign);
        Assert.Contains(
            PortableDeviceAdaptationReason.VirtualAspectRatioMismatch,
            result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_MonitorCountMismatch_IsIncompatible()
    {
        var source = new ExactWheelDisplayTopology(
            0,
            0,
            3840,
            1080,
            [
                Monitor(new ExactWheelRect(0, 0, 1920, 1080)),
                Monitor(new ExactWheelRect(1920, 0, 3840, 1080))
            ]);
        var current = Display(
            0,
            0,
            1920,
            1080,
            new ExactWheelRect(0, 0, 1920, 1080));

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(
            PortableMacroAdaptationStatus.Incompatible,
            result.Status);
        Assert.Contains(
            PortableDeviceAdaptationReason.MonitorCountMismatch,
            result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_NegativeOriginEquivalentTopology_IsCompatible()
    {
        var source = new ExactWheelDisplayTopology(
            -1920,
            -200,
            3840,
            1080,
            [
                Monitor(new ExactWheelRect(-1920, -200, 0, 880), 96),
                Monitor(new ExactWheelRect(0, -200, 1920, 880), 144)
            ]);
        var current = new ExactWheelDisplayTopology(
            100,
            50,
            1920,
            540,
            [
                Monitor(new ExactWheelRect(100, 50, 1060, 590), 192),
                Monitor(new ExactWheelRect(1060, 50, 2020, 590), 96)
            ]);

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(PortableMacroAdaptationStatus.Compatible, result.Status);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_DifferentNormalizedArrangement_IsIncompatible()
    {
        var source = new ExactWheelDisplayTopology(
            0,
            0,
            2000,
            1000,
            [
                Monitor(new ExactWheelRect(0, 0, 1000, 1000)),
                Monitor(new ExactWheelRect(1000, 0, 2000, 1000))
            ]);
        var current = new ExactWheelDisplayTopology(
            0,
            0,
            2000,
            1000,
            [
                Monitor(new ExactWheelRect(0, 0, 1200, 1000)),
                Monitor(new ExactWheelRect(1200, 0, 2000, 1000))
            ]);

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(
            PortableMacroAdaptationStatus.Incompatible,
            result.Status);
        Assert.Contains(
            PortableDeviceAdaptationReason.MonitorArrangementMismatch,
            result.Reasons);
    }

    [Fact]
    public void ForWholeLayoutMacro_InvalidCurrentVirtualSize_ReportsReason()
    {
        var source = Display(
            0,
            0,
            1920,
            1080,
            new ExactWheelRect(0, 0, 1920, 1080));
        var current = new ExactWheelDisplayTopology(
            0,
            0,
            0,
            1080,
            [Monitor(new ExactWheelRect(0, 0, 1920, 1080))]);

        var result = PortableDeviceAdaptationPolicy.ForWholeLayoutMacro(
            source,
            current);

        Assert.Equal(
            PortableMacroAdaptationStatus.Incompatible,
            result.Status);
        Assert.Equal(
            [PortableDeviceAdaptationReason.CurrentTopologyInvalid],
            result.Reasons);
    }

    [Fact]
    public void TrySanitizePlacement_ClearsMachineIdentityAndPreservesGeometry()
    {
        var source = new NormalizedClientWindowPlacement
        {
            MonitorStableId = @"\\?\DISPLAY#SERIAL",
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorIndex = 1,
            Left = 0.125,
            Top = 0.25,
            Width = 0.5,
            Height = 0.625
        };

        var success = PortableDeviceAdaptationPolicy.TrySanitizePlacement(
            source,
            out var portable);

        Assert.True(success);
        Assert.NotNull(portable);
        Assert.Null(portable.MonitorStableId);
        Assert.Null(portable.MonitorDeviceName);
        Assert.Equal(source.MonitorIndex, portable.MonitorIndex);
        Assert.Equal(source.Left, portable.Left);
        Assert.Equal(source.Top, portable.Top);
        Assert.Equal(source.Width, portable.Width);
        Assert.Equal(source.Height, portable.Height);
        Assert.NotSame(source, portable);
        Assert.NotNull(source.MonitorStableId);
        Assert.NotNull(source.MonitorDeviceName);
    }

    [Fact]
    public void TrySanitizePlacement_InvalidNormalizedRectangle_IsRejected()
    {
        var source = new NormalizedClientWindowPlacement
        {
            MonitorIndex = 0,
            Left = 0.75,
            Top = 0,
            Width = 0.5,
            Height = 1
        };

        var success = PortableDeviceAdaptationPolicy.TrySanitizePlacement(
            source,
            out var portable);

        Assert.False(success);
        Assert.Null(portable);
    }

    private static ExactWheelDisplayTopology Display(
        int left,
        int top,
        int width,
        int height,
        ExactWheelRect monitorBounds,
        uint dpi = 96) =>
        new(
            left,
            top,
            width,
            height,
            [Monitor(monitorBounds, dpi)]);

    private static ExactWheelMonitorSnapshot Monitor(
        ExactWheelRect bounds,
        uint dpi = 96) =>
        new(bounds, dpi, dpi);
}
