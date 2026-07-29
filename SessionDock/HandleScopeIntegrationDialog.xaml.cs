using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class HandleScopeIntegrationDialog : Window
{
    private readonly HandleScopeIntegrationService _integrationService = new();
    private readonly HandleScopeReleaseInstaller _releaseInstaller = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly AccessibilityLiveRegion _stateLiveRegion;
    private readonly AccessibilityLiveRegion _actionStatusLiveRegion;
    private HandleScopeIntegrationState _state =
        HandleScopeIntegrationState.NotInstalled;
    private bool _canRepairConfiguration;
    private bool _repairEnablesIntegration = true;
    private bool _isBusy;
    private bool _installCommitInProgress;
    private bool _isClosed;

    public HandleScopeIntegrationDialog()
    {
        InitializeComponent();
        _stateLiveRegion = new AccessibilityLiveRegion(StateTitleText);
        _actionStatusLiveRegion =
            new AccessibilityLiveRegion(ActionStatusText);
        WindowLayoutService.FitToWorkArea(this);
        Loaded += HandleScopeIntegrationDialog_Loaded;
        Closing += HandleScopeIntegrationDialog_Closing;
        Closed += HandleScopeIntegrationDialog_Closed;
    }

    private async void HandleScopeIntegrationDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= HandleScopeIntegrationDialog_Loaded;
        WindowLayoutService.FitToWorkArea(this);
        await RunActionAsync(
            cancellationToken => _integrationService.InspectAsync(cancellationToken),
            "Local setup inspected. Use Test connection to contact the API.",
            repairEnablesIntegration: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.InspectAsync(cancellationToken),
            "Local setup refreshed. No connection test was performed.",
            repairEnablesIntegration: true);

    private async void StartApiButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.StartAsync(cancellationToken),
            "Start checked. A validated running API was left unchanged; otherwise a start was requested. Select Test connection to confirm readiness.",
            repairEnablesIntegration: true);

    private async void EnableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.EnableAsync(
                repairExisting: false,
                cancellationToken),
            "Integration enabled locally. Start the API, then test the connection.",
            repairEnablesIntegration: true);

    private async void DisableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: false,
                cancellationToken),
            "Integration disabled. HandleScope may keep running, but SessionDock will not use it during launches.",
            repairEnablesIntegration: false);

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken =>
                _integrationService.TestConnectionAsync(cancellationToken),
            "Connection test completed without closing or inspecting any handles.",
            repairEnablesIntegration: true);

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repairEnablesIntegration)
        {
            await RunActionAsync(
                cancellationToken => _integrationService.EnableAsync(
                    repairExisting: true,
                    cancellationToken),
                "Integration configuration repaired and enabled with the fixed minimal policy. Start the API, then test the connection.",
                repairEnablesIntegration: true);
            return;
        }

        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: true,
                cancellationToken),
            "Integration configuration repaired and disabled. HandleScope may keep running, but SessionDock will not use it during launches.",
            repairEnablesIntegration: false);
    }

    private async void InstallLatestHandleScopeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var confirmation = MessageBox.Show(
            this,
            "SessionDock will download the latest stable Windows x64 release from the canonical Makmatoe/HandleScope repository. Before anything runs, it verifies the immutable GitHub asset digest, matching checksum, safe ZIP layout, and every file in the internal inventory. It also saves that verified inventory and rechecks the installed API before starting or trusting it.\n\nHandleScope is unsigned, so this confirms the canonical GitHub release rather than a certificate-backed publisher. The per-user installer starts the API now and enables its limited autostart at Windows sign-in. SessionDock will not elevate or change the integration setting.\n\nContinue?",
            "Install latest HandleScope release",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        SetActionStatus("Checking the latest stable HandleScope release...");
        try
        {
            var progress = new ImmediateProgress<HandleScopeReleaseInstallProgress>(
                UpdateInstallProgress);
            var installed = await _releaseInstaller.InstallLatestAsync(
                progress,
                _lifetimeCancellation.Token);
            if (_isClosed)
                return;

            var result = await _integrationService.TestConnectionAsync(
                _lifetimeCancellation.Token);
            _state = result.State;
            _canRepairConfiguration = result.CanRepairConfiguration;
            RenderState();
            SetActionStatus(_state switch
            {
                HandleScopeIntegrationState.Ready =>
                    $"HandleScope {installed.Version} was verified and installed. Its API is running, autostart is enabled, and the integration is ready.",
                HandleScopeIntegrationState.RunningDisabled =>
                    $"HandleScope {installed.Version} was verified and installed. Its API is running with autostart enabled; select Enable once to opt in.",
                _ =>
                    $"HandleScope {installed.Version} was verified and installed with autostart enabled. Use Start API if it is not yet running."
            });
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                SetActionStatus(
                    "HandleScope installation was cancelled before the installer started.");
            }
        }
        catch (HandleScopeInstallException exception)
        {
            Trace.WriteLine(
                $"HandleScope install failed safely: {exception.GetType().Name}.");
            if (!_isClosed)
                SetActionStatus(exception.Message, isError: true);
        }
        finally
        {
            _installCommitInProgress = false;
            if (!_isClosed)
                SetBusy(false);
        }
    }

    private void UpdateInstallProgress(HandleScopeReleaseInstallProgress progress)
    {
        if (_isClosed)
            return;
        var visibleStatus = progress.Stage switch
        {
            HandleScopeReleaseInstallStage.CheckingRelease =>
                "Checking the latest stable HandleScope release...",
            HandleScopeReleaseInstallStage.DownloadingPackage =>
                $"Downloading HandleScope {progress.Version}... {progress.Percentage ?? 0}%",
            HandleScopeReleaseInstallStage.VerifyingPackage =>
                $"Verifying HandleScope {progress.Version}...",
            HandleScopeReleaseInstallStage.InstallingPackage =>
                MarkInstallCommitStarted(progress.Version),
            _ => throw new InvalidOperationException(
                "Unexpected HandleScope installation stage.")
        };
        var accessibleStatus = progress.Stage ==
            HandleScopeReleaseInstallStage.DownloadingPackage
                ? $"Downloading HandleScope {progress.Version}."
                : visibleStatus;
        SetActionStatus(
            visibleStatus,
            accessibleAnnouncement: accessibleStatus);
    }

    private string MarkInstallCommitStarted(string? version)
    {
        _installCommitInProgress = true;
        return $"Installing HandleScope {version} for this Windows user...";
    }

    private async Task RunActionAsync(
        Func<CancellationToken, Task<HandleScopeIntegrationResult>> action,
        string completedMessage,
        bool repairEnablesIntegration)
    {
        if (_isBusy)
            return;

        SetBusy(true);
        SetActionStatus("Working...");
        try
        {
            var result = await action(_lifetimeCancellation.Token);
            _state = result.State;
            _canRepairConfiguration = result.CanRepairConfiguration;
            if (_canRepairConfiguration)
                _repairEnablesIntegration = repairEnablesIntegration;
            RenderState();
            var configurationError = _state ==
                HandleScopeIntegrationState.ConfigurationError;
            SetActionStatus(
                configurationError
                    ? _canRepairConfiguration
                        ? "The existing opt-in was preserved. Review the warning before choosing Repair."
                        : "The action was refused safely. Check the official installation, then refresh the status."
                    : completedMessage,
                isError: configurationError);
        }
        catch (OperationCanceledException)
        {
            SetActionStatus(string.Empty);
        }
        finally
        {
            if (!_isClosed)
                SetBusy(false);
        }
    }

    private void RenderState()
    {
        RepairWarningPanel.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Collapsed;

        switch (_state)
        {
            case HandleScopeIntegrationState.NotInstalled:
                SetStatePresentation(
                    "HandleScope is not installed",
                    "Select Install Latest HandleScope release below. SessionDock will verify the download before installing it for this Windows user.",
                    "NOT INSTALLED",
                    "MutedBrush",
                    "UtilitySurfaceBrush",
                    "IconUpdate");
                break;
            case HandleScopeIntegrationState.InstalledStopped:
                SetStatePresentation(
                    "Installed - connection not tested",
                    "HandleScope is installed locally. Start its API explicitly, enable the integration if needed, then test the connection.",
                    "NOT CONNECTED",
                    "VioletTextBrush",
                    "VioletSurfaceBrush",
                    "IconActivity");
                break;
            case HandleScopeIntegrationState.StartPending:
                SetStatePresentation(
                    "API start requested",
                    "SessionDock recently started the expected local API. Wait briefly, then test the connection.",
                    "STARTING",
                    "VioletTextBrush",
                    "VioletSurfaceBrush",
                    "IconActivity");
                break;
            case HandleScopeIntegrationState.RunningUntested:
                SetStatePresentation(
                    "API running - connection not tested",
                    "The expected local API process is already running. Test the loopback connection before relying on the integration.",
                    "RUNNING",
                    "VioletTextBrush",
                    "VioletSurfaceBrush",
                    "IconActivity");
                break;
            case HandleScopeIntegrationState.RunningDisabled:
                SetStatePresentation(
                    "API running - integration disabled",
                    "The API at the expected local path answered, but SessionDock's fixed Roblox policy is not enabled.",
                    "DISABLED",
                    "WarningTextBrush",
                    "WarningSurfaceBrush",
                    "IconLock");
                break;
            case HandleScopeIntegrationState.Ready:
                SetStatePresentation(
                    "Ready for SessionDock",
                    "The checked loopback API is running and the fixed Roblox policy is enabled.",
                    "READY",
                    "SuccessTextBrush",
                    "SuccessSurfaceBrush",
                    "IconCheck");
                break;
            case HandleScopeIntegrationState.UpdateRequired:
                SetStatePresentation(
                    "HandleScope update required",
                    "Select Install Latest HandleScope release to download, verify, and replace the installed API with the latest stable release.",
                    "UPDATE REQUIRED",
                    "WarningTextBrush",
                    "WarningSurfaceBrush",
                    "IconUpdate");
                break;
            case HandleScopeIntegrationState.ConfigurationError:
                if (_canRepairConfiguration)
                {
                    SetStatePresentation(
                        "Configuration was preserved",
                        "The local opt-in is invalid or does not match SessionDock's fixed policy, so the integration remains unavailable.",
                        "ACTION REQUIRED",
                        "ErrorTextBrush",
                        "ErrorSurfaceBrush",
                        "IconWarning");
                    RepairWarningPanel.Visibility = Visibility.Visible;
                    RepairButton.Visibility = Visibility.Visible;
                    RepairWarningText.Text = _repairEnablesIntegration
                        ? "SessionDock preserved the existing configuration and will not use it. Repair replaces only the SessionDock opt-in with the fixed, minimal enabled policy; it does not reinstall or stop HandleScope."
                        : "SessionDock preserved the existing configuration. Repair replaces only the SessionDock opt-in with the fixed, minimal disabled policy; it does not reinstall or stop HandleScope.";
                    RepairButtonLabel.Text = _repairEnablesIntegration
                        ? "Repair and enable"
                        : "Repair and disable";
                }
                else
                {
                    SetStatePresentation(
                        "Local safety check failed",
                        "SessionDock refused the local installation, start request, or health response. Use Install Latest HandleScope release to replace it safely, then refresh.",
                        "UNAVAILABLE",
                        "ErrorTextBrush",
                        "ErrorSurfaceBrush",
                        "IconError");
                }
                break;
            default:
                throw new InvalidOperationException("Unexpected integration state.");
        }

        UpdateActionAvailability();
    }

    private void SetStatePresentation(
        string title,
        string description,
        string badge,
        string foregroundResource,
        string surfaceResource,
        string iconResource)
    {
        var tone = StatusToneClassifier.Classify(badge);
        _stateLiveRegion.Update(
            title,
            $"{title} {description} {badge}",
            tone is StatusTone.Error or StatusTone.Warning
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        StateDescriptionText.Text = description;
        StateBadgeText.Text = badge;
        StateIcon.SetResourceReference(
            Shape.StrokeProperty,
            foregroundResource);
        StateBadgeText.SetResourceReference(
            TextBlock.ForegroundProperty,
            foregroundResource);
        StateIconShell.SetResourceReference(
            Border.BackgroundProperty,
            surfaceResource);
        StateBadge.SetResourceReference(
            Border.BackgroundProperty,
            surfaceResource);
        StateIcon.Data = (Geometry)FindResource(iconResource);
    }

    private void SetActionStatus(
        string text,
        bool isError = false,
        string? accessibleAnnouncement = null) =>
        _actionStatusLiveRegion.Update(
            text,
            accessibleAnnouncement,
            severity: isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        InstallLatestHandleScopeButton.IsEnabled = !_isBusy;
        RefreshButton.IsEnabled = !_isBusy;
        StartApiButton.IsEnabled = !_isBusy && _state ==
            HandleScopeIntegrationState.InstalledStopped;
        EnableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.StartPending or
            HandleScopeIntegrationState.RunningUntested or
            HandleScopeIntegrationState.RunningDisabled;
        DisableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.StartPending or
            HandleScopeIntegrationState.RunningUntested or
            HandleScopeIntegrationState.Ready or
            HandleScopeIntegrationState.UpdateRequired;
        TestConnectionButton.IsEnabled = !_isBusy && _state is not
            HandleScopeIntegrationState.NotInstalled and not
            HandleScopeIntegrationState.ConfigurationError;
        RepairButton.IsEnabled = !_isBusy && _state ==
            HandleScopeIntegrationState.ConfigurationError &&
            _canRepairConfiguration;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void HandleScopeIntegrationDialog_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_installCommitInProgress)
            return;
        e.Cancel = true;
        MessageBox.Show(
            this,
            "HandleScope is finishing its verified per-user file replacement. Keep this window open until installation completes.",
            "HandleScope installation in progress",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void HandleScopeIntegrationDialog_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCancellation.Cancel();
        _releaseInstaller.Dispose();
        _integrationService.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        internal ImmediateProgress(Action<T> report)
        {
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public void Report(T value) => _report(value);
    }
}
