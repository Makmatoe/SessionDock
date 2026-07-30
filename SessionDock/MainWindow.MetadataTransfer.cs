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
                Localize("Main.MetadataTransferClosedTitle"),
                Localize("Main.MetadataTransferClosedDetail"),
                Localize("Main.LocalOnlyBadge"),
                StatusTone.Neutral);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetOperationBusy(true);
        try
        {
            if (!await TryCommitSettingsMutationAsync(
                    () => plan.Apply(_settings),
                    Localize("Main.MetadataImportFailureTitle"),
                    Localize("Main.MetadataImportRolledBackBadge"),
                    Localize("Main.MetadataImportFailureDetail"),
                    onCommitted: () =>
                    {
                        RenderAccountList();
                        RenderRecentExperiences();
                    }))
            {
                return;
            }

            var accountSummary = plan.AccountUpdateCount == 1
                ? Localize("Main.MetadataImportedAccountOne")
                : Localize(
                    "Main.MetadataImportedAccountMany",
                    plan.AccountUpdateCount);
            var orderSummary = plan.OrderWillChange
                ? Localize("Main.MetadataImportedOrder")
                : string.Empty;
            var addedSummary = plan.FavoritesToAdd == 1
                ? Localize("Main.MetadataImportedFavoriteAddOne")
                : Localize(
                    "Main.MetadataImportedFavoriteAddMany",
                    plan.FavoritesToAdd);
            var updatedSummary = plan.FavoritesToUpdate == 1
                ? Localize("Main.MetadataImportedFavoriteUpdateOne")
                : Localize(
                    "Main.MetadataImportedFavoriteUpdateMany",
                    plan.FavoritesToUpdate);
            SetStatus(
                Localize("Main.MetadataImportedTitle"),
                Localize(
                    "Main.MetadataImportedDetail",
                    accountSummary,
                    orderSummary,
                    addedSummary,
                    updatedSummary),
                Localize("Main.MetadataImportedBadge"),
                StatusTone.Success);
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }
}
