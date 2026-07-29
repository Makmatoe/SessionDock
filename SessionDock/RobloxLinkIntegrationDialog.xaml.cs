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
            "Enable Open with SessionDock for this Windows user?\n\nWindows will receive a small per-user registration under HKCU\\Software\\Classes. Roblox's default handler will not be replaced. Every accepted link still requires an account choice and a separate launch confirmation.",
            "Enable Open with SessionDock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var status = _registration.Enable();
        RenderStatus(status);
        ActionStatusText.Text = status.State == RobloxLinkRegistrationState.Enabled
            ? "Open with SessionDock was enabled for this Windows user."
            : status.Description;
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            "Disable Open with SessionDock and remove only SessionDock's owned per-user registration?",
            "Disable Open with SessionDock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var status = _registration.Disable();
        RenderStatus(status);
        ActionStatusText.Text = status.State == RobloxLinkRegistrationState.Disabled
            ? "SessionDock's owned link-handler registration was removed."
            : status.Description;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        ActionStatusText.Text = "Windows registration status refreshed locally.";
    }

    private void RefreshStatus() => RenderStatus(_registration.Inspect());

    private void RenderStatus(RobloxLinkRegistrationStatus status)
    {
        StateTitleText.Text = status.State switch
        {
            RobloxLinkRegistrationState.Enabled => "Enabled for this user",
            RobloxLinkRegistrationState.Disabled => "Disabled",
            RobloxLinkRegistrationState.UpdateRequired =>
                "Owned registration needs repair",
            RobloxLinkRegistrationState.Conflict =>
                "Foreign registration preserved",
            RobloxLinkRegistrationState.Unavailable =>
                "Windows registration unavailable",
            _ => throw new InvalidOperationException(
                "Unexpected link-handler registration state.")
        };
        StateDescriptionText.Text = status.Description;
        EnableButton.IsEnabled = status.State is
            RobloxLinkRegistrationState.Disabled or
            RobloxLinkRegistrationState.UpdateRequired;
        EnableButton.Content = status.State ==
            RobloxLinkRegistrationState.UpdateRequired
                ? "Repair owned registration"
                : "Enable for this user";
        DisableButton.IsEnabled = status.State is
            RobloxLinkRegistrationState.Enabled or
            RobloxLinkRegistrationState.UpdateRequired;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
