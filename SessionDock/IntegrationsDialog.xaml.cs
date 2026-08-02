using System.Windows;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class IntegrationsDialog : Window
{
    private readonly HandleScopeRuntimeCoordinator _handleScopeRuntimeCoordinator;

    internal IntegrationsDialog(
        HandleScopeRuntimeCoordinator handleScopeRuntimeCoordinator)
    {
        _handleScopeRuntimeCoordinator = handleScopeRuntimeCoordinator ??
            throw new ArgumentNullException(nameof(handleScopeRuntimeCoordinator));
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
        var dialog = new HandleScopeIntegrationDialog(
            _handleScopeRuntimeCoordinator)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
