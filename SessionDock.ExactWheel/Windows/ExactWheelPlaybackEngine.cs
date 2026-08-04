using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.ExactWheel.Windows;

internal enum DeadlineWaitResult
{
    Reached,
    Cancelled,
    PhysicalIntervention,
    Failed
}

internal interface IPlaybackClock
{
    long Frequency { get; }

    long Timestamp { get; }
}

internal sealed class StopwatchPlaybackClock : IPlaybackClock
{
    public long Frequency => Stopwatch.Frequency;

    public long Timestamp => Stopwatch.GetTimestamp();
}

internal interface IPlaybackWaiter : IDisposable
{
    DeadlineWaitResult WaitUntil(
        long deadlineTicks,
        ulong finalSpinMicroseconds,
        IPlaybackClock clock,
        WaitHandle cancellationEvent,
        WaitHandle? interventionEvent,
        out int win32Error);
}

internal sealed class Win32PlaybackWaiter : IPlaybackWaiter
{
    private SafeWaitHandle? _timer;
    private bool _timerCreationAttempted;

    public DeadlineWaitResult WaitUntil(
        long deadlineTicks,
        ulong finalSpinMicroseconds,
        IPlaybackClock clock,
        WaitHandle cancellationEvent,
        WaitHandle? interventionEvent,
        out int win32Error)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cancellationEvent);
        win32Error = 0;
        while (true)
        {
            var immediate = Poll(cancellationEvent, interventionEvent);
            if (immediate is not null)
                return immediate.Value;

            var now = clock.Timestamp;
            if (now >= deadlineTicks)
                return DeadlineWaitResult.Reached;
            var remainingTicks = deadlineTicks - now;
            var remainingMicroseconds = ExactWheelTiming.TicksToMicroseconds(
                remainingTicks,
                clock.Frequency);
            if (remainingMicroseconds <=
                checked((long)finalSpinMicroseconds))
            {
                Thread.Yield();
                continue;
            }

            var coarseMicroseconds = Math.Max(
                1L,
                remainingMicroseconds - checked((long)finalSpinMicroseconds));
            if (EnsureTimer())
            {
                var dueTime = -checked(coarseMicroseconds * 10L);
                if (!ExactWheelNativeMethods.SetWaitableTimer(
                        _timer!,
                        ref dueTime,
                        0,
                        0,
                        0,
                        resume: false))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return DeadlineWaitResult.Failed;
                }

                nint[] handles = interventionEvent is null
                    ?
                    [
                        cancellationEvent.SafeWaitHandle.DangerousGetHandle(),
                        _timer!.DangerousGetHandle()
                    ]
                    :
                    [
                        cancellationEvent.SafeWaitHandle.DangerousGetHandle(),
                        interventionEvent.SafeWaitHandle.DangerousGetHandle(),
                        _timer!.DangerousGetHandle()
                    ];
                var waited = ExactWheelNativeMethods.WaitForMultipleObjects(
                    checked((uint)handles.Length),
                    handles,
                    waitAll: false,
                    ExactWheelNativeMethods.Infinite);
                if (waited == ExactWheelNativeMethods.WaitObject0)
                    return DeadlineWaitResult.Cancelled;
                if (interventionEvent is not null &&
                    waited == ExactWheelNativeMethods.WaitObject0 + 1)
                {
                    return DeadlineWaitResult.PhysicalIntervention;
                }

                var timerIndex = interventionEvent is null ? 1U : 2U;
                if (waited == ExactWheelNativeMethods.WaitObject0 + timerIndex)
                    continue;
                win32Error = Marshal.GetLastWin32Error();
                return DeadlineWaitResult.Failed;
            }

            var timeoutMilliseconds = checked((int)Math.Clamp(
                (coarseMicroseconds + 999L) / 1_000L,
                1L,
                int.MaxValue));
            WaitHandle[] managedHandles = interventionEvent is null
                ? [cancellationEvent]
                : [cancellationEvent, interventionEvent];
            var managedResult = WaitHandle.WaitAny(
                managedHandles,
                timeoutMilliseconds);
            if (managedResult == 0)
                return DeadlineWaitResult.Cancelled;
            if (interventionEvent is not null && managedResult == 1)
                return DeadlineWaitResult.PhysicalIntervention;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private bool EnsureTimer()
    {
        if (_timer is not null && !_timer.IsInvalid)
            return true;
        if (_timerCreationAttempted)
            return false;
        _timerCreationAttempted = true;
        _timer = ExactWheelNativeMethods.CreateWaitableTimerEx(
            0,
            null,
            ExactWheelNativeMethods.CreateWaitableTimerHighResolution,
            ExactWheelNativeMethods.TimerAllAccess);
        if (_timer.IsInvalid)
        {
            _timer.Dispose();
            _timer = null;
            return false;
        }

        return true;
    }

    private static DeadlineWaitResult? Poll(
        WaitHandle cancellationEvent,
        WaitHandle? interventionEvent)
    {
        if (cancellationEvent.WaitOne(0))
            return DeadlineWaitResult.Cancelled;
        if (interventionEvent?.WaitOne(0) == true)
            return DeadlineWaitResult.PhysicalIntervention;
        return null;
    }
}

internal sealed class ExactWheelPlaybackEngine : IAsyncDisposable
{
    // A paused whole-session macro can remain here indefinitely. Ten
    // milliseconds keeps refocus responsive without polling foreground and
    // process identity hundreds of times per second on a laptop.
    private const ulong TargetGuardPollMicroseconds = 10_000;
    // A malformed or legacy zero-duration timeline must never turn an
    // infinite repeat into an AboveNormal-priority busy loop. The playable
    // boundary rejects empty recordings; this floor is the final defense for
    // any very short nonempty timeline that is repeated indefinitely.
    private const ulong MinimumInfiniteInterLoopDelayMicroseconds = 10_000;
    private const string UnsafeHeldInputPauseMessage =
        "Playback stopped before pausing because an injected key or mouse button was still held. Cleanup emitted the required global release input to avoid leaving Windows in a stuck state.";

    private readonly object _gate = new();
    private readonly ExactWheelInputInjector _injector;
    private readonly IPlaybackClock _clock;
    private readonly IPlaybackWaiter _waiter;
    private readonly IPhysicalInputState _physicalInput;
    private readonly Func<IExactWheelInputCapture> _captureFactory;
    private EventWaitHandle? _activeStopEvent;
    private Task<ExactWheelPlaybackResult>? _activeTask;
    private bool _disposed;

    internal ExactWheelPlaybackEngine()
        : this(
            new ExactWheelInputInjector(),
            new StopwatchPlaybackClock(),
            new Win32PlaybackWaiter(),
            new Win32PhysicalInputState(),
            static () => new LowLevelInputCapture())
    {
    }

    internal ExactWheelPlaybackEngine(
        ExactWheelInputInjector injector,
        IPlaybackClock clock,
        IPlaybackWaiter waiter,
        IPhysicalInputState physicalInput,
        Func<IExactWheelInputCapture> captureFactory)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _physicalInput = physicalInput ??
            throw new ArgumentNullException(nameof(physicalInput));
        _captureFactory = captureFactory ??
            throw new ArgumentNullException(nameof(captureFactory));
    }

    internal Task<ExactWheelPlaybackResult> PlayAsync(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var ordered = new ExactWheelRecording(
            recording.DurationMicroseconds,
            recording.Display,
            recording.Target,
            recording.Events
                .OrderBy(inputEvent => inputEvent.TimestampMicroseconds)
                .ThenBy(inputEvent => inputEvent.Sequence));
        ExactWheelRecordingValidator.ValidatePlayable(ordered);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeTask is { IsCompleted: false })
                throw new InvalidOperationException("Playback is already running.");
            _activeStopEvent?.Dispose();
            _activeStopEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset);
            var stopEvent = _activeStopEvent;
            _activeTask = Task.Factory.StartNew(
                    () => Run(
                        ordered,
                        options,
                        stopEvent,
                        cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
            return _activeTask;
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
            _activeStopEvent?.Set();
    }

    public async ValueTask DisposeAsync()
    {
        Task<ExactWheelPlaybackResult>? active;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _activeStopEvent?.Set();
            active = _activeTask;
        }

        if (active is not null)
        {
            try
            {
                _ = await active.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposal has already requested a safe stop.
            }
        }

        lock (_gate)
        {
            _activeStopEvent?.Dispose();
            _activeStopEvent = null;
        }

        _waiter.Dispose();
        _injector.Dispose();
    }

    private async Task<ExactWheelPlaybackResult> Run(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((EventWaitHandle)state!).Set(),
            stopEvent);
        using var interventionEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset);
        using var interventionCapture = options.StopOnPhysicalInput ||
            options.PauseOnPhysicalInput
            ? _captureFactory()
            : null;
        try
        {
            if (options.EnforcePhysicalInputRelease &&
                !_physicalInput.AreReleased(
                    options.WaitForReleaseVirtualKeys))
            {
                return Result(
                    ExactWheelPlaybackStopReason.PhysicalInputHeld,
                    message: "Release unrelated held keys and mouse buttons before playback.");
            }

            interventionCapture?.StartInterventionMonitor(interventionEvent);
            return RunWorker(
                recording,
                options,
                stopEvent,
                options.StopOnPhysicalInput || options.PauseOnPhysicalInput
                    ? interventionEvent
                    : null);
        }
        catch (Win32Exception exception)
        {
            return Result(
                ExactWheelPlaybackStopReason.TimerFailed,
                exception.NativeErrorCode,
                message: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Result(
                ExactWheelPlaybackStopReason.InvalidTimeline,
                message: exception.Message);
        }
        finally
        {
            interventionCapture?.StopInterventionMonitor();
        }
    }

    private ExactWheelPlaybackResult RunWorker(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (Exception exception) when (
            exception is ThreadStateException or
                System.Security.SecurityException)
        {
            // Timing remains safe at the default priority; dangerous lateness
            // still aborts instead of bursting stale input.
        }

        if (!WaitForControlRelease(
                options,
                stopEvent,
                interventionEvent))
        {
            return Cleanup(Result(
                ExactWheelPlaybackStopReason.Cancelled,
                message: "Playback was cancelled before input began."));
        }

        interventionEvent?.Reset();
        if (options.EnforcePhysicalInputRelease &&
            !_physicalInput.AreReleased(Array.Empty<int>()))
        {
            return Cleanup(Result(
                ExactWheelPlaybackStopReason.PhysicalInputHeld,
                message: "Release held keys and mouse buttons before playback."));
        }

        var frequency = _clock.Frequency;
        var origin = _clock.Timestamp;
        if (frequency <= 0 || origin < 0)
        {
            return Cleanup(Result(
                ExactWheelPlaybackStopReason.InvalidTimeline,
                message: "The playback clock is invalid."));
        }
        var interLoopDelayMicroseconds = options.Infinite
            ? Math.Max(
                options.InterLoopDelayMicroseconds,
                MinimumInfiniteInterLoopDelayMicroseconds)
            : options.InterLoopDelayMicroseconds;

        var result = Result(ExactWheelPlaybackStopReason.Completed);
        ulong loopIndex = 0;
        while (result.Reason == ExactWheelPlaybackStopReason.Completed &&
               (options.Infinite || loopIndex < options.LoopCount))
        {
            for (var eventIndex = 0;
                 result.Reason == ExactWheelPlaybackStopReason.Completed &&
                 eventIndex < recording.Events.Count;
                 eventIndex++)
            {
                if (stopEvent.WaitOne(0))
                {
                    result = Result(
                        ExactWheelPlaybackStopReason.Cancelled,
                        loopIndex: loopIndex,
                        eventIndex: eventIndex);
                    break;
                }

                if (!options.PauseOnPhysicalInput &&
                    interventionEvent?.WaitOne(0) == true)
                {
                    result = Result(
                        ExactWheelPlaybackStopReason.PhysicalIntervention,
                        loopIndex: loopIndex,
                        eventIndex: eventIndex);
                    break;
                }

                var inputEvent = recording.Events[eventIndex];
                while (result.Reason == ExactWheelPlaybackStopReason.Completed)
                {
                    long deadline;
                    try
                    {
                        deadline = ExactWheelTiming.PlaybackDeadlineTicks(
                            origin,
                            loopIndex,
                            recording.DurationMicroseconds,
                            inputEvent.TimestampMicroseconds,
                            options.Rate,
                            interLoopDelayMicroseconds,
                            frequency);
                    }
                    catch (Exception exception) when (
                        exception is OverflowException or
                            ArgumentException)
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.InvalidTimeline,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            message: exception.Message);
                        break;
                    }

                    var dispatchWait = WaitUntilDispatchReady(
                        ref deadline,
                        inputEvent,
                        options,
                        stopEvent,
                        interventionEvent,
                        frequency,
                        authorizeAtDeadline: true,
                        out var dispatchWaitError,
                        out var playbackPauseTicks);
                    if (dispatchWait != DispatchGuardWaitResult.Authorized)
                    {
                        result = Result(
                            dispatchWait switch
                            {
                                DispatchGuardWaitResult.Cancelled =>
                                    ExactWheelPlaybackStopReason.Cancelled,
                                DispatchGuardWaitResult.PhysicalIntervention =>
                                    ExactWheelPlaybackStopReason
                                        .PhysicalIntervention,
                                DispatchGuardWaitResult.TimerFailed =>
                                    ExactWheelPlaybackStopReason.TimerFailed,
                                _ => ExactWheelPlaybackStopReason.TargetLost
                            },
                            dispatchWaitError,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            message: dispatchWait switch
                            {
                                DispatchGuardWaitResult.UnsafeHeldInputs =>
                                    UnsafeHeldInputPauseMessage,
                                DispatchGuardWaitResult.Denied =>
                                    "The verified playback target is no longer available.",
                                _ =>
                                    "Playback stopped while waiting for the verified target."
                            });
                        break;
                    }

                    if (playbackPauseTicks > 0)
                    {
                        try
                        {
                            origin = checked(origin + playbackPauseTicks);
                        }
                        catch (OverflowException exception)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.InvalidTimeline,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: exception.Message);
                            break;
                        }
                    }

                    var lateness = ExactWheelTiming.TicksToMicroseconds(
                        Math.Max(0, _clock.Timestamp - deadline),
                        frequency);
                    if (options.DangerouslyLateMicroseconds != 0 &&
                        lateness > checked(
                            (long)options.DangerouslyLateMicroseconds))
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.DangerouslyLate,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            latenessMicroseconds: lateness,
                            message: "Playback stopped rather than bursting stale input after a stall.");
                        break;
                    }

                    if (stopEvent.WaitOne(0))
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.Cancelled,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex);
                        break;
                    }
                    if (interventionEvent?.WaitOne(0) == true)
                    {
                        if (_injector.HasHeldInputs &&
                            options.PauseOnPhysicalInput)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.TargetLost,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: UnsafeHeldInputPauseMessage);
                            break;
                        }
                        if (!options.PauseOnPhysicalInput)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason
                                    .PhysicalIntervention,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex);
                            break;
                        }
                        continue;
                    }

                    // This event-aware authorization is deliberately the last
                    // operation before SendInput. It rechecks foreground,
                    // process ownership, and mouse hit-test after all timing
                    // work, narrowing the unavoidable native TOCTOU window.
                    var finalAuthorization = GetDispatchAuthorization(
                        options,
                        inputEvent);
                    if (finalAuthorization !=
                        ExactWheelDispatchAuthorization.Authorized)
                    {
                        if (finalAuthorization ==
                            ExactWheelDispatchAuthorization
                                .TemporarilyUnavailable &&
                            _injector.HasHeldInputs)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.TargetLost,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: UnsafeHeldInputPauseMessage);
                            break;
                        }
                        if (finalAuthorization ==
                            ExactWheelDispatchAuthorization.Denied)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.TargetLost,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: "The verified playback target is no longer available.");
                            break;
                        }
                        continue;
                    }

                    var injected = _injector.Inject(
                        inputEvent,
                        recording.Display);
                    if (!injected.Succeeded)
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.InjectionFailed,
                            injected.Win32Error,
                            injected.Submitted,
                            injected.Expected,
                            loopIndex,
                            eventIndex,
                            lateness,
                            message: "SendInput accepted only part of an event; it was not retried.");
                        break;
                    }

                    try
                    {
                        options.Progress?.Report(
                            new ExactWheelPlaybackProgress(
                                loopIndex,
                                eventIndex,
                                recording.Events.Count,
                                inputEvent.TimestampMicroseconds,
                                lateness));
                    }
                    catch (Exception)
                    {
                        // Progress is advisory and cannot perturb input timing.
                    }
                    break;
                }
            }

            if (result.Reason != ExactWheelPlaybackStopReason.Completed)
                break;

            long loopEnd;
            try
            {
                loopEnd = ExactWheelTiming.PlaybackDeadlineTicks(
                    origin,
                    loopIndex,
                    recording.DurationMicroseconds,
                    recording.DurationMicroseconds,
                    options.Rate,
                    interLoopDelayMicroseconds,
                    frequency);
            }
            catch (Exception exception) when (
                exception is OverflowException or ArgumentException)
            {
                result = Result(
                    ExactWheelPlaybackStopReason.InvalidTimeline,
                    loopIndex: loopIndex,
                    message: exception.Message);
                break;
            }

            var endWait = WaitUntilDispatchReady(
                ref loopEnd,
                null,
                options,
                stopEvent,
                interventionEvent,
                frequency,
                authorizeAtDeadline: false,
                out var endWaitError,
                out var endPauseTicks);
            if (endWait != DispatchGuardWaitResult.Authorized)
            {
                result = Result(
                    endWait switch
                    {
                        DispatchGuardWaitResult.Cancelled =>
                            ExactWheelPlaybackStopReason.Cancelled,
                        DispatchGuardWaitResult.PhysicalIntervention =>
                            ExactWheelPlaybackStopReason.PhysicalIntervention,
                        DispatchGuardWaitResult.TimerFailed =>
                            ExactWheelPlaybackStopReason.TimerFailed,
                        _ => ExactWheelPlaybackStopReason.TargetLost
                    },
                    endWaitError,
                    loopIndex: loopIndex,
                    message: endWait ==
                        DispatchGuardWaitResult.UnsafeHeldInputs
                        ? UnsafeHeldInputPauseMessage
                        : string.Empty);
                break;
            }

            if (endPauseTicks > 0)
            {
                try
                {
                    origin = checked(origin + endPauseTicks);
                }
                catch (OverflowException exception)
                {
                    result = Result(
                        ExactWheelPlaybackStopReason.InvalidTimeline,
                        loopIndex: loopIndex,
                        message: exception.Message);
                    break;
                }
            }

            var endLateness = ExactWheelTiming.TicksToMicroseconds(
                Math.Max(0, _clock.Timestamp - loopEnd),
                frequency);
            if (options.DangerouslyLateMicroseconds != 0 &&
                endLateness > checked((long)options.DangerouslyLateMicroseconds))
            {
                result = Result(
                    ExactWheelPlaybackStopReason.DangerouslyLate,
                    loopIndex: loopIndex,
                    latenessMicroseconds: endLateness);
                break;
            }

            loopIndex++;
            if (loopIndex == 0)
            {
                result = Result(
                    ExactWheelPlaybackStopReason.InvalidTimeline,
                    message: "Playback loop count overflowed.");
            }
        }

        return Cleanup(result);
    }

    private bool WaitForControlRelease(
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent)
    {
        while (!_physicalInput.AreKeysReleased(
                   options.WaitForReleaseVirtualKeys))
        {
            if (stopEvent.WaitOne(5))
                return false;
        }

        return true;
    }

    private DispatchGuardWaitResult WaitUntilDispatchReady(
        ref long deadline,
        ExactWheelInputEvent? inputEvent,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        long frequency,
        bool authorizeAtDeadline,
        out int win32Error,
        out long playbackPauseTicks)
    {
        win32Error = 0;
        playbackPauseTicks = 0;
        while (true)
        {
            var waited = _waiter.WaitUntil(
                deadline,
                options.FinalSpinMicroseconds,
                _clock,
                stopEvent,
                interventionEvent,
                out win32Error);
            if (waited == DeadlineWaitResult.Cancelled)
                return DispatchGuardWaitResult.Cancelled;
            if (waited == DeadlineWaitResult.Failed)
                return DispatchGuardWaitResult.TimerFailed;
            if (waited == DeadlineWaitResult.PhysicalIntervention &&
                !options.PauseOnPhysicalInput)
            {
                return DispatchGuardWaitResult.PhysicalIntervention;
            }
            if (waited == DeadlineWaitResult.Reached && !authorizeAtDeadline)
                return DispatchGuardWaitResult.Authorized;

            var authorization = WaitForDispatchAuthorization(
                inputEvent,
                options,
                stopEvent,
                interventionEvent,
                frequency,
                out win32Error,
                out var currentPauseTicks);
            if (authorization != DispatchGuardWaitResult.Authorized)
                return authorization;
            if (currentPauseTicks == 0)
            {
                if (waited == DeadlineWaitResult.Reached)
                    return DispatchGuardWaitResult.Authorized;
                continue;
            }

            try
            {
                playbackPauseTicks = checked(
                    playbackPauseTicks + currentPauseTicks);
                deadline = checked(deadline + currentPauseTicks);
            }
            catch (OverflowException)
            {
                return DispatchGuardWaitResult.TimerFailed;
            }

            if (_clock.Timestamp >= deadline)
                return DispatchGuardWaitResult.Authorized;
        }
    }

    private DispatchGuardWaitResult WaitForDispatchAuthorization(
        ExactWheelInputEvent? inputEvent,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        long frequency,
        out int win32Error,
        out long playbackPauseTicks)
    {
        win32Error = 0;
        playbackPauseTicks = 0;
        long? pauseStarted = null;
        while (true)
        {
            if (stopEvent.WaitOne(0))
                return DispatchGuardWaitResult.Cancelled;

            var physicalIntervention =
                interventionEvent?.WaitOne(0) == true;
            if (physicalIntervention && !options.PauseOnPhysicalInput)
                return DispatchGuardWaitResult.PhysicalIntervention;
            if (physicalIntervention)
            {
                if (_injector.HasHeldInputs)
                    return DispatchGuardWaitResult.UnsafeHeldInputs;
                pauseStarted ??= _clock.Timestamp;
                // Reset first. A hook callback racing the physical-state read
                // will set the manual-reset event again and survive the final
                // signal check below instead of being accidentally erased.
                interventionEvent!.Reset();
                if (!_physicalInput.AreReleased(Array.Empty<int>()))
                    interventionEvent.Set();
            }

            var authorization = GetDispatchAuthorization(
                options,
                inputEvent);
            if (authorization == ExactWheelDispatchAuthorization.Denied)
                return DispatchGuardWaitResult.Denied;
            if (authorization ==
                    ExactWheelDispatchAuthorization.TemporarilyUnavailable &&
                _injector.HasHeldInputs)
            {
                return DispatchGuardWaitResult.UnsafeHeldInputs;
            }

            var interventionStillActive =
                interventionEvent?.WaitOne(0) == true;
            if (!interventionStillActive &&
                options.PauseOnPhysicalInput &&
                !_physicalInput.AreReleased(Array.Empty<int>()))
            {
                interventionEvent!.Set();
                interventionStillActive = true;
            }
            if (interventionStillActive && _injector.HasHeldInputs)
                return DispatchGuardWaitResult.UnsafeHeldInputs;
            if (authorization == ExactWheelDispatchAuthorization.Authorized &&
                !interventionStillActive)
            {
                if (pauseStarted is not null)
                {
                    playbackPauseTicks = Math.Max(
                        0,
                        _clock.Timestamp - pauseStarted.Value);
                }
                return DispatchGuardWaitResult.Authorized;
            }

            pauseStarted ??= _clock.Timestamp;
            long pollDeadline;
            try
            {
                pollDeadline = ExactWheelTiming.EventDeadlineTicks(
                    _clock.Timestamp,
                    TargetGuardPollMicroseconds,
                    ExactWheelPlaybackRate.Recorded,
                    frequency);
            }
            catch (Exception exception) when (
                exception is OverflowException or ArgumentException)
            {
                return DispatchGuardWaitResult.TimerFailed;
            }

            var waited = _waiter.WaitUntil(
                pollDeadline,
                0,
                _clock,
                stopEvent,
                options.PauseOnPhysicalInput ? null : interventionEvent,
                out win32Error);
            if (waited == DeadlineWaitResult.Cancelled)
                return DispatchGuardWaitResult.Cancelled;
            if (waited == DeadlineWaitResult.PhysicalIntervention)
                return DispatchGuardWaitResult.PhysicalIntervention;
            if (waited == DeadlineWaitResult.Failed)
                return DispatchGuardWaitResult.TimerFailed;
        }
    }

    private ExactWheelPlaybackResult Cleanup(
        ExactWheelPlaybackResult result)
    {
        // SendInput state is global, so a held transition must always receive
        // its matching release—even after fail-closed target loss. The unsafe
        // pause path stops every further recorded event first; this cleanup
        // release is the unavoidable action that prevents a system-wide stuck
        // modifier or mouse button and is surfaced in that result's message.
        var cleanup = _injector.ReleaseHeld();
        if (cleanup.Succeeded)
            return result with { CleanupSucceeded = true };

        return result with
        {
            Reason = result.Reason == ExactWheelPlaybackStopReason.Completed
                ? ExactWheelPlaybackStopReason.CleanupFailed
                : result.Reason,
            CleanupSucceeded = false,
            Win32Error = result.Win32Error == 0
                ? cleanup.Win32Error
                : result.Win32Error,
            Submitted = result.Win32Error == 0
                ? cleanup.Submitted
                : result.Submitted,
            Expected = result.Win32Error == 0
                ? cleanup.Expected
                : result.Expected,
            Message = string.IsNullOrEmpty(result.Message)
                ? "ExactWheel could not release every held injected input."
                : string.Concat(
                    result.Message,
                    " ExactWheel could not release every held injected input.")
        };
    }

    private static ExactWheelDispatchAuthorization GetDispatchAuthorization(
        ExactWheelPlaybackOptions options,
        ExactWheelInputEvent? inputEvent)
    {
        try
        {
            if (options.EventDispatchAuthorization is not null &&
                inputEvent is { } currentEvent)
            {
                return NormalizeAuthorization(
                    options.EventDispatchAuthorization(currentEvent));
            }
            if (options.DispatchAuthorization is not null)
            {
                return NormalizeAuthorization(
                    options.DispatchAuthorization());
            }
            return options.PreDispatchGuard?.Invoke() != false
                ? ExactWheelDispatchAuthorization.Authorized
                : ExactWheelDispatchAuthorization.Denied;
        }
        catch (Exception)
        {
            return ExactWheelDispatchAuthorization.Denied;
        }
    }

    private static ExactWheelDispatchAuthorization NormalizeAuthorization(
        ExactWheelDispatchAuthorization authorization) =>
        authorization switch
        {
            ExactWheelDispatchAuthorization.Authorized =>
                ExactWheelDispatchAuthorization.Authorized,
            ExactWheelDispatchAuthorization.TemporarilyUnavailable =>
                ExactWheelDispatchAuthorization.TemporarilyUnavailable,
            _ => ExactWheelDispatchAuthorization.Denied
        };

    private static void ValidateOptions(ExactWheelPlaybackOptions options)
    {
        var dispatchGuardCount =
            (options.PreDispatchGuard is null ? 0 : 1) +
            (options.DispatchAuthorization is null ? 0 : 1) +
            (options.EventDispatchAuthorization is null ? 0 : 1);
        if (options.Rate.Numerator == 0 ||
            options.Rate.Denominator == 0 ||
            options.LoopCount == 0 ||
            options.InterLoopDelayMicroseconds >
                ExactWheelLimits.MaximumDurationMicroseconds ||
            options.FinalSpinMicroseconds > 10_000 ||
            options.DangerouslyLateMicroseconds > long.MaxValue ||
            (options.StopOnPhysicalInput && options.PauseOnPhysicalInput) ||
            dispatchGuardCount > 1)
        {
            throw new ArgumentException(
                "Playback options are outside the supported limits.",
                nameof(options));
        }
    }

    private static ExactWheelPlaybackResult Result(
        ExactWheelPlaybackStopReason reason,
        int win32Error = 0,
        uint submitted = 0,
        uint expected = 0,
        ulong loopIndex = 0,
        int eventIndex = 0,
        long latenessMicroseconds = 0,
        string message = "") =>
        new(
            reason,
            win32Error,
            submitted,
            expected,
            loopIndex,
            eventIndex,
            latenessMicroseconds,
            CleanupSucceeded: true,
            message);

    private enum DispatchGuardWaitResult
    {
        Authorized,
        Denied,
        Cancelled,
        PhysicalIntervention,
        UnsafeHeldInputs,
        TimerFailed
    }
}
