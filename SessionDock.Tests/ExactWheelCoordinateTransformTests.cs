using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelCoordinateTransformTests
{
    [Fact]
    public void TransformClientRelative_Scales4kStyleRecordingTo1080StyleClient()
    {
        var source = ExactWheelTestData.Recording();
        var originalEvents = source.Events.ToArray();
        var destinationDisplay = ExactWheelTestData.Display(
            0,
            0,
            1_920,
            1_080);
        var destinationTarget = ExactWheelTestData.Target(
            new ExactWheelRect(10, 20, 710, 430));

        var transformed = ExactWheelCoordinateTransforms.TransformClientRelative(
            source,
            destinationDisplay,
            destinationTarget);

        Assert.Equal((10, 20), (transformed.Events[0].X, transformed.Events[0].Y));
        Assert.Equal((360, 225), (transformed.Events[1].X, transformed.Events[1].Y));
        Assert.Equal((709, 429), (transformed.Events[3].X, transformed.Events[3].Y));
        Assert.Equal(originalEvents[5], transformed.Events[5]);
        Assert.Equal(originalEvents, source.Events);
        Assert.NotSame(source, transformed);
    }

    [Fact]
    public void TransformClientRelative_MouseOutsideRecordedClient_IsRejected()
    {
        var source = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    1,
                    1,
                    ExactWheelInputEventType.MouseMove,
                    99,
                    80,
                    0,
                    0)
            ]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelCoordinateTransforms.TransformClientRelative(
                source,
                ExactWheelTestData.Display(0, 0, 1_920, 1_080),
                ExactWheelTestData.Target(new ExactWheelRect(0, 0, 800, 600))));

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransformClientRelative_ZeroSizeClient_IsRejected()
    {
        var destination = ExactWheelTestData.Target(
            new ExactWheelRect(10, 10, 10, 500));

        Assert.Throws<InvalidDataException>(() =>
            ExactWheelCoordinateTransforms.TransformClientRelative(
                ExactWheelTestData.Recording(),
                ExactWheelTestData.Display(0, 0, 1_920, 1_080),
                destination));
    }

    [Fact]
    public void TransformVirtualDesktopNormalized_MapsNegativeOriginAndEndpoints()
    {
        var source = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1, -1_920, 0),
                MouseMove(1, 2, 1_919, 1_079)
            ]);
        var destinationDisplay = ExactWheelTestData.Display(
            100,
            50,
            1_000,
            500);

        var transformed =
            ExactWheelCoordinateTransforms.TransformVirtualDesktopNormalized(
                source,
                destinationDisplay,
                ExactWheelTestData.Target(new ExactWheelRect(200, 100, 800, 500)));

        Assert.Equal((100, 50), (transformed.Events[0].X, transformed.Events[0].Y));
        Assert.Equal((1_099, 549), (transformed.Events[1].X, transformed.Events[1].Y));
    }

    [Fact]
    public void TransformMonitorNormalized_MapsEachMonitorByStableOrdinal()
    {
        var source = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1, -1_920, 0),
                MouseMove(1, 2, -1, 1_079),
                MouseMove(2, 3, 0, 0),
                MouseMove(3, 4, 1_919, 1_079)
            ]);
        var destinationDisplay = new ExactWheelDisplayTopology(
            0,
            0,
            3_840,
            1_440,
            [
                new ExactWheelMonitorSnapshot(
                    new ExactWheelRect(0, 0, 1_280, 720)),
                new ExactWheelMonitorSnapshot(
                    new ExactWheelRect(1_280, 0, 3_840, 1_440))
            ]);

        var transformed =
            ExactWheelCoordinateTransforms.TransformMonitorNormalized(
                source,
                destinationDisplay,
                ExactWheelTestData.Target());

        Assert.Equal((0, 0), (transformed.Events[0].X, transformed.Events[0].Y));
        Assert.Equal((1_279, 719), (transformed.Events[1].X, transformed.Events[1].Y));
        Assert.Equal((1_280, 0), (transformed.Events[2].X, transformed.Events[2].Y));
        Assert.Equal((3_839, 1_439), (transformed.Events[3].X, transformed.Events[3].Y));
    }

    [Fact]
    public void TransformMonitorNormalized_MonitorCountMismatch_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            ExactWheelCoordinateTransforms.TransformMonitorNormalized(
                ExactWheelTestData.Recording(),
                ExactWheelTestData.Display(0, 0, 1_920, 1_080),
                ExactWheelTestData.Target()));
    }

    [Fact]
    public void TransformMonitorNormalized_VirtualDesktopGap_IsRejected()
    {
        var gapDisplay = new ExactWheelDisplayTopology(
            0,
            0,
            1_800,
            600,
            [
                new ExactWheelMonitorSnapshot(new ExactWheelRect(0, 0, 800, 600)),
                new ExactWheelMonitorSnapshot(new ExactWheelRect(1_000, 0, 1_800, 600))
            ]);
        var source = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 900, 300)],
            display: gapDisplay);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelCoordinateTransforms.TransformMonitorNormalized(
                source,
                gapDisplay,
                ExactWheelTestData.Target()));

        Assert.Contains("gap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1_920, -1_920, 3_840, 0)]
    [InlineData(1_919, -1_920, 3_840, 65_535)]
    [InlineData(-1_921, -1_920, 3_840, 0)]
    [InlineData(1_920, -1_920, 3_840, 65_535)]
    public void NormalizeForSendInput_UsesInclusiveEndpointsAndClamps(
        int pixel,
        int origin,
        int extent,
        int expected)
    {
        Assert.Equal(
            expected,
            ExactWheelCoordinateTransforms.NormalizeForSendInput(
                pixel,
                origin,
                extent));
    }

    private static ExactWheelInputEvent MouseMove(
        ulong timestamp,
        ulong sequence,
        int x,
        int y) =>
        new(
            timestamp,
            sequence,
            ExactWheelInputEventType.MouseMove,
            x,
            y,
            0,
            0);
}
