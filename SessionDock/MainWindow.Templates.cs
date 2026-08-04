using System.Diagnostics;
using System.IO;
using System.Windows;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private readonly SessionTemplateStore _sessionTemplateStore = new();
    private readonly RobloxWindowService _robloxWindowService = new();
    private RobloxSessionLayoutCoordinator? _robloxSessionLayoutCoordinator;
    private ExactWheelMacroStore? _exactWheelMacroStore;
    private SessionTemplateCatalog? _sessionTemplateCatalog;
    private bool _sessionTemplateCatalogNeedsRepair;

    private async void RunTemplateButtonClick(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(RunTemplateButtonClickAsync);
    }

    private async void SessionAutomationSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(
            SessionAutomationSettingsButtonClickAsync);
    }

    private async void WindowLayoutSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(cancellationToken =>
            SessionAutomationSettingsButtonClickAsync(
                SessionAutomationSettingsRoute.WindowLayout,
                cancellationToken));
    }

    private async void MacroLibrarySettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(cancellationToken =>
            SessionAutomationSettingsButtonClickAsync(
                SessionAutomationSettingsRoute.MacroLibrary,
                cancellationToken));
    }

    private async void TemplateSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(cancellationToken =>
            SessionAutomationSettingsButtonClickAsync(
                SessionAutomationSettingsRoute.Templates,
                cancellationToken));
    }

    private Task SessionAutomationSettingsButtonClickAsync(
        CancellationToken cancellationToken) =>
        SessionAutomationSettingsButtonClickAsync(
            SessionAutomationSettingsRoute.WindowLayout,
            cancellationToken);

    private async Task SessionAutomationSettingsButtonClickAsync(
        SessionAutomationSettingsRoute route,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy)
            return;
        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;

        var dialog = new SessionAutomationSettingsDialog(
            catalog,
            _robloxWindowService.GetMonitors(),
            _settings.Accounts,
            route,
            _settings.NamedDestinations)
        {
            Owner = this
        };
        var accepted = dialog.ShowDialog() == true;
        if (!accepted || dialog.UpdatedCatalog is not { } updated)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TrySaveSessionTemplateCatalog(updated))
            return;

        CleanupRemovedMacroArtifacts(catalog, _sessionTemplateCatalog!);

        SetStatus(
            Localize("AutomationSettings.SavedTitle"),
            Localize("AutomationSettings.SavedDetail"),
            Localize("Main.SettingsSavedBadge"),
            StatusTone.Success);

        if (dialog.RequestedAction ==
            SessionAutomationSettingsDialogAction.RecordMacro)
        {
            await RecordMacroButtonClickAsync(cancellationToken);
        }
        else if (dialog.RequestedAction ==
                 SessionAutomationSettingsDialogAction.SaveCurrentTemplate)
        {
            await SaveTemplateButtonClickAsync(cancellationToken);
        }
    }

    private async Task RunTemplateButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            IsAutoJoinWatchActive ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
        {
            return;
        }

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;

        var dialog = new RunTemplateDialog(
            catalog.Templates,
            _settings.BatchLaunchPresets,
            _settings.Accounts)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true ||
            dialog.SelectedTemplate is not { } template)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Artifact and assignment failures must fail closed before destination
        // persistence or RunBatchLaunchAsync can cancel the prior macro,
        // close clients, or enqueue replacement launches.
        var macroPreflight = PreflightTemplateMacros(template);
        if (!macroPreflight.Success)
        {
            ShowBatchResult(
                new BatchLaunchResult(
                    0,
                    template.ClientSlots.Count,
                    [],
                    ClientsWereClosed: false,
                    Cancelled: false,
                    AutomationWarning: null,
                    MacroPreflightFailure: macroPreflight.FailureKind),
                restoredOriginalProfile: true,
                launchPlans: []);
            return;
        }

        if (!await FlushDestinationPersistenceAsync())
            return;
        cancellationToken.ThrowIfCancellationRequested();

        var accountsByKey = _settings.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var selectedAccounts = new List<AccountProfile>();
        foreach (var slot in template.ClientSlots.OrderBy(slot => slot.Order))
        {
            if (!accountsByKey.TryGetValue(slot.AccountKey, out var account))
            {
                SetStatus(
                    Localize("Main.BatchDestinationsNotReadyTitle"),
                    Localize("Template.MissingAccounts", 1),
                    Localize("Main.InvalidDestinationBadge"),
                    StatusTone.Error);
                return;
            }

            var selected = AppSettingsSnapshot.Clone(account);
            if (!string.IsNullOrWhiteSpace(slot.Destination))
                selected.Destination = slot.Destination;
            selectedAccounts.Add(selected);
        }

        if (!BatchDestinationPlanner.TryCreate(
                selectedAccounts,
                _settings.RecentExperiences,
                out var launchPlans,
                out var planningError))
        {
            SetStatus(
                Localize("Main.BatchDestinationsNotReadyTitle"),
                LocalizeBatchPlanningError(planningError),
                Localize("Main.InvalidDestinationBadge"),
                StatusTone.Error);
            return;
        }

        ClearBatchRetryState();
        var originalProfile = _activeProfile;
        _batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        SetOperationBusy(true);
        SetBatchCancellationControls(active: true, enabled: true);
        BatchLaunchResult? result = null;
        var restoredOriginalProfile = true;
        try
        {
            result = await RunBatchLaunchAsync(
                launchPlans,
                TimeSpan.FromSeconds(template.DelaySeconds),
                _batchCancellation.Token,
                template);
        }
        catch (OperationCanceledException)
        {
            result = BatchLaunchResult.CancelledResult(selectedAccounts.Count);
        }
        finally
        {
            _launchInProgress = false;
            SetBatchCancellationControls(active: true, enabled: false);
            if (!cancellationToken.IsCancellationRequested &&
                result?.MacroPreflightFailure is null)
            {
                try
                {
                    restoredOriginalProfile = await RestoreBatchProfileAsync(
                        originalProfile,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    restoredOriginalProfile = false;
                }
            }

            _batchCancellation.Dispose();
            _batchCancellation = null;
            if (!_operationLifetime.IsShuttingDown)
            {
                SetBatchCancellationControls(active: false, enabled: false);
                SetOperationBusy(false);
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return;
        ShowBatchResult(
            result ?? BatchLaunchResult.CancelledResult(selectedAccounts.Count),
            restoredOriginalProfile,
            launchPlans);
    }

    private async void RecordMacroButtonClick(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(RecordMacroButtonClickAsync);
    }

    private async Task RecordMacroButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;

        ExactWheelMacroSaveResult? pendingMacroSave = null;
        var macroCatalogWasCommitted = false;
        SetOperationBusy(true);
        try
        {
            SetStatus(
                Localize("Macro.FindingClientsTitle"),
                Localize("Macro.FindingClientsDetail"),
                Localize("Macro.PlaybackBadge"),
                StatusTone.Neutral);
            var scan = await _robloxClient.GetRunningPlayersAsync(
                cancellationToken);
            _runningClients.Reconcile(
                scan.Clients.Select(client => client.Identity),
                scanIsComplete: scan.UnverifiedCount == 0);
            var runningIdentities = scan.Clients
                .Select(client => client.Identity)
                .ToHashSet(RobloxClientProcessIdentityComparer.Instance);
            var attributed = _runningClients.Snapshot()
                .Where(client => runningIdentities.Contains(client.Identity))
                .ToArray();
            if (attributed.Length == 0)
            {
                SetStatus(
                    Localize("Macro.NoClientsTitle"),
                    Localize("Macro.NoClientsDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Warning);
                return;
            }

            var discovered = await Task.WhenAll(attributed.Select(async client =>
            {
                var result = await _robloxWindowService.WaitForWindowAsync(
                    client.Identity,
                    timeout: null,
                    cancellationToken);
                return (Client: client, Result: result);
            }));
            cancellationToken.ThrowIfCancellationRequested();
            var targets = discovered
                .Where(item => item.Result.Success)
                .Select(item => new MacroRecorderTargetOption(
                    item.Client,
                    item.Result.Window!))
                .ToArray();
            if (targets.Length == 0)
            {
                SetStatus(
                    Localize("Macro.NoWindowsTitle"),
                    Localize("Macro.NoWindowsDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Error);
                return;
            }

            var dialog = new MacroRecorderDialog(
                targets,
                _robloxWindowService,
                catalog.TemplatePreferences.MacroRecordingStopHotkey)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true ||
                dialog.Recording is not { } recording)
            {
                return;
            }

            _exactWheelMacroStore ??=
                new ExactWheelMacroStore(_sessionTemplateStore);
            pendingMacroSave = _exactWheelMacroStore.SaveWithResult(
                dialog.MacroName,
                dialog.MacroKind,
                recording,
                dialog.RecordedAccountKey);
            var definition = pendingMacroSave.Definition;
            var updated = SessionTemplatePolicy.Normalize(catalog);
            updated.MacroDefinitions.RemoveAll(existing =>
                existing.ContentId.Equals(
                    definition.ContentId,
                    StringComparison.OrdinalIgnoreCase));
            updated.MacroDefinitions.Add(definition);
            updated = SessionTemplatePolicy.Normalize(updated);
            if (!TrySaveSessionTemplateCatalog(updated))
                return;
            macroCatalogWasCommitted = true;

            SetStatus(
                Localize("Macro.SavedTitle", definition.Name),
                Localize(
                    "Macro.SavedDetail",
                    definition.EventCount,
                    definition.DurationMilliseconds),
                Localize("Main.SettingsSavedBadge"),
                StatusTone.Success);
        }
        catch (Exception exception) when (
            IsExpectedMacroArtifactFailure(exception))
        {
            Trace.WriteLine(
                $"ExactWheel recording save failed safely: {exception.GetType().Name}.");
            SetStatus(
                Localize("Macro.SaveFailureTitle"),
                Localize("Macro.SaveFailureDetail"),
                Localize("Main.LocalDataErrorBadge"),
                StatusTone.Error);
        }
        finally
        {
            if (pendingMacroSave is
                { PayloadCreated: true, Definition: { } definition } &&
                !macroCatalogWasCommitted)
            {
                CleanupNewMacroAfterFailedCatalogSave(definition);
            }
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private void CleanupNewMacroAfterFailedCatalogSave(
        MacroDefinition definition)
    {
        try
        {
            var catalogRead = _sessionTemplateStore.Read();
            if (!catalogRead.IsValid)
            {
                // Without an authoritative post-failure catalog, retaining one
                // verified payload is safer than deleting a possibly referenced
                // recording.
                Trace.WriteLine(
                    $"New macro rollback cleanup was skipped because the catalog could not be verified: {definition.SafeFileName}.");
                return;
            }

            _exactWheelMacroStore ??=
                new ExactWheelMacroStore(_sessionTemplateStore);
            var cleanup = _exactWheelMacroStore
                .TryDeleteExactBytesIfUnreferenced(
                    definition,
                    catalogRead.Catalog.MacroDefinitions);
            if (cleanup == MacroArtifactCleanupResult.Failed)
            {
                Trace.WriteLine(
                    $"New macro rollback cleanup failed: {definition.SafeFileName}.");
            }
        }
        catch (Exception exception) when (
            IsExpectedMacroArtifactFailure(exception))
        {
            Trace.WriteLine(
                $"New macro rollback cleanup failed safely: {exception.GetType().Name}.");
        }
    }

    private void CleanupRemovedMacroArtifacts(
        SessionTemplateCatalog previousCatalog,
        SessionTemplateCatalog resultingCatalog)
    {
        try
        {
            var candidates = ExactWheelMacroStore
                .FindNewlyUnreferencedPayloads(
                    previousCatalog.MacroDefinitions,
                    resultingCatalog.MacroDefinitions);
            if (candidates.Count == 0)
                return;

            _exactWheelMacroStore ??=
                new ExactWheelMacroStore(_sessionTemplateStore);
            foreach (var definition in candidates)
            {
                var cleanup = _exactWheelMacroStore
                    .TryDeleteExactBytesIfUnreferenced(
                        definition,
                        resultingCatalog.MacroDefinitions);
                if (cleanup == MacroArtifactCleanupResult.Failed)
                {
                    // The catalog commit is already durable. A locked or changed
                    // payload stays on disk for safety and never rolls that commit
                    // back; the diagnostic makes the retained orphan observable.
                    Trace.WriteLine(
                        $"Removed macro payload cleanup failed: {definition.SafeFileName}.");
                }
            }
        }
        catch (Exception exception) when (
            IsExpectedMacroArtifactFailure(exception))
        {
            Trace.WriteLine(
                $"Removed macro payload cleanup failed safely: {exception.GetType().Name}.");
        }
    }

    private async void SaveTemplateButtonClick(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(SaveTemplateButtonClickAsync);
    }

    private async Task SaveTemplateButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;

        SetOperationBusy(true);
        try
        {
            SetStatus(
                Localize("Template.CapturingTitle"),
                Localize("Template.CapturingDetail"),
                Localize("Template.ArrangingBadge"),
                StatusTone.Neutral);
            var scan = await _robloxClient.GetRunningPlayersAsync(
                cancellationToken);
            _runningClients.Reconcile(
                scan.Clients.Select(client => client.Identity),
                scanIsComplete: scan.UnverifiedCount == 0);
            var runningIdentities = scan.Clients
                .Select(client => client.Identity)
                .ToHashSet(RobloxClientProcessIdentityComparer.Instance);
            var attributed = _runningClients.Snapshot()
                .Where(client => runningIdentities.Contains(client.Identity))
                .ToArray();
            if (attributed.Length == 0)
            {
                SetStatus(
                    Localize("Template.NoAttributedClientsTitle"),
                    Localize("Template.NoAttributedClientsDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Warning);
                return;
            }

            var discovered = await Task.WhenAll(attributed.Select(async client =>
            {
                var result = await _robloxWindowService.WaitForWindowAsync(
                    client.Identity,
                    timeout: null,
                    cancellationToken);
                return (Client: client, Result: result);
            }));
            cancellationToken.ThrowIfCancellationRequested();
            if (discovered.Any(item => !item.Result.Success))
            {
                SetStatus(
                    Localize("Template.CaptureFailureTitle"),
                    Localize("Template.CaptureFailureDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Error);
                return;
            }

            var windows = discovered
                .Select(item => new RobloxSessionLayoutWindow(
                    item.Client.Attribution.AccountKey,
                    item.Client.Identity,
                    item.Result.Window!.Handle))
                .ToArray();
            _robloxSessionLayoutCoordinator ??=
                new RobloxSessionLayoutCoordinator(_robloxWindowService);
            var captured = await _robloxSessionLayoutCoordinator
                .CapturePlacementsAsync(windows, cancellationToken);
            if (!captured.Success)
            {
                SetStatus(
                    Localize("Template.CaptureFailureTitle"),
                    captured.GlobalError ??
                        captured.Items.FirstOrDefault(item => !item.Success)?.Error ??
                        Localize("Template.CaptureFailureDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Error);
                return;
            }

            var placementByKey = captured.Items.ToDictionary(
                item => item.Key,
                item => item.Placement!,
                StringComparer.OrdinalIgnoreCase);
            var editorClients = attributed.Select(client =>
            {
                var attribution = client.Attribution;
                var displayName = string.IsNullOrWhiteSpace(attribution.AccountLabel)
                    ? $"@{attribution.AccountUsername}"
                    : $"{attribution.AccountLabel} (@{attribution.AccountUsername})";
                return new TemplateEditorClient(
                    attribution.AccountKey,
                    displayName,
                    placementByKey[attribution.AccountKey],
                    attribution.LaunchDestination);
            }).ToArray();
            var dialog = new TemplateEditorDialog(
                editorClients,
                catalog.MacroDefinitions,
                _settings.BatchLaunchDelaySeconds,
                namedDestinations: _settings.NamedDestinations)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true ||
                dialog.SavedTemplate is not { } savedTemplate)
            {
                return;
            }

            var updated = SessionTemplatePolicy.Normalize(catalog);
            updated.Templates.Add(savedTemplate);
            updated = SessionTemplatePolicy.Normalize(updated);
            if (!TrySaveSessionTemplateCatalog(updated))
                return;

            SetStatus(
                Localize("Template.SavedTitle", savedTemplate.Name),
                Localize("Template.SavedDetail", savedTemplate.ClientSlots.Count),
                Localize("Main.SettingsSavedBadge"),
                StatusTone.Success);
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private SessionTemplateCatalog? TryLoadSessionTemplateCatalog()
    {
        if (_sessionTemplateCatalog is not null)
            return _sessionTemplateCatalog;

        var result = _sessionTemplateStore.Read();
        if (!result.IsValid)
        {
            SetStatus(
                Localize("Template.CatalogErrorTitle"),
                Localize("Template.CatalogErrorDetail"),
                Localize("Main.LocalDataErrorBadge"),
                StatusTone.Error);
            return null;
        }

        _sessionTemplateCatalog = result.Catalog;
        _sessionTemplateCatalogNeedsRepair = result.RecoveredFromBackup;
        if (result.RecoveredFromBackup)
        {
            SetStatus(
                Localize("Template.CatalogRecoveredTitle"),
                Localize("Template.CatalogRecoveredDetail"),
                Localize("Main.BatchPartialBadge"),
                StatusTone.Warning);
        }
        return _sessionTemplateCatalog;
    }

    private bool TrySaveSessionTemplateCatalog(SessionTemplateCatalog catalog)
    {
        try
        {
            _sessionTemplateStore.Write(
                catalog,
                repairInvalidCatalog: _sessionTemplateCatalogNeedsRepair);
            _sessionTemplateCatalog = SessionTemplatePolicy.Normalize(catalog);
            _sessionTemplateCatalogNeedsRepair = false;
            return true;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception) ||
            exception is ArgumentException)
        {
            Trace.WriteLine(
                $"Session-template catalog save failed: {exception.GetType().Name}.");
            SetStatus(
                Localize("Template.SaveFailureTitle"),
                Localize("Template.SaveFailureDetail"),
                Localize("Main.LocalDataErrorBadge"),
                StatusTone.Error);
            return false;
        }
    }

    private SessionTemplateMacroPreflightResult PreflightTemplateMacros(
        SessionTemplate? template)
    {
        if (template is null ||
            template.MacroMode == SessionTemplateMacroMode.None)
        {
            return SessionTemplateMacroPreflightResult.Passed;
        }

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return SessionTemplateMacroPreflightResult.Unavailable();

        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        var result = SessionTemplateMacroPreflight.Validate(
            template,
            catalog,
            _exactWheelMacroStore);
        if (!result.Success)
        {
            Trace.WriteLine(
                $"Template macro preflight failed safely: {result.FailureKind}.");
        }

        return result;
    }

    private async Task<SessionPostLaunchResult> ApplySessionPostLaunchAsync(
        SessionTemplate? template,
        IReadOnlyList<LaunchedBatchClient> launchedClients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchedClients);
        var catalog = TryLoadSessionTemplateCatalog();
        var preferences = catalog?.TemplatePreferences ?? new TemplatePreferences();
        var tracked = launchedClients
            .Where(client =>
                client is not null &&
                !string.IsNullOrWhiteSpace(client.AccountKey) &&
                client.Identity is not null)
            .GroupBy(
                client => client.AccountKey,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
        if (tracked.Length == 0)
        {
            ClearCurrentBatchMacroContext();
            return new SessionPostLaunchResult(
                Localize("Template.LayoutNoAttributedClients"));
        }

        var shouldArrange = template is not null ||
            preferences.AutoArrangeNormalBatch;
        if (shouldArrange)
        {
            SetStatus(
                Localize("Template.ArrangingTitle"),
                template?.LayoutMode == SessionTemplateLayoutMode.Saved
                    ? Localize("Template.ArrangingSavedDetail")
                    : Localize("Template.ArrangingCascadeDetail"),
                Localize("Template.ArrangingBadge"),
                StatusTone.Neutral);
        }

        var discovered = await Task.WhenAll(tracked.Select(async client =>
        {
            var result = await _robloxWindowService.WaitForWindowAsync(
                client.Identity,
                timeout: null,
                cancellationToken);
            return (Client: client, Result: result);
        }));
        cancellationToken.ThrowIfCancellationRequested();

        var windows = discovered
            .Where(item => item.Result.Success)
            .Select(item => new RobloxSessionLayoutWindow(
                item.Client.AccountKey,
                item.Client.Identity,
                item.Result.Window!.Handle))
            .ToArray();
        var discoveryFailureCount = discovered.Length - windows.Length;
        if (windows.Length == 0)
        {
            return new SessionPostLaunchResult(
                Localize("Template.LayoutWindowDiscoveryFailure"));
        }

        RobloxSessionLayoutResult? layout = null;
        if (shouldArrange)
        {
            _robloxSessionLayoutCoordinator ??=
                new RobloxSessionLayoutCoordinator(_robloxWindowService);
            if (template?.LayoutMode == SessionTemplateLayoutMode.Saved)
            {
                var placements = template.ClientSlots
                    .Where(slot => slot.Placement is not null)
                    .GroupBy(
                        slot => slot.AccountKey,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Placement!,
                        StringComparer.OrdinalIgnoreCase);
                layout = await _robloxSessionLayoutCoordinator
                    .RestorePlacementsAsync(
                        windows,
                        placements,
                        preferences,
                        cancellationToken);
            }
            else
            {
                layout = await _robloxSessionLayoutCoordinator.ArrangeAsync(
                    windows,
                    preferences,
                    cancellationToken);
            }
        }

        var failedLayoutCount = layout?.Items.Count(item => !item.Success) ?? 0;
        var warnings = new List<string>();
        if (discoveryFailureCount > 0)
        {
            warnings.Add(Localize(
                "Template.LayoutSomeWindowsMissing",
                discoveryFailureCount));
        }
        if (layout is not null && !layout.Success)
        {
            warnings.Add(layout.GlobalError ??
                layout.ZOrderError ??
                Localize("Template.LayoutPartialFailure", failedLayoutCount));
        }
        if (layout is { GroupCount: > 1 })
        {
            warnings.Add(Localize(
                "Template.LayoutMultipleGroups",
                layout.GroupCount));
        }

        var templateOrder = (template?.ClientSlots ?? [])
            .ToDictionary(
                slot => slot.AccountKey,
                slot => slot.Order,
                StringComparer.OrdinalIgnoreCase);
        var accountsByKey = _settings.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var targets = windows.Select((window, index) =>
        {
            accountsByKey.TryGetValue(window.Key, out var account);
            var displayName = account is null
                ? window.Key
                : string.IsNullOrWhiteSpace(account.Label)
                    ? $"@{account.Username}"
                    : $"{account.Label} (@{account.Username})";
            return new SessionMacroClientTarget(
                window.Key,
                displayName,
                templateOrder.GetValueOrDefault(window.Key, index),
                window.Identity,
                window.Handle);
        }).ToArray();
        var macroPlan = SessionMacroRuntimePlanner.Create(
            template,
            targets,
            catalog?.MacroDefinitions ?? [],
            wholeLayoutCompletedSuccessfully:
                template is null || layout is { Success: true });
        InstallCurrentBatchMacroContext(macroPlan);
        if (macroPlan.Issues.Count > 0)
        {
            warnings.Add(Localize(
                "Macro.ControllerSkippedInvalid",
                macroPlan.Issues.Count));
        }

        return warnings.Count == 0
            ? SessionPostLaunchResult.Completed
            : new SessionPostLaunchResult(string.Join(" ", warnings));
    }

    private async Task<TemplateMacroPlaybackResult> PlayTemplateMacrosAsync(
        SessionTemplate template,
        IReadOnlyList<RobloxSessionLayoutWindow> windows,
        ExactWheelPlaybackRate playbackRate,
        CancellationToken cancellationToken)
    {
        if (template.MacroMode == SessionTemplateMacroMode.None)
            return TemplateMacroPlaybackResult.Completed;

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
        {
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.CatalogUnavailable"));
        }
        var windowsByKey = windows.ToDictionary(
            window => window.Key,
            StringComparer.OrdinalIgnoreCase);
        var definitionsById = catalog.MacroDefinitions.ToDictionary(
            definition => definition.ContentId,
            StringComparer.OrdinalIgnoreCase);
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);

        try
        {
            return template.MacroMode switch
            {
                SessionTemplateMacroMode.PerClient =>
                    await PlayPerClientMacrosAsync(
                        template,
                        windowsByKey,
                        definitionsById,
                        sharedMacroId: null,
                        playbackRate,
                        cancellationToken),
                SessionTemplateMacroMode.Shared =>
                    await PlayPerClientMacrosAsync(
                        template,
                        windowsByKey,
                        definitionsById,
                        template.SharedMacroId,
                        playbackRate,
                        cancellationToken),
                SessionTemplateMacroMode.WholeLayout =>
                    await PlayWholeLayoutMacroAsync(
                        template,
                        windows,
                        definitionsById,
                        playbackRate,
                        cancellationToken),
                _ => TemplateMacroPlaybackResult.Stopped(
                    Localize("Macro.InvalidAssignment"))
            };
        }
        catch (Exception exception) when (
            IsExpectedMacroArtifactFailure(exception))
        {
            Trace.WriteLine(
                $"ExactWheel template playback stopped safely: {exception.GetType().Name}.");
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.PlaybackFailure", exception.Message));
        }
    }

    private async Task<TemplateMacroPlaybackResult>
        PlayPerClientMacrosAsync(
        SessionTemplate template,
        IReadOnlyDictionary<string, RobloxSessionLayoutWindow> windowsByKey,
        IReadOnlyDictionary<string, MacroDefinition> definitionsById,
        string? sharedMacroId,
        ExactWheelPlaybackRate playbackRate,
        CancellationToken cancellationToken)
    {
        var targetSlots = SelectClientMacroPlaybackSlots(
            template,
            sharedMacroId);
        if (sharedMacroId is not null && targetSlots.Count == 0)
        {
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.InvalidAssignment"));
        }

        var assigned = 0;
        var completed = 0;
        var skipped = 0;
        foreach (var slot in targetSlots)
        {
            var macroId = sharedMacroId ?? slot.PerClientMacroId;
            if (string.IsNullOrWhiteSpace(macroId))
                continue;
            assigned++;
            if (!windowsByKey.TryGetValue(slot.AccountKey, out var window) ||
                !definitionsById.TryGetValue(macroId, out var definition) ||
                definition.Kind != SessionMacroKind.Client)
            {
                skipped++;
                continue;
            }

            SetStatus(
                Localize("Macro.FocusingClientTitle"),
                Localize("Macro.FocusingClientDetail", slot.Order + 1),
                Localize("Macro.PlaybackBadge"),
                StatusTone.Neutral);
            var focused = await _robloxWindowService.FocusAsync(
                window.Identity,
                window.Handle,
                timeout: null,
                cancellationToken);
            if (!focused.Success)
            {
                skipped++;
                continue;
            }

            string? warning;
            try
            {
                var leaseResult = _robloxWindowService
                    .AcquirePlaybackTargetLease(
                        window.Identity,
                        window.Handle);
                if (!leaseResult.Success || leaseResult.Lease is null)
                {
                    Trace.WriteLine(
                        $"One client macro target lease was rejected: {leaseResult.Failure?.Kind}.");
                    skipped++;
                    continue;
                }
                using var playbackLease = leaseResult.Lease;
                if (!playbackLease.IsDispatchAuthorized())
                {
                    skipped++;
                    continue;
                }
                var destination =
                    ExactWheelDesktopCapture.CaptureRecordingTarget(
                        window.Handle,
                        requireForeground: true);
                var source = _exactWheelMacroStore!.Load(definition);
                var transformed = ExactWheelCoordinateTransforms
                    .TransformClientRelative(
                        source,
                        destination.Display,
                        destination.Metadata);
                warning = await PlayRecordingAsync(
                    transformed,
                    playbackLease,
                    playbackRate,
                    cancellationToken,
                    pauseOnFocusLoss: true);
            }
            catch (Exception exception) when (
                IsExpectedMacroArtifactFailure(exception) ||
                exception is System.ComponentModel.Win32Exception)
            {
                Trace.WriteLine(
                    $"One client macro assignment was skipped safely: {exception.GetType().Name}.");
                skipped++;
                continue;
            }
            if (warning is not null)
                return TemplateMacroPlaybackResult.Stopped(warning);
            completed++;
        }

        if (assigned == 0)
        {
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.InvalidAssignment"));
        }
        if (completed == 0)
        {
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.ControllerSkippedInvalid", skipped));
        }
        return skipped == 0
            ? TemplateMacroPlaybackResult.Completed
            : new TemplateMacroPlaybackResult(
                Localize("Macro.ControllerSkippedInvalid", skipped),
                MayContinue: true);
    }

    internal static IReadOnlyList<SessionTemplateClientSlot>
        SelectClientMacroPlaybackSlots(
            SessionTemplate template,
            string? sharedMacroId)
    {
        ArgumentNullException.ThrowIfNull(template);
        return sharedMacroId is null
            ? template.ClientSlots.OrderBy(slot => slot.Order).ToArray()
            : SessionTemplatePolicy.SelectSharedMacroTargetSlots(template);
    }

    private async Task<TemplateMacroPlaybackResult>
        PlayWholeLayoutMacroAsync(
        SessionTemplate template,
        IReadOnlyList<RobloxSessionLayoutWindow> windows,
        IReadOnlyDictionary<string, MacroDefinition> definitionsById,
        ExactWheelPlaybackRate playbackRate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(template.WholeLayoutMacroId) ||
            !definitionsById.TryGetValue(
                template.WholeLayoutMacroId,
                out var definition) ||
            definition.Kind != SessionMacroKind.WholeLayout ||
            windows.Count == 0)
        {
            return TemplateMacroPlaybackResult.Stopped(
                Localize("Macro.InvalidAssignment"));
        }

        var first = windows[0];
        var focused = await _robloxWindowService.FocusAsync(
            first.Identity,
            first.Handle,
            timeout: null,
            cancellationToken);
        if (!focused.Success)
        {
            return TemplateMacroPlaybackResult.Stopped(
                focused.Error ?? Localize("Macro.FocusDenied"));
        }

        var leaseResult = _robloxWindowService.AcquirePlaybackTargetLease(
            windows.Select(window => new RobloxPlaybackTarget(
                window.Identity,
                window.Handle)).ToArray());
        if (!leaseResult.Success || leaseResult.Lease is null)
        {
            Trace.WriteLine(
                $"Whole-session macro target lease was rejected: {leaseResult.Failure?.Kind}.");
            return TemplateMacroPlaybackResult.Stopped(
                leaseResult.Failure?.Error ?? Localize("Macro.FocusDenied"));
        }
        using var playbackLease = leaseResult.Lease;
        if (!playbackLease.IsDispatchAuthorized())
        {
            return TemplateMacroPlaybackResult.Stopped(
                playbackLease.Failure?.Error ??
                    Localize("Macro.FocusDenied"));
        }

        var destination = ExactWheelDesktopCapture.CaptureRecordingTarget(
            first.Handle,
            requireForeground: true);
        var source = _exactWheelMacroStore!.Load(definition);
        var transformed = ExactWheelCoordinateTransforms
            .TransformVirtualDesktopNormalized(
                source,
                destination.Display,
                destination.Metadata);
        var warning = await PlayRecordingAsync(
            transformed,
            playbackLease,
            playbackRate,
            cancellationToken,
            pauseOnFocusLoss: true);
        return warning is null
            ? TemplateMacroPlaybackResult.Completed
            : TemplateMacroPlaybackResult.Stopped(warning);
    }

    private async Task<string?> PlayRecordingAsync(
        ExactWheelRecording recording,
        RobloxPlaybackTargetLease playbackLease,
        ExactWheelPlaybackRate playbackRate,
        CancellationToken cancellationToken,
        bool pauseOnFocusLoss = false)
    {
        await using var session = new ExactWheelSession();
        var result = await session.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                LoopCount = 1,
                Rate = playbackRate,
                Infinite = false,
                StopOnPhysicalInput = !pauseOnFocusLoss,
                PauseOnPhysicalInput = pauseOnFocusLoss,
                EnforcePhysicalInputRelease = true,
                EventDispatchAuthorization = inputEvent =>
                {
                    var authorization = playbackLease
                        .GetDispatchAuthorization(inputEvent);
                    return pauseOnFocusLoss || authorization ==
                        ExactWheelDispatchAuthorization.Authorized
                        ? authorization
                        : ExactWheelDispatchAuthorization.Denied;
                }
            },
            cancellationToken);
        SessionMacroPlaybackCancellation.ThrowIfCleanCancellation(
            result,
            cancellationToken);
        if (result.Succeeded)
            return null;
        return result.Reason == ExactWheelPlaybackStopReason.PhysicalIntervention
            ? Localize("Macro.StoppedByPhysicalInput")
            : Localize("Macro.PlaybackFailure", result.Message);
    }

    private static bool IsExpectedMacroArtifactFailure(Exception exception) =>
        exception is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or OverflowException or
            System.Security.SecurityException;

    private sealed record SessionPostLaunchResult(string? Warning)
    {
        internal static SessionPostLaunchResult Completed { get; } =
            new((string?)null);
    }

    private sealed record TemplateMacroPlaybackResult(
        string? Warning,
        bool MayContinue)
    {
        internal static TemplateMacroPlaybackResult Completed { get; } =
            new(null, MayContinue: true);

        internal static TemplateMacroPlaybackResult Stopped(string warning) =>
            new(warning, MayContinue: false);
    }
}
