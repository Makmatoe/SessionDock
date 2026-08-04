using System.IO;
using System.Windows;
using SessionDock.Models;
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
        if (!await FlushDestinationPersistenceAsync())
            return;
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        var portableDialog = new PortableDataDialog(
            _settings,
            catalog,
            _exactWheelMacroStore.ReadExactBytes)
        {
            Owner = this
        };
        var portableAccepted = portableDialog.ShowDialog() == true;
        if (portableDialog.OpenLegacyTransferRequested)
        {
            await ShowLegacyMetadataTransferAsync(cancellationToken);
            return;
        }
        if (!portableAccepted ||
            portableDialog.ImportPlan is not { } portablePlan)
        {
            SetStatus(
                Localize("Main.MetadataTransferClosedTitle"),
                Localize("Main.MetadataTransferClosedDetail"),
                Localize("Main.LocalOnlyBadge"),
                StatusTone.Neutral);
            return;
        }

        await ApplyPortableImportAsync(portablePlan, cancellationToken);
    }

    private async Task ShowLegacyMetadataTransferAsync(
        CancellationToken cancellationToken)
    {
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

    private async Task ApplyPortableImportAsync(
        PortableImportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var currentCatalog = TryLoadSessionTemplateCatalog();
        if (currentCatalog is null)
            return;

        var priorCatalog = SessionTemplatePolicy.Normalize(currentCatalog);
        var prepared = plan.Apply();
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        var newlyCreatedMacros = new List<MacroDefinition>();
        var catalogWasWritten = false;
        var settingsWereCommitted = false;
        SetOperationBusy(true);
        try
        {
            foreach (var blob in prepared.MacroBlobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definitions = prepared.Catalog.MacroDefinitions.Where(
                        definition => definition.ContentId.Equals(
                            blob.ContentId,
                            StringComparison.OrdinalIgnoreCase) &&
                            definition.Kind == blob.Kind &&
                            definition.Sha256.Equals(
                                blob.Sha256,
                                StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (definitions.Length != 1)
                {
                    throw new InvalidDataException(
                        "A verified portable macro no longer has one catalog definition.");
                }
                if (_exactWheelMacroStore.SaveExactBytes(
                        definitions[0],
                        blob.Bytes))
                {
                    newlyCreatedMacros.Add(definitions[0]);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySaveSessionTemplateCatalog(prepared.Catalog))
                return;
            catalogWasWritten = true;

            var settingsCommitted = await TryCommitSettingsMutationAsync(
                () => ApplyPortableSettings(prepared.Settings),
                Localize("Portable.ImportFailed"),
                Localize("Main.MetadataImportRolledBackBadge"),
                Localize("Main.SettingsRollbackDetail"));
            if (!settingsCommitted)
            {
                if (!TrySaveSessionTemplateCatalog(priorCatalog))
                {
                    SetStatus(
                        Localize("Portable.ImportFailed"),
                        Localize("Main.LocalOperationFailureDetail"),
                        Localize("Main.LocalDataErrorBadge"),
                        StatusTone.Error);
                }
                else
                {
                    catalogWasWritten = false;
                }
                return;
            }
            settingsWereCommitted = true;
            RefreshPortableImportUiBestEffort();

            SetStatus(
                Localize("Portable.Imported"),
                string.Join(
                    " ",
                    Localize(
                        "Portable.ResultTemplates",
                        plan.ImportedTemplateCount),
                    Localize(
                        "Portable.ResultMacros",
                        plan.ImportedMacroCount,
                        plan.DeduplicatedMacroCount),
                    Localize(
                        "Portable.ResultDestinations",
                        plan.ImportedNamedDestinationCount),
                    Localize(
                        "Portable.ResultPresets",
                        plan.ImportedBatchPresetCount),
                    Localize(
                        "Portable.ResultWholeLayoutUnassigned",
                        plan.UnassignedWholeLayoutMacroCount)),
                Localize("Main.SettingsSavedBadge"),
                plan.UnassignedWholeLayoutMacroCount > 0
                    ? StatusTone.Warning
                    : StatusTone.Success);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception) ||
            exception is InvalidDataException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Portable import failed safely: {exception.GetType().Name}.");
            var catalogRestored = !catalogWasWritten ||
                settingsWereCommitted ||
                TrySaveSessionTemplateCatalog(priorCatalog);
            if (catalogRestored && !settingsWereCommitted)
                catalogWasWritten = false;
            SetStatus(
                Localize("Portable.ImportFailed"),
                catalogRestored
                    ? Localize("Main.SettingsRollbackDetail")
                    : Localize("Main.LocalOperationFailureDetail"),
                Localize("Main.LocalDataErrorBadge"),
                StatusTone.Error);
        }
        finally
        {
            if (!settingsWereCommitted && !catalogWasWritten)
            {
                CleanupPortableImportMacros(
                    newlyCreatedMacros,
                    priorCatalog);
            }
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private void RefreshPortableImportUiBestEffort()
    {
        try
        {
            // Portable import can replace the active account's destination.
            // Rebind the Advanced editor's draft/persisted cache before any
            // later edit can write its pre-import value back to settings.
            ShowDestinationForProfile(_activeProfile);
            RenderAccountList();
            RenderRecentExperiences();
            RefreshDestinationsWorkspace();
            RefreshAccountsWorkspace();
            RefreshLaunchAvailability();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or
                InvalidDataException)
        {
            // Persistence is already complete. A presentation refresh must not
            // turn a successful import into a split settings/catalog rollback.
            System.Diagnostics.Trace.WriteLine(
                $"Portable import UI refresh failed: {exception.GetType().Name}.");
        }
    }

    private void CleanupPortableImportMacros(
        IEnumerable<MacroDefinition> created,
        SessionTemplateCatalog priorCatalog)
    {
        var priorFiles = priorCatalog.MacroDefinitions
            .Select(definition => definition.SafeFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in created
                     .GroupBy(
                         item => item.SafeFileName,
                         StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            if (priorFiles.Contains(definition.SafeFileName))
                continue;
            if (!_exactWheelMacroStore!.TryDeleteExactBytes(definition))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Portable macro rollback cleanup failed: {definition.SafeFileName}.");
            }
        }
    }

    private void ApplyPortableSettings(AppSettings imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var importedAccounts = imported.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var account in _settings.Accounts)
        {
            if (!importedAccounts.TryGetValue(account.Key, out var source))
            {
                throw new InvalidDataException(
                    "The account set changed after the portable import review.");
            }
            account.Destination = source.Destination;
        }

        _settings.NamedDestinations = imported.NamedDestinations
            .Select(NamedDestinationPolicy.Clone)
            .ToList();
        _settings.BatchLaunchPresets = imported.BatchLaunchPresets
            .Select(AppSettingsSnapshot.Clone)
            .ToList();
    }
}
