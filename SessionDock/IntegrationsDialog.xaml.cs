using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class IntegrationsDialog : Window
{
    public IntegrationsDialog()
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
    }

    private void ManageRobloxLinksButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new RobloxLinkIntegrationDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void ManageHandleScopeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new HandleScopeIntegrationDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
