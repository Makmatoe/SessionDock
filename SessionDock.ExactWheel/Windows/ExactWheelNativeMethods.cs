using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.ExactWheel.Windows;

internal static class ExactWheelNativeMethods
{
    internal const int WhKeyboardLowLevel = 13;
    internal const int WhMouseLowLevel = 14;
    internal const uint WmQuit = 0x0012;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLeftButtonDown = 0x0201;
    internal const uint WmLeftButtonUp = 0x0202;
    internal const uint WmRightButtonDown = 0x0204;
    internal const uint WmRightButtonUp = 0x0205;
    internal const uint WmMiddleButtonDown = 0x0207;
    internal const uint WmMiddleButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmMouseHorizontalWheel = 0x020E;
    internal const uint LlMouseInjected = 0x00000001;
    internal const uint LlKeyboardExtended = 0x00000001;
    internal const uint LlKeyboardInjected = 0x00000010;
    internal const uint LlKeyboardAltDown = 0x00000020;
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint MouseEventMove = 0x0001;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventRightDown = 0x0008;
    internal const uint MouseEventRightUp = 0x0010;
    internal const uint MouseEventMiddleDown = 0x0020;
    internal const uint MouseEventMiddleUp = 0x0040;
    internal const uint MouseEventXDown = 0x0080;
    internal const uint MouseEventXUp = 0x0100;
    internal const uint MouseEventWheel = 0x0800;
    internal const uint MouseEventHorizontalWheel = 0x1000;
    internal const uint MouseEventMoveNoCoalesce = 0x2000;
    internal const uint MouseEventVirtualDesktop = 0x4000;
    internal const uint MouseEventAbsolute = 0x8000;
    internal const uint KeyboardEventExtendedKey = 0x0001;
    internal const uint KeyboardEventKeyUp = 0x0002;
    internal const uint KeyboardEventScanCode = 0x0008;
    internal const uint XButton1 = 0x0001;
    internal const uint XButton2 = 0x0002;
    internal const uint QsAllInput = 0x04FF;
    internal const uint MwmoInputAvailable = 0x0004;
    internal const uint PmRemove = 0x0001;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint WaitFailed = uint.MaxValue;
    internal const uint Infinite = uint.MaxValue;
    internal const uint CreateWaitableTimerManualReset = 0x00000001;
    internal const uint CreateWaitableTimerHighResolution = 0x00000002;
    internal const uint TimerAllAccess = 0x001F0003;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint MonitorInfoPrimary = 0x00000001;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint GaRoot = 2;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;
    internal const int ErrorSuccess = 0;
    internal const int ErrorBusy = 170;
    internal const int ErrorBufferOverflow = 111;
    internal const int ErrorInvalidData = 13;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint HookProcedure(
        int code,
        nuint message,
        nint data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool MonitorEnumerationProcedure(
        nint monitor,
        nint deviceContext,
        ref NativeRect monitorRect,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseLowLevelHookData
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardLowLevelHookData
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect MonitorArea;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern SafeHookHandle SetWindowsHookEx(
        int hookType,
        HookProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(
        nint hook,
        int code,
        nuint message,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(
        ref NativeMessage message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(
        ref NativeMessage message);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle CreateWaitableTimerEx(
        nint attributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWaitableTimer(
        SafeWaitHandle timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForMultipleObjects(
        uint count,
        [In] nint[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumerationProcedure procedure,
        nint data);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeNativeHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeNativeHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(
        nint window,
        [Out] char[] className,
        int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(
        nint window,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(
        nint window,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(
        nint window,
        ref NativePoint point);
}

internal sealed class SafeHookHandle :
    SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeHookHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        ExactWheelNativeMethods.UnhookWindowsHookEx(handle);
}

internal sealed class SafeNativeHandle :
    SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeNativeHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        ExactWheelNativeMethods.CloseHandle(handle);
}
