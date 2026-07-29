using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock;

public partial class App : Application
{
    private const string ProductionApplicationId = "RobloxOneLauncher";
    private readonly string _applicationId;
    private readonly string? _startupExternalLink;
#if SESSIONDOCK_SMOKE_HARNESS
    private readonly RuntimeSmokeTestOptions? _runtimeSmokeTest;
#endif
    private SingleInstanceService? _singleInstance;
    private readonly LatestOnlyRequestQueue<string> _externalLinkDispatchQueue =
        new();
    private AppThemeService? _themeService;
    private AppLocalizationService? _localizationService;
    public UiSoundService SoundService { get; private set; } = null!;
    public bool UiSoundsEnabled { get; set; } = true;
#if SESSIONDOCK_SMOKE_HARNESS
    private bool IsRuntimeSmokeTest => _runtimeSmokeTest is not null;
#endif
    internal AppThemeService ThemeService => _themeService ??
        throw new InvalidOperationException(
            "The application theme service has not started.");
    internal AppLocalizationService LocalizationService =>
        _localizationService ?? throw new InvalidOperationException(
            "The application localization service has not started.");

    public App(string? startupExternalLink = null)
    {
        _applicationId = ProductionApplicationId;
        _startupExternalLink = startupExternalLink;
    }

#if SESSIONDOCK_SMOKE_HARNESS
    internal App(RuntimeSmokeTestOptions runtimeSmokeTest)
    {
        ArgumentNullException.ThrowIfNull(runtimeSmokeTest);
        _applicationId = runtimeSmokeTest.ApplicationId;
        _runtimeSmokeTest = runtimeSmokeTest;
    }
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        // Production retains the original mutex name so renamed and older
        // copies cannot run against the same browser profiles at the same time.
        _singleInstance = new SingleInstanceService(_applicationId);
        if (!_singleInstance.IsPrimaryInstance)
        {
            var linkForwarded = true;
            if (_startupExternalLink is not null)
            {
                linkForwarded = _singleInstance.ForwardExternalLinkAsync(
                        _startupExternalLink,
                        TimeSpan.FromSeconds(3))
                    .GetAwaiter()
                    .GetResult();
            }
            _singleInstance.NotifyPrimaryInstance();
            if (!linkForwarded)
            {
                MessageBox.Show(
                    "The running SessionDock window did not accept the link in time. No account was launched. Try the link again after the window finishes starting.",
                    "SessionDock could not forward the link",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(Button_Click));
        EventManager.RegisterClassHandler(
            typeof(ToggleButton),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(Button_Click));
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded),
            handledEventsToo: true);
        base.OnStartup(e);
        _themeService = new AppThemeService(this);
        _themeService.ThemeChanged += ThemeService_ThemeChanged;
        _localizationService = new AppLocalizationService(this);

        if (!ApplicationStartup.TryStart(
                () =>
                {
                    SoundService = new UiSoundService();
                    var mainWindow = new MainWindow();
                    MainWindow = mainWindow;
#if SESSIONDOCK_SMOKE_HARNESS
                    if (_runtimeSmokeTest is not null)
                    {
                        // Start WPF normally so Loaded, layout, and dispatcher
                        // behavior are exercised without flashing or activating
                        // a window on the maintainer's desktop.
                        mainWindow.ShowActivated = false;
                        mainWindow.ShowInTaskbar = false;
                        mainWindow.Opacity = 0;
                        mainWindow.WindowStartupLocation =
                            WindowStartupLocation.Manual;
                        mainWindow.Left = SystemParameters.VirtualScreenLeft - 10000;
                        mainWindow.Top = SystemParameters.VirtualScreenTop - 10000;
                    }
#endif
                    mainWindow.Show();
#if SESSIONDOCK_SMOKE_HARNESS
                    if (_runtimeSmokeTest is not null)
                    {
                        _ = CompleteRuntimeSmokeTestAsync(
                            mainWindow,
                            _runtimeSmokeTest);
                    }
#endif
                },
                ReportStartupFailure))
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            SoundService?.Dispose();
            Shutdown(1);
            return;
        }

        _singleInstance.ListenForActivationRequests(() =>
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                ActivateExistingWindow));
        _singleInstance.ListenForExternalLinkRequests(externalLink =>
            QueueExternalLinkForDispatch(externalLink));
        if (_startupExternalLink is not null)
            QueueExternalLinkForDispatch(_startupExternalLink);
    }

    private void ReportStartupFailure(string message)
    {
#if SESSIONDOCK_SMOKE_HARNESS
        if (IsRuntimeSmokeTest)
        {
            // A non-interactive harness must fail by exit code rather than
            // wait forever behind an invisible modal dialog.
            System.Diagnostics.Trace.WriteLine(
                $"Isolated runtime-smoke startup failed safely: {message}");
            return;
        }
#endif

        MessageBox.Show(
            message,
            "SessionDock cannot start",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        if (_themeService is not null)
            _themeService.ThemeChanged -= ThemeService_ThemeChanged;
        _themeService?.Dispose();
        _localizationService?.Dispose();
        SoundService?.Dispose();
        base.OnExit(e);
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element &&
            IsInsideCaptionControls(element))
        {
            return;
        }

        SoundService?.PlayUiClick(UiSoundsEnabled);
    }

    private static bool IsInsideCaptionControls(DependencyObject element)
    {
        for (var current = element;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is WindowCaptionControls)
                return true;
        }

        return false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        ApplyNativeWindowTheme(window);
#if SESSIONDOCK_SMOKE_HARNESS
        if (_runtimeSmokeTest is null)
            WindowLayoutService.FitToWorkArea(window);
#else
        WindowLayoutService.FitToWorkArea(window);
#endif
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        foreach (Window window in Windows)
            ApplyNativeWindowTheme(window);
    }

    private void ApplyNativeWindowTheme(Window window)
    {
        if (_themeService is null)
            return;

        NativeWindowFrameService.ApplyTheme(
            window,
            _themeService.UseLightThemePreference,
            _themeService.IsHighContrastActive);
    }

    private void ActivateExistingWindow()
    {
        var window = Windows.OfType<MainWindow>().FirstOrDefault() ?? MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void ReceiveExternalLink(string externalLink)
    {
        ActivateExistingWindow();
        if (MainWindow is MainWindow mainWindow)
            mainWindow.QueueExternalRobloxLink(externalLink);
    }

    private void QueueExternalLinkForDispatch(string externalLink)
    {
        if (!_externalLinkDispatchQueue.Enqueue(
                externalLink,
                out var firstRequest))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => DispatchExternalLinks(firstRequest!));
    }

    private void DispatchExternalLinks(string firstRequest)
    {
        var current = firstRequest;
        while (true)
        {
            try
            {
                ReceiveExternalLink(current);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"An external-link UI dispatch failed safely: {exception.GetType().Name}.");
            }

            if (!_externalLinkDispatchQueue.CompleteCurrent(out var nextRequest))
                return;
            current = nextRequest!;
        }
    }

#if SESSIONDOCK_SMOKE_HARNESS
    private async Task CompleteRuntimeSmokeTestAsync(
        MainWindow mainWindow,
        RuntimeSmokeTestOptions options)
    {
        try
        {
            var startupFailure = await mainWindow.StartupCompletion;
            if (startupFailure is not null)
            {
                throw new InvalidOperationException(
                    "The isolated runtime smoke-test startup failed.",
                    startupFailure);
            }
            VerifyIntegratedWindowChrome(mainWindow);
            mainWindow.VerifyThemeSwitchForRuntimeSmoke();
            mainWindow.VerifyLocalizationSwitchForRuntimeSmoke();
            mainWindow.VerifyJoinUserUiForRuntimeSmoke();

            void HandleShutdownCompleted(Exception? shutdownFailure)
            {
                mainWindow.ShutdownCompleted -= HandleShutdownCompleted;
                if (shutdownFailure is not null)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"Isolated runtime smoke shutdown failed: {shutdownFailure.GetType().Name}.");
                    return;
                }

                try
                {
                    WriteRuntimeSmokeSuccessMarker(options.ResultPath);
                }
                catch (Exception exception)
                {
                    // A missing marker makes the outer smoke script fail.
                    System.Diagnostics.Trace.WriteLine(
                        $"Isolated runtime smoke result failed: {exception.GetType().Name}.");
                }
            }

            // This deliberately takes the normal Closing path so the smoke
            // validates bounded persistence and teardown before process exit.
            mainWindow.ShutdownCompleted += HandleShutdownCompleted;
            mainWindow.CaptionControls.CloseForRuntimeSmoke();
        }
        catch (Exception exception)
        {
            // Smoke mode converts every startup fault into a failed process;
            // it must never report a false success or wait for interaction.
            System.Diagnostics.Trace.WriteLine(
                $"Isolated runtime smoke failed: {exception.GetType().Name}.");
            Shutdown(1);
        }
    }

    private static void VerifyIntegratedWindowChrome(MainWindow mainWindow)
    {
        var chrome = WindowChrome.GetWindowChrome(mainWindow);
        if (mainWindow.WindowStyle != WindowStyle.None ||
            mainWindow.AllowsTransparency ||
            chrome is null ||
            chrome.CaptionHeight != 64 ||
            chrome.GlassFrameThickness != new Thickness(0) ||
            chrome.UseAeroCaptionButtons)
        {
            throw new InvalidOperationException(
                "The integrated native window chrome was not initialized.");
        }

        mainWindow.CaptionControls.VerifyForRuntimeSmoke();
    }

    private static void WriteRuntimeSmokeSuccessMarker(string resultPath)
    {
        var temporaryPath = resultPath + ".pending";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("SessionDock isolated runtime startup and shutdown completed.");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, resultPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Isolated runtime smoke temporary result cleanup failed: {exception.GetType().Name}.");
            }
        }
    }
#endif
}
