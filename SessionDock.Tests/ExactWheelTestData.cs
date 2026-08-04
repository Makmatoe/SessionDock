using SessionDock.ExactWheel;

namespace SessionDock.Tests;

internal static class ExactWheelTestData
{
    internal static ExactWheelDisplayTopology Display(
        int virtualLeft = -1_920,
        int virtualTop = 0,
        int virtualWidth = 3_840,
        int virtualHeight = 1_080) =>
        new(
            virtualLeft,
            virtualTop,
            virtualWidth,
            virtualHeight,
            virtualWidth == 3_840 && virtualLeft == -1_920
                ?
                [
                    new ExactWheelMonitorSnapshot(
                        new ExactWheelRect(-1_920, 0, 0, 1_080),
                        96,
                        96),
                    new ExactWheelMonitorSnapshot(
                        new ExactWheelRect(0, 0, 1_920, 1_080),
                        144,
                        144)
                ]
                :
                [
                    new ExactWheelMonitorSnapshot(
                        new ExactWheelRect(
                            virtualLeft,
                            virtualTop,
                            checked(virtualLeft + virtualWidth),
                            checked(virtualTop + virtualHeight)),
                        96,
                        96)
                ]);

    internal static ExactWheelTargetMetadata Target(
        ExactWheelRect? client = null,
        string processBasename = "RobloxPlayerBeta.exe",
        string windowClass = "WINDOWSCLIENT") =>
        new(
            processBasename,
            windowClass,
            new ExactWheelRect(90, 40, 1_510, 940),
            client ?? new ExactWheelRect(100, 80, 1_500, 900));

    internal static ExactWheelRecording Recording(
        IEnumerable<ExactWheelInputEvent>? events = null,
        ulong durationMicroseconds = 500_000,
        ExactWheelDisplayTopology? display = null,
        ExactWheelTargetMetadata? target = null) =>
        new(
            durationMicroseconds,
            display ?? Display(),
            target ?? Target(),
            events ?? Events());

    internal static ExactWheelInputEvent[] Events() =>
    [
        new(
            0,
            1,
            ExactWheelInputEventType.MouseMove,
            100,
            80,
            0,
            0),
        new(
            10_000,
            2,
            ExactWheelInputEventType.MouseButtonDown,
            800,
            490,
            (int)ExactWheelMouseButton.Left,
            0),
        new(
            20_000,
            3,
            ExactWheelInputEventType.MouseButtonUp,
            800,
            490,
            (int)ExactWheelMouseButton.Left,
            0),
        new(
            30_000,
            4,
            ExactWheelInputEventType.VerticalWheel,
            1_499,
            899,
            -240,
            0),
        new(
            40_000,
            5,
            ExactWheelInputEventType.HorizontalWheel,
            400,
            300,
            30,
            0),
        new(
            50_000,
            6,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E,
            ExactWheelKeyboardFlags.Extended |
            ExactWheelKeyboardFlags.AltContext),
        new(
            60_000,
            7,
            ExactWheelInputEventType.KeyUp,
            0,
            0,
            0x41,
            0x1E,
            ExactWheelKeyboardFlags.Extended |
            ExactWheelKeyboardFlags.AltContext)
    ];
}
