namespace SessionDock.ExactWheel;

public enum ExactWheelPlaybackStopReason
{
    Completed,
    Cancelled,
    PhysicalIntervention,
    PhysicalInputHeld,
    TargetLost,
    TargetUnavailable,
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

public enum ExactWheelFocusTransitionWaitResult
{
    Ready,
    PhysicalInputHeld,
    AuthorizationDenied,
    InterventionMonitorUnavailable
}

public sealed class ExactWheelPlaybackOptions
{
    public ExactWheelPlaybackRate Rate { get; init; } =
        ExactWheelPlaybackRate.Recorded;

    public ulong LoopCount { get; init; } = 1;

    public bool Infinite { get; init; }

    public ulong InterLoopDelayMicroseconds { get; init; }

    // A recovered foreground/pointer authorization can need a small
    // non-scaled drain before the next SendInput. Zero preserves the generic
    // ExactWheel behavior; SessionDock enables this for cross-client macros.
    public ulong DispatchRecoverySettleMicroseconds { get; init; }

    // A permanently unavailable target must not monopolize a serial
    // multi-target playback loop. Zero keeps the generic ExactWheel behavior
    // of waiting indefinitely; hosts can opt into a bounded, retryable yield.
    public ulong DispatchAuthorizationTimeoutMicroseconds { get; init; }

    public ulong DangerouslyLateMicroseconds { get; init; } = 250_000;

    // Preserve semantic input after CPU stalls by shifting the remaining
    // absolute timeline instead of bursting an unbounded stale backlog.
    public bool RecoverFromTimingStalls { get; init; } = true;

    // A small backlog may catch up naturally; anything older rebases time.
    public ulong MaximumCatchUpMicroseconds { get; init; } = 5_000;

    // Only redundant, overdue moves are coalesced. Button, wheel, and key
    // events are never coalesced, nor are moves while injected input is held.
    public bool CoalesceOverdueMouseMoves { get; init; } = true;

    // UI progress is advisory and latest-value throttled to avoid flooding.
    public ulong ProgressIntervalMicroseconds { get; init; } = 50_000;

    // High-resolution waitable timers do not need a busy-spin by default.
    public ulong FinalSpinMicroseconds { get; init; }

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

    // Runs synchronously after Windows accepted the complete input event.
    // Hosts can use this acknowledgement to bind a click-driven focus change
    // to the exact event that caused it before any later input is authorized.
    public Action<ExactWheelInputEvent>? DispatchCompleted { get; init; }

    /// <summary>
    /// Maps pointer coordinates immediately before authorization and
    /// injection. This avoids materializing a full transformed event array
    /// for every destination client while ensuring target checks observe the
    /// final coordinates that will be sent to Windows.
    /// </summary>
    public ExactWheelPlaybackCoordinateTransform? CoordinateTransform
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
