using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionDock.ExactWheel.Windows;

internal enum InputCaptureMode
{
    Idle,
    Armed,
    Recording,
    Intervention
}

internal sealed record InputCaptureResult(
    IReadOnlyList<ExactWheelInputEvent> Events,
    ulong DurationMicroseconds,
    bool Overflowed,
    int Win32Error);

internal interface IExactWheelInputCapture : IDisposable
{
    InputCaptureMode Mode { get; }

    void StartRecording(
        int maximumEvents,
        IReadOnlyCollection<int> waitForReleaseVirtualKeys,
        Func<ExactWheelInputEvent, bool>? eventAdmission);

    InputCaptureResult StopRecording();

    void StartInterventionMonitor(EventWaitHandle interventionEvent);

    void StopInterventionMonitor();
}

internal interface ITrackedPhysicalInputState
{
    bool AreReleased(IReadOnlyCollection<int> ignoredVirtualKeys);

    bool AreKeysReleased(IReadOnlyCollection<int> virtualKeys);
}

internal sealed class TrackedPhysicalInputState : ITrackedPhysicalInputState
{
    private const int VirtualKeyCount = 256;
    private const int BitsPerWord = 32;
    private readonly int[] _held = new int[VirtualKeyCount / BitsPerWord];

    internal void Reset() => Array.Clear(_held);

    internal void Set(int virtualKey, bool isDown)
    {
        if (virtualKey is <= 0 or >= VirtualKeyCount)
            return;
        var wordIndex = virtualKey / BitsPerWord;
        var mask = 1 << (virtualKey % BitsPerWord);
        if (isDown)
            _ = Interlocked.Or(ref _held[wordIndex], mask);
        else
            _ = Interlocked.And(ref _held[wordIndex], ~mask);
    }

    public bool AreReleased(
        IReadOnlyCollection<int> ignoredVirtualKeys)
    {
        ArgumentNullException.ThrowIfNull(ignoredVirtualKeys);
        if (ignoredVirtualKeys.Count == 0)
        {
            for (var index = 0; index < _held.Length; index++)
            {
                if (Volatile.Read(ref _held[index]) != 0)
                    return false;
            }
            return true;
        }

        for (var virtualKey = 1;
             virtualKey < VirtualKeyCount;
             virtualKey++)
        {
            if (IsDown(virtualKey) &&
                !PhysicalInputVirtualKeyFamilies.IsIgnored(
                    virtualKey,
                    ignoredVirtualKeys))
            {
                return false;
            }
        }
        return true;
    }

    public bool AreKeysReleased(
        IReadOnlyCollection<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        foreach (var virtualKey in virtualKeys)
        {
            if (IsDown(virtualKey))
                return false;
        }
        return true;
    }

    private bool IsDown(int virtualKey)
    {
        if (virtualKey is <= 0 or >= VirtualKeyCount)
            return false;
        return virtualKey switch
        {
            PhysicalInputVirtualKeyFamilies.Shift =>
                IsRawDown(PhysicalInputVirtualKeyFamilies.Shift) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.LeftShift) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.RightShift),
            PhysicalInputVirtualKeyFamilies.Control =>
                IsRawDown(PhysicalInputVirtualKeyFamilies.Control) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.LeftControl) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.RightControl),
            PhysicalInputVirtualKeyFamilies.Menu =>
                IsRawDown(PhysicalInputVirtualKeyFamilies.Menu) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.LeftMenu) ||
                IsRawDown(PhysicalInputVirtualKeyFamilies.RightMenu),
            _ => IsRawDown(virtualKey)
        };
    }

    private bool IsRawDown(int virtualKey)
    {
        var wordIndex = virtualKey / BitsPerWord;
        var mask = 1 << (virtualKey % BitsPerWord);
        return (Volatile.Read(ref _held[wordIndex]) & mask) != 0;
    }
}

/// <summary>
/// Stores captured events in small pooled segments instead of reserving the
/// complete 500,000-event safety ceiling for every recording. Hook callbacks
/// never resize or copy previously captured input; they rent one additional
/// bounded segment only when the current segment fills.
/// </summary>
internal sealed class PooledExactWheelEventBuffer : IDisposable
{
    internal const int SegmentEventCapacity = 4_096;

    private readonly int _maximumEvents;
    private readonly List<ExactWheelInputEvent[]> _segments;
    private int _count;
    private bool _disposed;

    internal PooledExactWheelEventBuffer(int maximumEvents)
    {
        if (maximumEvents <= 0 ||
            (ulong)maximumEvents > ExactWheelLimits.MaximumEventCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        _maximumEvents = maximumEvents;
        var maximumSegments = checked(
            (maximumEvents + SegmentEventCapacity - 1) /
            SegmentEventCapacity);
        _segments = new List<ExactWheelInputEvent[]>(maximumSegments);
        RentNextSegment();
    }

    internal int Count => _count;

    internal int SegmentCount => _segments.Count;

    internal int MaximumEvents => _maximumEvents;

    internal int ReservedEventCapacity => Math.Min(
        _maximumEvents,
        checked(_segments.Count * SegmentEventCapacity));

    internal bool TryAdd(ExactWheelInputEvent inputEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count >= _maximumEvents)
            return false;

        var segmentIndex = _count / SegmentEventCapacity;
        if (segmentIndex == _segments.Count)
        {
            try
            {
                RentNextSegment();
            }
            catch (OutOfMemoryException)
            {
                // This method runs inside a native low-level hook callback.
                // Exhaustion must stop capture safely rather than unwind into
                // Windows through the unmanaged callback boundary.
                return false;
            }
        }

        var segmentOffset = _count % SegmentEventCapacity;
        _segments[segmentIndex][segmentOffset] = inputEvent;
        _count++;
        return true;
    }

    internal ExactWheelInputEvent[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count == 0)
            return [];

        var events = new ExactWheelInputEvent[_count];
        var copied = 0;
        for (var segmentIndex = 0;
             segmentIndex < _segments.Count && copied < _count;
             segmentIndex++)
        {
            var copyCount = Math.Min(
                SegmentEventCapacity,
                _count - copied);
            Array.Copy(
                _segments[segmentIndex],
                0,
                events,
                copied,
                copyCount);
            copied += copyCount;
        }

        return events;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var segment in _segments)
        {
            ArrayPool<ExactWheelInputEvent>.Shared.Return(
                segment,
                // Captured input coordinates and key identities should not
                // remain observable in a shared pool after recording ends.
                clearArray: true);
        }

        _segments.Clear();
        _count = 0;
        _disposed = true;
    }

    private void RentNextSegment()
    {
        var remaining = _maximumEvents -
            checked(_segments.Count * SegmentEventCapacity);
        if (remaining <= 0)
            throw new InvalidOperationException(
                "The bounded capture buffer cannot grow further.");

        _segments.Add(ArrayPool<ExactWheelInputEvent>.Shared.Rent(
            Math.Min(SegmentEventCapacity, remaining)));
    }
}

internal sealed class LowLevelInputCapture :
    IExactWheelInputCapture,
    ITrackedPhysicalInputState
{
    private static int ActiveCapture;

    private readonly object _gate = new();
    private readonly ExactWheelNativeMethods.HookProcedure _mouseProcedure;
    private readonly ExactWheelNativeMethods.HookProcedure _keyboardProcedure;
    private readonly TrackedPhysicalInputState _trackedPhysicalInput = new();
    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private PooledExactWheelEventBuffer? _eventBuffer;
    private int[] _waitForReleaseKeys = [];
    private Func<ExactWheelInputEvent, bool>? _eventAdmission;
    private EventWaitHandle? _interventionEvent;
    private Exception? _startException;
    private int _mode;
    private int _requestedMode;
    private int _stopRequested;
    private int _callbacksInFlight;
    private int _overflowed;
    private int _threadError;
    private uint _threadId;
    private long _originTicks;
    private long _nextSequence;
    private bool _disposed;

    internal LowLevelInputCapture()
    {
        _mouseProcedure = MouseHook;
        _keyboardProcedure = KeyboardHook;
    }

    public InputCaptureMode Mode =>
        (InputCaptureMode)Volatile.Read(ref _mode);

    public bool AreReleased(
        IReadOnlyCollection<int> ignoredVirtualKeys) =>
        _trackedPhysicalInput.AreReleased(ignoredVirtualKeys);

    public bool AreKeysReleased(
        IReadOnlyCollection<int> virtualKeys) =>
        _trackedPhysicalInput.AreKeysReleased(virtualKeys);

    public void StartRecording(
        int maximumEvents,
        IReadOnlyCollection<int> waitForReleaseVirtualKeys,
        Func<ExactWheelInputEvent, bool>? eventAdmission)
    {
        ArgumentNullException.ThrowIfNull(waitForReleaseVirtualKeys);
        if (maximumEvents <= 0 ||
            (ulong)maximumEvents > ExactWheelLimits.MaximumEventCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureIdle();
            _eventBuffer = new PooledExactWheelEventBuffer(maximumEvents);
            _waitForReleaseKeys = waitForReleaseVirtualKeys
                .Distinct()
                .ToArray();
            _eventAdmission = eventAdmission;
            StartThread(
                _waitForReleaseKeys.Length == 0
                    ? InputCaptureMode.Recording
                    : InputCaptureMode.Armed);
        }
    }

    public InputCaptureResult StopRecording()
    {
        Thread? thread;
        long stoppedTicks;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_thread is null ||
                Mode is not (InputCaptureMode.Armed or
                    InputCaptureMode.Recording))
            {
                throw new InvalidOperationException(
                    "Input recording is not active.");
            }

            Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
            var spinner = new SpinWait();
            while (Volatile.Read(ref _callbacksInFlight) != 0)
                spinner.SpinOnce();
            stoppedTicks = Stopwatch.GetTimestamp();
            RequestThreadStop();
            thread = _thread;
        }

        thread.Join();
        lock (_gate)
        {
            var events = _eventBuffer?.ToArray() ?? [];
            var duration = _originTicks <= 0
                ? 0
                : ExactWheelTiming.TimestampOffsetMicroseconds(
                    _originTicks,
                    Math.Max(stoppedTicks, _originTicks),
                    Stopwatch.Frequency);
            var result = new InputCaptureResult(
                events,
                duration,
                Volatile.Read(ref _overflowed) != 0,
                Volatile.Read(ref _threadError));
            ResetAfterStop();
            return result;
        }
    }

    public void StartInterventionMonitor(
        EventWaitHandle interventionEvent)
    {
        ArgumentNullException.ThrowIfNull(interventionEvent);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureIdle();
            _interventionEvent = interventionEvent;
            StartThread(InputCaptureMode.Intervention);
        }
    }

    public void StopInterventionMonitor()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_thread is null)
                return;
            if ((InputCaptureMode)Volatile.Read(ref _requestedMode) !=
                InputCaptureMode.Intervention)
            {
                throw new InvalidOperationException(
                    "The active hook is not an intervention monitor.");
            }

            // An unexpected intervention-thread exit publishes Idle before
            // signalling playback. It still belongs to this monitor and must
            // be joined/reset cleanly instead of masking the fail-closed
            // playback result with a teardown exception.
            Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
            RequestThreadStop();
            thread = _thread;
        }

        thread.Join();
        lock (_gate)
            ResetAfterStop();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
            RequestThreadStop();
            thread = _thread;
        }

        thread?.Join();
        lock (_gate)
            ResetAfterStop();
    }

    internal static bool IsInjected(
        ExactWheelNativeMethods.MouseLowLevelHookData data) =>
        (data.Flags & ExactWheelNativeMethods.LlMouseInjected) != 0 ||
        data.ExtraInfo == unchecked((nuint)ExactWheelLimits.PrivateInputMarker);

    internal static bool IsInjected(
        ExactWheelNativeMethods.KeyboardLowLevelHookData data) =>
        (data.Flags & ExactWheelNativeMethods.LlKeyboardInjected) != 0 ||
        data.ExtraInfo == unchecked((nuint)ExactWheelLimits.PrivateInputMarker);

    internal static bool TryTranslateMouse(
        uint message,
        ExactWheelNativeMethods.MouseLowLevelHookData data,
        ulong timestampMicroseconds,
        ulong sequence,
        out ExactWheelInputEvent inputEvent)
    {
        var type = ExactWheelInputEventType.MouseMove;
        var data1 = 0;
        switch (message)
        {
            case ExactWheelNativeMethods.WmMouseMove:
                break;
            case ExactWheelNativeMethods.WmLeftButtonDown:
                type = ExactWheelInputEventType.MouseButtonDown;
                data1 = (int)ExactWheelMouseButton.Left;
                break;
            case ExactWheelNativeMethods.WmLeftButtonUp:
                type = ExactWheelInputEventType.MouseButtonUp;
                data1 = (int)ExactWheelMouseButton.Left;
                break;
            case ExactWheelNativeMethods.WmRightButtonDown:
                type = ExactWheelInputEventType.MouseButtonDown;
                data1 = (int)ExactWheelMouseButton.Right;
                break;
            case ExactWheelNativeMethods.WmRightButtonUp:
                type = ExactWheelInputEventType.MouseButtonUp;
                data1 = (int)ExactWheelMouseButton.Right;
                break;
            case ExactWheelNativeMethods.WmMiddleButtonDown:
                type = ExactWheelInputEventType.MouseButtonDown;
                data1 = (int)ExactWheelMouseButton.Middle;
                break;
            case ExactWheelNativeMethods.WmMiddleButtonUp:
                type = ExactWheelInputEventType.MouseButtonUp;
                data1 = (int)ExactWheelMouseButton.Middle;
                break;
            case ExactWheelNativeMethods.WmXButtonDown:
            case ExactWheelNativeMethods.WmXButtonUp:
                {
                    var button = data.MouseData >> 16;
                    if (button is not (ExactWheelNativeMethods.XButton1 or
                        ExactWheelNativeMethods.XButton2))
                    {
                        inputEvent = default;
                        return false;
                    }

                    type = message == ExactWheelNativeMethods.WmXButtonDown
                        ? ExactWheelInputEventType.MouseButtonDown
                        : ExactWheelInputEventType.MouseButtonUp;
                    data1 = button == ExactWheelNativeMethods.XButton1
                        ? (int)ExactWheelMouseButton.X1
                        : (int)ExactWheelMouseButton.X2;
                    break;
                }
            case ExactWheelNativeMethods.WmMouseWheel:
                type = ExactWheelInputEventType.VerticalWheel;
                data1 = (short)(data.MouseData >> 16);
                break;
            case ExactWheelNativeMethods.WmMouseHorizontalWheel:
                type = ExactWheelInputEventType.HorizontalWheel;
                data1 = (short)(data.MouseData >> 16);
                break;
            default:
                inputEvent = default;
                return false;
        }

        inputEvent = new ExactWheelInputEvent(
            timestampMicroseconds,
            sequence,
            type,
            data.Point.X,
            data.Point.Y,
            data1,
            0);
        return true;
    }

    internal static bool TryTranslateKeyboard(
        uint message,
        ExactWheelNativeMethods.KeyboardLowLevelHookData data,
        ulong timestampMicroseconds,
        ulong sequence,
        out ExactWheelInputEvent inputEvent)
    {
        ExactWheelInputEventType type;
        var flags = ExactWheelKeyboardFlags.None;
        switch (message)
        {
            case ExactWheelNativeMethods.WmKeyDown:
                type = ExactWheelInputEventType.KeyDown;
                break;
            case ExactWheelNativeMethods.WmKeyUp:
                type = ExactWheelInputEventType.KeyUp;
                break;
            case ExactWheelNativeMethods.WmSysKeyDown:
                type = ExactWheelInputEventType.KeyDown;
                flags |= ExactWheelKeyboardFlags.System;
                break;
            case ExactWheelNativeMethods.WmSysKeyUp:
                type = ExactWheelInputEventType.KeyUp;
                flags |= ExactWheelKeyboardFlags.System;
                break;
            default:
                inputEvent = default;
                return false;
        }

        if ((data.Flags & ExactWheelNativeMethods.LlKeyboardExtended) != 0)
            flags |= ExactWheelKeyboardFlags.Extended;
        if ((data.Flags & ExactWheelNativeMethods.LlKeyboardAltDown) != 0)
            flags |= ExactWheelKeyboardFlags.AltContext;
        inputEvent = new ExactWheelInputEvent(
            timestampMicroseconds,
            sequence,
            type,
            0,
            0,
            checked((int)data.VirtualKey),
            checked((int)data.ScanCode),
            flags);
        return true;
    }

    private void StartThread(InputCaptureMode requestedMode)
    {
        Volatile.Write(ref _requestedMode, (int)requestedMode);
        _startException = null;
        _overflowed = 0;
        _threadError = ExactWheelNativeMethods.ErrorSuccess;
        _originTicks = 0;
        _nextSequence = 0;
        _stopRequested = 0;
        _ready = new ManualResetEventSlim(initialState: false);
        _thread = new Thread(() => ThreadMain(requestedMode))
        {
            IsBackground = true,
            Name = "SessionDock ExactWheel input hook"
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            RequestThreadStop();
            _thread.Join();
            var timeout = new TimeoutException(
                "Starting the low-level input hooks timed out.");
            ResetAfterStop();
            throw timeout;
        }

        if (_startException is not null)
        {
            _thread.Join();
            var exception = _startException;
            ResetAfterStop();
            throw new InvalidOperationException(
                "The low-level input hooks could not start.",
                exception);
        }
    }

    private void ThreadMain(InputCaptureMode requestedMode)
    {
        if (Interlocked.CompareExchange(ref ActiveCapture, 1, 0) != 0)
        {
            _startException = new Win32Exception(
                ExactWheelNativeMethods.ErrorBusy,
                "Another ExactWheel capture or intervention monitor is active.");
            _ready?.Set();
            return;
        }

        try
        {
            _threadId = ExactWheelNativeMethods.GetCurrentThreadId();
            _ = ExactWheelNativeMethods.PeekMessage(
                out _,
                0,
                0,
                0,
                removeMessage: 0);
            var module = ExactWheelNativeMethods.GetModuleHandle(null);
            using var mouseHook = ExactWheelNativeMethods.SetWindowsHookEx(
                ExactWheelNativeMethods.WhMouseLowLevel,
                _mouseProcedure,
                module,
                threadId: 0);
            if (mouseHook.IsInvalid)
                throw LastWin32("Installing the mouse hook failed.");
            using var keyboardHook = ExactWheelNativeMethods.SetWindowsHookEx(
                ExactWheelNativeMethods.WhKeyboardLowLevel,
                _keyboardProcedure,
                module,
                threadId: 0);
            if (keyboardHook.IsInvalid)
                throw LastWin32("Installing the keyboard hook failed.");

            if (requestedMode == InputCaptureMode.Intervention)
                CapturePhysicalInputBaseline();
            if (requestedMode == InputCaptureMode.Recording)
                _originTicks = Stopwatch.GetTimestamp();
            Volatile.Write(ref _mode, (int)requestedMode);
            _ready?.Set();

            while (Volatile.Read(ref _stopRequested) == 0)
            {
                var armed = Mode == InputCaptureMode.Armed;
                var wait = ExactWheelNativeMethods.MsgWaitForMultipleObjectsEx(
                    0,
                    0,
                    armed ? 5U : ExactWheelNativeMethods.Infinite,
                    ExactWheelNativeMethods.QsAllInput,
                    ExactWheelNativeMethods.MwmoInputAvailable);
                if (wait == ExactWheelNativeMethods.WaitFailed)
                {
                    _threadError = Marshal.GetLastWin32Error();
                    break;
                }

                if (wait == ExactWheelNativeMethods.WaitObject0)
                {
                    while (ExactWheelNativeMethods.PeekMessage(
                               out var message,
                               0,
                               0,
                               0,
                               ExactWheelNativeMethods.PmRemove))
                    {
                        if (message.Message == ExactWheelNativeMethods.WmQuit)
                        {
                            Volatile.Write(ref _stopRequested, 1);
                            break;
                        }

                        _ = ExactWheelNativeMethods.TranslateMessage(ref message);
                        _ = ExactWheelNativeMethods.DispatchMessage(ref message);
                    }
                }

                if (armed && ReleaseKeysAreUp())
                {
                    _originTicks = Stopwatch.GetTimestamp();
                    Volatile.Write(
                        ref _mode,
                        (int)InputCaptureMode.Recording);
                }
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or
                InvalidOperationException or
                DllNotFoundException or
                EntryPointNotFoundException)
        {
            _startException ??= exception;
            _threadError = exception is Win32Exception win32
                ? win32.NativeErrorCode
                : ExactWheelNativeMethods.ErrorInvalidData;
            _ready?.Set();
        }
        finally
        {
            var interventionStoppedUnexpectedly =
                requestedMode == InputCaptureMode.Intervention &&
                Mode == InputCaptureMode.Intervention;
            Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
            if (interventionStoppedUnexpectedly)
            {
                // A retained monitor that exits without an explicit stop
                // must fail closed. Publish Idle before waking playback so
                // the waiter reliably observes the lost protection.
                TrySignalIntervention();
            }
            _threadId = 0;
            Volatile.Write(ref ActiveCapture, 0);
            _ready?.Set();
        }
    }

    private nint MouseHook(int code, nuint message, nint dataPointer)
    {
        if (code >= 0 && dataPointer != 0)
        {
            Interlocked.Increment(ref _callbacksInFlight);
            try
            {
                var data = Marshal.PtrToStructure<
                    ExactWheelNativeMethods.MouseLowLevelHookData>(dataPointer);
                if (!IsInjected(data))
                {
                    if (Mode == InputCaptureMode.Intervention)
                    {
                        TrackMouseButton(
                            checked((uint)message),
                            data.MouseData);
                        TrySignalIntervention();
                    }
                    else if (Mode == InputCaptureMode.Recording)
                    {
                        var timestamp = CurrentTimestamp();
                        var sequence = checked((ulong)Interlocked.Increment(
                            ref _nextSequence));
                        if (TryTranslateMouse(
                                checked((uint)message),
                                data,
                                timestamp,
                                sequence,
                                out var inputEvent) &&
                            IsEventAdmitted(inputEvent, _eventAdmission))
                        {
                            Store(inputEvent);
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _callbacksInFlight);
            }
        }

        return ExactWheelNativeMethods.CallNextHookEx(
            0,
            code,
            message,
            dataPointer);
    }

    private nint KeyboardHook(int code, nuint message, nint dataPointer)
    {
        if (code >= 0 && dataPointer != 0)
        {
            Interlocked.Increment(ref _callbacksInFlight);
            try
            {
                var data = Marshal.PtrToStructure<
                    ExactWheelNativeMethods.KeyboardLowLevelHookData>(dataPointer);
                if (!IsInjected(data))
                {
                    if (Mode == InputCaptureMode.Intervention)
                    {
                        TrackKeyboardKey(
                            checked((uint)message),
                            checked((int)data.VirtualKey));
                        TrySignalIntervention();
                    }
                    else if (Mode == InputCaptureMode.Recording)
                    {
                        var timestamp = CurrentTimestamp();
                        var sequence = checked((ulong)Interlocked.Increment(
                            ref _nextSequence));
                        if (TryTranslateKeyboard(
                                checked((uint)message),
                                data,
                                timestamp,
                                sequence,
                                out var inputEvent) &&
                            IsEventAdmitted(inputEvent, _eventAdmission))
                        {
                            Store(inputEvent);
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _callbacksInFlight);
            }
        }

        return ExactWheelNativeMethods.CallNextHookEx(
            0,
            code,
            message,
            dataPointer);
    }

    private void Store(ExactWheelInputEvent inputEvent)
    {
        if (_eventBuffer is null || !_eventBuffer.TryAdd(inputEvent))
        {
            Volatile.Write(ref _overflowed, 1);
            _threadError = ExactWheelNativeMethods.ErrorBufferOverflow;
            Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
            Volatile.Write(ref _stopRequested, 1);
            return;
        }
    }

    internal static bool IsEventAdmitted(
        ExactWheelInputEvent inputEvent,
        Func<ExactWheelInputEvent, bool>? eventAdmission)
    {
        if (eventAdmission is null)
            return true;

        try
        {
            return eventAdmission(inputEvent);
        }
        catch (Exception)
        {
            // Admission runs inside a low-level hook callback. A failed or
            // buggy policy must never admit input or escape into Windows.
            return false;
        }
    }

    private ulong CurrentTimestamp() =>
        ExactWheelTiming.TimestampOffsetMicroseconds(
            _originTicks,
            Math.Max(Stopwatch.GetTimestamp(), _originTicks),
            Stopwatch.Frequency);

    private bool ReleaseKeysAreUp() =>
        _waitForReleaseKeys.All(virtualKey =>
            (ExactWheelNativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) == 0);

    private void RequestThreadStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        var threadId = _threadId;
        if (threadId != 0)
        {
            _ = ExactWheelNativeMethods.PostThreadMessage(
                threadId,
                ExactWheelNativeMethods.WmQuit,
                0,
                0);
        }
    }

    private void CapturePhysicalInputBaseline()
    {
        _trackedPhysicalInput.Reset();
        for (var virtualKey = 1; virtualKey < 256; virtualKey++)
        {
            // Left/right modifier keys are tracked separately. Omitting the
            // aggregate aliases prevents a released side-specific key from
            // leaving a stale generic Shift/Ctrl/Alt bit in the snapshot.
            if (virtualKey is 0x10 or 0x11 or 0x12)
                continue;
            _trackedPhysicalInput.Set(
                virtualKey,
                (ExactWheelNativeMethods.GetAsyncKeyState(virtualKey) &
                    0x8000) != 0);
        }
    }

    private void TrackMouseButton(uint message, uint mouseData)
    {
        var virtualKey = message switch
        {
            ExactWheelNativeMethods.WmLeftButtonDown or
                ExactWheelNativeMethods.WmLeftButtonUp => 0x01,
            ExactWheelNativeMethods.WmRightButtonDown or
                ExactWheelNativeMethods.WmRightButtonUp => 0x02,
            ExactWheelNativeMethods.WmMiddleButtonDown or
                ExactWheelNativeMethods.WmMiddleButtonUp => 0x04,
            ExactWheelNativeMethods.WmXButtonDown or
                ExactWheelNativeMethods.WmXButtonUp
                when mouseData >> 16 ==
                    ExactWheelNativeMethods.XButton1 => 0x05,
            ExactWheelNativeMethods.WmXButtonDown or
                ExactWheelNativeMethods.WmXButtonUp
                when mouseData >> 16 ==
                    ExactWheelNativeMethods.XButton2 => 0x06,
            _ => 0
        };
        if (virtualKey == 0)
            return;
        var isDown = message is
            ExactWheelNativeMethods.WmLeftButtonDown or
            ExactWheelNativeMethods.WmRightButtonDown or
            ExactWheelNativeMethods.WmMiddleButtonDown or
            ExactWheelNativeMethods.WmXButtonDown;
        _trackedPhysicalInput.Set(virtualKey, isDown);
    }

    private void TrackKeyboardKey(uint message, int virtualKey)
    {
        var isDown = message is ExactWheelNativeMethods.WmKeyDown or
            ExactWheelNativeMethods.WmSysKeyDown;
        var isUp = message is ExactWheelNativeMethods.WmKeyUp or
            ExactWheelNativeMethods.WmSysKeyUp;
        if (isDown || isUp)
            _trackedPhysicalInput.Set(virtualKey, isDown);
    }

    private void TrySignalIntervention()
    {
        try
        {
            _interventionEvent?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Teardown owns the event lifecycle. Never unwind through a
            // native low-level hook callback if a defensive caller violates
            // that ownership contract.
        }
    }

    private void EnsureIdle()
    {
        if (_thread is not null)
            throw new InvalidOperationException("An input hook is already active.");
    }

    private void ResetAfterStop()
    {
        _ready?.Dispose();
        _ready = null;
        _thread = null;
        _eventBuffer?.Dispose();
        _eventBuffer = null;
        _waitForReleaseKeys = [];
        _eventAdmission = null;
        _interventionEvent = null;
        _trackedPhysicalInput.Reset();
        _startException = null;
        _threadId = 0;
        _originTicks = 0;
        _nextSequence = 0;
        _overflowed = 0;
        _threadError = 0;
        _stopRequested = 0;
        Volatile.Write(ref _requestedMode, (int)InputCaptureMode.Idle);
        Volatile.Write(ref _mode, (int)InputCaptureMode.Idle);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static Win32Exception LastWin32(string message) =>
        new(Marshal.GetLastWin32Error(), message);
}
