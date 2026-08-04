namespace SessionDock.ExactWheel;

public enum ExactWheelPlaybackStopReason
{
    Completed,
    Cancelled,
    PhysicalIntervention,
    PhysicalInputHeld,
    TargetLost,
    DangerouslyLate,
    InjectionFailed,
    InvalidTimeline,
    TimerFailed,
    CleanupFailed
}

public enum ExactWheelDispatchAuthorization
{
    Authorized,
    TemporarilyUnavailable,
    Denied
}

public sealed class ExactWheelPlaybackOptions
{
    public ExactWheelPlaybackRate Rate { get; init; } =
        ExactWheelPlaybackRate.Recorded;

    public ulong LoopCount { get; init; } = 1;

    public bool Infinite { get; init; }

    public ulong InterLoopDelayMicroseconds { get; init; }

    public ulong DangerouslyLateMicroseconds { get; init; } = 250_000;

    public ulong FinalSpinMicroseconds { get; init; } = 500;

    public bool StopOnPhysicalInput { get; init; } = true;

    public bool PauseOnPhysicalInput { get; init; }

    public bool EnforcePhysicalInputRelease { get; init; } = true;

    public IReadOnlyCollection<int> WaitForReleaseVirtualKeys { get; init; } =
        Array.Empty<int>();

    public Func<bool>? PreDispatchGuard { get; init; }

    public Func<ExactWheelDispatchAuthorization>? DispatchAuthorization
    { get; init; }

    public Func<ExactWheelInputEvent, ExactWheelDispatchAuthorization>?
        EventDispatchAuthorization
    { get; init; }

    public IProgress<ExactWheelPlaybackProgress>? Progress { get; init; }
}

public readonly record struct ExactWheelPlaybackProgress(
    ulong LoopIndex,
    int EventIndex,
    int EventCount,
    ulong EventTimestampMicroseconds,
    long LatenessMicroseconds);

public sealed record ExactWheelPlaybackResult(
    ExactWheelPlaybackStopReason Reason,
    int Win32Error,
    uint Submitted,
    uint Expected,
    ulong LoopIndex,
    int EventIndex,
    long LatenessMicroseconds,
    bool CleanupSucceeded,
    string Message)
{
    public bool Succeeded =>
        Reason == ExactWheelPlaybackStopReason.Completed;
}

public sealed class ExactWheelRecordingOptions
{
    public int MaximumEvents { get; init; } =
        ExactWheelLimits.DefaultCaptureEventCapacity;

    public IReadOnlyCollection<int> ArmUntilReleasedVirtualKeys { get; init; } =
        Array.Empty<int>();

    public Func<ExactWheelInputEvent, bool>? EventAdmission { get; init; }
}
