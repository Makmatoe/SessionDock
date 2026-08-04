using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private const string WholeLayoutMacroRetryTargetKey =
        "sessiondock:whole-layout";
    private const ulong MacroDispatchRecoverySettleMicroseconds = 34_000;
    private const ulong MacroDispatchAuthorizationTimeoutMicroseconds =
        750_000;
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
        var preflightCatalog = SessionTemplatePolicy.Normalize(catalog);
        var preflightTemplate = preflightCatalog.Templates.SingleOrDefault(
            candidate => candidate.Id.Equals(
                template.Id,
                StringComparison.OrdinalIgnoreCase));
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        var preflightStore = _exactWheelMacroStore;
        using var macroPlaybackCacheReservation =
            new SessionMacroPlaybackCacheReservation();
        var preflightPlaybackCache =
            macroPlaybackCacheReservation.Cache;
        SetOperationBusy(true);
        try
        {
            // Artifact and assignment failures must fail closed before
            // destination persistence or RunBatchLaunchAsync can cancel the
            // prior macro, close clients, or enqueue replacement launches.
            // All WPF-bound state was captured above; only immutable catalog,
            // template, store, and run-cache references enter this worker.
            var macroPreflight = preflightTemplate is null
                ? SessionTemplateMacroPreflightResult.Invalid(template.Id)
                : await Task.Run(
                    () => SessionTemplateMacroPreflight.ValidateCancellable(
                        preflightTemplate,
                        preflightCatalog,
                        preflightStore,
                        preflightPlaybackCache,
                        cancellationToken),
                    cancellationToken);
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
            template = preflightTemplate!;

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
                    template,
                    macroPlaybackCacheReservation);
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
                result ?? BatchLaunchResult.CancelledResult(
                    selectedAccounts.Count),
                restoredOriginalProfile,
                launchPlans);
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
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

            using var trustContext = new RobloxExecutableTrustContext();
            var discovered = await Task.WhenAll(attributed.Select(async client =>
            {
                var result = await _robloxWindowService.WaitForWindowAsync(
                    client.Identity,
                    timeout: null,
                    trustContext,
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

            using var trustContext = new RobloxExecutableTrustContext();
            var discovered = await Task.WhenAll(attributed.Select(async client =>
            {
                var result = await _robloxWindowService.WaitForWindowAsync(
                    client.Identity,
                    timeout: null,
                    trustContext,
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

    private async Task<SessionPostLaunchResult> ApplySessionPostLaunchAsync(
        SessionTemplate? template,
        IReadOnlyList<LaunchedBatchClient> launchedClients,
        CancellationToken cancellationToken,
        SessionMacroPlaybackCacheReservation?
            macroPlaybackCacheReservation = null)
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

        using var trustContext = new RobloxExecutableTrustContext();
        var discovered = await Task.WhenAll(tracked.Select(async client =>
        {
            var result = await _robloxWindowService.WaitForWindowAsync(
                client.Identity,
                timeout: null,
                trustContext,
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
        var preflightPlaybackCache =
            macroPlaybackCacheReservation?.Take();
        InstallCurrentBatchMacroContext(
            macroPlan,
            preflightPlaybackCache);
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
        RuntimeMacroPlan plan,
        ExactWheelSession playbackSession,
        ExactWheelPlaybackRate playbackRate,
        MacroPlaybackText playbackText,
        CancellationToken cancellationToken)
    {
        if (template.MacroMode == SessionTemplateMacroMode.None)
            return TemplateMacroPlaybackResult.Completed;

        try
        {
            return template.MacroMode switch
            {
                SessionTemplateMacroMode.PerClient =>
                    await PlayPerClientMacrosAsync(
                        plan.ClientPlaybackSlots,
                        plan.WindowsByKey,
                        plan.DefinitionsById,
                        plan.ProcessBasenamesByKey,
                        plan.PlaybackCache,
                        plan.PlaybackLeases,
                        plan.PlaybackRetryTracker,
                        playbackSession,
                        sharedMacroId: null,
                        playbackRate,
                        playbackText,
                        cancellationToken),
                SessionTemplateMacroMode.Shared =>
                    await PlayPerClientMacrosAsync(
                        SelectClientMacroPlaybackSlots(
                            template,
                            template.SharedMacroId),
                        plan.WindowsByKey,
                        plan.DefinitionsById,
                        plan.ProcessBasenamesByKey,
                        plan.PlaybackCache,
                        plan.PlaybackLeases,
                        plan.PlaybackRetryTracker,
                        playbackSession,
                        template.SharedMacroId,
                        playbackRate,
                        playbackText,
                        cancellationToken),
                SessionTemplateMacroMode.WholeLayout =>
                    await PlayWholeLayoutMacroAsync(
                        template,
                        plan.Windows,
                        plan.DefinitionsById,
                        plan.ProcessBasenamesByKey,
                        plan.PlaybackCache,
                        plan.PlaybackLeases,
                        plan.PlaybackRetryTracker,
                        playbackSession,
                        playbackRate,
                        playbackText,
                        cancellationToken),
                _ => TemplateMacroPlaybackResult.Retired(
                    playbackText.InvalidAssignment)
            };
        }
        catch (Exception exception) when (
            IsExpectedMacroArtifactFailure(exception))
        {
            Trace.WriteLine(
                $"ExactWheel template playback stopped safely: {exception.GetType().Name}.");
            return TemplateMacroPlaybackResult.Retired(
                playbackText.PlaybackFailure(exception.Message));
        }
    }

    private async Task<TemplateMacroPlaybackResult>
        PlayPerClientMacrosAsync(
        IReadOnlyList<SessionTemplateClientSlot> targetSlots,
        IReadOnlyDictionary<string, RobloxSessionLayoutWindow> windowsByKey,
        IReadOnlyDictionary<string, MacroDefinition> definitionsById,
        IReadOnlyDictionary<string, string> processBasenamesByKey,
        SessionMacroPlaybackCache playbackCache,
        SessionMacroPlaybackLeaseCache playbackLeases,
        SessionMacroPlaybackRetryTracker playbackRetryTracker,
        ExactWheelSession playbackSession,
        string? sharedMacroId,
        ExactWheelPlaybackRate playbackRate,
        MacroPlaybackText playbackText,
        CancellationToken cancellationToken)
    {
        if (sharedMacroId is not null && targetSlots.Count == 0)
        {
            return TemplateMacroPlaybackResult.Retired(
                playbackText.InvalidAssignment);
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
            if (!playbackRetryTracker.CanAttempt(window.Key))
            {
                skipped++;
                continue;
            }

            ReportMacroPlaybackProgress(slot.Order + 1);

            MacroRecordingPlaybackOutcome playbackOutcome;
            RobloxPlaybackTargetLease? retainedPlaybackLease = null;
            try
            {
                var leaseResult = playbackLeases.GetOrAcquire(
                    _robloxWindowService,
                    window);
                if (!leaseResult.Success || leaseResult.Lease is null)
                {
                    Trace.WriteLine(
                        $"One client macro target lease was rejected: {leaseResult.Failure?.Kind}.");
                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        ClassifyMacroPlaybackRetry(
                            leaseResult.Failure?.Kind));
                    skipped++;
                    continue;
                }
                var playbackLease = leaseResult.Lease;
                retainedPlaybackLease = playbackLease;
                var focusTransition = await playbackSession
                    .WaitForFocusTransitionAsync(
                        playbackLease.GetDispatchAuthorization,
                        cancellationToken);
                if (focusTransition !=
                    ExactWheelFocusTransitionWaitResult.Ready)
                {
                    if (focusTransition == ExactWheelFocusTransitionWaitResult
                            .InterventionMonitorUnavailable)
                    {
                        return TemplateMacroPlaybackResult.Stopped(
                            playbackText.StoppedByPhysicalInput);
                    }
                    if (focusTransition == ExactWheelFocusTransitionWaitResult
                            .PhysicalInputHeld)
                    {
                        playbackRetryTracker.ReportFailure(
                            window.Key,
                            SessionMacroPlaybackRetryDisposition.Transient);
                        skipped++;
                        continue;
                    }

                    if (playbackLease.Failure is { } transitionFailure)
                    {
                        playbackRetryTracker.ReportFailure(
                            window.Key,
                            ClassifyMacroPlaybackRetry(
                                transitionFailure.Kind));
                        skipped++;
                        continue;
                    }

                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        SessionMacroPlaybackRetryDisposition.Transient);
                    skipped++;
                    continue;
                }
                var focused = await _robloxWindowService.FocusAsync(
                    playbackLease,
                    window.Identity,
                    window.Handle,
                    timeout: null,
                    cancellationToken: cancellationToken,
                    canProgrammaticallyActivate:
                        playbackSession.IsProgrammaticFocusAllowed);
                if (!focused.Success)
                {
                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        ClassifyMacroPlaybackRetry(
                            playbackLease.Failure?.Kind,
                            focused.Status));
                    skipped++;
                    continue;
                }
                ExactWheelRecordingTarget destination;
                try
                {
                    var windowClass = playbackLeases
                        .GetOrCaptureWindowClass(window);
                    var destinationDisplay = playbackCache.GetDisplayTopology(
                        ExactWheelDesktopCapture.CaptureDisplayTopology);
                    destination =
                        ExactWheelDesktopCapture.CapturePlaybackTarget(
                            window.Handle,
                            destinationDisplay,
                            processBasenamesByKey[window.Key],
                            windowClass,
                            ToExactWheelRect(focused.Window!.OuterBounds),
                            ToExactWheelRect(focused.Window.ClientBounds),
                            // Focus may legitimately move after FocusAsync.
                            // Event authorization below pauses until this
                            // exact leased target is foreground again.
                            requireForeground: false);
                }
                catch (Exception exception) when (
                    IsExpectedMacroTargetCaptureFailure(exception))
                {
                    Trace.WriteLine(
                        $"One client macro target snapshot was deferred: {exception.GetType().Name}.");
                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        SessionMacroPlaybackRetryDisposition.Transient);
                    skipped++;
                    continue;
                }
                var playbackPlan = playbackCache
                    .GetOrLoadAndCreateTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    destination,
                    _exactWheelMacroStore!,
                    static (store, candidate) => store.Load(candidate),
                    static (recording, target) => ExactWheelCoordinateTransforms
                        .CreateClientRelativePlaybackTransform(
                            recording,
                            target.Display,
                            target.Metadata));
                playbackOutcome = await PlayRecordingAsync(
                    playbackPlan.Recording,
                    playbackLease,
                    playbackSession,
                    playbackRate,
                    playbackText,
                    cancellationToken,
                    pauseOnFocusLoss: true,
                    coordinateTransform: playbackPlan.CoordinateTransform);
            }
            catch (Exception exception) when (
                IsExpectedMacroArtifactFailure(exception) ||
                exception is System.ComponentModel.Win32Exception)
            {
                Trace.WriteLine(
                    $"One client macro assignment was skipped safely: {exception.GetType().Name}.");
                playbackRetryTracker.ReportFailure(
                    window.Key,
                    IsExpectedMacroArtifactFailure(exception)
                        ? SessionMacroPlaybackRetryDisposition.Terminal
                        : SessionMacroPlaybackRetryDisposition.Transient);
                skipped++;
                continue;
            }
            if (playbackOutcome.Warning is { } warning)
            {
                // A failed global release can leave an injected key or mouse
                // button held. Never enter a retry cycle in that state, even
                // when the target loss itself would normally be transient.
                if (!playbackOutcome.Result.CleanupSucceeded)
                    return TemplateMacroPlaybackResult.Stopped(warning);
                if (retainedPlaybackLease?.Failure is { } playbackFailure)
                {
                    var disposition = ClassifyMacroPlaybackRetry(
                        playbackFailure.Kind);
                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        disposition);
                    // A terminal identity failure retires only this exact
                    // assignment. It must not stop healthy clients in the
                    // same indefinitely looping n-client session.
                    skipped++;
                    continue;
                }
                if (ClassifyMacroPlaybackRetry(
                        playbackOutcome.Result.Reason) is { }
                    playbackDisposition)
                {
                    playbackRetryTracker.ReportFailure(
                        window.Key,
                        playbackDisposition);
                    skipped++;
                    continue;
                }
                return TemplateMacroPlaybackResult.Stopped(warning);
            }
            playbackRetryTracker.ReportSuccess(window.Key);
            completed++;
        }

        if (assigned == 0)
        {
            return TemplateMacroPlaybackResult.Retired(
                playbackText.InvalidAssignment);
        }
        if (completed == 0)
        {
            if (playbackRetryTracker.GetDelayUntilNextAttempt() is not null)
            {
                return TemplateMacroPlaybackResult.Deferred(
                    playbackText.SkippedInvalid(skipped));
            }
            return TemplateMacroPlaybackResult.Retired(
                playbackText.SkippedInvalid(skipped));
        }
        return skipped == 0
            ? TemplateMacroPlaybackResult.Completed
            : new TemplateMacroPlaybackResult(
                playbackText.SkippedInvalid(skipped),
                MayContinue: true,
                MadeProgress: true,
                StopAll: false);
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
        IReadOnlyDictionary<string, string> processBasenamesByKey,
        SessionMacroPlaybackCache playbackCache,
        SessionMacroPlaybackLeaseCache playbackLeases,
        SessionMacroPlaybackRetryTracker playbackRetryTracker,
        ExactWheelSession playbackSession,
        ExactWheelPlaybackRate playbackRate,
        MacroPlaybackText playbackText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(template.WholeLayoutMacroId) ||
            !definitionsById.TryGetValue(
                template.WholeLayoutMacroId,
                out var definition) ||
            definition.Kind != SessionMacroKind.WholeLayout ||
            windows.Count == 0)
        {
            return TemplateMacroPlaybackResult.Retired(
                playbackText.InvalidAssignment);
        }
        if (!playbackRetryTracker.CanAttempt(
                WholeLayoutMacroRetryTargetKey))
        {
            return TemplateMacroPlaybackResult.Deferred(
                playbackText.FocusDenied);
        }

        var first = windows[0];
        var leaseResult = playbackLeases.GetOrAcquire(
            _robloxWindowService,
            windows);
        if (!leaseResult.Success || leaseResult.Lease is null)
        {
            Trace.WriteLine(
                $"Whole-session macro target lease was rejected: {leaseResult.Failure?.Kind}.");
            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                ClassifyMacroPlaybackRetry(
                    leaseResult.Failure?.Kind),
                leaseResult.Failure?.Error ?? playbackText.FocusDenied);
        }
        var playbackLease = leaseResult.Lease;
        var focusTransition = await playbackSession
            .WaitForFocusTransitionAsync(
                playbackLease.GetDispatchAuthorization,
                cancellationToken);
        if (focusTransition != ExactWheelFocusTransitionWaitResult.Ready)
        {
            if (focusTransition == ExactWheelFocusTransitionWaitResult
                    .InterventionMonitorUnavailable)
            {
                return TemplateMacroPlaybackResult.Stopped(
                    playbackText.StoppedByPhysicalInput);
            }
            if (focusTransition == ExactWheelFocusTransitionWaitResult
                    .PhysicalInputHeld)
            {
                return ReportMacroPlaybackRetryFailure(
                    playbackRetryTracker,
                    WholeLayoutMacroRetryTargetKey,
                    SessionMacroPlaybackRetryDisposition.Transient,
                    playbackText.StoppedByPhysicalInput);
            }

            if (playbackLease.Failure is { } transitionFailure)
            {
                return ReportMacroPlaybackRetryFailure(
                    playbackRetryTracker,
                    WholeLayoutMacroRetryTargetKey,
                    ClassifyMacroPlaybackRetry(transitionFailure.Kind),
                    transitionFailure.Error);
            }

            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                SessionMacroPlaybackRetryDisposition.Transient,
                playbackText.FocusDenied);
        }
        var focused = await _robloxWindowService.FocusAsync(
            playbackLease,
            first.Identity,
            first.Handle,
            timeout: null,
            cancellationToken: cancellationToken,
            canProgrammaticallyActivate:
                playbackSession.IsProgrammaticFocusAllowed);
        if (!focused.Success)
        {
            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                ClassifyMacroPlaybackRetry(
                    playbackLease.Failure?.Kind,
                    focused.Status),
                focused.Error ?? playbackText.FocusDenied);
        }
        ExactWheelRecordingTarget destination;
        try
        {
            var windowClass = playbackLeases.GetOrCaptureWindowClass(first);
            var destinationDisplay = playbackCache.GetDisplayTopology(
                ExactWheelDesktopCapture.CaptureDisplayTopology);
            destination = ExactWheelDesktopCapture.CapturePlaybackTarget(
                first.Handle,
                destinationDisplay,
                processBasenamesByKey[first.Key],
                windowClass,
                ToExactWheelRect(focused.Window!.OuterBounds),
                ToExactWheelRect(focused.Window.ClientBounds),
                // Playback authorization owns foreground changes after the
                // verified focus snapshot and pauses without stealing focus.
                requireForeground: false);
        }
        catch (Exception exception) when (
            IsExpectedMacroTargetCaptureFailure(exception))
        {
            Trace.WriteLine(
                $"Whole-session macro target snapshot was deferred: {exception.GetType().Name}.");
            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                SessionMacroPlaybackRetryDisposition.Transient,
                playbackText.FocusDenied);
        }
        var playbackPlan = playbackCache.GetOrLoadAndCreateTransform(
            definition,
            SessionMacroTransformKind.WholeLayout,
            destination,
            _exactWheelMacroStore!,
            static (store, candidate) => store.Load(candidate),
            static (recording, target) => ExactWheelCoordinateTransforms
                .CreateVirtualDesktopNormalizedPlaybackTransform(
                    recording,
                    target.Display,
                    target.Metadata));
        var playbackOutcome = await PlayRecordingAsync(
            playbackPlan.Recording,
            playbackLease,
            playbackSession,
            playbackRate,
            playbackText,
            cancellationToken,
            pauseOnFocusLoss: true,
            coordinateTransform: playbackPlan.CoordinateTransform);
        if (playbackOutcome.Warning is not { } warning)
        {
            playbackRetryTracker.ReportSuccess(
                WholeLayoutMacroRetryTargetKey);
            return TemplateMacroPlaybackResult.Completed;
        }
        if (!playbackOutcome.Result.CleanupSucceeded)
            return TemplateMacroPlaybackResult.Stopped(warning);
        if (playbackLease.Failure is { } playbackFailure)
        {
            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                ClassifyMacroPlaybackRetry(playbackFailure.Kind),
                warning);
        }

        if (ClassifyMacroPlaybackRetry(
                playbackOutcome.Result.Reason) is { } disposition)
        {
            return ReportMacroPlaybackRetryFailure(
                playbackRetryTracker,
                WholeLayoutMacroRetryTargetKey,
                disposition,
                warning);
        }

        return TemplateMacroPlaybackResult.Stopped(warning);
    }

    private async Task<MacroRecordingPlaybackOutcome> PlayRecordingAsync(
        ExactWheelRecording recording,
        RobloxPlaybackTargetLease playbackLease,
        ExactWheelSession playbackSession,
        ExactWheelPlaybackRate playbackRate,
        MacroPlaybackText playbackText,
        CancellationToken cancellationToken,
        bool pauseOnFocusLoss = false,
        ExactWheelPlaybackCoordinateTransform? coordinateTransform = null)
    {
        ExactWheelPlaybackResult result;
        try
        {
            result = await playbackSession.PlayAsync(
                recording,
                new ExactWheelPlaybackOptions
                {
                    LoopCount = 1,
                    Rate = playbackRate,
                    Infinite = false,
                    DispatchRecoverySettleMicroseconds =
                    MacroDispatchRecoverySettleMicroseconds,
                    DispatchAuthorizationTimeoutMicroseconds =
                    MacroDispatchAuthorizationTimeoutMicroseconds,
                    StopOnPhysicalInput = !pauseOnFocusLoss,
                    PauseOnPhysicalInput = pauseOnFocusLoss,
                    EnforcePhysicalInputRelease = true,
                    CoordinateTransform = coordinateTransform,
                    EventDispatchAuthorization = inputEvent =>
                    {
                        var authorization = playbackLease
                            .GetDispatchAuthorization(inputEvent);
                        return pauseOnFocusLoss || authorization ==
                            ExactWheelDispatchAuthorization.Authorized
                            ? authorization
                            : ExactWheelDispatchAuthorization.Denied;
                    },
                    DispatchCompleted = playbackLease.NotifyDispatchCompleted
                },
                cancellationToken);
        }
        finally
        {
            playbackLease.CompletePlaybackSegment();
        }
        SessionMacroPlaybackCancellation.ThrowIfCleanCancellation(
            result,
            cancellationToken);
        string? warning = null;
        if (!result.Succeeded)
        {
            warning = result.Reason ==
                    ExactWheelPlaybackStopReason.PhysicalIntervention &&
                result.CleanupSucceeded
                ? playbackText.StoppedByPhysicalInput
                : playbackText.PlaybackFailure(result.Message);
        }
        return new MacroRecordingPlaybackOutcome(result, warning);
    }

    private void ReportMacroPlaybackProgress(int clientNumber)
    {
        var dispatch = Volatile.Read(
            ref _macroPlaybackProgressDispatch);
        if (dispatch is null)
            return;
        Volatile.Write(ref dispatch.LatestClientNumber, clientNumber);
        if (!_macroPlaybackProgressThrottle.TryAcquire())
            return;
        if (Interlocked.Exchange(ref dispatch.PostPending, 1) != 0)
            return;

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _ = Interlocked.Exchange(
                        ref dispatch.PostPending,
                        0);
                    if (!ReferenceEquals(
                            Volatile.Read(
                                ref _macroPlaybackProgressDispatch),
                            dispatch) ||
                        !_macroPlaybackInProgress)
                    {
                        return;
                    }

                    var latestClient = Volatile.Read(
                        ref dispatch.LatestClientNumber);
                    SetStatus(
                        Localize("Macro.FocusingClientTitle"),
                        Localize(
                            "Macro.FocusingClientDetail",
                            latestClient),
                        Localize("Macro.PlaybackBadge"),
                        StatusTone.Neutral,
                        announceChanges: false);
                }));
        }
        catch (InvalidOperationException)
        {
            _ = Interlocked.Exchange(ref dispatch.PostPending, 0);
        }
    }

    private static ExactWheelRect ToExactWheelRect(
        RobloxPixelRect rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            checked(rectangle.Left + rectangle.Width),
            checked(rectangle.Top + rectangle.Height));

    private static bool IsExpectedMacroArtifactFailure(Exception exception) =>
        exception is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or OverflowException or
            System.Security.SecurityException;

    private static bool IsExpectedMacroTargetCaptureFailure(
        Exception exception) =>
        exception is InvalidOperationException or ArgumentException or
            OverflowException or System.ComponentModel.Win32Exception;

    private static SessionMacroPlaybackRetryDisposition
        ClassifyMacroPlaybackRetry(
        RobloxPlaybackTargetLeaseFailureKind? leaseFailureKind,
        RobloxWindowOperationStatus? windowStatus = null)
    {
        if (windowStatus == RobloxWindowOperationStatus.IdentityRejected ||
            leaseFailureKind is
                RobloxPlaybackTargetLeaseFailureKind.InvalidTargetSet or
                RobloxPlaybackTargetLeaseFailureKind.ConflictingTargetSet or
                RobloxPlaybackTargetLeaseFailureKind.ProcessExited or
                RobloxPlaybackTargetLeaseFailureKind.IdentityRejected or
                RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged or
                RobloxPlaybackTargetLeaseFailureKind.Disposed)
        {
            return SessionMacroPlaybackRetryDisposition.Terminal;
        }

        return SessionMacroPlaybackRetryDisposition.Transient;
    }

    private static SessionMacroPlaybackRetryDisposition?
        ClassifyMacroPlaybackRetry(
        ExactWheelPlaybackStopReason reason) =>
        reason switch
        {
            ExactWheelPlaybackStopReason.TargetLost or
                ExactWheelPlaybackStopReason.TargetUnavailable or
                ExactWheelPlaybackStopReason.DangerouslyLate or
                ExactWheelPlaybackStopReason.InjectionFailed or
                ExactWheelPlaybackStopReason.TimerFailed or
                ExactWheelPlaybackStopReason.PhysicalInputHeld =>
                SessionMacroPlaybackRetryDisposition.Transient,
            ExactWheelPlaybackStopReason.InvalidTimeline =>
                SessionMacroPlaybackRetryDisposition.Terminal,
            _ => null
        };

    private static TemplateMacroPlaybackResult
        ReportMacroPlaybackRetryFailure(
        SessionMacroPlaybackRetryTracker playbackRetryTracker,
        string targetKey,
        SessionMacroPlaybackRetryDisposition disposition,
        string warning)
    {
        playbackRetryTracker.ReportFailure(targetKey, disposition);
        return disposition == SessionMacroPlaybackRetryDisposition.Terminal
            ? TemplateMacroPlaybackResult.Retired(warning)
            : TemplateMacroPlaybackResult.Deferred(warning);
    }

    private sealed record SessionPostLaunchResult(string? Warning)
    {
        internal static SessionPostLaunchResult Completed { get; } =
            new((string?)null);
    }

    private sealed record MacroRecordingPlaybackOutcome(
        ExactWheelPlaybackResult Result,
        string? Warning);

    private sealed record TemplateMacroPlaybackResult(
        string? Warning,
        bool MayContinue,
        bool MadeProgress,
        bool StopAll)
    {
        internal static TemplateMacroPlaybackResult Completed { get; } =
            new(
                null,
                MayContinue: true,
                MadeProgress: true,
                StopAll: false);

        internal static TemplateMacroPlaybackResult Deferred(string warning) =>
            new(
                warning,
                MayContinue: true,
                MadeProgress: false,
                StopAll: false);

        internal static TemplateMacroPlaybackResult Retired(string warning) =>
            new(
                warning,
                MayContinue: false,
                MadeProgress: false,
                StopAll: false);

        internal static TemplateMacroPlaybackResult Stopped(string warning) =>
            new(
                warning,
                MayContinue: false,
                MadeProgress: false,
                StopAll: true);
    }
}
