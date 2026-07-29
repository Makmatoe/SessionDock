using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private async void MetadataTransferButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(ShowMetadataTransferAsync);

    private async Task ShowMetadataTransferAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var exportPackage = MetadataTransferService.CreateExport(_settings);
        var dialog = new MetadataTransferDialog(exportPackage, _settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.ImportPlan is not { } plan)
        {
            SetStatus(
                "Safe metadata transfer closed",
                "Nothing was imported. A file was exported only if you selected Export reviewed JSON and chose a destination.",
                "LOCAL ONLY");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetOperationBusy(true);
        try
        {
            if (!await TryCommitSettingsMutationAsync(
                    () => plan.Apply(_settings),
                    "Metadata could not be imported",
                    "IMPORT ROLLED BACK",
                    "SessionDock could not confirm the settings update, so every imported change was rolled back. Check that %LOCALAPPDATA%\\SessionDock is writable, then retry.",
                    onCommitted: () =>
                    {
                        RenderAccountList();
                        RenderRecentExperiences();
                    }))
            {
                return;
            }

            SetStatus(
                "Safe metadata imported",
                $"Updated {plan.AccountUpdateCount} account appearance entr{(plan.AccountUpdateCount == 1 ? "y" : "ies")}{(plan.OrderWillChange ? " and the matched account order" : string.Empty)}, added {plan.FavoritesToAdd} public favorite{(plan.FavoritesToAdd == 1 ? string.Empty : "s")}, and updated {plan.FavoritesToUpdate}. Sign-ins and private destinations were untouched.",
                "IMPORTED");
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }
}
