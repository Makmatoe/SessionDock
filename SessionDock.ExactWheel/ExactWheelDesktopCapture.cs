using System.ComponentModel;
using System.Runtime.InteropServices;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.ExactWheel;

public static class ExactWheelDesktopCapture
{
    private const int EffectiveDpi = 0;

    public static ExactWheelRecordingTarget CaptureRecordingTarget(
        nint windowHandle,
        bool requireForeground = true)
    {
        var root = ValidateTargetWindow(windowHandle, requireForeground);

        _ = ExactWheelNativeMethods.GetWindowThreadProcessId(
            root,
            out var processId);
        if (processId == 0)
            throw LastWin32("The selected target process could not be identified.");

        using var process = ExactWheelNativeMethods.OpenProcess(
            ExactWheelNativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (process.IsInvalid)
            throw LastWin32("The selected target process could not be opened.");

        var executablePath = new char[32_768];
        var pathLength = (uint)executablePath.Length;
        if (!ExactWheelNativeMethods.QueryFullProcessImageName(
                process,
                flags: 0,
                executablePath,
                ref pathLength))
        {
            throw LastWin32("The selected target executable could not be identified.");
        }

        var processBasename = Path.GetFileName(
            new string(executablePath, 0, checked((int)pathLength)));
        return new ExactWheelRecordingTarget(
            root,
            CaptureDisplayTopology(),
            CaptureTargetMetadata(root, processBasename));
    }

    /// <summary>
    /// Captures the changing geometry for an already verified playback target.
    /// Callers can share one display snapshot across multiple clients and avoid
    /// reopening and querying the same trusted process on every macro loop.
    /// </summary>
    public static ExactWheelRecordingTarget CapturePlaybackTarget(
        nint windowHandle,
        ExactWheelDisplayTopology display,
        string verifiedProcessBasename,
        bool requireForeground = true)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidateProcessBasename(verifiedProcessBasename);

        var root = ValidateTargetWindow(windowHandle, requireForeground);
        return new ExactWheelRecordingTarget(
            root,
            display,
            CaptureTargetMetadata(root, verifiedProcessBasename));
    }

    /// <summary>
    /// Reads the immutable Win32 class of a currently validated playback
    /// window. A per-run caller can cache it by exact retained process/HWND.
    /// </summary>
    public static string CapturePlaybackWindowClass(
        nint windowHandle,
        bool requireForeground = true)
    {
        var root = ValidateTargetWindow(windowHandle, requireForeground);
        return CaptureWindowClass(root);
    }

    /// <summary>
    /// Captures immutable target metadata while reusing geometry read from an
    /// immediately preceding exact-process focus validation. Foreground,
    /// visibility, HWND validity, and window class are still checked live.
    /// </summary>
    public static ExactWheelRecordingTarget CapturePlaybackTarget(
        nint windowHandle,
        ExactWheelDisplayTopology display,
        string verifiedProcessBasename,
        ExactWheelRect verifiedWindowRect,
        ExactWheelRect verifiedClientRect,
        bool requireForeground = true)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidateProcessBasename(verifiedProcessBasename);
        ValidateSnapshotRect(verifiedWindowRect, nameof(verifiedWindowRect));
        ValidateSnapshotRect(verifiedClientRect, nameof(verifiedClientRect));

        var root = ValidateTargetWindow(windowHandle, requireForeground);
        return new ExactWheelRecordingTarget(
            root,
            display,
            new ExactWheelTargetMetadata(
                verifiedProcessBasename,
                CaptureWindowClass(root),
                verifiedWindowRect,
                verifiedClientRect));
    }

    /// <summary>
    /// Creates live-validated playback metadata from geometry returned by the
    /// immediately preceding exact-target focus and a window class cached by
    /// the same retained process/HWND lease. The focus snapshot's client rect
    /// is screen-space because focus mapped both client corners before
    /// returning the snapshot. Geometry is necessarily point-in-time; every
    /// loop refreshes it before playback,
    /// while dispatch guards still fail closed if focus or pointer ownership
    /// changes afterward.
    /// </summary>
    public static ExactWheelRecordingTarget CapturePlaybackTarget(
        nint windowHandle,
        ExactWheelDisplayTopology display,
        string verifiedProcessBasename,
        string verifiedWindowClass,
        ExactWheelRect verifiedWindowRect,
        ExactWheelRect verifiedClientRect,
        bool requireForeground = true)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidateProcessBasename(verifiedProcessBasename);
        ValidateWindowClass(verifiedWindowClass);
        ValidateSnapshotRect(verifiedWindowRect, nameof(verifiedWindowRect));
        ValidateSnapshotRect(verifiedClientRect, nameof(verifiedClientRect));

        var root = ValidateTargetWindow(windowHandle, requireForeground);
        return new ExactWheelRecordingTarget(
            root,
            display,
            new ExactWheelTargetMetadata(
                verifiedProcessBasename,
                verifiedWindowClass,
                verifiedWindowRect,
                verifiedClientRect));
    }

    public static ExactWheelDisplayTopology CaptureDisplayTopology()
    {
        var virtualLeft = ExactWheelNativeMethods.GetSystemMetrics(
            ExactWheelNativeMethods.SmXVirtualScreen);
        var virtualTop = ExactWheelNativeMethods.GetSystemMetrics(
            ExactWheelNativeMethods.SmYVirtualScreen);
        var virtualWidth = ExactWheelNativeMethods.GetSystemMetrics(
            ExactWheelNativeMethods.SmCxVirtualScreen);
        var virtualHeight = ExactWheelNativeMethods.GetSystemMetrics(
            ExactWheelNativeMethods.SmCyVirtualScreen);
        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            throw new InvalidOperationException(
                "The current virtual desktop bounds are invalid.");
        }

        var monitors = new List<ExactWheelMonitorSnapshot>();
        ExactWheelNativeMethods.MonitorEnumerationProcedure callback =
            (nint monitor,
             nint _,
             ref ExactWheelNativeMethods.NativeRect bounds,
             nint _) =>
            {
                var dpiX = 96U;
                var dpiY = 96U;
                try
                {
                    if (ExactWheelNativeMethods.GetDpiForMonitor(
                            monitor,
                            EffectiveDpi,
                            out var reportedX,
                            out var reportedY) == 0 &&
                        reportedX > 0 &&
                        reportedY > 0)
                    {
                        dpiX = reportedX;
                        dpiY = reportedY;
                    }
                }
                catch (DllNotFoundException)
                {
                    // Windows without shcore.dll uses the safe 96-DPI fallback.
                }
                catch (EntryPointNotFoundException)
                {
                    // Windows without this API uses the safe 96-DPI fallback.
                }

                monitors.Add(new ExactWheelMonitorSnapshot(
                    ToModel(bounds),
                    dpiX,
                    dpiY));
                return true;
            };

        if (!ExactWheelNativeMethods.EnumDisplayMonitors(
                0,
                0,
                callback,
                0) ||
            monitors.Count == 0)
        {
            throw LastWin32("The current monitor layout could not be read.");
        }

        monitors.Sort(static (left, right) =>
        {
            var comparison = left.Bounds.Left.CompareTo(right.Bounds.Left);
            if (comparison != 0)
                return comparison;
            comparison = left.Bounds.Top.CompareTo(right.Bounds.Top);
            if (comparison != 0)
                return comparison;
            comparison = left.Bounds.Right.CompareTo(right.Bounds.Right);
            return comparison != 0
                ? comparison
                : left.Bounds.Bottom.CompareTo(right.Bounds.Bottom);
        });
        var topology = new ExactWheelDisplayTopology(
            virtualLeft,
            virtualTop,
            virtualWidth,
            virtualHeight,
            monitors);

        // Exercise the same validation boundary used by macro loading without
        // fabricating an otherwise meaningful recording.
        var diagnostic = new ExactWheelRecording(
            0,
            topology,
            new ExactWheelTargetMetadata(
                "SessionDock.exe",
                string.Empty,
                default,
                default),
            []);
        ExactWheelRecordingValidator.Validate(diagnostic);
        return topology;
    }

    public static bool IsForeground(nint windowHandle)
    {
        var expected = GetRootWindow(windowHandle);
        var foreground = GetRootWindow(
            ExactWheelNativeMethods.GetForegroundWindow());
        return expected != 0 && expected == foreground;
    }

    internal static nint GetRootWindow(nint windowHandle) =>
        windowHandle == 0
            ? 0
            : ExactWheelNativeMethods.GetAncestor(
                windowHandle,
                ExactWheelNativeMethods.GaRoot) is var root && root != 0
                ? root
                : windowHandle;

    private static nint ValidateTargetWindow(
        nint windowHandle,
        bool requireForeground)
    {
        var root = GetRootWindow(windowHandle);
        if (root == 0 || !ExactWheelNativeMethods.IsWindow(root))
        {
            throw new ArgumentException(
                "A valid target window is required.",
                nameof(windowHandle));
        }
        if (!ExactWheelNativeMethods.IsWindowVisible(root))
        {
            throw new InvalidOperationException(
                "The selected target window is not visible.");
        }
        if (ExactWheelNativeMethods.IsIconic(root))
        {
            throw new InvalidOperationException(
                "The selected target window is minimized.");
        }
        if (requireForeground && !IsForeground(root))
        {
            throw new InvalidOperationException(
                "The selected target must be foreground before recording starts.");
        }

        return root;
    }

    private static ExactWheelTargetMetadata CaptureTargetMetadata(
        nint root,
        string processBasename)
    {
        if (!ExactWheelNativeMethods.GetWindowRect(
                root,
                out var nativeWindowRect) ||
            !ExactWheelNativeMethods.GetClientRect(
                root,
                out var nativeClientRect))
        {
            throw LastWin32("The selected target bounds could not be read.");
        }

        var clientTopLeft = new ExactWheelNativeMethods.NativePoint
        {
            X = nativeClientRect.Left,
            Y = nativeClientRect.Top
        };
        var clientBottomRight = new ExactWheelNativeMethods.NativePoint
        {
            X = nativeClientRect.Right,
            Y = nativeClientRect.Bottom
        };
        if (!ExactWheelNativeMethods.ClientToScreen(root, ref clientTopLeft) ||
            !ExactWheelNativeMethods.ClientToScreen(root, ref clientBottomRight))
        {
            throw LastWin32(
                "The selected target client bounds could not be mapped.");
        }

        return new ExactWheelTargetMetadata(
            processBasename,
            CaptureWindowClass(root),
            ToModel(nativeWindowRect),
            new ExactWheelRect(
                clientTopLeft.X,
                clientTopLeft.Y,
                clientBottomRight.X,
                clientBottomRight.Y));
    }

    private static string CaptureWindowClass(nint root)
    {
        var classNameBuffer = new char[
            ExactWheelLimits.MaximumWindowClassUtf16Units + 1];
        var classLength = ExactWheelNativeMethods.GetClassName(
            root,
            classNameBuffer,
            classNameBuffer.Length);
        if (classLength <= 0)
            throw LastWin32("The selected target window class could not be read.");
        return new string(classNameBuffer, 0, classLength);
    }

    private static void ValidateProcessBasename(string processBasename)
    {
        if (string.IsNullOrWhiteSpace(processBasename) ||
            processBasename.Length >
                ExactWheelLimits.MaximumProcessBasenameUtf16Units ||
            !string.Equals(
                Path.GetFileName(processBasename),
                processBasename,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A verified process basename is required.",
                nameof(processBasename));
        }
    }

    private static void ValidateWindowClass(string windowClass)
    {
        if (string.IsNullOrWhiteSpace(windowClass) ||
            windowClass.Length > ExactWheelLimits.MaximumWindowClassUtf16Units)
        {
            throw new ArgumentException(
                "A verified window class is required.",
                nameof(windowClass));
        }
    }

    private static void ValidateSnapshotRect(
        ExactWheelRect rectangle,
        string parameterName)
    {
        try
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                rectangle,
                $"The verified target bounds are invalid: {exception.Message}");
        }
    }

    private static ExactWheelRect ToModel(
        ExactWheelNativeMethods.NativeRect rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);

    private static Win32Exception LastWin32(string message) =>
        new(Marshal.GetLastWin32Error(), message);
}
