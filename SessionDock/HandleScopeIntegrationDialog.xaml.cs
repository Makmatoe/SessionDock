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
    internal const string OfficialSetupUrl =
        "https://github.com/Makmatoe/HandleScope/blob/v0.1.4/docs/INSTALL.md";

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
            Localize("Handle.ActionInspected"),
            repairEnablesIntegration: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.InspectAsync(cancellationToken),
            Localize("Handle.ActionRefreshed"),
            repairEnablesIntegration: true);

    private async void EnableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.EnableAsync(
                repairExisting: false,
                cancellationToken),
            Localize("Handle.ActionEnabled"),
            repairEnablesIntegration: true);

    private async void DisableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: false,
                cancellationToken),
            Localize("Handle.ActionDisabled"),
            repairEnablesIntegration: false);

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken =>
                _integrationService.TestConnectionAsync(cancellationToken),
            Localize("Handle.ActionConnectionTested"),
            repairEnablesIntegration: true);

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repairEnablesIntegration)
        {
            await RunActionAsync(
                cancellationToken => _integrationService.EnableAsync(
                    repairExisting: true,
                    cancellationToken),
                Localize("Handle.ActionRepairEnabled"),
                repairEnablesIntegration: true);
            return;
        }

        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: true,
                cancellationToken),
            Localize("Handle.ActionRepairDisabled"),
            repairEnablesIntegration: false);
    }

    private async void InstallHandleScopeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var confirmation = MessageBox.Show(
            this,
            Localize("Handle.InstallConfirm"),
            Localize("Handle.InstallConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        SetActionStatus(Localize("Handle.ProgressPreparing"));
        try
        {
            var progress = new ImmediateProgress<HandleScopeReleaseInstallProgress>(
                UpdateInstallProgress);
            var installed = await _releaseInstaller.InstallPinnedAsync(
                progress,
                _lifetimeCancellation.Token);
            if (_isClosed)
                return;

            var result = await _integrationService.InspectAsync(
                _lifetimeCancellation.Token);
            _state = result.State;
            _canRepairConfiguration = result.CanRepairConfiguration;
            RenderState();
            SetActionStatus(Localize(
                "Handle.InstallSucceeded",
                installed.Version));
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
                SetActionStatus(Localize("Handle.InstallCanceled"));
        }
        catch (ObjectDisposedException) when (
            _lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the dialog cancels and disposes an in-flight download.
        }
        catch (HandleScopeInstallException exception)
        {
            Trace.WriteLine(
                $"HandleScope installation failed safely: {exception.Message}");
            SetActionStatus(
                Localize(
                    "Handle.InstallFailed",
                    LocalizeInstallFailureReason(exception.FailureKind),
                    OfficialSetupUrl),
                isError: true);
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
                Localize("Handle.ProgressPreparing"),
            HandleScopeReleaseInstallStage.DownloadingPackage =>
                Localize(
                    "Handle.ProgressDownloading",
                    progress.Version,
                    progress.Percentage ?? 0),
            HandleScopeReleaseInstallStage.VerifyingPackage =>
                Localize("Handle.ProgressVerifying", progress.Version),
            HandleScopeReleaseInstallStage.InstallingPackage =>
                MarkInstallCommitStarted(progress.Version),
            _ => throw new InvalidOperationException(
                "Unexpected HandleScope installation stage.")
        };
        var accessibleStatus = progress.Stage ==
            HandleScopeReleaseInstallStage.DownloadingPackage
                ? Localize(
                    "Handle.ProgressDownloadingAccessible",
                    progress.Version)
                : visibleStatus;
        SetActionStatus(
            visibleStatus,
            accessibleAnnouncement: accessibleStatus);
    }

    private string MarkInstallCommitStarted(string? version)
    {
        _installCommitInProgress = true;
        return Localize("Handle.ProgressInstalling", version);
    }

    private void OpenHandleScopeSetupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Process.Start(CreateOfficialSetupStartInfo());
            SetActionStatus(Localize("Handle.SetupGuideOpened"));
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or
                NotSupportedException)
        {
            Trace.WriteLine(
                $"HandleScope setup guide could not be opened: {exception.GetType().Name}.");
            SetActionStatus(
                Localize("Handle.SetupGuideFailed", OfficialSetupUrl),
                isError: true);
        }
    }

    internal static ProcessStartInfo CreateOfficialSetupStartInfo() => new()
    {
        FileName = OfficialSetupUrl,
        UseShellExecute = true
    };

    private async Task RunActionAsync(
        Func<CancellationToken, Task<HandleScopeIntegrationResult>> action,
        string completedMessage,
        bool repairEnablesIntegration)
    {
        if (_isBusy)
            return;

        SetBusy(true);
        SetActionStatus(Localize("Handle.Working"));
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
                        ? Localize("Handle.ConfigurationPreservedAction")
                        : Localize("Handle.ActionRefused")
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
                    Localize("Handle.StateNotInstalledTitle"),
                    Localize("Handle.StateNotInstalledDescription"),
                    Localize("Handle.StateNotInstalledBadge"),
                    "MutedBrush",
                    "UtilitySurfaceBrush",
                    "IconUpdate",
                    requiresAttention: false);
                break;
            case HandleScopeIntegrationState.InstalledStopped:
                SetStatePresentation(
                    Localize("Handle.StateStoppedTitle"),
                    Localize("Handle.StateStoppedDescription"),
                    Localize("Handle.StateStoppedBadge"),
                    "VioletTextBrush",
                    "VioletSurfaceBrush",
                    "IconActivity",
                    requiresAttention: false);
                break;
            case HandleScopeIntegrationState.RunningDisabled:
                SetStatePresentation(
                    Localize("Handle.StateDisabledTitle"),
                    Localize("Handle.StateDisabledDescription"),
                    Localize("Handle.StateDisabledBadge"),
                    "WarningTextBrush",
                    "WarningSurfaceBrush",
                    "IconLock",
                    requiresAttention: true);
                break;
            case HandleScopeIntegrationState.Ready:
                SetStatePresentation(
                    Localize("Handle.StateReadyTitle"),
                    Localize("Handle.StateReadyDescription"),
                    Localize("Handle.StateReadyBadge"),
                    "SuccessTextBrush",
                    "SuccessSurfaceBrush",
                    "IconCheck",
                    requiresAttention: false);
                break;
            case HandleScopeIntegrationState.UpdateRequired:
                SetStatePresentation(
                    Localize("Handle.StateUpdateTitle"),
                    Localize("Handle.StateUpdateDescription"),
                    Localize("Handle.StateUpdateBadge"),
                    "WarningTextBrush",
                    "WarningSurfaceBrush",
                    "IconUpdate",
                    requiresAttention: true);
                break;
            case HandleScopeIntegrationState.ConfigurationError:
                if (_canRepairConfiguration)
                {
                    SetStatePresentation(
                        Localize("Handle.StateConfigurationTitle"),
                        Localize("Handle.StateConfigurationDescription"),
                        Localize("Handle.StateConfigurationBadge"),
                        "ErrorTextBrush",
                        "ErrorSurfaceBrush",
                        "IconWarning",
                        requiresAttention: true);
                    RepairWarningPanel.Visibility = Visibility.Visible;
                    RepairButton.Visibility = Visibility.Visible;
                    RepairWarningText.Text = _repairEnablesIntegration
                        ? Localize("Handle.RepairWarningEnabled")
                        : Localize("Handle.RepairWarningDisabled");
                    RepairButtonLabel.Text = _repairEnablesIntegration
                        ? Localize("Handle.Repair")
                        : Localize("Handle.RepairDisable");
                }
                else
                {
                    SetStatePresentation(
                        Localize("Handle.StateUnavailableTitle"),
                        Localize("Handle.StateUnavailableDescription"),
                        Localize("Handle.StateUnavailableBadge"),
                        "ErrorTextBrush",
                        "ErrorSurfaceBrush",
                        "IconError",
                        requiresAttention: true);
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
        string iconResource,
        bool requiresAttention)
    {
        _stateLiveRegion.Update(
            title,
            $"{title} {description} {badge}",
            requiresAttention
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
        InstallHandleScopeButton.IsEnabled = !_isBusy;
        OpenHandleScopeSetupButton.IsEnabled = !_isBusy;
        RefreshButton.IsEnabled = !_isBusy;
        EnableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.RunningDisabled;
        DisableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.Ready or
            HandleScopeIntegrationState.UpdateRequired;
        TestConnectionButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.Ready or
            HandleScopeIntegrationState.UpdateRequired;
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
            Localize("Handle.InstallCommitInProgress"),
            Localize("Handle.InstallCommitInProgressTitle"),
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

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private string LocalizeInstallFailureReason(
        HandleScopeInstallFailureKind failureKind) => Localize(
        failureKind switch
        {
            HandleScopeInstallFailureKind.ReleaseDownload =>
                "Handle.InstallFailureDownload",
            HandleScopeInstallFailureKind.ReleaseIntegrity =>
                "Handle.InstallFailureIntegrity",
            HandleScopeInstallFailureKind.LocalEnvironment =>
                "Handle.InstallFailureEnvironment",
            HandleScopeInstallFailureKind.Installer =>
                "Handle.InstallFailureInstaller",
            _ => throw new InvalidOperationException(
                "Unexpected HandleScope install failure kind.")
        });

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
