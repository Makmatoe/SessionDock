using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SessionDock.Models;

namespace SessionDock.Services;

public static class WindowLayoutService
{
    private const double WorkAreaMargin = 16;
    private const uint MonitorInfoPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPositionFlags = 0x0004 | 0x0010;
    private const uint EffectiveDpi = 0;

    public static bool RestoreMainWindowPlacement(
        Window window,
        WindowPlacementSettings? placement)
    {
        ArgumentNullException.ThrowIfNull(window);
        var normalized = WindowPlacementPolicy.Normalize(placement);
        if (normalized is null)
            return false;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            window.SourceInitialized -= handler;
            ApplyMainWindowPlacement(window, normalized);
        };

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
            window.SourceInitialized += handler;
        else
            ApplyMainWindowPlacement(window, normalized);
        return true;
    }

    public static WindowPlacementSettings? CaptureMainWindowPlacement(
        Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!TryGetMonitorWorkArea(
                window,
                out var workArea,
                out var monitorDeviceName))
        {
            return null;
        }

        var bounds = window.RestoreBounds;
        return WindowPlacementPolicy.Normalize(new WindowPlacementSettings
        {
            MonitorDeviceName = monitorDeviceName,
            OffsetX = bounds.Left - workArea.Left,
            OffsetY = bounds.Top - workArea.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            // Minimized windows intentionally restore as normal.
            IsMaximized = window.WindowState == WindowState.Maximized
        });
    }

    public static void FitToWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!TryGetMonitorWorkArea(window, out var workArea))
            return;

        var availableWidth = Math.Max(1, workArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (WorkAreaMargin * 2));

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);

        var width = GetCurrentDimension(window.ActualWidth, window.Width);
        var height = GetCurrentDimension(window.ActualHeight, window.Height);
        if (width > availableWidth)
        {
            DisableWidthSizeToContent(window);
            window.Width = availableWidth;
            width = availableWidth;
        }
        if (height > availableHeight)
        {
            DisableHeightSizeToContent(window);
            window.Height = availableHeight;
            height = availableHeight;
        }

        if (!IsFinitePositive(width) ||
            !IsFinitePositive(height) ||
            !double.IsFinite(window.Left) ||
            !double.IsFinite(window.Top))
        {
            return;
        }

        var fitted = CalculateFittedBounds(
            workArea,
            new Rect(window.Left, window.Top, width, height));
        window.Left = fitted.Left;
        window.Top = fitted.Top;
    }

    internal static Rect CalculateFittedBounds(
        Rect workArea,
        Rect windowBounds)
    {
        var availableWidth = Math.Max(
            1,
            workArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(
            1,
            workArea.Height - (WorkAreaMargin * 2));
        var width = Math.Min(windowBounds.Width, availableWidth);
        var height = Math.Min(windowBounds.Height, availableHeight);
        var minimumLeft = workArea.Left + WorkAreaMargin;
        var minimumTop = workArea.Top + WorkAreaMargin;
        var maximumLeft = Math.Max(
            minimumLeft,
            workArea.Right - WorkAreaMargin - width);
        var maximumTop = Math.Max(
            minimumTop,
            workArea.Bottom - WorkAreaMargin - height);

        return new Rect(
            Math.Clamp(windowBounds.Left, minimumLeft, maximumLeft),
            Math.Clamp(windowBounds.Top, minimumTop, maximumTop),
            width,
            height);
    }

    private static double GetCurrentDimension(
        double actualDimension,
        double requestedDimension) =>
        IsFinitePositive(actualDimension)
            ? actualDimension
            : requestedDimension;

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static void DisableWidthSizeToContent(Window window)
    {
        window.SizeToContent = window.SizeToContent switch
        {
            SizeToContent.Width => SizeToContent.Manual,
            SizeToContent.WidthAndHeight => SizeToContent.Height,
            _ => window.SizeToContent
        };
    }

    private static void DisableHeightSizeToContent(Window window)
    {
        window.SizeToContent = window.SizeToContent switch
        {
            SizeToContent.Height => SizeToContent.Manual,
            SizeToContent.WidthAndHeight => SizeToContent.Width,
            _ => window.SizeToContent
        };
    }

    private static bool TryGetMonitorWorkArea(
        Window window,
        out Rect workArea) =>
        TryGetMonitorWorkArea(window, out workArea, out _);

    private static bool TryGetMonitorWorkArea(
        Window window,
        out Rect workArea,
        out string? monitorDeviceName)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            workArea = default;
            monitorDeviceName = null;
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (monitor == IntPtr.Zero ||
            !GetMonitorInfoEx(monitor, ref monitorInfo))
        {
            workArea = default;
            monitorDeviceName = null;
            return false;
        }

        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            workArea = default;
            monitorDeviceName = null;
            return false;
        }

        var topLeft = transform.Value.Transform(new Point(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top));
        var bottomRight = transform.Value.Transform(new Point(
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom));
        workArea = new Rect(topLeft, bottomRight);
        monitorDeviceName = monitorInfo.DeviceName;
        return true;
    }

    private static void ApplyMainWindowPlacement(
        Window window,
        WindowPlacementSettings placement)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero ||
            !TrySelectMonitor(placement.MonitorDeviceName, out var monitor))
        {
            return;
        }

        var scaleX = monitor.DpiX / 96d;
        var scaleY = monitor.DpiY / 96d;
        var workArea = new Rect(
            0,
            0,
            monitor.WorkArea.Width / scaleX,
            monitor.WorkArea.Height / scaleY);
        var restoredBounds = WindowPlacementPolicy.CalculateRestoredBounds(
            workArea,
            placement,
            window.MinWidth,
            window.MinHeight);
        if (restoredBounds is null)
            return;

        window.SizeToContent = SizeToContent.Manual;
        window.WindowState = WindowState.Normal;
        window.Width = restoredBounds.Value.Width;
        window.Height = restoredBounds.Value.Height;
        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            monitor.WorkArea.Left + ToDevicePixels(
                restoredBounds.Value.Left,
                scaleX),
            monitor.WorkArea.Top + ToDevicePixels(
                restoredBounds.Value.Top,
                scaleY),
            ToDevicePixels(restoredBounds.Value.Width, scaleX),
            ToDevicePixels(restoredBounds.Value.Height, scaleY),
            SetWindowPositionFlags);

        window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                FitToWorkArea(window);
                if (placement.IsMaximized)
                    window.WindowState = WindowState.Maximized;
            });
    }

    private static bool TrySelectMonitor(
        string? preferredDeviceName,
        out NativeMonitor selected)
    {
        var monitors = EnumerateMonitors();
        var preferred = monitors.FirstOrDefault(monitor => string.Equals(
            monitor.DeviceName,
            preferredDeviceName,
            StringComparison.OrdinalIgnoreCase));
        if (preferred.Handle != IntPtr.Zero)
        {
            selected = preferred;
            return true;
        }

        selected = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        if (selected.Handle != IntPtr.Zero)
            return true;

        selected = monitors.Count > 0 ? monitors[0] : default;
        return selected.Handle != IntPtr.Zero;
    }

    private static IReadOnlyList<NativeMonitor> EnumerateMonitors()
    {
        var monitors = new List<NativeMonitor>();

        bool AddMonitor(
            IntPtr monitorHandle,
            IntPtr monitorDeviceContext,
            ref NativeRect monitorBounds,
            IntPtr data)
        {
            _ = monitorDeviceContext;
            _ = monitorBounds;
            _ = data;
            var info = new MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty
            };
            if (!GetMonitorInfoEx(monitorHandle, ref info))
                return true;

            GetMonitorDpi(monitorHandle, out var dpiX, out var dpiY);
            monitors.Add(new NativeMonitor(
                monitorHandle,
                info.DeviceName,
                info.WorkArea,
                (info.Flags & MonitorInfoPrimary) != 0,
                dpiX,
                dpiY));
            return true;
        }

        _ = EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            AddMonitor,
            IntPtr.Zero);
        return monitors;
    }

    private static void GetMonitorDpi(
        IntPtr monitorHandle,
        out uint dpiX,
        out uint dpiY)
    {
        dpiX = 96;
        dpiY = 96;
        try
        {
            if (GetDpiForMonitor(
                    monitorHandle,
                    EffectiveDpi,
                    out var reportedDpiX,
                    out var reportedDpiY) == 0 &&
                reportedDpiX > 0 &&
                reportedDpiY > 0)
            {
                dpiX = reportedDpiX;
                dpiY = reportedDpiY;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows versions without per-monitor DPI use 96 DPI fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // Windows versions without per-monitor DPI use 96 DPI fallback.
        }
    }

    private static int ToDevicePixels(double value, double scale) =>
        Math.Max(1, (int)Math.Round(value * scale));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal uint Size;
        internal NativeRect MonitorArea;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    private readonly record struct NativeMonitor(
        IntPtr Handle,
        string DeviceName,
        NativeRect WorkArea,
        bool IsPrimary,
        uint DpiX,
        uint DpiY);

    private delegate bool MonitorEnumerationCallback(
        IntPtr monitorHandle,
        IntPtr monitorDeviceContext,
        ref NativeRect monitorBounds,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr windowHandle,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(
        IntPtr monitorHandle,
        ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumerationCallback callback,
        IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitorHandle,
        uint dpiType,
        out uint dpiX,
        out uint dpiY);
}
