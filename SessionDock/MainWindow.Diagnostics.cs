using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private async void AboutDiagnosticsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(ShowAboutDiagnosticsAsync);

    private async Task ShowAboutDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var app = (App)Application.Current;
        var theme = app.ThemeService.IsHighContrastActive
            ? DiagnosticTheme.WindowsHighContrast
            : _settings.UseLightTheme
                ? DiagnosticTheme.Light
                : DiagnosticTheme.Dark;
        var context = new SupportDiagnosticsContext(
            typeof(MainWindow).Assembly.GetName().Version,
            _updateService.CanSelfUpdate,
            _settings.Accounts.Count,
            _settings.RecentExperiences.Count(item => !item.IsPinned),
            _settings.RecentExperiences.Count(item => item.IsPinned),
            _runningClients.Count,
            theme,
            _settings.UiSoundsEnabled);

        SupportDiagnosticsDocument document;
        SetStatus(
            "Gathering privacy-safe diagnostics",
            "Checking installed components without collecting local paths or account details...",
            "DIAGNOSTICS");
        SetOperationBusy(true);
        try
        {
            var snapshot = await Task.Run(
                () => SupportDiagnosticsService.Capture(
                    context,
                    _robloxClient.FindPlayerPath),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            document = SupportDiagnosticsService.BuildDocument(snapshot);
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new AboutDiagnosticsDialog(
            document,
            context.SessionDockVersion)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
        SetStatus(
            "Diagnostics ready",
            "The privacy-safe summary stayed local unless you chose Copy or Export.",
            "LOCAL ONLY");
    }
}
