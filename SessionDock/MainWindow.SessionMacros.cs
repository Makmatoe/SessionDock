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
    private bool _macroPlaybackInProgress;
    private bool _macroAssignmentInProgress;

    private void InitializeMacroSessionUi()
    {
        Closed += (_, _) => CloseMacroControllerPermanently();
        UpdateCurrentMacroActions();
    }

    private void InstallCurrentBatchMacroContext(
        SessionMacroLaunchPlanResult plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CancelCurrentMacroPlayback();
        if (_currentMacroContext is not null)
            _currentMacroContext.Changed -= CurrentMacroContext_Changed;
        _currentMacroContext = plan.Context;
        _currentMacroContext.Changed += CurrentMacroContext_Changed;
        if (_macroController is not null)
            _macroController.UpdateContext(plan.Context);
        UpdateCurrentMacroActions();

        if (plan.Context.Snapshot().HasAssignments)
            OpenMacroController(userInitiated: false);
        else
            _macroController?.Hide();
    }

    private void ClearCurrentBatchMacroContext()
    {
        CancelCurrentMacroPlayback();
        if (_currentMacroContext is not null)
            _currentMacroContext.Changed -= CurrentMacroContext_Changed;
        _currentMacroContext = null;
        _macroController?.Hide();
        UpdateCurrentMacroActions();
    }

    private void CurrentMacroContext_Changed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
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

            var validated = await Task.WhenAll(snapshot.Clients.Select(
                async client =>
                {
                    var capture = await _robloxWindowService.CaptureAsync(
                        client.Identity,
                        client.WindowHandle,
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

        var prepared = PrepareRuntimeMacroPlan(snapshot, catalog);
        if (!prepared.HasAssignments)
        {
            return new SessionMacroPlaybackOutcome(
                false,
                prepared.Warning ??
                    Localize("Macro.ControllerNoValidAssignments"));
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
        _macroPlaybackInProgress = true;
        UpdateCurrentMacroActions();
        SetStatus(
            Localize("Macro.ControllerPlayingTitle"),
            Localize("Macro.ControllerPlayingDetail", speed),
            Localize("Macro.PlaybackBadge"),
            StatusTone.Warning);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(prepared.Warning))
            warnings.Add(prepared.Warning);
        IDisposable? playbackPerformanceMode = null;
        try
        {
            playbackPerformanceMode =
                await TryEnterMacroPlaybackPerformanceModeAsync(
                    playbackCancellation.Token);

            await SessionMacroPlaybackLoop.RunUntilStoppedAsync(
                async cycleCancellationToken =>
                {
                    ExactWheelDisplayTopology destinationDisplay;
                    try
                    {
                        destinationDisplay = prepared.PlaybackCache
                            .GetDisplayTopology(
                                ExactWheelDesktopCapture
                                    .CaptureDisplayTopology);
                    }
                    catch (Exception exception) when (
                        IsExpectedMacroArtifactFailure(exception) ||
                        exception is System.ComponentModel.Win32Exception)
                    {
                        warnings.Add(Localize(
                            "Macro.PlaybackFailure",
                            exception.Message));
                        return false;
                    }

                    var mayContinue = true;
                    if (prepared.ClientTemplate is not null)
                    {
                        var result = await PlayTemplateMacrosAsync(
                            prepared.ClientTemplate,
                            prepared,
                            destinationDisplay,
                            rate,
                            cycleCancellationToken);
                        mayContinue = result.MayContinue;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                            warnings.Add(result.Warning);
                    }
                    if (mayContinue && prepared.WholeTemplate is not null)
                    {
                        var result = await PlayTemplateMacrosAsync(
                            prepared.WholeTemplate,
                            prepared,
                            destinationDisplay,
                            rate,
                            cycleCancellationToken);
                        mayContinue = result.MayContinue;
                        if (!string.IsNullOrWhiteSpace(result.Warning))
                            warnings.Add(result.Warning);
                    }

                    return mayContinue;
                },
                playbackCancellation.Token);

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
            prepared.PlaybackLeases.Dispose();
            playbackPerformanceMode?.Dispose();
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

    private RuntimeMacroPlan PrepareRuntimeMacroPlan(
        SessionMacroLaunchSnapshot snapshot,
        SessionTemplateCatalog catalog)
    {
        _exactWheelMacroStore ??=
            new ExactWheelMacroStore(_sessionTemplateStore);
        var playbackCache = new SessionMacroPlaybackCache();
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
                if (assignment.AccountKey is null ||
                    !clientByKey.ContainsKey(assignment.AccountKey) ||
                    !IsMacroFileUsable(assignment.Definition))
                {
                    invalidCount++;
                    continue;
                }

                validClientAssignments[assignment.AccountKey] =
                    assignment.Definition.ContentId;
            }
        }

        string? validWholeMacroId = null;
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
                IsMacroFileUsable(assignment.Definition))
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

        return new RuntimeMacroPlan(
            windows,
            windowsByKey,
            definitionsById,
            playbackCache,
            playbackLeases,
            clientTemplate,
            wholeTemplate,
            invalidCount == 0
                ? null
                : Localize("Macro.ControllerSkippedInvalid", invalidCount));

        bool IsMacroFileUsable(MacroDefinition definition)
        {
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
                _ = playbackCache.GetOrLoad(
                    definition,
                    _exactWheelMacroStore.Load);
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

        var prepared = PrepareRuntimeMacroPlan(snapshot, catalog);
        if (!prepared.HasAssignments)
        {
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

                var leaseResult = _robloxWindowService
                    .AcquirePlaybackTargetLease(
                        window.Identity,
                        window.Handle);
                if (!leaseResult.Success || leaseResult.Lease is null)
                {
                    unavailableTargetCount++;
                    continue;
                }

                using (leaseResult.Lease)
                    validAssignmentCount++;
            }
        }

        if (prepared.WholeTemplate is not null)
        {
            var targets = prepared.Windows
                .Select(window => new RobloxPlaybackTarget(
                    window.Identity,
                    window.Handle))
                .ToArray();
            var leaseResult = _robloxWindowService
                .AcquirePlaybackTargetLease(targets);
            if (leaseResult.Success && leaseResult.Lease is not null)
            {
                using (leaseResult.Lease)
                    validAssignmentCount++;
            }
            else
            {
                unavailableTargetCount++;
            }
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
        SessionMacroPlaybackCache PlaybackCache,
        SessionMacroPlaybackLeaseCache PlaybackLeases,
        SessionTemplate? ClientTemplate,
        SessionTemplate? WholeTemplate,
        string? Warning)
    {
        internal bool HasAssignments =>
            ClientTemplate is not null || WholeTemplate is not null;
    }
}
