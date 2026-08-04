using System.Collections.ObjectModel;

namespace SessionDock.ExactWheel;

public static class ExactWheelLimits
{
    // A 500k-event macro is roughly 24 MiB on disk before object overhead and
    // comfortably covers about an hour of high-frequency raw input. Keeping
    // this below the old multi-million-event limit prevents validation and
    // coordinate transforms from multiplying hundreds of MiB per target.
    public const ulong MaximumEventCount = 500_000;
    public const ulong MaximumDurationMicroseconds =
        24UL * 60UL * 60UL * 1_000_000UL;
    public const int MaximumMacroFileMebibytes = 64;
    public const long MaximumMacroFileBytes =
        MaximumMacroFileMebibytes * 1024L * 1024L;
    public const int DefaultCaptureEventCapacity =
        (int)MaximumEventCount;
    public const int MaximumMonitorCount = 64;
    public const int MaximumProcessBasenameUtf16Units = 260;
    public const int MaximumWindowClassUtf16Units = 256;
    public const int MaximumVirtualExtent = 1_000_000;
    public const uint MaximumPlausibleDpi = 9_600;
    public const ulong PrivateInputMarker = 0x455741435457484C;
}

public enum ExactWheelInputEventType : byte
{
    MouseMove = 1,
    MouseButtonDown = 2,
    MouseButtonUp = 3,
    VerticalWheel = 4,
    HorizontalWheel = 5,
    KeyDown = 6,
    KeyUp = 7
}

public enum ExactWheelMouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5
}

[Flags]
public enum ExactWheelKeyboardFlags : uint
{
    None = 0,
    Extended = 1U << 0,
    System = 1U << 1,
    AltContext = 1U << 2
}

public readonly record struct ExactWheelInputEvent(
    ulong TimestampMicroseconds,
    ulong Sequence,
    ExactWheelInputEventType Type,
    int X,
    int Y,
    int Data1,
    int Data2,
    ExactWheelKeyboardFlags Flags = ExactWheelKeyboardFlags.None)
{
    public bool IsMouseEvent => Type is
        ExactWheelInputEventType.MouseMove or
        ExactWheelInputEventType.MouseButtonDown or
        ExactWheelInputEventType.MouseButtonUp or
        ExactWheelInputEventType.VerticalWheel or
        ExactWheelInputEventType.HorizontalWheel;

    public bool IsKeyboardEvent => Type is
        ExactWheelInputEventType.KeyDown or
        ExactWheelInputEventType.KeyUp;
}

public readonly record struct ExactWheelRect(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => checked(Right - Left);

    public int Height => checked(Bottom - Top);

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct ExactWheelMonitorSnapshot(
    ExactWheelRect Bounds,
    uint DpiX = 96,
    uint DpiY = 96);

public sealed class ExactWheelDisplayTopology
{
    private readonly ReadOnlyCollection<ExactWheelMonitorSnapshot> _monitors;

    public ExactWheelDisplayTopology(
        int virtualLeft,
        int virtualTop,
        int virtualWidth,
        int virtualHeight,
        IEnumerable<ExactWheelMonitorSnapshot> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        VirtualLeft = virtualLeft;
        VirtualTop = virtualTop;
        VirtualWidth = virtualWidth;
        VirtualHeight = virtualHeight;
        _monitors = Array.AsReadOnly(monitors.ToArray());
    }

    public int VirtualLeft { get; }

    public int VirtualTop { get; }

    public int VirtualWidth { get; }

    public int VirtualHeight { get; }

    public IReadOnlyList<ExactWheelMonitorSnapshot> Monitors => _monitors;

    public ExactWheelRect VirtualBounds => new(
        VirtualLeft,
        VirtualTop,
        checked(VirtualLeft + VirtualWidth),
        checked(VirtualTop + VirtualHeight));
}

public sealed class ExactWheelTargetMetadata
{
    public ExactWheelTargetMetadata(
        string processBasename,
        string windowClass,
        ExactWheelRect windowRect,
        ExactWheelRect clientRect)
    {
        ProcessBasename = processBasename ??
            throw new ArgumentNullException(nameof(processBasename));
        WindowClass = windowClass ??
            throw new ArgumentNullException(nameof(windowClass));
        WindowRect = windowRect;
        ClientRect = clientRect;
    }

    public string ProcessBasename { get; }

    public string WindowClass { get; }

    public ExactWheelRect WindowRect { get; }

    public ExactWheelRect ClientRect { get; }
}

public sealed class ExactWheelRecording
{
    private readonly ReadOnlyCollection<ExactWheelInputEvent> _events;

    public ExactWheelRecording(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IEnumerable<ExactWheelInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(events);
        DurationMicroseconds = durationMicroseconds;
        Display = display;
        Target = target;
        _events = Array.AsReadOnly(events.ToArray());
    }

    public ulong DurationMicroseconds { get; }

    public ExactWheelDisplayTopology Display { get; }

    public ExactWheelTargetMetadata Target { get; }

    public IReadOnlyList<ExactWheelInputEvent> Events => _events;
}

public sealed record ExactWheelRecordingTarget(
    nint WindowHandle,
    ExactWheelDisplayTopology Display,
    ExactWheelTargetMetadata Metadata);
