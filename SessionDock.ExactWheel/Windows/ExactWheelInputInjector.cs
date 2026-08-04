using System.Runtime.InteropServices;

namespace SessionDock.ExactWheel.Windows;

internal readonly record struct InjectionAttempt(
    bool Succeeded,
    uint Submitted,
    uint Expected,
    int Win32Error);

internal interface IExactWheelInputBackend
{
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

internal sealed class Win32PhysicalInputState : IPhysicalInputState
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyLeftMenu = 0xA4;
    private const int VirtualKeyRightMenu = 0xA5;

    public bool AreReleased(IReadOnlyCollection<int> ignoredVirtualKeys)
    {
        ArgumentNullException.ThrowIfNull(ignoredVirtualKeys);
        for (var virtualKey = 1; virtualKey < 255; virtualKey++)
        {
            if (IsIgnored(virtualKey, ignoredVirtualKeys))
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

    private static bool IsIgnored(
        int virtualKey,
        IReadOnlyCollection<int> ignored)
    {
        if (ignored.Contains(virtualKey))
            return true;
        return virtualKey switch
        {
            VirtualKeyControl or
                VirtualKeyLeftControl or
                VirtualKeyRightControl =>
                ignored.Contains(VirtualKeyControl) ||
                ignored.Contains(VirtualKeyLeftControl) ||
                ignored.Contains(VirtualKeyRightControl),
            VirtualKeyShift or
                VirtualKeyLeftShift or
                VirtualKeyRightShift =>
                ignored.Contains(VirtualKeyShift) ||
                ignored.Contains(VirtualKeyLeftShift) ||
                ignored.Contains(VirtualKeyRightShift),
            VirtualKeyMenu or
                VirtualKeyLeftMenu or
                VirtualKeyRightMenu =>
                ignored.Contains(VirtualKeyMenu) ||
                ignored.Contains(VirtualKeyLeftMenu) ||
                ignored.Contains(VirtualKeyRightMenu),
            _ => false
        };
    }
}

internal sealed class ExactWheelInputInjector : IDisposable
{
    private const int MaximumHeldKeyboardInputs = 256;

    private readonly IExactWheelInputBackend _backend;
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

    internal bool HasHeldInputs =>
        _heldKeys.Count != 0 || _heldMouseButtons.Any(held => held);

    internal InjectionAttempt Inject(
        ExactWheelInputEvent inputEvent,
        ExactWheelDisplayTopology topology)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(topology);
        var inputs = BuildBatch(inputEvent, topology);
        if (inputs.Length == 0)
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
