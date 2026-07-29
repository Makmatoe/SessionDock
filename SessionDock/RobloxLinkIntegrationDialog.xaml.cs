using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class RobloxLinkIntegrationDialog : Window
{
    private readonly RobloxLinkRegistrationService _registration = new();

    public RobloxLinkIntegrationDialog()
    {
        InitializeComponent();
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

        var status = _registration.Enable();
        RenderStatus(status);
        ActionStatusText.Text = status.State == RobloxLinkRegistrationState.Enabled
            ? Localize("LinkIntegration.EnabledAction")
            : LocalizeDescription(status.State);
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

        var status = _registration.Disable();
        RenderStatus(status);
        ActionStatusText.Text = status.State == RobloxLinkRegistrationState.Disabled
            ? Localize("LinkIntegration.DisabledAction")
            : LocalizeDescription(status.State);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        ActionStatusText.Text = Localize("LinkIntegration.RefreshedAction");
    }

    private void RefreshStatus() => RenderStatus(_registration.Inspect());

    private void RenderStatus(RobloxLinkRegistrationStatus status)
    {
        StateTitleText.Text = status.State switch
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
        StateDescriptionText.Text = LocalizeDescription(status.State);
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
