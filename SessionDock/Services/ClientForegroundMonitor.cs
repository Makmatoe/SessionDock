using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionDock.Services;

internal interface IClientForegroundMonitorNative
{
    nint InstallForegroundChangedHook(Action<nint> foregroundChanged);

    nint GetForegroundWindow();

    bool RemoveForegroundChangedHook(nint hook);

    int LastError { get; }
}

internal sealed class ClientForegroundMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly IClientForegroundMonitorNative _native;
    private readonly nint _expectedWindow;
    private readonly Action<nint> _foregroundChangedCallback;
    private nint _hook;
    private bool _active;
    private bool _lostForeground;

    internal ClientForegroundMonitor(
        IClientForegroundMonitorNative native,
        nint expectedWindow)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        if (expectedWindow == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedWindow),
                "A foreground monitor requires a non-zero target window.");
        }

        _expectedWindow = expectedWindow;
        _foregroundChangedCallback = ObserveForegroundWindow;
        _active = true;
        _hook = _native.InstallForegroundChangedHook(
            _foregroundChangedCallback);
        if (_hook == nint.Zero)
        {
            _active = false;
            throw new Win32Exception(
                _native.LastError,
                "Windows could not install the foreground-change monitor.");
        }

        // Installing first and sampling second closes the setup gap: a change
        // before the sample is visible in the sample, and a later change is
        // delivered by the WinEvent hook.
        ObserveForegroundWindow(_native.GetForegroundWindow());
    }

    internal static ClientForegroundMonitor Start(nint expectedWindow) =>
        new(new WindowsClientForegroundMonitorNative(), expectedWindow);

    internal event EventHandler? ForegroundLost;

    internal bool HasLostForeground
    {
        get
        {
            lock (_gate)
                return _lostForeground;
        }
    }

    internal bool Complete()
    {
        return Stop(checkCurrentForeground: true);
    }

    public void Dispose()
    {
        _ = Stop(checkCurrentForeground: false);
    }

    private void ObserveForegroundWindow(nint foregroundWindow)
    {
        if (foregroundWindow == _expectedWindow)
            return;

        EventHandler? handlers = null;
        lock (_gate)
        {
            if (!_active || _lostForeground)
                return;

            _lostForeground = true;
            handlers = ForegroundLost;
        }

        NotifyForegroundLost(handlers);
    }

    private bool Stop(bool checkCurrentForeground)
    {
        EventHandler? handlers = null;
        nint hook;
        bool lostForeground;
        lock (_gate)
        {
            if (!_active)
                return _lostForeground;

            // Marking the monitor inactive defines the capture boundary. The
            // current-window sample then detects any change that preceded it,
            // even if its WinEvent callback has not run yet.
            _active = false;
            if (checkCurrentForeground &&
                _native.GetForegroundWindow() != _expectedWindow &&
                !_lostForeground)
            {
                _lostForeground = true;
                handlers = ForegroundLost;
            }

            hook = _hook;
            _hook = nint.Zero;
            lostForeground = _lostForeground;
        }

        if (hook != nint.Zero &&
            !_native.RemoveForegroundChangedHook(hook))
        {
            // The production native adapter deliberately keeps the callback
            // rooted when Windows rejects unhooking, preventing a native call
            // through a collected delegate.
            Trace.WriteLine(
                $"Foreground WinEvent hook cleanup failed with {_native.LastError}.");
        }

        NotifyForegroundLost(handlers);
        return lostForeground;
    }

    private void NotifyForegroundLost(EventHandler? handlers)
    {
        if (handlers is null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // Exceptions must never escape a native WinEvent callback.
                Trace.WriteLine(
                    $"Foreground-loss observer failed: {exception.GetType().Name}.");
            }
        }
    }
}

internal sealed class WindowsClientForegroundMonitorNative :
    IClientForegroundMonitorNative
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjectIdWindow = 0;
    private static readonly ConcurrentDictionary<nint, WinEventCallback>
        ActiveCallbacks = new();

    public int LastError => Marshal.GetLastPInvokeError();

    public nint InstallForegroundChangedHook(
        Action<nint> foregroundChanged)
    {
        ArgumentNullException.ThrowIfNull(foregroundChanged);
        WinEventCallback callback = (
            _,
            eventType,
            window,
            objectId,
            childId,
            _,
            _) =>
        {
            if (eventType == EventSystemForeground &&
                objectId == ObjectIdWindow &&
                childId == 0)
            {
                foregroundChanged(window);
            }
        };
        var hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            callback,
            0,
            0,
            WineventOutOfContext);
        if (hook != nint.Zero)
            ActiveCallbacks[hook] = callback;
        return hook;
    }

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public bool RemoveForegroundChangedHook(nint hook)
    {
        if (!UnhookWinEvent(hook))
            return false;

        _ = ActiveCallbacks.TryRemove(hook, out _);
        return true;
    }

    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();
}
