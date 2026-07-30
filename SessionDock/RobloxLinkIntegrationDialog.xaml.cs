using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class RobloxLinkIntegrationDialog : Window
{
    private readonly RobloxLinkRegistrationService _registration = new();
    private readonly AccessibilityLiveRegion _registrationStatusLiveRegion;
    private readonly AccessibilityLiveRegion _actionStatusLiveRegion;

    public RobloxLinkIntegrationDialog()
    {
        InitializeComponent();
        _registrationStatusLiveRegion =
            new AccessibilityLiveRegion(StateTitleText);
        _actionStatusLiveRegion =
            new AccessibilityLiveRegion(ActionStatusText);
        WindowLayoutService.FitToWorkArea(this);
        Loaded += (_, _) => RefreshStatus();
    }

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            Localize("LinkIntegration.EnableConfirm"),
            Localize("LinkIntegration.EnableConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var status = _registration.Enable(
            Localize("LinkIntegration.RegistryProgIdDescription"),
            Localize("LinkIntegration.RegistryProtocolDescription"));
        RenderStatus(status);
        var succeeded = status.State == RobloxLinkRegistrationState.Enabled;
        SetActionStatus(
            succeeded
                ? Localize("LinkIntegration.EnabledAction")
                : LocalizeDescription(status.State),
            isError: !succeeded);
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            Localize("LinkIntegration.DisableConfirm"),
            Localize("LinkIntegration.DisableConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var status = _registration.Disable(
            Localize("LinkIntegration.RegistryProgIdDescription"),
            Localize("LinkIntegration.RegistryProtocolDescription"));
        RenderStatus(status);
        var succeeded = status.State == RobloxLinkRegistrationState.Disabled;
        SetActionStatus(
            succeeded
                ? Localize("LinkIntegration.DisabledAction")
                : LocalizeDescription(status.State),
            isError: !succeeded);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        SetActionStatus(Localize("LinkIntegration.RefreshedAction"));
    }

    private void RefreshStatus() => RenderStatus(_registration.Inspect(
        Localize("LinkIntegration.RegistryProgIdDescription"),
        Localize("LinkIntegration.RegistryProtocolDescription")));

    private void RenderStatus(RobloxLinkRegistrationStatus status)
    {
        var title = status.State switch
        {
            RobloxLinkRegistrationState.Enabled =>
                Localize("LinkIntegration.StateEnabled"),
            RobloxLinkRegistrationState.Disabled =>
                Localize("LinkIntegration.StateDisabled"),
            RobloxLinkRegistrationState.UpdateRequired =>
                Localize("LinkIntegration.StateRepair"),
            RobloxLinkRegistrationState.Conflict =>
                Localize("LinkIntegration.StateConflict"),
            RobloxLinkRegistrationState.Unavailable =>
                Localize("LinkIntegration.StateUnavailable"),
            _ => throw new InvalidOperationException(
                "Unexpected link-handler registration state.")
        };
        var description = LocalizeDescription(status.State);
        _registrationStatusLiveRegion.Update(
            title,
            $"{title} {description}",
            status.State is RobloxLinkRegistrationState.UpdateRequired or
                RobloxLinkRegistrationState.Conflict or
                RobloxLinkRegistrationState.Unavailable
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        StateDescriptionText.Text = description;
        EnableButton.IsEnabled = status.State is
            RobloxLinkRegistrationState.Disabled or
            RobloxLinkRegistrationState.UpdateRequired;
        EnableButton.Content = status.State ==
            RobloxLinkRegistrationState.UpdateRequired
                ? Localize("LinkIntegration.Repair")
                : Localize("LinkIntegration.Enable");
        DisableButton.IsEnabled = status.State is
            RobloxLinkRegistrationState.Enabled or
            RobloxLinkRegistrationState.UpdateRequired;
    }

    private void SetActionStatus(string text, bool isError = false) =>
        _actionStatusLiveRegion.Update(
            text,
            severity: isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string LocalizeDescription(RobloxLinkRegistrationState state) =>
        state switch
        {
            RobloxLinkRegistrationState.Enabled =>
                Localize("LinkIntegration.DescriptionEnabled"),
            RobloxLinkRegistrationState.Disabled =>
                Localize("LinkIntegration.DescriptionDisabled"),
            RobloxLinkRegistrationState.UpdateRequired =>
                Localize("LinkIntegration.DescriptionRepair"),
            RobloxLinkRegistrationState.Conflict =>
                Localize("LinkIntegration.DescriptionConflict"),
            RobloxLinkRegistrationState.Unavailable =>
                Localize("LinkIntegration.DescriptionUnavailable"),
            _ => throw new InvalidOperationException(
                "Unexpected link-handler registration state.")
        };
}
