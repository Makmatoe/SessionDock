using System.Runtime.InteropServices;

namespace SessionDock.ExactWheel.Windows;

internal readonly record struct InjectionAttempt(
    bool Succeeded,
    uint Submitted,
    uint Expected,
    int Win32Error);

internal interface IExactWheelInputBackend
{
    // Implementations consume the complete batch synchronously and must not
    // retain or mutate the array after this call returns.
    uint Send(
        ExactWheelNativeMethods.NativeInput[] inputs,
        out int win32Error);
}

internal sealed class Win32InputBackend : IExactWheelInputBackend
{
    public uint Send(
        ExactWheelNativeMethods.NativeInput[] inputs,
        out int win32Error)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Length == 0)
        {
            win32Error = 87;
            return 0;
        }

        Marshal.SetLastPInvokeError(0);
        var sent = ExactWheelNativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<ExactWheelNativeMethods.NativeInput>());
        win32Error = sent == inputs.Length
            ? 0
            : Marshal.GetLastWin32Error();
        return sent;
    }
}

internal interface IPhysicalInputState
{
    bool AreReleased(IReadOnlyCollection<int> ignoredVirtualKeys);

    bool AreKeysReleased(IReadOnlyCollection<int> virtualKeys);
}

internal static class PhysicalInputVirtualKeyFamilies
{
    internal const int Control = 0x11;
    internal const int Shift = 0x10;
    internal const int Menu = 0x12;
    internal const int LeftControl = 0xA2;
    internal const int RightControl = 0xA3;
    internal const int LeftShift = 0xA0;
    internal const int RightShift = 0xA1;
    internal const int LeftMenu = 0xA4;
    internal const int RightMenu = 0xA5;

    internal static bool IsIgnored(
        int virtualKey,
        IReadOnlyCollection<int> ignored)
    {
        if (ignored.Contains(virtualKey))
            return true;
        return virtualKey switch
        {
            Control or LeftControl or RightControl =>
                ignored.Contains(Control) ||
                ignored.Contains(LeftControl) ||
                ignored.Contains(RightControl),
            Shift or LeftShift or RightShift =>
                ignored.Contains(Shift) ||
                ignored.Contains(LeftShift) ||
                ignored.Contains(RightShift),
            Menu or LeftMenu or RightMenu =>
                ignored.Contains(Menu) ||
                ignored.Contains(LeftMenu) ||
                ignored.Contains(RightMenu),
            _ => false
        };
    }
}

internal sealed class Win32PhysicalInputState : IPhysicalInputState
{
    public bool AreReleased(IReadOnlyCollection<int> ignoredVirtualKeys)
    {
        ArgumentNullException.ThrowIfNull(ignoredVirtualKeys);
        for (var virtualKey = 1; virtualKey < 255; virtualKey++)
        {
            if (PhysicalInputVirtualKeyFamilies.IsIgnored(
                    virtualKey,
                    ignoredVirtualKeys))
                continue;
            if ((ExactWheelNativeMethods.GetAsyncKeyState(virtualKey) &
                 0x8000) != 0)
            {
                return false;
            }
        }

        return true;
    }

    public bool AreKeysReleased(IReadOnlyCollection<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        return virtualKeys.All(virtualKey =>
            (ExactWheelNativeMethods.GetAsyncKeyState(virtualKey) &
             0x8000) == 0);
    }

}

internal sealed class ExactWheelInputInjector : IDisposable
{
    private const int MaximumHeldKeyboardInputs = 256;

    private readonly IExactWheelInputBackend _backend;
    // SendInput consumes the native array synchronously. Reusing the two
    // possible event-batch shapes avoids one managed array allocation for
    // every recorded event while preserving atomic move+transition batches.
    private readonly ExactWheelNativeMethods.NativeInput[] _singleInputBatch =
        new ExactWheelNativeMethods.NativeInput[1];
    private readonly ExactWheelNativeMethods.NativeInput[] _doubleInputBatch =
        new ExactWheelNativeMethods.NativeInput[2];
    private readonly List<HeldKey> _heldKeys = [];
    private readonly bool[] _heldMouseButtons = new bool[6];
    private bool _disposed;

    internal ExactWheelInputInjector()
        : this(new Win32InputBackend())
    {
    }

    internal ExactWheelInputInjector(IExactWheelInputBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    internal bool HasHeldInputs
    {
        get
        {
            if (_heldKeys.Count != 0)
                return true;
            for (var index = 1; index < _heldMouseButtons.Length; index++)
            {
                if (_heldMouseButtons[index])
                    return true;
            }

            return false;
        }
    }

    internal InjectionAttempt Inject(
        ExactWheelInputEvent inputEvent,
        ExactWheelDisplayTopology topology)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(topology);
        var inputs = BuildReusableBatch(inputEvent, topology);
        if (inputs is null)
            return new InjectionAttempt(false, 0, 0, 13);

        if (inputEvent.Type == ExactWheelInputEventType.KeyDown)
        {
            var identity = GetHeldKey(inputEvent);
            if (!_heldKeys.Contains(identity) &&
                _heldKeys.Count >= MaximumHeldKeyboardInputs)
            {
                return new InjectionAttempt(
                    false,
                    0,
                    checked((uint)inputs.Length),
                    56);
            }
        }

        var submitted = _backend.Send(inputs, out var error);
        var expected = checked((uint)inputs.Length);
        var succeeded = submitted == expected;
        if (succeeded)
            TrackSuccessfulTransition(inputEvent);
        return new InjectionAttempt(
            succeeded,
            submitted,
            expected,
            error);
    }

    internal InjectionAttempt ReleaseHeld()
    {
        if (!HasHeldInputs)
            return new InjectionAttempt(true, 0, 0, 0);

        var inputs = new List<ExactWheelNativeMethods.NativeInput>();
        var releases = new List<ReleaseIdentity>();
        foreach (var key in _heldKeys)
        {
            inputs.Add(BuildKeyboardRelease(key));
            releases.Add(new ReleaseIdentity(key, 0));
        }

        for (var index = 1; index < _heldMouseButtons.Length; index++)
        {
            if (!_heldMouseButtons[index])
                continue;
            inputs.Add(BuildMouseRelease((ExactWheelMouseButton)index));
            releases.Add(new ReleaseIdentity(default, index));
        }

        if (inputs.Count == 0)
            return new InjectionAttempt(true, 0, 0, 0);

        var batch = inputs.ToArray();
        var submitted = _backend.Send(batch, out var error);
        var accepted = Math.Min(
            checked((int)submitted),
            releases.Count);
        for (var index = 0; index < accepted; index++)
        {
            var identity = releases[index];
            if (identity.MouseButton != 0)
                _heldMouseButtons[identity.MouseButton] = false;
            else
                _heldKeys.Remove(identity.Key);
        }

        var expected = checked((uint)batch.Length);
        return new InjectionAttempt(
            submitted == expected,
            submitted,
            expected,
            error);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _ = ReleaseHeld();
        _disposed = true;
    }

    internal static ExactWheelNativeMethods.NativeInput[] BuildBatch(
        ExactWheelInputEvent inputEvent,
        ExactWheelDisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        switch (inputEvent.Type)
        {
            case ExactWheelInputEventType.MouseMove:
                return [BuildAbsoluteMove(inputEvent.X, inputEvent.Y, topology)];
            case ExactWheelInputEventType.VerticalWheel:
            case ExactWheelInputEventType.HorizontalWheel:
                return
                [
                    BuildAbsoluteMove(inputEvent.X, inputEvent.Y, topology),
                    BuildWheel(inputEvent)
                ];
            case ExactWheelInputEventType.MouseButtonDown:
            case ExactWheelInputEventType.MouseButtonUp:
                {
                    var transition = BuildMouseTransition(inputEvent);
                    return transition is null
                        ? []
                        :
                        [
                            BuildAbsoluteMove(
                            inputEvent.X,
                            inputEvent.Y,
                            topology),
                        transition.Value
                        ];
                }
            case ExactWheelInputEventType.KeyDown:
            case ExactWheelInputEventType.KeyUp:
                return inputEvent.Data1 is < 0 or > ushort.MaxValue
                    ? []
                    : [BuildKeyboard(inputEvent)];
            default:
                return [];
        }
    }

    private ExactWheelNativeMethods.NativeInput[]? BuildReusableBatch(
        ExactWheelInputEvent inputEvent,
        ExactWheelDisplayTopology topology)
    {
        switch (inputEvent.Type)
        {
            case ExactWheelInputEventType.MouseMove:
                _singleInputBatch[0] = BuildAbsoluteMove(
                    inputEvent.X,
                    inputEvent.Y,
                    topology);
                return _singleInputBatch;
            case ExactWheelInputEventType.VerticalWheel:
            case ExactWheelInputEventType.HorizontalWheel:
                _doubleInputBatch[0] = BuildAbsoluteMove(
                    inputEvent.X,
                    inputEvent.Y,
                    topology);
                _doubleInputBatch[1] = BuildWheel(inputEvent);
                return _doubleInputBatch;
            case ExactWheelInputEventType.MouseButtonDown:
            case ExactWheelInputEventType.MouseButtonUp:
                {
                    var transition = BuildMouseTransition(inputEvent);
                    if (transition is null)
                        return null;
                    _doubleInputBatch[0] = BuildAbsoluteMove(
                        inputEvent.X,
                        inputEvent.Y,
                        topology);
                    _doubleInputBatch[1] = transition.Value;
                    return _doubleInputBatch;
                }
            case ExactWheelInputEventType.KeyDown:
            case ExactWheelInputEventType.KeyUp:
                if (inputEvent.Data1 is < 0 or > ushort.MaxValue)
                    return null;
                _singleInputBatch[0] = BuildKeyboard(inputEvent);
                return _singleInputBatch;
            default:
                return null;
        }
    }

    private static ExactWheelNativeMethods.NativeInput BuildAbsoluteMove(
        int x,
        int y,
        ExactWheelDisplayTopology topology) =>
        new()
        {
            Type = ExactWheelNativeMethods.InputMouse,
            Data = new ExactWheelNativeMethods.InputUnion
            {
                Mouse = new ExactWheelNativeMethods.MouseInput
                {
                    X = ExactWheelCoordinateTransforms.NormalizeForSendInput(
                        x,
                        topology.VirtualLeft,
                        topology.VirtualWidth),
                    Y = ExactWheelCoordinateTransforms.NormalizeForSendInput(
                        y,
                        topology.VirtualTop,
                        topology.VirtualHeight),
                    Flags =
                        ExactWheelNativeMethods.MouseEventMove |
                        ExactWheelNativeMethods.MouseEventAbsolute |
                        ExactWheelNativeMethods.MouseEventVirtualDesktop |
                        ExactWheelNativeMethods.MouseEventMoveNoCoalesce,
                    ExtraInfo =
                        unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
                }
            }
        };

    private static ExactWheelNativeMethods.NativeInput BuildWheel(
        ExactWheelInputEvent inputEvent) =>
        new()
        {
            Type = ExactWheelNativeMethods.InputMouse,
            Data = new ExactWheelNativeMethods.InputUnion
            {
                Mouse = new ExactWheelNativeMethods.MouseInput
                {
                    MouseData = unchecked((uint)inputEvent.Data1),
                    Flags = inputEvent.Type ==
                        ExactWheelInputEventType.VerticalWheel
                        ? ExactWheelNativeMethods.MouseEventWheel
                        : ExactWheelNativeMethods.MouseEventHorizontalWheel,
                    ExtraInfo =
                        unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
                }
            }
        };

    private static ExactWheelNativeMethods.NativeInput? BuildMouseTransition(
        ExactWheelInputEvent inputEvent)
    {
        var down = inputEvent.Type ==
            ExactWheelInputEventType.MouseButtonDown;
        var button = (ExactWheelMouseButton)inputEvent.Data1;
        uint flags;
        uint data = 0;
        switch (button)
        {
            case ExactWheelMouseButton.Left:
                flags = down
                    ? ExactWheelNativeMethods.MouseEventLeftDown
                    : ExactWheelNativeMethods.MouseEventLeftUp;
                break;
            case ExactWheelMouseButton.Right:
                flags = down
                    ? ExactWheelNativeMethods.MouseEventRightDown
                    : ExactWheelNativeMethods.MouseEventRightUp;
                break;
            case ExactWheelMouseButton.Middle:
                flags = down
                    ? ExactWheelNativeMethods.MouseEventMiddleDown
                    : ExactWheelNativeMethods.MouseEventMiddleUp;
                break;
            case ExactWheelMouseButton.X1:
                flags = down
                    ? ExactWheelNativeMethods.MouseEventXDown
                    : ExactWheelNativeMethods.MouseEventXUp;
                data = ExactWheelNativeMethods.XButton1;
                break;
            case ExactWheelMouseButton.X2:
                flags = down
                    ? ExactWheelNativeMethods.MouseEventXDown
                    : ExactWheelNativeMethods.MouseEventXUp;
                data = ExactWheelNativeMethods.XButton2;
                break;
            default:
                return null;
        }

        return new ExactWheelNativeMethods.NativeInput
        {
            Type = ExactWheelNativeMethods.InputMouse,
            Data = new ExactWheelNativeMethods.InputUnion
            {
                Mouse = new ExactWheelNativeMethods.MouseInput
                {
                    MouseData = data,
                    Flags = flags,
                    ExtraInfo =
                        unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
                }
            }
        };
    }

    private static ExactWheelNativeMethods.NativeInput BuildKeyboard(
        ExactWheelInputEvent inputEvent)
    {
        var hasScanCode = inputEvent.Data2 is > 0 and <= ushort.MaxValue;
        var flags = hasScanCode
            ? ExactWheelNativeMethods.KeyboardEventScanCode
            : 0U;
        if ((inputEvent.Flags & ExactWheelKeyboardFlags.Extended) != 0)
            flags |= ExactWheelNativeMethods.KeyboardEventExtendedKey;
        if (inputEvent.Type == ExactWheelInputEventType.KeyUp)
            flags |= ExactWheelNativeMethods.KeyboardEventKeyUp;

        return new ExactWheelNativeMethods.NativeInput
        {
            Type = ExactWheelNativeMethods.InputKeyboard,
            Data = new ExactWheelNativeMethods.InputUnion
            {
                Keyboard = new ExactWheelNativeMethods.KeyboardInput
                {
                    VirtualKey = hasScanCode
                        ? (ushort)0
                        : checked((ushort)inputEvent.Data1),
                    ScanCode = hasScanCode
                        ? checked((ushort)inputEvent.Data2)
                        : (ushort)0,
                    Flags = flags,
                    ExtraInfo =
                        unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
                }
            }
        };
    }

    private static ExactWheelNativeMethods.NativeInput BuildKeyboardRelease(
        HeldKey key)
    {
        var flags = ExactWheelNativeMethods.KeyboardEventKeyUp;
        if (key.ScanCode)
            flags |= ExactWheelNativeMethods.KeyboardEventScanCode;
        if (key.Extended)
            flags |= ExactWheelNativeMethods.KeyboardEventExtendedKey;
        return new ExactWheelNativeMethods.NativeInput
        {
            Type = ExactWheelNativeMethods.InputKeyboard,
            Data = new ExactWheelNativeMethods.InputUnion
            {
                Keyboard = new ExactWheelNativeMethods.KeyboardInput
                {
                    VirtualKey = key.ScanCode ? (ushort)0 : key.Code,
                    ScanCode = key.ScanCode ? key.Code : (ushort)0,
                    Flags = flags,
                    ExtraInfo =
                        unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
                }
            }
        };
    }

    private static ExactWheelNativeMethods.NativeInput BuildMouseRelease(
        ExactWheelMouseButton button)
    {
        var inputEvent = new ExactWheelInputEvent(
            0,
            0,
            ExactWheelInputEventType.MouseButtonUp,
            0,
            0,
            (int)button,
            0);
        return BuildMouseTransition(inputEvent) ?? default;
    }

    private void TrackSuccessfulTransition(
        ExactWheelInputEvent inputEvent)
    {
        if (inputEvent.Type is ExactWheelInputEventType.KeyDown or
            ExactWheelInputEventType.KeyUp)
        {
            var identity = GetHeldKey(inputEvent);
            if (inputEvent.Type == ExactWheelInputEventType.KeyDown)
            {
                if (!_heldKeys.Contains(identity))
                    _heldKeys.Add(identity);
            }
            else
            {
                _heldKeys.Remove(identity);
            }

            return;
        }

        if (inputEvent.Type is not (
                ExactWheelInputEventType.MouseButtonDown or
                ExactWheelInputEventType.MouseButtonUp))
        {
            return;
        }

        var index = inputEvent.Data1;
        if (index > 0 && index < _heldMouseButtons.Length)
        {
            _heldMouseButtons[index] = inputEvent.Type ==
                ExactWheelInputEventType.MouseButtonDown;
        }
    }

    private static HeldKey GetHeldKey(ExactWheelInputEvent inputEvent)
    {
        var scanCode = inputEvent.Data2 is > 0 and <= ushort.MaxValue;
        return new HeldKey(
            checked((ushort)(scanCode
                ? inputEvent.Data2
                : inputEvent.Data1)),
            scanCode,
            (inputEvent.Flags & ExactWheelKeyboardFlags.Extended) != 0);
    }

    private readonly record struct HeldKey(
        ushort Code,
        bool ScanCode,
        bool Extended);

    private readonly record struct ReleaseIdentity(
        HeldKey Key,
        int MouseButton);
}
