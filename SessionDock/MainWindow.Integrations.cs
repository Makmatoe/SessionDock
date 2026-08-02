using System.Windows;

namespace SessionDock;

public partial class MainWindow
{
    private void IntegrationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        var dialog = new IntegrationsDialog(_handleScopeRuntimeCoordinator)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}
