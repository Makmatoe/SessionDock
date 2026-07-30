using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;

namespace SessionDock;

public partial class WindowCaptionControls : UserControl
{
    private const int NonClientHitTestMessage = 0x0084;
    private const int HitClient = 1;
    private const int HitCaption = 2;
    private const int HitMaximizeButton = 9;
    private const int WindowStyleIndex = -16;
    private const int SystemMenuStyle = 0x00080000;
    private const int ThickFrameStyle = 0x00040000;
    private const int MinimizeBoxStyle = 0x00020000;
    private const int MaximizeBoxStyle = 0x00010000;
    private Window? _window;
    private HwndSource? _source;

    public WindowCaptionControls()
    {
        InitializeComponent();
        Loaded += WindowCaptionControls_Loaded;
        Unloaded += WindowCaptionControls_Unloaded;
    }

    internal void AttachToWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window == window)
            return;
        if (_window is not null)
            throw new InvalidOperationException(
                "Caption controls cannot be moved between windows.");

        _window = window;
        _window.StateChanged += Window_StateChanged;
        _window.SourceInitialized += Window_SourceInitialized;
        UpdatePresentation();
        AttachNativeHook();
    }

    internal void VerifyForRuntimeSmoke()
    {
        if (_window is null || _source is null)
        {
            throw new InvalidOperationException(
                "Caption controls were not attached to the native window.");
        }

        UpdateLayout();
        if (MinimizeButton.ActualWidth < 32 ||
            MaximizeButton.ActualWidth < 32 ||
            CloseButton.ActualWidth < 32)
        {
            throw new InvalidOperationException(
                "Caption controls do not have usable pointer targets.");
        }

        const int requiredWindowStyles =
            SystemMenuStyle |
            ThickFrameStyle |
            MinimizeBoxStyle |
            MaximizeBoxStyle;
        var windowStyles = GetWindowLong(
            _source.Handle,
            WindowStyleIndex);
        if ((windowStyles & requiredWindowStyles) != requiredWindowStyles)
        {
            throw new InvalidOperationException(
                "The custom frame lost native system-menu or resize behavior.");
        }

        VerifyHitTest(
            MinimizeButton.PointToScreen(new Point(
                MinimizeButton.ActualWidth / 2,
                MinimizeButton.ActualHeight / 2)),
            HitClient,
            "The minimize button is being consumed by the drag surface.");
        VerifyHitTest(
            MaximizeButton.PointToScreen(new Point(
                MaximizeButton.ActualWidth / 2,
                MaximizeButton.ActualHeight / 2)),
            HitMaximizeButton,
            "The maximize button is not exposed to Windows Snap Layouts.");
        VerifyHitTest(
            CloseButton.PointToScreen(new Point(
                CloseButton.ActualWidth / 2,
                CloseButton.ActualHeight / 2)),
            HitClient,
            "The close button is being consumed by the drag surface.");
        VerifyHitTest(
            PointToScreen(new Point(-4, ActualHeight / 2)),
            HitCaption,
            "The integrated header no longer exposes a native drag surface.");

        var originalState = _window.WindowState;
        try
        {
            _window.WindowState = WindowState.Maximized;
            UpdatePresentation();
            if (_window.WindowState != WindowState.Maximized ||
                !string.Equals(
                    AutomationProperties.GetName(MaximizeButton),
                    ((App)Application.Current).LocalizationService.GetString(
                        "Caption.RestoreName"),
                    StringComparison.Ordinal) ||
                RestoreGlyph.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException(
                    "Maximize/restore presentation did not follow window state.");
            }

            _window.WindowState = WindowState.Normal;
            UpdatePresentation();
            if (_window.WindowState != WindowState.Normal ||
                !string.Equals(
                    AutomationProperties.GetName(MaximizeButton),
                    ((App)Application.Current).LocalizationService.GetString(
                        "Caption.MaximizeName"),
                    StringComparison.Ordinal) ||
                MaximizeGlyph.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException(
                    "The caption did not return to its normal state.");
            }
        }
        finally
        {
            _window.WindowState = originalState;
            UpdatePresentation();
        }
    }

    internal void CloseForRuntimeSmoke() =>
        CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private void WindowCaptionControls_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        AttachToWindow(Window.GetWindow(this) ??
            throw new InvalidOperationException(
                "Caption controls must be hosted inside a Window."));
    }

    private void WindowCaptionControls_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window.SourceInitialized -= Window_SourceInitialized;
            _window = null;
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        AttachNativeHook();

    private void AttachNativeHook()
    {
        if (_window is null || _source is not null)
            return;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(handle) ??
            throw new InvalidOperationException(
                "The native window source could not be resolved.");
        _source.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != NonClientHitTestMessage ||
            MaximizeButton.Visibility != Visibility.Visible ||
            !MaximizeButton.IsEnabled)
        {
            return IntPtr.Zero;
        }

        var packedPoint = longParameter.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var buttonPoint = MaximizeButton.PointFromScreen(screenPoint);
        if (buttonPoint.X < 0 ||
            buttonPoint.Y < 0 ||
            buttonPoint.X > MaximizeButton.ActualWidth ||
            buttonPoint.Y > MaximizeButton.ActualHeight)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HitMaximizeButton);
    }

    private void Window_StateChanged(object? sender, EventArgs e) =>
        UpdatePresentation();

    private void UpdatePresentation()
    {
        if (_window is null)
            return;

        MinimizeButton.Visibility = _window.ResizeMode == ResizeMode.NoResize
            ? Visibility.Collapsed
            : Visibility.Visible;
        MaximizeButton.Visibility = _window.ResizeMode is
            ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
                ? Visibility.Visible
                : Visibility.Collapsed;

        var isMaximized = _window.WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = isMaximized
            ? Visibility.Collapsed
            : Visibility.Visible;
        RestoreGlyph.Visibility = isMaximized
            ? Visibility.Visible
            : Visibility.Collapsed;
        var actionKey = isMaximized
            ? "Caption.Restore"
            : "Caption.Maximize";
        var nameKey = isMaximized
            ? "Caption.RestoreName"
            : "Caption.MaximizeName";
        MaximizeButton.SetResourceReference(
            FrameworkElement.ToolTipProperty,
            actionKey);
        MaximizeButton.SetResourceReference(
            AutomationProperties.NameProperty,
            nameKey);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
            SystemCommands.MinimizeWindow(_window);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
            return;

        if (_window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(_window);
        else
            SystemCommands.MaximizeWindow(_window);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
            SystemCommands.CloseWindow(_window);
    }

    private static IntPtr PackScreenPoint(Point point)
    {
        var x = unchecked((ushort)(short)Math.Round(point.X));
        var y = unchecked((ushort)(short)Math.Round(point.Y));
        return new IntPtr(x | (y << 16));
    }

    private void VerifyHitTest(
        Point screenPoint,
        int expectedResult,
        string errorMessage)
    {
        var actualResult = SendMessage(
            _source!.Handle,
            NonClientHitTestMessage,
            IntPtr.Zero,
            PackScreenPoint(screenPoint));
        if (actualResult.ToInt32() != expectedResult)
            throw new InvalidOperationException(errorMessage);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(
        IntPtr windowHandle,
        int index);
}
