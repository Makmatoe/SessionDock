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
            Localize("Main.DiagnosticsGatheringTitle"),
            Localize("Main.DiagnosticsGatheringDetail"),
            Localize("Main.DiagnosticsBadge"),
            StatusTone.Neutral);
        SetOperationBusy(true);
        try
        {
            var snapshot = await Task.Run(
                () => SupportDiagnosticsService.Capture(
                    context,
                    _robloxClient.FindPlayerPath),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            document = SupportDiagnosticsService.BuildDocument(
                snapshot,
                Localization);
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
            Localize("Main.DiagnosticsReadyTitle"),
            Localize("Main.DiagnosticsReadyDetail"),
            Localize("Main.LocalOnlyBadge"),
            StatusTone.Success);
    }
}
