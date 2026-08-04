using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class HandleScopeIntegrationDialog : Window
{
    private readonly HandleScopeRuntimeCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly AccessibilityLiveRegion _stateLiveRegion;
    private readonly AccessibilityLiveRegion _actionStatusLiveRegion;
    private readonly IReadOnlyList<RuntimeSourceChoice> _runtimeSourceChoices;
    private readonly IReadOnlyList<ApiContractChoice> _apiContractChoices;
    private IReadOnlyList<RuntimeVersionChoice> _runtimeVersionChoices = [];
    private HandleScopeRuntimeSnapshot? _snapshot;
    private bool _isBusy;
    private bool _isRendering;
    private bool _isClosed;

    internal HandleScopeIntegrationDialog(
        HandleScopeRuntimeCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();

        _stateLiveRegion = new AccessibilityLiveRegion(StateTitleText);
        _actionStatusLiveRegion = new AccessibilityLiveRegion(ActionStatusText);
        _runtimeSourceChoices =
        [
            new RuntimeSourceChoice(
                Localize("Handle.RuntimeSourceBundled"),
                HandleScopeRuntimeSource.Bundled),
            new RuntimeSourceChoice(
                Localize("Handle.RuntimeSourceStandalone"),
                HandleScopeRuntimeSource.Standalone)
        ];
        _apiContractChoices =
        [
            new ApiContractChoice(
                Localize("Handle.ApiVersionAutomatic"),
                HandleScopeApiContract.Automatic),
            new ApiContractChoice(
                Localize("Handle.ApiVersionV2"),
                HandleScopeApiContract.V2),
            new ApiContractChoice(
                Localize("Handle.ApiVersionV1"),
                HandleScopeApiContract.V1)
        ];
        RuntimeSourceComboBox.ItemsSource = _runtimeSourceChoices;
        ApiContractComboBox.ItemsSource = _apiContractChoices;

        WindowLayoutService.FitToWorkArea(this);
        Loaded += HandleScopeIntegrationDialog_Loaded;
        Closed += HandleScopeIntegrationDialog_Closed;
    }

    private async void HandleScopeIntegrationDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= HandleScopeIntegrationDialog_Loaded;
        WindowLayoutService.FitToWorkArea(this);
        await RunActionAsync(
            cancellationToken => _coordinator.InspectAsync(cancellationToken),
            completedMessageKey: null);
    }

    private async void IntegrationEnabledCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRendering)
            return;

        await RunActionAsync(
            cancellationToken => _coordinator.EnableAsync(cancellationToken),
            "Handle.ActionEnabled");
    }

    private async void IntegrationEnabledCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRendering)
            return;

        await RunActionAsync(
            cancellationToken => _coordinator.DisableAsync(cancellationToken),
            "Handle.ActionDisabled");
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _coordinator.RestartAsync(cancellationToken),
            "Handle.ActionRestarted");

    private async void RetryButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _coordinator.RestartAsync(cancellationToken),
            "Handle.ActionRetried");

    private async void RepairButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _coordinator.RepairAsync(cancellationToken),
            "Handle.ActionRepaired");

    private async void RuntimeSourceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRendering || _isBusy ||
            RuntimeSourceComboBox.SelectedItem is not RuntimeSourceChoice choice ||
            choice.Source == _snapshot?.Source)
        {
            return;
        }

        await RunActionAsync(
            cancellationToken => _coordinator.SetRuntimeSourceAsync(
                choice.Source,
                cancellationToken),
            "Handle.ActionSourceChanged");
    }

    private async void ApiContractComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRendering || _isBusy ||
            ApiContractComboBox.SelectedItem is not ApiContractChoice choice ||
            choice.ApiContract == _snapshot?.ApiContract)
        {
            return;
        }

        await RunActionAsync(
            cancellationToken => _coordinator.SetApiContractAsync(
                choice.ApiContract,
                cancellationToken),
            "Handle.ActionContractChanged");
    }

    private async void StandaloneRuntimeVersionComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRendering || _isBusy ||
            StandaloneRuntimeVersionComboBox.SelectedItem is not
                RuntimeVersionChoice choice ||
            choice.Mode == _snapshot?.RuntimeVersionMode &&
            choice.ExactVersion == _snapshot?.ExactRuntimeVersion)
        {
            return;
        }

        await RunActionAsync(
            cancellationToken => _coordinator.SetRuntimeVersionAsync(
                choice.Mode,
                choice.ExactVersion,
                cancellationToken),
            "Handle.ActionRuntimeVersionChanged");
    }

    private async void RefreshReviewedVersionsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _coordinator.RefreshReviewedVersionsAsync(
                cancellationToken),
            "Handle.ActionVersionsRefreshed");

    private async Task RunActionAsync(
        Func<CancellationToken, Task<HandleScopeRuntimeSnapshot>> action,
        string? completedMessageKey)
    {
        if (_isBusy || _isClosed)
            return;

        SetBusy(true);
        SetActionStatus(Localize("Handle.Working"));
        try
        {
            _snapshot = await action(_lifetimeCancellation.Token);
            if (_isClosed)
                return;

            RenderSnapshot();
            SetActionStatus(
                completedMessageKey is null
                    ? string.Empty
                    : Localize(completedMessageKey));
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
                SetActionStatus(string.Empty);
        }
        catch (Exception exception) when (IsExpectedRuntimeFailure(exception))
        {
            Trace.WriteLine(
                $"HandleScope action failed safely: {exception.GetType().Name}.");
            if (!_isClosed)
                SetActionStatus(Localize("Handle.ActionFailed"), isError: true);
        }
        finally
        {
            if (!_isClosed)
                SetBusy(false);
        }
    }

    private void RenderSnapshot()
    {
        if (_snapshot is null)
            return;

        _isRendering = true;
        try
        {
            IntegrationEnabledCheckBox.IsChecked = _snapshot.State is
                HandleScopeRuntimeState.Starting or
                HandleScopeRuntimeState.Ready or
                HandleScopeRuntimeState.NeedsAttention or
                HandleScopeRuntimeState.StandaloneUnavailable;
            RuntimeSourceComboBox.SelectedItem = _runtimeSourceChoices
                .First(choice => choice.Source == _snapshot.Source);
            ApiContractComboBox.SelectedItem = _apiContractChoices
                .First(choice => choice.ApiContract == _snapshot.ApiContract);
            PopulateRuntimeVersionChoices(_snapshot);
            BundledVersionText.Text = FormatVersion(_snapshot.ComponentVersion);
            StandaloneVersionText.Text = FormatVersion(_snapshot.StandaloneVersion);
            RuntimeSourceDescriptionText.Text = Localize(
                _snapshot.Source == HandleScopeRuntimeSource.Bundled
                    ? "Handle.RuntimeSourceBundledDetail"
                    : "Handle.RuntimeSourceStandaloneDetail");
            if (_snapshot.Source == HandleScopeRuntimeSource.Standalone)
                AdvancedOptionsExpander.IsExpanded = true;
        }
        finally
        {
            _isRendering = false;
        }

        AttentionPanel.Visibility = Visibility.Collapsed;
        switch (_snapshot.State)
        {
            case HandleScopeRuntimeState.Off:
                SetStatePresentation(
                    "Handle.StateOffTitle",
                    "Handle.StateOffDescription",
                    "Handle.StateOffBadge",
                    "SubtleBrush",
                    "UtilitySurfaceBrush",
                    "IconLock",
                    requiresAttention: false);
                break;
            case HandleScopeRuntimeState.Starting:
                SetStatePresentation(
                    "Handle.StateStartingTitle",
                    "Handle.StateStartingDescription",
                    "Handle.StateStartingBadge",
                    "VioletTextBrush",
                    "VioletSurfaceBrush",
                    "IconActivity",
                    requiresAttention: false);
                break;
            case HandleScopeRuntimeState.Ready:
                SetStatePresentation(
                    "Handle.StateReadyTitle",
                    _snapshot.Source == HandleScopeRuntimeSource.Bundled
                        ? "Handle.StateReadyBundledDescription"
                        : "Handle.StateReadyStandaloneDescription",
                    "Handle.StateReadyBadge",
                    "SuccessTextBrush",
                    "SuccessSurfaceBrush",
                    "IconCheck",
                    requiresAttention: false);
                break;
            case HandleScopeRuntimeState.NeedsAttention:
                SetFailurePresentation(
                    "Handle.StateNeedsAttentionTitle",
                    "Handle.StateNeedsAttentionDescription",
                    "Handle.StateNeedsAttentionBadge",
                    "Handle.AttentionRuntime");
                break;
            case HandleScopeRuntimeState.StandaloneUnavailable:
                SetFailurePresentation(
                    "Handle.StateStandaloneUnavailableTitle",
                    "Handle.StateStandaloneUnavailableDescription",
                    "Handle.StateStandaloneUnavailableBadge",
                    "Handle.AttentionStandalone");
                break;
            case HandleScopeRuntimeState.ConfigurationError:
                SetFailurePresentation(
                    "Handle.StateConfigurationTitle",
                    "Handle.StateConfigurationDescription",
                    "Handle.StateConfigurationBadge",
                    "Handle.AttentionConfiguration");
                break;
            default:
                throw new InvalidOperationException("Unexpected HandleScope runtime state.");
        }

        UpdateActionAvailability();
    }

    private void PopulateRuntimeVersionChoices(HandleScopeRuntimeSnapshot snapshot)
    {
        var choices = new List<RuntimeVersionChoice>
        {
            new(
                Localize("Handle.RuntimeVersionAutomatic"),
                HandleScopeVersionSelectionMode.Automatic,
                ExactVersion: null),
            new(
                Localize("Handle.RuntimeVersionKeepInstalled"),
                HandleScopeVersionSelectionMode.KeepInstalled,
                ExactVersion: null)
        };
        choices.AddRange(snapshot.CompatibleStandaloneVersions.Select(version =>
            new RuntimeVersionChoice(
                Localize("Handle.RuntimeVersionExact", version.ToString(3)),
                HandleScopeVersionSelectionMode.Exact,
                version)));

        if (snapshot.RuntimeVersionMode == HandleScopeVersionSelectionMode.Exact &&
            snapshot.ExactRuntimeVersion is { } savedVersion &&
            !snapshot.CompatibleStandaloneVersions.Contains(savedVersion))
        {
            choices.Add(new RuntimeVersionChoice(
                Localize(
                    "Handle.RuntimeVersionExactUnavailable",
                    savedVersion.ToString(3)),
                HandleScopeVersionSelectionMode.Exact,
                savedVersion));
        }

        _runtimeVersionChoices = choices;
        StandaloneRuntimeVersionComboBox.ItemsSource = _runtimeVersionChoices;
        StandaloneRuntimeVersionComboBox.SelectedItem = _runtimeVersionChoices
            .First(choice =>
                choice.Mode == snapshot.RuntimeVersionMode &&
                choice.ExactVersion == snapshot.ExactRuntimeVersion);
    }

    private void SetFailurePresentation(
        string titleKey,
        string descriptionKey,
        string badgeKey,
        string attentionDetailKey)
    {
        SetStatePresentation(
            titleKey,
            descriptionKey,
            badgeKey,
            "ErrorTextBrush",
            "ErrorSurfaceBrush",
            "IconWarning",
            requiresAttention: true);
        AttentionDetailText.Text = Localize(attentionDetailKey);
        AttentionPanel.Visibility = Visibility.Visible;
    }

    private void SetStatePresentation(
        string titleKey,
        string descriptionKey,
        string badgeKey,
        string foregroundResource,
        string surfaceResource,
        string iconResource,
        bool requiresAttention)
    {
        var title = Localize(titleKey);
        var description = Localize(descriptionKey);
        var badge = Localize(badgeKey);
        _stateLiveRegion.Update(
            title,
            $"{title} {description} {badge}",
            requiresAttention
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        StateDescriptionText.Text = description;
        StateBadgeText.Text = badge;
        StateIcon.SetResourceReference(Shape.StrokeProperty, foregroundResource);
        StateBadgeText.SetResourceReference(
            TextBlock.ForegroundProperty,
            foregroundResource);
        StateIconShell.SetResourceReference(Border.BackgroundProperty, surfaceResource);
        StateBadge.SetResourceReference(Border.BackgroundProperty, surfaceResource);
        StateIcon.Data = (Geometry)FindResource(iconResource);
    }

    private string FormatVersion(object? version)
    {
        var text = Convert.ToString(version, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? Localize("Handle.VersionUnknown")
            : text;
    }

    private void SetActionStatus(string text, bool isError = false) =>
        _actionStatusLiveRegion.Update(
            text,
            severity: isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        var state = _snapshot?.State;
        var hasSnapshot = _snapshot is not null;
        IntegrationEnabledCheckBox.IsEnabled = !_isBusy && hasSnapshot;
        RuntimeSourceComboBox.IsEnabled = !_isBusy && hasSnapshot;
        ApiContractComboBox.IsEnabled = !_isBusy && hasSnapshot;
        StandaloneRuntimeVersionComboBox.IsEnabled = !_isBusy && hasSnapshot &&
            _snapshot?.Source == HandleScopeRuntimeSource.Standalone;
        RefreshReviewedVersionsButton.Visibility = _snapshot?.Source ==
                HandleScopeRuntimeSource.Standalone
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshReviewedVersionsButton.IsEnabled = !_isBusy && hasSnapshot;

        RestartButton.Visibility = _snapshot?.Source ==
                HandleScopeRuntimeSource.Bundled &&
            state is (HandleScopeRuntimeState.Starting or
                HandleScopeRuntimeState.Ready)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RetryButton.Visibility = state is HandleScopeRuntimeState.NeedsAttention or
            HandleScopeRuntimeState.StandaloneUnavailable
                ? Visibility.Visible
                : Visibility.Collapsed;
        RepairButton.Visibility = state == HandleScopeRuntimeState.ConfigurationError &&
            _snapshot?.CanRepairConfiguration == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        RestartButton.IsEnabled = !_isBusy;
        RetryButton.IsEnabled = !_isBusy;
        RepairButton.IsEnabled = !_isBusy;
    }

    private static bool IsExpectedRuntimeFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or
            NotSupportedException or Win32Exception or HttpRequestException or
            TimeoutException or SecurityException or HandleScopeCatalogException;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void HandleScopeIntegrationDialog_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? Localization.GetString(key)
            : Localization.Format(key, arguments);

    private sealed record RuntimeSourceChoice(
        string DisplayName,
        HandleScopeRuntimeSource Source) : IDropdownLabel;

    private sealed record ApiContractChoice(
        string DisplayName,
        HandleScopeApiContract ApiContract) : IDropdownLabel;

    private sealed record RuntimeVersionChoice(
        string DisplayName,
        HandleScopeVersionSelectionMode Mode,
        Version? ExactVersion) : IDropdownLabel;
}
