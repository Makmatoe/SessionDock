using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    private readonly nint[] _nativeHandles = new nint[3];
    private readonly WaitHandle[] _managedCancellationOnly = new WaitHandle[1];
    private readonly WaitHandle[] _managedWithIntervention = new WaitHandle[2];
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

                _nativeHandles[0] =
                    cancellationEvent.SafeWaitHandle.DangerousGetHandle();
                var timerIndex = interventionEvent is null ? 1 : 2;
                if (interventionEvent is not null)
                {
                    _nativeHandles[1] =
                        interventionEvent.SafeWaitHandle.DangerousGetHandle();
                }
                _nativeHandles[timerIndex] = _timer!.DangerousGetHandle();
                var waited = ExactWheelNativeMethods.WaitForMultipleObjects(
                    checked((uint)(timerIndex + 1)),
                    _nativeHandles,
                    waitAll: false,
                    ExactWheelNativeMethods.Infinite);
                if (waited == ExactWheelNativeMethods.WaitObject0)
                    return DeadlineWaitResult.Cancelled;
                if (interventionEvent is not null &&
                    waited == ExactWheelNativeMethods.WaitObject0 + 1)
                {
                    return DeadlineWaitResult.PhysicalIntervention;
                }

                if (waited == ExactWheelNativeMethods.WaitObject0 +
                    checked((uint)timerIndex))
                    continue;
                win32Error = Marshal.GetLastWin32Error();
                return DeadlineWaitResult.Failed;
            }

            var timeoutMilliseconds = checked((int)Math.Clamp(
                (coarseMicroseconds + 999L) / 1_000L,
                1L,
                int.MaxValue));
            _managedCancellationOnly[0] = cancellationEvent;
            _managedWithIntervention[0] = cancellationEvent;
            _managedWithIntervention[1] = interventionEvent!;
            var managedResult = WaitHandle.WaitAny(
                interventionEvent is null
                    ? _managedCancellationOnly
                    : _managedWithIntervention,
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
    // infinite repeat into a dedicated-thread busy loop. The playable
    // boundary rejects empty recordings; this floor is the final defense for
    // any very short nonempty timeline that is repeated indefinitely.
    private const ulong MinimumInfiniteInterLoopDelayMicroseconds = 10_000;
    private static readonly TimeSpan FocusTransitionPollInterval =
        TimeSpan.FromMilliseconds(50);
    private const string UnsafeHeldInputPauseMessage =
        "Playback stopped before pausing because an injected key or mouse button was still held. Cleanup emitted the required global release input to avoid leaving Windows in a stuck state.";

    private readonly object _gate = new();
    private readonly ExactWheelInputInjector _injector;
    private readonly IPlaybackClock _clock;
    private readonly IPlaybackWaiter _waiter;
    private readonly IPhysicalInputState _physicalInput;
    private readonly Func<IExactWheelInputCapture> _captureFactory;
    private readonly AutoResetEvent _playbackRequested = new(false);
    private EventWaitHandle? _activeStopEvent;
    private EventWaitHandle? _activeInterventionEvent;
    private Task<ExactWheelPlaybackResult>? _activeTask;
    private PlaybackRequest? _pendingRequest;
    private Task? _workerTask;
    private Exception? _workerFailure;
    private IExactWheelInputCapture? _retainedInterventionCapture;
    private Task? _interventionSequenceEndTask;
    private long _interventionSequenceGeneration;
    private bool _interventionSequencePhysicalInputPrevalidated;
    private bool _interventionSequenceEnding;
    private bool _workerShutdownRequested;
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

        var ordered = PreparePlaybackRecording(recording);
        options.CoordinateTransform?.ValidateRecording(ordered);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_interventionSequenceEnding)
            {
                throw new InvalidOperationException(
                    "The retained intervention monitor is stopping.");
            }
            if (_activeTask is { IsCompleted: false })
                throw new InvalidOperationException("Playback is already running.");
            if (_retainedInterventionCapture is not null &&
                (options.StopOnPhysicalInput || options.PauseOnPhysicalInput) &&
                options.WaitForReleaseVirtualKeys.Count != 0)
            {
                throw new InvalidOperationException(
                    "A retained playback sequence cannot arm from held control keys. Release the keys before beginning the sequence or use one-off playback.");
            }
            if (_activeStopEvent is null)
            {
                _activeStopEvent = new EventWaitHandle(
                    initialState: false,
                    EventResetMode.ManualReset);
            }
            else
            {
                // A shared ExactWheel session plays each client serially.
                // Reuse its cancellation kernel handle instead of creating
                // and closing one for every client on every loop.
                _activeStopEvent.Reset();
            }
            var stopEvent = _activeStopEvent;
            EventWaitHandle? interventionEvent = null;
            var interventionMonitorRetained = false;
            if (options.StopOnPhysicalInput || options.PauseOnPhysicalInput)
            {
                if (_activeInterventionEvent is null)
                {
                    _activeInterventionEvent = new EventWaitHandle(
                        initialState: false,
                        EventResetMode.ManualReset);
                }
                else if (_retainedInterventionCapture is null)
                {
                    _activeInterventionEvent.Reset();
                }
                interventionMonitorRetained =
                    _retainedInterventionCapture is not null;
                if (interventionMonitorRetained &&
                    _retainedInterventionCapture!.Mode !=
                        InputCaptureMode.Intervention)
                {
                    throw new InvalidOperationException(
                        "The retained intervention monitor is no longer active.");
                }
                interventionEvent = _activeInterventionEvent;
            }
            EnsureWorkerLocked();
            if (_pendingRequest is not null)
                throw new InvalidOperationException("Playback is already queued.");
            var completion = new TaskCompletionSource<
                ExactWheelPlaybackResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequest = new PlaybackRequest(
                ordered,
                options,
                stopEvent,
                interventionEvent,
                interventionMonitorRetained,
                cancellationToken,
                completion);
            _activeTask = completion.Task;
            _playbackRequested.Set();
            return _activeTask;
        }
    }

    internal IAsyncDisposable BeginInterventionSequence()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workerFailure is not null)
            {
                throw new InvalidOperationException(
                    "The ExactWheel playback worker stopped after an unexpected failure.",
                    _workerFailure);
            }
            if (_activeTask is { IsCompleted: false } ||
                _retainedInterventionCapture is not null ||
                _interventionSequenceEnding)
            {
                throw new InvalidOperationException(
                    "Finish the active playback sequence first.");
            }

            _activeInterventionEvent ??= new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset);
            _activeInterventionEvent.Reset();
            var capture = _captureFactory();
            var physicalInputPrevalidated = false;
            try
            {
                capture.StartInterventionMonitor(_activeInterventionEvent);
                physicalInputPrevalidated = ArePhysicalInputsReleased(
                    capture,
                    Array.Empty<int>());
            }
            catch
            {
                try
                {
                    capture.Dispose();
                }
                catch
                {
                    // Preserve the hook-installation failure as the primary
                    // error. The capture was never published to the session.
                }
                throw;
            }

            _retainedInterventionCapture = capture;
            _interventionSequencePhysicalInputPrevalidated =
                physicalInputPrevalidated;
            _interventionSequenceGeneration = unchecked(
                _interventionSequenceGeneration + 1);
            return new InterventionSequenceLease(
                this,
                _interventionSequenceGeneration);
        }
    }

    internal async Task<ExactWheelFocusTransitionWaitResult>
        WaitForFocusTransitionAsync(
        Func<ExactWheelDispatchAuthorization> resumeAuthorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resumeAuthorization);
        cancellationToken.ThrowIfCancellationRequested();

        IExactWheelInputCapture? capture;
        EventWaitHandle? interventionEvent;
        bool physicalInputPrevalidated;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_interventionSequenceEnding ||
                _retainedInterventionCapture is null ||
                _activeInterventionEvent is null)
            {
                throw new InvalidOperationException(
                    "A retained intervention monitor is required before a focus transition.");
            }

            capture = _retainedInterventionCapture;
            interventionEvent = _activeInterventionEvent;
            physicalInputPrevalidated =
                _interventionSequencePhysicalInputPrevalidated;
        }

        if (!IsInterventionMonitorHealthy(capture))
        {
            return ExactWheelFocusTransitionWaitResult
                .InterventionMonitorUnavailable;
        }

        var physicalInputReleased = ArePhysicalInputsReleased(
            capture,
            Array.Empty<int>());
        if (!physicalInputPrevalidated)
        {
            if (!physicalInputReleased)
            {
                return ExactWheelFocusTransitionWaitResult
                    .PhysicalInputHeld;
            }

            MarkRetainedPhysicalInputPrevalidated(capture);
        }

        // The common serial transition performs no polling or authorization
        // work. Only a physical signal (including one that arrived between two
        // PlayAsync calls) latches the explicit-user-resume path.
        if (physicalInputReleased && !interventionEvent.WaitOne(0))
            return ExactWheelFocusTransitionWaitResult.Ready;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInterventionMonitorHealthy(capture))
            {
                return ExactWheelFocusTransitionWaitResult
                    .InterventionMonitorUnavailable;
            }

            if (ArePhysicalInputsReleased(capture, Array.Empty<int>()))
            {
                var authorization = GetResumeAuthorization(
                    resumeAuthorization);
                if (authorization == ExactWheelDispatchAuthorization.Denied)
                {
                    return ExactWheelFocusTransitionWaitResult
                        .AuthorizationDenied;
                }
                if (authorization ==
                    ExactWheelDispatchAuthorization.Authorized)
                {
                    // Reset first, then recheck all state. A hook callback that
                    // races the reset sets the manual-reset event again and
                    // keeps the transition paused instead of being lost.
                    interventionEvent.Reset();
                    if (IsInterventionMonitorHealthy(capture) &&
                        ArePhysicalInputsReleased(
                            capture,
                            Array.Empty<int>()) &&
                        !interventionEvent.WaitOne(0) &&
                        GetResumeAuthorization(resumeAuthorization) ==
                            ExactWheelDispatchAuthorization.Authorized)
                    {
                        return ExactWheelFocusTransitionWaitResult.Ready;
                    }
                }
            }

            await Task.Delay(
                    FocusTransitionPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal bool IsProgrammaticFocusAllowed()
    {
        IExactWheelInputCapture? capture;
        EventWaitHandle? interventionEvent;
        bool physicalInputPrevalidated;
        lock (_gate)
        {
            if (_disposed ||
                _interventionSequenceEnding ||
                _retainedInterventionCapture is null ||
                _activeInterventionEvent is null)
            {
                return false;
            }

            capture = _retainedInterventionCapture;
            interventionEvent = _activeInterventionEvent;
            physicalInputPrevalidated =
                _interventionSequencePhysicalInputPrevalidated;
        }

        return physicalInputPrevalidated &&
            IsInterventionMonitorHealthy(capture) &&
            ArePhysicalInputsReleased(capture, Array.Empty<int>()) &&
            !interventionEvent.WaitOne(0);
    }

    internal void RequestStop()
    {
        lock (_gate)
            _activeStopEvent?.Set();
    }

    public async ValueTask DisposeAsync()
    {
        Task<ExactWheelPlaybackResult>? active;
        Task? interventionSequenceEnd;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _activeStopEvent?.Set();
            active = _activeTask;
            interventionSequenceEnd = _interventionSequenceEndTask;
        }

        Exception? disposalFailure = null;
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
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
        }

        if (interventionSequenceEnd is not null)
        {
            try
            {
                await interventionSequenceEnd.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposalFailure ??= exception;
            }
        }

        var retainedCapture = DetachRetainedInterventionCapture();
        StopAndDisposeInterventionCapture(
            retainedCapture,
            ref disposalFailure);

        Task? worker;
        lock (_gate)
        {
            _workerShutdownRequested = true;
            _playbackRequested.Set();
            worker = _workerTask;
        }
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposalFailure ??= exception;
            }
        }

        lock (_gate)
        {
            _activeStopEvent?.Dispose();
            _activeStopEvent = null;
            _activeInterventionEvent?.Dispose();
            _activeInterventionEvent = null;
        }

        _playbackRequested.Dispose();
        _waiter.Dispose();
        _injector.Dispose();
        if (disposalFailure is not null)
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
    }

    private void EnsureWorkerLocked()
    {
        if (_workerFailure is not null)
        {
            throw new InvalidOperationException(
                "The ExactWheel playback worker stopped after an unexpected failure.",
                _workerFailure);
        }
        if (_workerTask is { IsCompleted: false })
            return;
        if (_workerTask is not null)
        {
            throw new InvalidOperationException(
                "The ExactWheel playback worker is no longer available.",
                _workerTask.Exception?.GetBaseException());
        }

        _workerTask = Task.Factory.StartNew(
            WorkerLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void WorkerLoop()
    {
        if (Thread.CurrentThread.Name is null)
            Thread.CurrentThread.Name = "SessionDock ExactWheel playback";
        while (true)
        {
            _playbackRequested.WaitOne();
            PlaybackRequest? request;
            lock (_gate)
            {
                request = _pendingRequest;
                _pendingRequest = null;
                if (request is null && _workerShutdownRequested)
                    return;
            }
            if (request is null)
                continue;

            try
            {
                var result = Run(
                    request.Recording,
                    request.Options,
                    request.StopEvent,
                    request.InterventionEvent,
                    request.InterventionMonitorRetained,
                    request.CancellationToken);
                request.Completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                // Publish terminal failure while holding the same gate used by
                // PlayAsync before waking the faulted request's continuations.
                // Otherwise a retry could enqueue into the brief interval
                // before this worker Task itself reaches IsCompleted.
                lock (_gate)
                    _workerFailure = exception;
                request.Completion.TrySetException(exception);
                throw;
            }
        }
    }

    private ExactWheelPlaybackResult Run(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        bool interventionMonitorRetained,
        CancellationToken cancellationToken)
    {
        IExactWheelInputCapture? retainedInterventionCapture = null;
        var physicalInputPrevalidated = false;
        if (interventionMonitorRetained)
        {
            lock (_gate)
            {
                retainedInterventionCapture = _retainedInterventionCapture;
                physicalInputPrevalidated =
                    _interventionSequencePhysicalInputPrevalidated;
            }
            if (!IsInterventionMonitorHealthy(
                    retainedInterventionCapture))
            {
                return Result(
                    ExactWheelPlaybackStopReason.PhysicalIntervention,
                    message: "The physical-input intervention monitor stopped unexpectedly.");
            }
        }
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((EventWaitHandle)state!).Set(),
            stopEvent);
        using var interventionCapture =
            (options.StopOnPhysicalInput || options.PauseOnPhysicalInput) &&
            !interventionMonitorRetained
            ? _captureFactory()
            : null;
        var activeInterventionCapture =
            retainedInterventionCapture ?? interventionCapture;
        try
        {
            if (options.EnforcePhysicalInputRelease &&
                !physicalInputPrevalidated &&
                activeInterventionCapture is not
                    ITrackedPhysicalInputState)
            {
                if (!_physicalInput.AreReleased(
                        options.WaitForReleaseVirtualKeys))
                {
                    return Result(
                        ExactWheelPlaybackStopReason.PhysicalInputHeld,
                        message: "Release unrelated held keys and mouse buttons before playback.");
                }
            }

            if (interventionEvent is not null &&
                !interventionMonitorRetained)
            {
                interventionCapture?.StartInterventionMonitor(interventionEvent);
            }
            return RunWorker(
                recording,
                options,
                stopEvent,
                options.StopOnPhysicalInput || options.PauseOnPhysicalInput
                    ? interventionEvent
                    : null,
                activeInterventionCapture,
                physicalInputPrevalidated);
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

    private ValueTask EndInterventionSequenceAsync(long generation)
    {
        lock (_gate)
        {
            if (_disposed ||
                _retainedInterventionCapture is null ||
                generation != _interventionSequenceGeneration)
            {
                return ValueTask.CompletedTask;
            }
            if (_interventionSequenceEndTask is not null)
                return new ValueTask(_interventionSequenceEndTask);

            _interventionSequenceEnding = true;
            _activeStopEvent?.Set();
            _interventionSequenceEndTask =
                EndInterventionSequenceCoreAsync(
                    generation,
                    _activeTask);
            return new ValueTask(_interventionSequenceEndTask);
        }
    }

    private async Task EndInterventionSequenceCoreAsync(
        long generation,
        Task<ExactWheelPlaybackResult>? active)
    {
        // Hook shutdown can join its message-pump thread. Always move that
        // bounded teardown away from the WPF caller and publish this Task
        // before any concurrent disposer can observe the sequence ending.
        await Task.CompletedTask.ConfigureAwait(
            ConfigureAwaitOptions.ForceYielding);
        Exception? failure = null;
        if (active is { IsCompleted: false })
        {
            try
            {
                _ = await active.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Sequence disposal requested the safe stop.
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        IExactWheelInputCapture? capture;
        lock (_gate)
        {
            capture = generation == _interventionSequenceGeneration
                ? _retainedInterventionCapture
                : null;
            if (capture is not null)
            {
                _retainedInterventionCapture = null;
                _interventionSequencePhysicalInputPrevalidated = false;
            }
        }
        StopAndDisposeInterventionCapture(capture, ref failure);
        lock (_gate)
        {
            if (generation == _interventionSequenceGeneration)
            {
                _interventionSequenceEnding = false;
                _interventionSequenceEndTask = null;
            }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private IExactWheelInputCapture? DetachRetainedInterventionCapture()
    {
        lock (_gate)
        {
            var capture = _retainedInterventionCapture;
            _retainedInterventionCapture = null;
            _interventionSequencePhysicalInputPrevalidated = false;
            return capture;
        }
    }

    private static void StopAndDisposeInterventionCapture(
        IExactWheelInputCapture? capture,
        ref Exception? failure)
    {
        if (capture is null)
            return;
        try
        {
            capture.StopInterventionMonitor();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        try
        {
            capture.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
    }

    private ExactWheelPlaybackResult RunWorker(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        IExactWheelInputCapture? retainedInterventionCapture,
        bool physicalInputPrevalidated)
    {
        if (!IsInterventionMonitorHealthy(retainedInterventionCapture))
        {
            return Cleanup(Result(
                ExactWheelPlaybackStopReason.PhysicalIntervention,
                message: "The physical-input intervention monitor stopped unexpectedly."));
        }
        if (!WaitForControlRelease(
                options,
                stopEvent,
                interventionEvent,
                retainedInterventionCapture))
        {
            if (!IsInterventionMonitorHealthy(
                    retainedInterventionCapture))
            {
                return Cleanup(Result(
                    ExactWheelPlaybackStopReason.PhysicalIntervention,
                    message: "The physical-input intervention monitor stopped unexpectedly."));
            }
            return Cleanup(Result(
                ExactWheelPlaybackStopReason.Cancelled,
                message: "Playback was cancelled before input began."));
        }

        if (retainedInterventionCapture is null)
            interventionEvent?.Reset();
        if (options.EnforcePhysicalInputRelease &&
            !physicalInputPrevalidated)
        {
            if (!ArePhysicalInputsReleased(
                    retainedInterventionCapture,
                    Array.Empty<int>()))
            {
                return Cleanup(Result(
                    ExactWheelPlaybackStopReason.PhysicalInputHeld,
                    message: "Release held keys and mouse buttons before playback."));
            }

            MarkRetainedPhysicalInputPrevalidated(
                retainedInterventionCapture);
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
        var playbackTopology = options.CoordinateTransform?
            .DestinationDisplay ?? recording.Display;

        var result = Result(ExactWheelPlaybackStopReason.Completed);
        var progress = new PlaybackProgressReporter(
            options.Progress,
            options.ProgressIntervalMicroseconds,
            frequency);
        ulong loopIndex = 0;
        while (result.Reason == ExactWheelPlaybackStopReason.Completed &&
               (options.Infinite || loopIndex < options.LoopCount))
        {
            for (var eventIndex = 0;
                 result.Reason == ExactWheelPlaybackStopReason.Completed &&
                 eventIndex < recording.Events.Count;
                 eventIndex++)
            {
                var timelineEvent = recording.Events[eventIndex];
                if (!TryTransformEvent(
                        options,
                        timelineEvent,
                        out var inputEvent,
                        out var transformError))
                {
                    result = Result(
                        ExactWheelPlaybackStopReason.InvalidTimeline,
                        loopIndex: loopIndex,
                        eventIndex: eventIndex,
                        message: transformError);
                    break;
                }
                while (result.Reason == ExactWheelPlaybackStopReason.Completed)
                {
                    long deadline;
                    try
                    {
                        deadline = ExactWheelTiming.PlaybackDeadlineTicks(
                            origin,
                            loopIndex,
                            recording.DurationMicroseconds,
                            timelineEvent.TimestampMicroseconds,
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
                        retainedInterventionCapture,
                        frequency,
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

                    try
                    {
                        var coalesced = CoalesceOverdueMouseMoves(
                            recording,
                            options,
                            origin,
                            loopIndex,
                            interLoopDelayMicroseconds,
                            frequency,
                            ref eventIndex,
                            ref timelineEvent,
                            ref deadline);
                        if (coalesced &&
                            !TryTransformEvent(
                                options,
                                timelineEvent,
                                out inputEvent,
                                out transformError))
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.InvalidTimeline,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: transformError);
                            break;
                        }
                    }
                    catch (Exception exception) when (
                        exception is OverflowException or ArgumentException)
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.InvalidTimeline,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            message: exception.Message);
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
                        ResetPhysicalPauseSignal(
                            interventionEvent,
                            retainedInterventionCapture);
                        continue;
                    }

                    // This single event-aware authorization is deliberately
                    // kept immediately before final scheduler accounting and
                    // SendInput. It rechecks foreground, process ownership,
                    // and mouse hit-test after the wait and move coalescing,
                    // narrowing the unavoidable native TOCTOU window.
                    var finalAuthorization = GetDispatchAuthorization(
                        options,
                        inputEvent);
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
                    if (finalAuthorization ==
                        ExactWheelDispatchAuthorization.TemporarilyUnavailable)
                    {
                        if (_injector.HasHeldInputs)
                        {
                            result = Result(
                                ExactWheelPlaybackStopReason.TargetLost,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: UnsafeHeldInputPauseMessage);
                            break;
                        }

                        var authorizationWait = WaitForDispatchAuthorization(
                            inputEvent,
                            options,
                            stopEvent,
                            interventionEvent,
                            retainedInterventionCapture,
                            frequency,
                            out var authorizationError,
                            out var authorizationPauseTicks);
                        if (authorizationWait !=
                            DispatchGuardWaitResult.Authorized)
                        {
                            result = Result(
                                authorizationWait switch
                                {
                                    DispatchGuardWaitResult.Cancelled =>
                                        ExactWheelPlaybackStopReason.Cancelled,
                                    DispatchGuardWaitResult
                                        .PhysicalIntervention =>
                                        ExactWheelPlaybackStopReason
                                            .PhysicalIntervention,
                                    DispatchGuardWaitResult.TimerFailed =>
                                        ExactWheelPlaybackStopReason.TimerFailed,
                                    _ => ExactWheelPlaybackStopReason.TargetLost
                                },
                                authorizationError,
                                loopIndex: loopIndex,
                                eventIndex: eventIndex,
                                message: authorizationWait switch
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

                        try
                        {
                            origin = checked(origin + authorizationPauseTicks);
                            deadline = checked(
                                deadline + authorizationPauseTicks);
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
                        continue;
                    }

                    // Authorization can occasionally perform a scheduled
                    // full process-identity verification. Account for any
                    // time spent there before injecting this event so a slow
                    // verification cannot collapse the following held-input
                    // transition or bypass dangerous-lateness handling.
                    if (!TryBoundSchedulerLateness(
                            options,
                            frequency,
                            ref origin,
                            ref deadline,
                            out var lateness,
                            out var recoveryError))
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.InvalidTimeline,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            message: recoveryError);
                        break;
                    }
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

                    // Recheck only cheap signals after the potentially slow
                    // final identity authorization. This closes its local
                    // cancellation/intervention window without repeating the
                    // expensive trust verification.
                    if (stopEvent.WaitOne(0))
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason.Cancelled,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex);
                        break;
                    }
                    if (!IsInterventionMonitorHealthy(
                            retainedInterventionCapture))
                    {
                        result = Result(
                            ExactWheelPlaybackStopReason
                                .PhysicalIntervention,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex,
                            message: "The physical-input intervention monitor stopped unexpectedly.");
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
                        if (options.PauseOnPhysicalInput)
                        {
                            ResetPhysicalPauseSignal(
                                interventionEvent,
                                retainedInterventionCapture);
                            continue;
                        }
                        result = Result(
                            ExactWheelPlaybackStopReason
                                .PhysicalIntervention,
                            loopIndex: loopIndex,
                            eventIndex: eventIndex);
                        break;
                    }

                    var injected = _injector.Inject(
                        inputEvent,
                        playbackTopology);
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

                    progress.Report(
                        _clock.Timestamp,
                        new ExactWheelPlaybackProgress(
                            loopIndex,
                            eventIndex,
                            recording.Events.Count,
                            timelineEvent.TimestampMicroseconds,
                            lateness));
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
                retainedInterventionCapture,
                frequency,
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

            if (!TryBoundSchedulerLateness(
                    options,
                    frequency,
                    ref origin,
                    ref loopEnd,
                    out var endLateness,
                    out var endRecoveryError))
            {
                result = Result(
                    ExactWheelPlaybackStopReason.InvalidTimeline,
                    loopIndex: loopIndex,
                    message: endRecoveryError);
                break;
            }
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

        progress.Flush();
        return Cleanup(result);
    }

    internal static ExactWheelRecording PreparePlaybackRecording(
        ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.IsValidated)
        {
            ExactWheelRecordingValidator.ValidatePlayable(recording);
            return recording;
        }
        if (ExactWheelRecordingValidator.IsInTimelineOrder(recording.Events))
        {
            ExactWheelRecordingValidator.ValidatePlayable(recording);
            return recording;
        }

        var ordered = ExactWheelRecordingValidator.Finalize(
            recording.Display,
            recording.Target,
            recording.Events,
            recording.DurationMicroseconds);
        ExactWheelRecordingValidator.ValidatePlayable(ordered);
        return ordered;
    }

    private bool CoalesceOverdueMouseMoves(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions options,
        long origin,
        ulong loopIndex,
        ulong interLoopDelayMicroseconds,
        long frequency,
        ref int eventIndex,
        ref ExactWheelInputEvent inputEvent,
        ref long deadline)
    {
        if (!options.CoalesceOverdueMouseMoves ||
            inputEvent.Type != ExactWheelInputEventType.MouseMove ||
            _injector.HasHeldInputs)
        {
            return false;
        }

        var now = _clock.Timestamp;
        var coalesced = false;
        while (eventIndex + 1 < recording.Events.Count)
        {
            var candidate = recording.Events[eventIndex + 1];
            if (candidate.Type != ExactWheelInputEventType.MouseMove)
                break;

            var candidateDeadline = ExactWheelTiming.PlaybackDeadlineTicks(
                origin,
                loopIndex,
                recording.DurationMicroseconds,
                candidate.TimestampMicroseconds,
                options.Rate,
                interLoopDelayMicroseconds,
                frequency);
            if (candidateDeadline > now)
                break;

            eventIndex++;
            coalesced = true;
            inputEvent = candidate;
            deadline = candidateDeadline;
        }

        return coalesced;
    }

    private static bool TryTransformEvent(
        ExactWheelPlaybackOptions options,
        ExactWheelInputEvent inputEvent,
        out ExactWheelInputEvent transformed,
        out string error)
    {
        try
        {
            transformed = options.CoordinateTransform?.TransformEvent(
                inputEvent) ?? inputEvent;
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                OverflowException)
        {
            transformed = default;
            error = exception.Message;
            return false;
        }
    }

    private bool TryBoundSchedulerLateness(
        ExactWheelPlaybackOptions options,
        long frequency,
        ref long origin,
        ref long deadline,
        out long latenessMicroseconds,
        out string error)
    {
        error = string.Empty;
        var now = _clock.Timestamp;
        var lateTicks = Math.Max(0, now - deadline);
        if (options.RecoverFromTimingStalls && lateTicks > 0)
        {
            var maximumCatchUpMicroseconds =
                options.DangerouslyLateMicroseconds == 0
                    ? options.MaximumCatchUpMicroseconds
                    : Math.Min(
                        options.MaximumCatchUpMicroseconds,
                        options.DangerouslyLateMicroseconds);
            long maximumCatchUpTicks;
            try
            {
                maximumCatchUpTicks = ExactWheelTiming.EventDeadlineTicks(
                    0,
                    maximumCatchUpMicroseconds,
                    ExactWheelPlaybackRate.Recorded,
                    frequency);
                if (lateTicks > maximumCatchUpTicks)
                {
                    var timelineShift = lateTicks - maximumCatchUpTicks;
                    origin = checked(origin + timelineShift);
                    deadline = checked(deadline + timelineShift);
                    lateTicks = maximumCatchUpTicks;
                }
            }
            catch (Exception exception) when (
                exception is OverflowException or ArgumentException)
            {
                latenessMicroseconds = 0;
                error = exception.Message;
                return false;
            }
        }

        latenessMicroseconds = ExactWheelTiming.TicksToMicroseconds(
            lateTicks,
            frequency);
        return true;
    }

    private bool WaitForControlRelease(
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        IExactWheelInputCapture? interventionCapture)
    {
        while (!AreControlKeysReleased(
                   interventionCapture,
                   options.WaitForReleaseVirtualKeys))
        {
            if (!IsInterventionMonitorHealthy(interventionCapture))
                return false;
            if (stopEvent.WaitOne(5))
                return false;
        }

        return IsInterventionMonitorHealthy(interventionCapture);
    }

    private DispatchGuardWaitResult WaitUntilDispatchReady(
        ref long deadline,
        ExactWheelInputEvent? inputEvent,
        ExactWheelPlaybackOptions options,
        EventWaitHandle stopEvent,
        EventWaitHandle? interventionEvent,
        IExactWheelInputCapture? retainedInterventionCapture,
        long frequency,
        out int win32Error,
        out long playbackPauseTicks)
    {
        win32Error = 0;
        playbackPauseTicks = 0;
        while (true)
        {
            if (!IsInterventionMonitorHealthy(
                    retainedInterventionCapture))
            {
                return DispatchGuardWaitResult.PhysicalIntervention;
            }
            var waited = _waiter.WaitUntil(
                deadline,
                options.FinalSpinMicroseconds,
                _clock,
                stopEvent,
                interventionEvent,
                out win32Error);
            if (!IsInterventionMonitorHealthy(
                    retainedInterventionCapture))
            {
                return DispatchGuardWaitResult.PhysicalIntervention;
            }
            if (waited == DeadlineWaitResult.Cancelled)
                return DispatchGuardWaitResult.Cancelled;
            if (waited == DeadlineWaitResult.Failed)
                return DispatchGuardWaitResult.TimerFailed;
            if (waited == DeadlineWaitResult.PhysicalIntervention &&
                !options.PauseOnPhysicalInput)
            {
                return DispatchGuardWaitResult.PhysicalIntervention;
            }
            if (waited == DeadlineWaitResult.Reached)
                return DispatchGuardWaitResult.Authorized;

            var authorization = WaitForDispatchAuthorization(
                inputEvent,
                options,
                stopEvent,
                interventionEvent,
                retainedInterventionCapture,
                frequency,
                out win32Error,
                out var currentPauseTicks);
            if (authorization != DispatchGuardWaitResult.Authorized)
                return authorization;
            if (currentPauseTicks == 0)
                continue;

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
        IExactWheelInputCapture? retainedInterventionCapture,
        long frequency,
        out int win32Error,
        out long playbackPauseTicks)
    {
        win32Error = 0;
        playbackPauseTicks = 0;
        long? pauseStarted = null;
        while (true)
        {
            if (!IsInterventionMonitorHealthy(
                    retainedInterventionCapture))
            {
                return DispatchGuardWaitResult.PhysicalIntervention;
            }
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
                if (!ArePhysicalInputsReleased(
                        retainedInterventionCapture,
                        Array.Empty<int>()))
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
                !ArePhysicalInputsReleased(
                    retainedInterventionCapture,
                    Array.Empty<int>()))
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

    private static ExactWheelDispatchAuthorization GetResumeAuthorization(
        Func<ExactWheelDispatchAuthorization> resumeAuthorization)
    {
        try
        {
            return NormalizeAuthorization(resumeAuthorization());
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

    private static bool IsInterventionMonitorHealthy(
        IExactWheelInputCapture? capture) =>
        capture is null || capture.Mode == InputCaptureMode.Intervention;

    private bool ArePhysicalInputsReleased(
        IExactWheelInputCapture? capture,
        IReadOnlyCollection<int> ignoredVirtualKeys) =>
        capture is ITrackedPhysicalInputState tracked
            ? tracked.AreReleased(ignoredVirtualKeys)
            : _physicalInput.AreReleased(ignoredVirtualKeys);

    private void MarkRetainedPhysicalInputPrevalidated(
        IExactWheelInputCapture? capture)
    {
        if (capture is null)
            return;
        lock (_gate)
        {
            if (ReferenceEquals(_retainedInterventionCapture, capture))
            {
                _interventionSequencePhysicalInputPrevalidated = true;
            }
        }
    }

    private bool AreControlKeysReleased(
        IExactWheelInputCapture? capture,
        IReadOnlyCollection<int> virtualKeys) =>
        capture is ITrackedPhysicalInputState tracked
            ? tracked.AreKeysReleased(virtualKeys)
            : _physicalInput.AreKeysReleased(virtualKeys);

    private void ResetPhysicalPauseSignal(
        EventWaitHandle interventionEvent,
        IExactWheelInputCapture? capture)
    {
        interventionEvent.Reset();
        if (!ArePhysicalInputsReleased(
                capture,
                Array.Empty<int>()))
        {
            interventionEvent.Set();
        }
    }

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
            options.MaximumCatchUpMicroseconds >
                ExactWheelLimits.MaximumDurationMicroseconds ||
            options.ProgressIntervalMicroseconds >
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

    private sealed class PlaybackProgressReporter
    {
        private readonly IProgress<ExactWheelPlaybackProgress>? _progress;
        private readonly long _intervalTicks;
        private ExactWheelPlaybackProgress _latest;
        private long _nextReportTimestamp;
        private bool _hasPending;
        private bool _reported;

        internal PlaybackProgressReporter(
            IProgress<ExactWheelPlaybackProgress>? progress,
            ulong intervalMicroseconds,
            long frequency)
        {
            _progress = progress;
            _intervalTicks = intervalMicroseconds == 0
                ? 0
                : ExactWheelTiming.EventDeadlineTicks(
                    0,
                    intervalMicroseconds,
                    ExactWheelPlaybackRate.Recorded,
                    frequency);
        }

        internal void Report(
            long timestamp,
            ExactWheelPlaybackProgress current)
        {
            if (_progress is null)
                return;

            _latest = current;
            _hasPending = true;
            if (_reported &&
                _intervalTicks != 0 &&
                timestamp < _nextReportTimestamp)
            {
                return;
            }

            TryReportLatest();
            _reported = true;
            _nextReportTimestamp = timestamp >
                long.MaxValue - _intervalTicks
                ? long.MaxValue
                : timestamp + _intervalTicks;
        }

        internal void Flush()
        {
            if (_hasPending)
                TryReportLatest();
        }

        private void TryReportLatest()
        {
            try
            {
                _progress!.Report(_latest);
            }
            catch (Exception)
            {
                // Advisory progress failures cannot terminate playback.
            }
            finally
            {
                _hasPending = false;
            }
        }
    }

    private sealed record PlaybackRequest(
        ExactWheelRecording Recording,
        ExactWheelPlaybackOptions Options,
        EventWaitHandle StopEvent,
        EventWaitHandle? InterventionEvent,
        bool InterventionMonitorRetained,
        CancellationToken CancellationToken,
        TaskCompletionSource<ExactWheelPlaybackResult> Completion);

    private sealed class InterventionSequenceLease(
        ExactWheelPlaybackEngine owner,
        long generation) : IAsyncDisposable
    {
        private readonly object _disposeGate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            await owner.EndInterventionSequenceAsync(generation)
                .ConfigureAwait(false);
        }
    }

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
