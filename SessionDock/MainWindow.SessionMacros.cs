using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private SessionMacroLaunchContext? _currentMacroContext;
    private SessionMacroControllerWindow? _macroController;
    private CancellationTokenSource? _macroPlaybackCancellation;
    private Task _macroPlaybackCompletion = Task.CompletedTask;
    private readonly SessionMacroPlaybackProgressThrottle
        _macroPlaybackProgressThrottle = new();
    private MacroPlaybackProgressDispatchState?
        _macroPlaybackProgressDispatch;
    private SessionMacroPlaybackCache? _preflightMacroPlaybackCache;
    private bool _macroPlaybackInProgress;
    private bool _macroAssignmentInProgress;

    private void InitializeMacroSessionUi()
    {
        Closed += (_, _) => CloseMacroControllerPermanently();
        UpdateCurrentMacroActions();
    }

    private void InstallCurrentBatchMacroContext(
        SessionMacroLaunchPlanResult plan,
        SessionMacroPlaybackCache? preflightPlaybackCache = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var cachePublished = false;
        try
        {
            CancelCurrentMacroPlayback();
            if (_currentMacroContext is not null)
                _currentMacroContext.Changed -= CurrentMacroContext_Changed;
            var previousPreflightCache = Interlocked.Exchange(
                ref _preflightMacroPlaybackCache,
                preflightPlaybackCache);
            cachePublished = preflightPlaybackCache is not null;
            previousPreflightCache?.Dispose();
            _currentMacroContext = plan.Context;
            _currentMacroContext.Changed += CurrentMacroContext_Changed;
            if (_macroController is not null)
                _macroController.UpdateContext(plan.Context);
            UpdateCurrentMacroActions();

            if (plan.Context.Snapshot().HasAssignments)
                OpenMacroController(userInitiated: false);
            else
            {
                Interlocked.Exchange(
                    ref _preflightMacroPlaybackCache,
                    null)?.Dispose();
                cachePublished = false;
                _macroController?.Hide();
            }
        }
        catch
        {
            if (preflightPlaybackCache is not null)
            {
                SessionMacroPlaybackCacheReservation.ReleaseFailedTransfer(
                    ref _preflightMacroPlaybackCache,
                    preflightPlaybackCache,
                    cachePublished);
            }
            throw;
        }
    }

    private void ClearCurrentBatchMacroContext()
    {
        CancelCurrentMacroPlayback();
        if (_currentMacroContext is not null)
            _currentMacroContext.Changed -= CurrentMacroContext_Changed;
        Interlocked.Exchange(
            ref _preflightMacroPlaybackCache,
            null)?.Dispose();
        _currentMacroContext = null;
        _macroController?.Hide();
        UpdateCurrentMacroActions();
    }

    private void CurrentMacroContext_Changed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        // Assignment edits invalidate the exact preflight working set. Drop
        // it as a unit so removed sources cannot consume the 129-artifact run
        // budget before the edited snapshot loads its current definitions.
        Interlocked.Exchange(
            ref _preflightMacroPlaybackCache,
            null)?.Dispose();
        UpdateCurrentMacroActions();
    }

    private async void CurrentBatchAssignmentsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(OpenCurrentBatchAssignmentsAsync);
    }

    private async Task OpenCurrentBatchAssignmentsAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            _macroPlaybackInProgress ||
            _macroAssignmentInProgress ||
            _currentMacroContext is null)
            return;

        var context = _currentMacroContext;

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
            return;
        var clientDefinitions = catalog.MacroDefinitions
            .Where(definition => definition.Kind == SessionMacroKind.Client)
            .ToArray();
        if (clientDefinitions.Length == 0)
        {
            SetStatus(
                Localize("Macro.AssignNoMacrosTitle"),
                Localize("Macro.AssignNoMacrosDetail"),
                Localize("Main.BatchErrorBadge"),
                StatusTone.Warning);
            return;
        }

        var snapshot = context.Snapshot();
        var controllerWasVisible = _macroController?.IsVisible == true;
        var assignmentDialogShown = false;
        _macroAssignmentInProgress = true;
        _macroController?.Hide();
        UpdateCurrentMacroActions();
        try
        {
            if (_macroPlaybackInProgress ||
                !ReferenceEquals(context, _currentMacroContext))
            {
                return;
            }

            using var trustContext = new RobloxExecutableTrustContext();
            var validated = await Task.WhenAll(snapshot.Clients.Select(
                async client =>
                {
                    var capture = await _robloxWindowService.CaptureAsync(
                        client.Identity,
                        client.WindowHandle,
                        trustContext,
                        cancellationToken);
                    return (Client: client, Valid: capture.Success);
                }));
            cancellationToken.ThrowIfCancellationRequested();
            if (_macroPlaybackInProgress ||
                !ReferenceEquals(context, _currentMacroContext))
            {
                return;
            }

            var selectableKeys = validated
                .Where(item => item.Valid)
                .Select(item => item.Client.AccountKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectableKeys.Count == 0)
            {
                SetStatus(
                    Localize("Macro.AssignNoClientsTitle"),
                    Localize("Macro.AssignNoClientsDetail"),
                    Localize("Main.BatchErrorBadge"),
                    StatusTone.Warning);
                return;
            }

            var dialog = new ClientMacroAssignmentDialog(
                context,
                clientDefinitions,
                _robloxWindowService,
                selectableKeys)
            {
                Owner = this
            };
            assignmentDialogShown = true;
            _ = dialog.ShowDialog();
        }
        finally
        {
            _macroAssignmentInProgress = false;
            UpdateCurrentMacroActions();
            if (!_operationLifetime.IsShuttingDown &&
                ReferenceEquals(context, _currentMacroContext) &&
                context.Snapshot().HasAssignments &&
                (assignmentDialogShown || controllerWasVisible))
            {
                OpenMacroController(userInitiated: assignmentDialogShown);
            }
        }
    }

    private void OpenMacroControllerButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenMacroController(userInitiated: true);
    }

    private void OpenMacroController(bool userInitiated)
    {
        if (_currentMacroContext is null ||
            !_currentMacroContext.Snapshot().HasAssignments)
        {
            UpdateCurrentMacroActions();
            return;
        }

        var catalog = TryLoadSessionTemplateCatalog();
        var initialSpeed = catalog?.TemplatePreferences.MacroPlaybackSpeed ?? 1;
        if (_macroController is null)
        {
            _macroController = new SessionMacroControllerWindow(
                _currentMacroContext,
                initialSpeed,
                PlayCurrentMacroSnapshotAsync,
                PrepareMacroControllerReadiness,
                PersistMacroPlaybackSpeed)
            {
                Owner = this
            };
        }
        else
        {
            _macroController.UpdateContext(_currentMacroContext);
        }
        _macroController.Reopen(userInitiated);
    }

    private async Task<SessionMacroPlaybackOutcome>
        PlayCurrentMacroSnapshotAsync(
            SessionMacroLaunchSnapshot snapshot,
            double speed,
            CancellationToken cancellationToken)
    {
        if (_macroPlaybackInProgress)
        {
            return new SessionMacroPlaybackOutcome(
                false,
                Localize("Macro.ControllerAlreadyPlaying"));
        }
        if (_macroAssignmentInProgress)
        {
            return new SessionMacroPlaybackOutcome(
                false,
                Localize("Macro.ControllerOperationBusy"));
        }
        if (_operationBusy)
        {
            return new SessionMacroPlaybackOutcome(
                false,
                Localize("Macro.ControllerOperationBusy"));
        }

        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
        {
            return new SessionMacroPlaybackOutcome(
                false,
                Localize("Macro.CatalogUnavailable"));
        }
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);

        ExactWheelPlaybackRate rate;
        try
        {
            rate = ExactWheelPlaybackRate.Parse(
                speed.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            return new SessionMacroPlaybackOutcome(false, exception.Message);
        }

        CancelCurrentMacroPlayback();
        var playbackCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _operationLifetime.Token,
                cancellationToken);
        _macroPlaybackCancellation = playbackCancellation;
        var playbackCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _macroPlaybackCompletion = playbackCompletion.Task;
        _macroPlaybackProgressThrottle.Reset();
        _macroPlaybackInProgress = true;
        UpdateCurrentMacroActions();
        var playbackText = CaptureMacroPlaybackText();
        var noValidAssignments = Localize(
            "Macro.ControllerNoValidAssignments");
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var progressDispatch = new MacroPlaybackProgressDispatchState();
        IDisposable? playbackPerformanceMode = null;
        RuntimeMacroPlan? prepared = null;
        var playbackCache = Interlocked.Exchange(
                ref _preflightMacroPlaybackCache,
                null) ??
            new SessionMacroPlaybackCache();
        try
        {
            playbackPerformanceMode =
                await TryEnterMacroPlaybackPerformanceModeAsync(
                    playbackCancellation.Token);
            prepared = await Task.Run(
                () => PrepareRuntimeMacroPlan(
                    snapshot,
                    catalog,
                    playbackText,
                    playbackCache,
                    cancellationToken: playbackCancellation.Token),
                CancellationToken.None);
            if (!prepared.HasAssignments)
            {
                var preparationFailure = prepared.Warning ??
                    noValidAssignments;
                SetStatus(
                    Localize("Macro.ControllerStoppedTitle"),
                    preparationFailure,
                    Localize("Main.BatchPartialBadge"),
                    StatusTone.Warning);
                return new SessionMacroPlaybackOutcome(
                    false,
                    preparationFailure);
            }
            if (!string.IsNullOrWhiteSpace(prepared.Warning))
                warnings.Add(prepared.Warning);

            SetStatus(
                Localize("Macro.ControllerPlayingTitle"),
                Localize("Macro.ControllerPlayingDetail", speed),
                Localize("Macro.PlaybackBadge"),
                StatusTone.Warning);
            Volatile.Write(
                ref _macroPlaybackProgressDispatch,
                progressDispatch);
            await Task.Run(
                () => RunMacroPlaybackCoreAsync(
                    prepared,
                    rate,
                    playbackText,
                    warnings,
                    playbackCancellation.Token),
                CancellationToken.None);
            _ = Interlocked.CompareExchange(
                ref _macroPlaybackProgressDispatch,
                null,
                progressDispatch);

            var message = string.Join(" ", warnings.Distinct());
            var externallyCancelled =
                playbackCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested;
            if (externallyCancelled)
            {
                Trace.WriteLine(
                    $"Externally cancelled macro playback reported a safety failure: {message}");
            }
            else
            {
                SetStatus(
                    Localize("Macro.ControllerStoppedTitle"),
                    message,
                    Localize("Main.BatchPartialBadge"),
                    StatusTone.Warning);
            }
            return new SessionMacroPlaybackOutcome(
                false,
                message,
                SuppressDialog: externallyCancelled);
        }
        catch (OperationCanceledException) when (
            playbackCancellation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested &&
                !_operationLifetime.IsShuttingDown)
            {
                var hasWarnings = warnings.Count > 0;
                var detail = hasWarnings
                    ? string.Concat(
                        Localize("Macro.ControllerStoppedDetail"),
                        " ",
                        string.Join(" ", warnings))
                    : Localize("Macro.ControllerStoppedDetail");
                SetStatus(
                    Localize("Macro.ControllerStoppedTitle"),
                    detail,
                    Localize(hasWarnings
                        ? "Main.BatchPartialBadge"
                        : "Macro.PlaybackBadge"),
                    hasWarnings
                        ? StatusTone.Warning
                        : StatusTone.Neutral);
            }
            throw;
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _macroPlaybackProgressDispatch,
                null,
                progressDispatch);
            prepared?.PlaybackLeases.Dispose();
            if (prepared is null)
                playbackCache.Dispose();
            else
                prepared.PlaybackCache.Dispose();
            playbackPerformanceMode?.Dispose();
            _macroPlaybackProgressThrottle.Reset();
            _macroPlaybackInProgress = false;
            if (ReferenceEquals(
                    _macroPlaybackCancellation,
                    playbackCancellation))
            {
                _macroPlaybackCancellation = null;
            }
            playbackCancellation.Dispose();
            playbackCompletion.TrySetResult();
            if (ReferenceEquals(
                    _macroPlaybackCompletion,
                    playbackCompletion.Task))
            {
                _macroPlaybackCompletion = Task.CompletedTask;
            }
            UpdateCurrentMacroActions();
        }
    }

    private async Task RunMacroPlaybackCoreAsync(
        RuntimeMacroPlan prepared,
        ExactWheelPlaybackRate rate,
        MacroPlaybackText playbackText,
        HashSet<string> warnings,
        CancellationToken cancellationToken)
    {
        await using var playbackSession = new ExactWheelSession();
        // A macro run is one serial playback sequence, even when it loops
        // indefinitely. Retaining one healthy intervention monitor avoids
        // creating a hook thread and sampling every physical key again at
        // each short n=1 cycle; every PlayAsync still verifies its health.
        await using var playbackSequence =
            playbackSession.BeginPlaybackSequence();
        await SessionMacroPlaybackLoop.RunUntilStoppedAsync(
            async cycleCancellationToken =>
            {
                var mayContinue = true;
                var madeProgress = false;
                if (prepared.ClientTemplate is not null)
                {
                    var result = await PlayTemplateMacrosAsync(
                        prepared.ClientTemplate,
                        prepared,
                        playbackSession,
                        rate,
                        playbackText,
                        cycleCancellationToken);
                    mayContinue = result.MayContinue;
                    madeProgress |= result.MadeProgress;
                    if (!string.IsNullOrWhiteSpace(result.Warning))
                        warnings.Add(result.Warning);
                }
                if (mayContinue && prepared.WholeTemplate is not null)
                {
                    var result = await PlayTemplateMacrosAsync(
                        prepared.WholeTemplate,
                        prepared,
                        playbackSession,
                        rate,
                        playbackText,
                        cycleCancellationToken);
                    mayContinue = result.MayContinue;
                    madeProgress |= result.MadeProgress;
                    if (!string.IsNullOrWhiteSpace(result.Warning))
                        warnings.Add(result.Warning);
                }

                if (!mayContinue)
                    return SessionMacroPlaybackCycleResult.Stop;
                if (madeProgress)
                    return SessionMacroPlaybackCycleResult.Continue();

                var retryDelay = prepared.PlaybackRetryTracker
                    .GetDelayUntilNextAttempt();
                return retryDelay is null
                    ? SessionMacroPlaybackCycleResult.Stop
                    : SessionMacroPlaybackCycleResult.Continue(
                        retryDelay.Value);
            },
            cancellationToken);
    }

    private MacroPlaybackText CaptureMacroPlaybackText()
    {
        Dispatcher.VerifyAccess();
        return new MacroPlaybackText(
            Localization.EffectiveCulture,
            Localize("Macro.InvalidAssignment"),
            Localize("Macro.PlaybackFailure"),
            Localize("Macro.ControllerSkippedInvalid"),
            Localize("Macro.FocusDenied"),
            Localize("Macro.StoppedByPhysicalInput"));
    }

    private RuntimeMacroPlan PrepareRuntimeMacroPlan(
        SessionMacroLaunchSnapshot snapshot,
        SessionTemplateCatalog catalog,
        MacroPlaybackText playbackText,
        SessionMacroPlaybackCache? playbackCache = null,
        bool validateMacroArtifacts = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        playbackCache ??= new SessionMacroPlaybackCache();
        var playbackLeases = new SessionMacroPlaybackLeaseCache();
        var clientByKey = snapshot.Clients
            .GroupBy(
                client => client.AccountKey,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var validClientAssignments = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var invalidCount = 0;
        var fileValidity = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        if (snapshot.ClientMacroAssignments.Count > 0)
        {
            var clientPolicyTemplate = new SessionTemplate
            {
                Id = snapshot.TemplateId ?? "runtime-client-policy",
                Name = snapshot.TemplateName ?? "Runtime client policy",
                MacroMode = SessionTemplateMacroMode.PerClient,
                ClientSlots = snapshot.ClientMacroAssignments
                    .Select((assignment, index) =>
                        new SessionTemplateClientSlot
                        {
                            SlotId = $"runtime-policy-{index}",
                            AccountKey = assignment.Key,
                            Order = clientByKey.TryGetValue(
                                assignment.Key,
                                out var client)
                                ? client.Order
                                : int.MaxValue - index,
                            PerClientMacroId = assignment.Value
                        })
                    .ToList()
            };
            var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
                clientPolicyTemplate,
                catalog);
            invalidCount += resolution.InvalidAssignments.Count;
            foreach (var assignment in resolution.ValidAssignments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (assignment.AccountKey is null ||
                    !clientByKey.ContainsKey(assignment.AccountKey) ||
                    (validateMacroArtifacts &&
                        !IsMacroFileUsable(assignment.Definition)))
                {
                    invalidCount++;
                    continue;
                }

                validClientAssignments[assignment.AccountKey] =
                    assignment.Definition.ContentId;
            }
        }

        string? validWholeMacroId = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(snapshot.WholeSessionMacroId))
        {
            var wholePolicyTemplate = new SessionTemplate
            {
                Id = snapshot.TemplateId ?? "runtime-whole-policy",
                Name = snapshot.TemplateName ?? "Runtime whole policy",
                MacroMode = SessionTemplateMacroMode.WholeLayout,
                WholeLayoutMacroId = snapshot.WholeSessionMacroId,
                RepeatWholeLayoutMacro = snapshot.RepeatWholeSessionMacro
            };
            var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
                wholePolicyTemplate,
                catalog);
            invalidCount += resolution.InvalidAssignments.Count;
            var assignment = resolution.ValidAssignments.SingleOrDefault();
            if (assignment is not null &&
                (!validateMacroArtifacts ||
                    IsMacroFileUsable(assignment.Definition)))
            {
                validWholeMacroId = assignment.Definition.ContentId;
            }
            else if (assignment is not null)
                invalidCount++;
        }

        var windows = snapshot.Clients
            .Select(client => new RobloxSessionLayoutWindow(
                client.AccountKey,
                client.Identity,
                client.WindowHandle))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var windowsByKey = windows
            .GroupBy(
                window => window.Key,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var definitionsById = catalog.MacroDefinitions
            .GroupBy(
                definition => definition.ContentId,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var processBasenamesByKey = windowsByKey.ToDictionary(
            pair => pair.Key,
            pair => Path.GetFileName(pair.Value.Identity.ExecutablePath) ??
                string.Empty,
            StringComparer.OrdinalIgnoreCase);
        SessionTemplate? clientTemplate = null;
        if (validClientAssignments.Count > 0)
        {
            clientTemplate = new SessionTemplate
            {
                Id = snapshot.TemplateId ?? "runtime-client-macros",
                Name = snapshot.TemplateName ?? "Runtime client macros",
                MacroMode = SessionTemplateMacroMode.PerClient,
                ClientSlots = snapshot.Clients
                    .Select(client => new SessionTemplateClientSlot
                    {
                        SlotId = $"runtime-{client.Order}",
                        AccountKey = client.AccountKey,
                        Order = client.Order,
                        PerClientMacroId = validClientAssignments
                            .GetValueOrDefault(client.AccountKey)
                    })
                    .ToList()
            };
        }
        SessionTemplate? wholeTemplate = null;
        if (validWholeMacroId is not null)
        {
            wholeTemplate = new SessionTemplate
            {
                Id = snapshot.TemplateId ?? "runtime-whole-macro",
                Name = snapshot.TemplateName ?? "Runtime whole-session macro",
                MacroMode = SessionTemplateMacroMode.WholeLayout,
                WholeLayoutMacroId = validWholeMacroId,
                RepeatWholeLayoutMacro = snapshot.RepeatWholeSessionMacro,
                ClientSlots = snapshot.Clients
                    .Select(client => new SessionTemplateClientSlot
                    {
                        SlotId = $"runtime-{client.Order}",
                        AccountKey = client.AccountKey,
                        Order = client.Order
                    })
                    .ToList()
            };
        }
        IReadOnlyList<SessionTemplateClientSlot> clientPlaybackSlots =
            clientTemplate is null
                ? []
                : clientTemplate.ClientSlots
                    .OrderBy(slot => slot.Order)
                    .ToArray();

        return new RuntimeMacroPlan(
            windows,
            windowsByKey,
            definitionsById,
            processBasenamesByKey,
            playbackCache,
            playbackLeases,
            new SessionMacroPlaybackRetryTracker(),
            clientPlaybackSlots,
            clientTemplate,
            wholeTemplate,
            invalidCount == 0
                ? null
                : playbackText.SkippedInvalid(invalidCount));

        bool IsMacroFileUsable(MacroDefinition definition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = string.Concat(
                definition.ContentId,
                "|",
                definition.Sha256,
                "|",
                definition.SafeFileName,
                "|",
                definition.Kind);
            if (fileValidity.TryGetValue(cacheKey, out var isValid))
                return isValid;
            try
            {
                _ = playbackCache.GetOrLoadCancellable(
                    definition,
                    _exactWheelMacroStore!,
                    static (store, candidate) => store.Load(candidate),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                fileValidity[cacheKey] = true;
                return true;
            }
            catch (Exception exception) when (
                IsExpectedMacroArtifactFailure(exception))
            {
                Trace.WriteLine(
                    $"Runtime macro validation skipped one assignment: {exception.GetType().Name}.");
                fileValidity[cacheKey] = false;
                return false;
            }
        }
    }

    private SessionMacroControllerReadiness PrepareMacroControllerReadiness(
        SessionMacroLaunchSnapshot snapshot)
    {
        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null)
        {
            return new SessionMacroControllerReadiness(
                false,
                0,
                Localize("Macro.CatalogUnavailable"));
        }

        // Controller readiness is advisory only. It resolves the current
        // catalog and assignment metadata without parsing macro payloads or
        // acquiring authorization leases. Play performs the sole fresh,
        // authoritative artifact and exact-target validation.
        var prepared = PrepareRuntimeMacroPlan(
            snapshot,
            catalog,
            CaptureMacroPlaybackText(),
            validateMacroArtifacts: false);
        if (!prepared.HasAssignments)
        {
            prepared.PlaybackLeases.Dispose();
            return new SessionMacroControllerReadiness(
                false,
                0,
                prepared.Warning ??
                    Localize("Macro.ControllerNoValidAssignments"));
        }

        var validAssignmentCount = 0;
        var unavailableTargetCount = 0;
        if (prepared.ClientTemplate is not null)
        {
            foreach (var slot in prepared.ClientTemplate.ClientSlots.Where(
                         slot => !string.IsNullOrWhiteSpace(
                             slot.PerClientMacroId)))
            {
                if (!prepared.WindowsByKey.TryGetValue(
                        slot.AccountKey,
                        out var window))
                {
                    unavailableTargetCount++;
                    continue;
                }

                validAssignmentCount++;
            }
        }

        if (prepared.WholeTemplate is not null)
        {
            if (prepared.Windows.Count > 0)
                validAssignmentCount++;
            else
                unavailableTargetCount++;
        }

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(prepared.Warning))
            warnings.Add(prepared.Warning);
        if (unavailableTargetCount > 0)
        {
            warnings.Add(Localize(
                "Macro.ControllerSkippedInvalid",
                unavailableTargetCount));
        }
        var warning = warnings.Count == 0
            ? null
            : string.Join(" ", warnings.Distinct());
        prepared.PlaybackLeases.Dispose();
        return new SessionMacroControllerReadiness(
            validAssignmentCount > 0,
            validAssignmentCount,
            validAssignmentCount == 0
                ? warning ?? Localize("Macro.ControllerNoValidAssignments")
                : warning);
    }

    private void PersistMacroPlaybackSpeed(double speed)
    {
        if (!double.IsFinite(speed) ||
            speed is < SessionTemplatePolicy.MinimumMacroPlaybackSpeed or
                > SessionTemplatePolicy.MaximumMacroPlaybackSpeed)
            return;
        var catalog = TryLoadSessionTemplateCatalog();
        if (catalog is null ||
            Math.Abs(catalog.TemplatePreferences.MacroPlaybackSpeed - speed) <
                0.000001)
        {
            return;
        }
        var updated = SessionTemplatePolicy.Normalize(catalog);
        updated.TemplatePreferences.MacroPlaybackSpeed = speed;
        _ = TrySaveSessionTemplateCatalog(updated);
    }

    private void UpdateCurrentMacroActions()
    {
        var snapshot = _currentMacroContext?.Snapshot();
        var hasClients = snapshot?.Clients.Count > 0;
        var hasAssignments = snapshot?.HasAssignments == true;
        var canInteract = !_operationBusy && !_macroAssignmentInProgress;
        var canAssign = canInteract && !_macroPlaybackInProgress;
        SetButtonState(
            "SettingsBatchAssignmentsButton",
            canAssign && hasClients);
        SetButtonState(
            "SettingsMacroControllerButton",
            canInteract && hasAssignments);
        SetButtonState(
            "HomeBatchAssignmentsButton",
            canAssign && hasClients,
            hasClients);
        SetButtonState(
            "HomeMacroControllerButton",
            canInteract && hasAssignments,
            hasAssignments);
    }

    private void SetButtonState(
        string name,
        bool enabled,
        bool? visible = null)
    {
        if (FindName(name) is not Button button)
            return;
        button.IsEnabled = enabled;
        if (visible is not null)
        {
            button.Visibility = visible.Value
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void CancelCurrentMacroPlayback()
    {
        try
        {
            _macroPlaybackCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completed playback disposed its linked cancellation first.
        }
    }

    private async Task CancelAndWaitForCurrentMacroPlaybackAsync(
        CancellationToken cancellationToken)
    {
        var completion = _macroPlaybackCompletion;
        CancelCurrentMacroPlayback();
        await completion.WaitAsync(cancellationToken);
    }

    private void CloseMacroControllerPermanently()
    {
        CancelCurrentMacroPlayback();
        if (_currentMacroContext is not null)
            _currentMacroContext.Changed -= CurrentMacroContext_Changed;
        Interlocked.Exchange(
            ref _preflightMacroPlaybackCache,
            null)?.Dispose();
        _currentMacroContext = null;
        if (_macroController is not null)
        {
            _macroController.ClosePermanently();
            _macroController = null;
        }
    }

    private sealed record RuntimeMacroPlan(
        IReadOnlyList<RobloxSessionLayoutWindow> Windows,
        IReadOnlyDictionary<string, RobloxSessionLayoutWindow> WindowsByKey,
        IReadOnlyDictionary<string, MacroDefinition> DefinitionsById,
        IReadOnlyDictionary<string, string> ProcessBasenamesByKey,
        SessionMacroPlaybackCache PlaybackCache,
        SessionMacroPlaybackLeaseCache PlaybackLeases,
        SessionMacroPlaybackRetryTracker PlaybackRetryTracker,
        IReadOnlyList<SessionTemplateClientSlot> ClientPlaybackSlots,
        SessionTemplate? ClientTemplate,
        SessionTemplate? WholeTemplate,
        string? Warning)
    {
        internal bool HasAssignments =>
            ClientTemplate is not null || WholeTemplate is not null;
    }

    private sealed record MacroPlaybackText(
        CultureInfo Culture,
        string InvalidAssignment,
        string PlaybackFailureFormat,
        string SkippedInvalidFormat,
        string FocusDenied,
        string StoppedByPhysicalInput)
    {
        internal string PlaybackFailure(string? message) =>
            LocalizationCulture.Format(
                Culture,
                PlaybackFailureFormat,
                message);

        internal string SkippedInvalid(int count) =>
            LocalizationCulture.Format(
                Culture,
                SkippedInvalidFormat,
                count);
    }

    private sealed class MacroPlaybackProgressDispatchState
    {
        internal int LatestClientNumber;
        internal int PostPending;
    }
}
