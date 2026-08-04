using System.Diagnostics;
using System.Windows;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private BatchRetryState? _batchRetryState;

    private async void BatchLaunchButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            BatchLaunchButtonClickAsync(retryState: null, cancellationToken));

    private async void RetryFailedBatchButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var retryState = _batchRetryState;
        if (retryState is null)
            return;

        await RunWindowOperationAsync(cancellationToken =>
            BatchLaunchButtonClickAsync(retryState, cancellationToken));
    }

    private async Task BatchLaunchButtonClickAsync(
        BatchRetryState? retryState,
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            IsAutoJoinWatchActive ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;

        if (!await FlushDestinationPersistenceAsync())
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy ||
            IsAutoJoinWatchActive ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;
        var dialogAccounts = retryState is null
            ? _settings.Accounts.Select(AppSettingsSnapshot.Clone).ToArray()
            : CreateRetryAccounts(retryState);
        if (dialogAccounts.Count == 0)
        {
            ClearBatchRetryState();
            SetStatus(
                Localize("Main.BatchNoFailedAccountsTitle"),
                Localize("Main.BatchNoFailedAccountsDetail"),
                Localize("Main.BatchRetryClearedBadge"),
                StatusTone.Neutral);
            return;
        }

        var dialog = new BatchLaunchDialog(
            dialogAccounts,
            retryState is null ? _settings.BatchLaunchPresets : [],
            _settings.BatchLaunchDelaySeconds,
            retryState?.AccountKeys,
            retryMode: retryState is not null)
        {
            Owner = this
        };
        var shouldStart = dialog.ShowDialog() == true;
        var persistPresets = retryState is null && dialog.PresetsChanged;
        var persistDelay = shouldStart &&
            dialog.DelaySeconds != _settings.BatchLaunchDelaySeconds;
        if (persistPresets || persistDelay)
        {
            var updatedPresets = dialog.UpdatedPresets
                .Select(AppSettingsSnapshot.Clone)
                .ToList();
            if (!await TryCommitSettingsMutationAsync(
                    () =>
                    {
                        if (persistPresets)
                        {
                            _settings.BatchLaunchPresets = updatedPresets
                                .Select(AppSettingsSnapshot.Clone)
                                .ToList();
                        }
                        if (persistDelay)
                        {
                            _settings.BatchLaunchDelaySeconds =
                                dialog.DelaySeconds;
                        }
                    },
                    Localize("Main.BatchPreferencesSaveFailureTitle"),
                    Localize("Main.BatchSettingsErrorBadge"),
                    Localize("Main.BatchPreferencesSaveFailureDetail")))
            {
                return;
            }
        }
        if (!shouldStart)
            return;

        if (!BatchDestinationPlanner.TryCreate(
                dialog.SelectedAccounts,
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

        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy ||
            IsAutoJoinWatchActive ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;

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
                dialog.Delay,
                _batchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            result = BatchLaunchResult.CancelledResult(dialog.SelectedAccounts.Count);
        }
        finally
        {
            _launchInProgress = false;
            SetBatchCancellationControls(active: true, enabled: false);
            if (!cancellationToken.IsCancellationRequested)
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
            result ?? BatchLaunchResult.CancelledResult(dialog.SelectedAccounts.Count),
            restoredOriginalProfile,
            launchPlans);
    }

    private string LocalizeBatchPlanningError(string error)
    {
        const string selectAccountError =
            "Select at least one account for the batch.";
        const string firstDestinationSuffix =
            " is the first selected account and needs a destination.";
        const string joinUserSuffix =
            ": Join-user destinations currently use single launch so Roblox can check that account's permission at launch time.";

        if (error.Equals(selectAccountError, StringComparison.Ordinal))
            return Localize("Main.BatchSelectAccountDetail");
        if (error.EndsWith(firstDestinationSuffix, StringComparison.Ordinal))
        {
            return Localize(
                "Main.BatchFirstDestinationRequiredDetail",
                error[..^firstDestinationSuffix.Length]);
        }
        if (error.EndsWith(joinUserSuffix, StringComparison.Ordinal))
        {
            return Localize(
                "Main.BatchJoinUserSingleDetail",
                error[..^joinUserSuffix.Length]);
        }

        var separatorIndex = error.LastIndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            var errorKey = error[(separatorIndex + 2)..];
            if (errorKey.StartsWith(
                    "Validation.Destination.",
                    StringComparison.Ordinal))
            {
                return Localize(
                    "Main.BatchAccountDestinationErrorDetail",
                    error[..separatorIndex],
                    Localize(errorKey));
            }
        }

        return Localize("Main.BatchDestinationsNotReadyDetail");
    }

    private IReadOnlyList<AccountProfile> CreateRetryAccounts(
        BatchRetryState retryState)
    {
        var accounts = BatchLaunchPreferences.ResolveAccounts(
            retryState.AccountKeys,
            _settings.Accounts);
        return accounts
            .Select(account => BatchRetryDestinationPolicy.CreateRetryAccount(
                account,
                retryState.EffectiveDestinations))
            .ToArray();
    }

    private void CancelBatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchCancellation is null || _batchCancellation.IsCancellationRequested)
            return;

        SetBatchCancellationControls(active: true, enabled: false);
        SetStatus(
            Localize("Main.BatchCancellingTitle"),
            Localize("Main.BatchCancellingDetail"),
            Localize("Main.BatchCancellingBadge"),
            StatusTone.Neutral);
        _batchCancellation.Cancel();
    }

    private void SetBatchCancellationControls(bool active, bool enabled)
    {
        var visibility = active
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelBatchButton.Visibility = visibility;
        HomeCancelBatchButton.Visibility = visibility;
        CancelBatchButton.IsEnabled = active && enabled;
        HomeCancelBatchButton.IsEnabled = active && enabled;

        if (!active || !enabled)
            return;

        if (HomeWorkspace.Visibility == Visibility.Visible)
            HomeCancelBatchButton.Focus();
        else
            CancelBatchButton.Focus();
    }

    private async Task<BatchLaunchResult> RunBatchLaunchAsync(
        IReadOnlyList<BatchLaunchPlan> launchPlans,
        TimeSpan delay,
        CancellationToken cancellationToken,
        SessionTemplate? sessionTemplate = null,
        SessionMacroPlaybackCacheReservation?
            macroPlaybackCacheReservation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Once the user confirms a new batch, input from the previous batch
        // must be fully quiescent before account preflight, process cleanup,
        // or any launch-state mutation begins.
        await CancelAndWaitForCurrentMacroPlaybackAsync(cancellationToken);
        var preflight = await PreflightBatchAccountsAsync(
            launchPlans,
            cancellationToken);
        if (preflight.Failures.Count > 0)
        {
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                preflight.Failures,
                ClientsWereClosed: false,
                Cancelled: false,
                AutomationWarning: null);
        }

        SetStatus(
            Localize("Main.BatchPreparingTitle"),
            Localize("Main.BatchPreparingDetail"),
            Localize("Main.BatchCleanupBadge"),
            StatusTone.Neutral);
        RobloxClientService.ClosePlayersResult closeResult;
        try
        {
            closeResult = await _robloxClient.CloseAllPlayersAsync(
                cancellationToken);
            if (closeResult.Success)
            {
                _runningClients.Clear();
                ClearCurrentBatchMacroContext();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"Roblox batch cleanup failed: {ex.GetType().Name}.");
            SetStatus(
                Localize("Main.BatchStoppedTitle"),
                Localize("Main.BatchCloseVerificationFailureDetail"),
                Localize("Main.BatchErrorBadge"),
                StatusTone.Error);
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                launchPlans.Select(plan => new BatchFailure(
                    plan.Account.Key,
                    Localize("Main.BatchFailureClientsNotClosed")))
                    .ToArray(),
                ClientsWereClosed: false,
                Cancelled: false,
                AutomationWarning: null);
        }

        if (!closeResult.Success)
        {
            var detail = closeResult.Unverified > 0
                ? closeResult.Unverified == 1
                    ? Localize("Main.BatchUnverifiedProcessDetailOne")
                    : Localize(
                        "Main.BatchUnverifiedProcessDetailMany",
                        closeResult.Unverified)
                : closeResult.Remaining == 1
                    ? Localize("Main.BatchRemainingClientDetailOne")
                    : Localize(
                        "Main.BatchRemainingClientDetailMany",
                        closeResult.Remaining);
            SetStatus(
                Localize("Main.BatchStoppedTitle"),
                detail,
                Localize("Main.BatchErrorBadge"),
                StatusTone.Error);
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                launchPlans.Select(plan => new BatchFailure(
                    plan.Account.Key,
                    detail))
                    .ToArray(),
                ClientsWereClosed: false,
                Cancelled: false,
                AutomationWarning: null);
        }

        Task cleanupSettled = Task.CompletedTask;
        if (closeResult.Closed > 0)
        {
            SetStatus(
                closeResult.Closed == 1
                    ? Localize("Main.BatchClosedClientTitleOne")
                    : Localize(
                        "Main.BatchClosedClientTitleMany",
                        closeResult.Closed),
                Localize("Main.BatchCleanupSettlingDetail"),
                Localize("Main.BatchCleanupBadge"),
                StatusTone.Success);
            cleanupSettled = Task.Delay(
                TimeSpan.FromSeconds(2),
                cancellationToken);
        }

        var outcomes = await BatchLaunchPipeline.RunAsync<
            VerifiedBatchLaunchPlan,
            QueuedBatchLaunchResult,
            StartedBatchLaunchResult,
            BatchAccountLaunchResult>(
            preflight.Plans,
            (plan, index, token) => QueueBatchLaunchAsync(
                plan,
                Localize(
                    "Main.BatchPosition",
                    index + 1,
                    preflight.Plans.Count),
                token),
            async (queued, index, token) =>
            {
                await cleanupSettled;
                token.ThrowIfCancellationRequested();
                return queued.Queued is null
                    ? StartedBatchLaunchResult.Failed(
                        queued.Account,
                        queued.Failure ??
                        Localize(
                            "Main.BatchFailurePreparation",
                            queued.Account.Username))
                    : await StartQueuedBatchAccountAsync(
                        queued.Queued,
                        Localize(
                            "Main.BatchPosition",
                            index + 1,
                            preflight.Plans.Count),
                        token);
            },
            async (started, index, hasNext, token) =>
            {
                if (started.Started is not null)
                    await CompleteStartedBatchLaunchAsync(started.Started, token);
                var launched = started.Started is not null;
                if (launched && hasNext)
                {
                    var delaySeconds = (int)Math.Ceiling(delay.TotalSeconds);
                    SetStatus(
                        Localize(
                            "Main.BatchAccountStartedTitle",
                            index + 1,
                            preflight.Plans.Count,
                            started.Account.Username),
                        delaySeconds == 1
                            ? Localize("Main.BatchWaitDetailSecondOne")
                            : Localize(
                                "Main.BatchWaitDetailSecondMany",
                                delaySeconds),
                        Localize("Main.BatchWaitBadge"),
                        StatusTone.Neutral);
                    await Task.Delay(delay, token);
                }

                return launched
                    ? new BatchAccountLaunchResult(
                        started.Account.Key,
                        true,
                        null,
                        started.Started!.Identity)
                    : new BatchAccountLaunchResult(
                        started.Account.Key,
                        false,
                        started.Failure ??
                        Localize(
                            "Main.BatchFailureLaunch",
                            started.Account.Username),
                        Identity: null);
            },
            cancellationToken);

        var startedCount = outcomes.Count(outcome => outcome.Started);
        var launchedClients = outcomes
            .Where(outcome => outcome.Started && outcome.Identity is not null)
            .Select(outcome => new LaunchedBatchClient(
                outcome.AccountKey,
                outcome.Identity!))
            .ToArray();
        var postLaunch = startedCount == 0
            ? SessionPostLaunchResult.Completed
            : await ApplySessionPostLaunchAsync(
                sessionTemplate,
                launchedClients,
                cancellationToken,
                macroPlaybackCacheReservation);

        return new BatchLaunchResult(
            startedCount,
            launchPlans.Count,
            outcomes
                .Where(outcome => outcome.Failure is not null)
                .Select(outcome => new BatchFailure(
                    outcome.AccountKey,
                    outcome.Failure!))
                .ToArray(),
            ClientsWereClosed: true,
            Cancelled: false,
            AutomationWarning: postLaunch.Warning);
    }

    private async Task<BatchPreflightResult> PreflightBatchAccountsAsync(
        IReadOnlyList<BatchLaunchPlan> launchPlans,
        CancellationToken cancellationToken)
    {
        var unavailable = new List<BatchFailure>();
        var verifiedPlans = new List<VerifiedBatchLaunchPlan>(launchPlans.Count);
        for (var index = 0; index < launchPlans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = launchPlans[index];
            var account = _settings.Accounts.FirstOrDefault(candidate =>
                candidate.Key.Equals(
                    plan.Account.Key,
                    StringComparison.OrdinalIgnoreCase)) ?? plan.Account;
            SetStatus(
                Localize(
                    "Main.BatchCheckingAccountTitle",
                    index + 1,
                    launchPlans.Count),
                Localize(
                    "Main.BatchCheckingAccountDetail",
                    GetAccountDisplayName(account)),
                Localize("Main.BatchCheckBadge"),
                StatusTone.Neutral);
            try
            {
                var sessionToken = await ActivateBatchAccountAsync(
                    account.Key,
                    cancellationToken);
                if (sessionToken is null)
                {
                    unavailable.Add(new BatchFailure(
                        account.Key,
                        Localize(
                            "Main.BatchFailureSignInUnavailable",
                            account.Username)));
                    continue;
                }

                var currentAccount = _settings.Accounts.FirstOrDefault(candidate =>
                    candidate.Key.Equals(
                        account.Key,
                        StringComparison.OrdinalIgnoreCase));
                if (currentAccount is null ||
                    !IsCurrentWebSessionOwner(sessionToken.Value) ||
                    !TryGetCurrentWebSessionToken(
                        currentAccount,
                        out var currentToken) ||
                    currentToken != sessionToken.Value)
                {
                    unavailable.Add(new BatchFailure(
                        account.Key,
                        Localize(
                            "Main.BatchFailureSessionUnavailable",
                            plan.Account.Username)));
                    continue;
                }
                account = currentAccount;
                var activeSessionToken = sessionToken.Value;
                var target = plan.LaunchInput.Target;

                if (target.ShareCode is not null)
                {
                    if (!IsCurrentWebSessionOwner(activeSessionToken))
                    {
                        unavailable.Add(new BatchFailure(
                            account.Key,
                            Localize(
                                "Main.BatchFailureSessionUnavailable",
                                account.Username)));
                        continue;
                    }
                    var resolvedTarget = await _webSession.ResolvePrivateServerAsync(
                        target.ShareCode,
                        activeSessionToken,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentWebSessionOwner(activeSessionToken))
                    {
                        unavailable.Add(new BatchFailure(
                            account.Key,
                            Localize(
                                "Main.BatchFailureSessionUnavailable",
                                account.Username)));
                        continue;
                    }
                    if (resolvedTarget is null ||
                        plan.LaunchInput.TrackedPlaceId is not null &&
                        resolvedTarget.PlaceId != plan.LaunchInput.TrackedPlaceId)
                    {
                        unavailable.Add(new BatchFailure(
                            account.Key,
                            Localize(
                                "Main.BatchFailurePrivateServerUnavailable",
                                account.Username)));
                        continue;
                    }
                    target = resolvedTarget;
                }

                verifiedPlans.Add(new VerifiedBatchLaunchPlan(plan, target));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                WebSessionException.IsExpectedLifecycleFailure(ex))
            {
                Trace.WriteLine(
                    $"Batch preflight failed for one account: {ex.GetType().Name}.");
                unavailable.Add(new BatchFailure(
                    account.Key,
                    Localize(
                        "Main.BatchFailureAccountCheck",
                        account.Username)));
            }
        }
        return new BatchPreflightResult(verifiedPlans, unavailable);
    }

    private async Task<bool> RestoreBatchProfileAsync(
        AccountProfile? profile,
        CancellationToken cancellationToken)
    {
        if (profile is null)
            return true;

        SetStatus(
            Localize(
                "Main.BatchRestoringTitle",
                GetAccountDisplayName(profile)),
            Localize("Main.BatchRestoringDetail"),
            Localize("Main.BatchRestoreBadge"),
            StatusTone.Neutral);
        try
        {
            var restoredToken = await ActivateBatchAccountAsync(
                profile.Key,
                cancellationToken);
            var restoredProfile = _settings.Accounts.FirstOrDefault(account =>
                account.Key.Equals(
                    profile.Key,
                    StringComparison.OrdinalIgnoreCase));
            ShowDestinationForProfile(restoredProfile ?? profile);
            return restoredToken is not null && restoredProfile is not null;
        }
        catch (Exception ex) when (
            WebSessionException.IsExpectedLifecycleFailure(ex))
        {
            Trace.WriteLine(
                $"Batch account restore failed: {ex.GetType().Name}.");
            var restoredProfile = _settings.Accounts.FirstOrDefault(account =>
                account.Key.Equals(
                    profile.Key,
                    StringComparison.OrdinalIgnoreCase));
            ShowDestinationForProfile(restoredProfile ?? profile);
            return false;
        }
    }

    private void ShowBatchResult(
        BatchLaunchResult result,
        bool restoredOriginalProfile,
        IReadOnlyList<BatchLaunchPlan> launchPlans)
    {
        if (result.Cancelled)
        {
            ClearBatchRetryState();
            SetStatus(
                Localize("Main.BatchCancelledTitle"),
                restoredOriginalProfile
                    ? Localize("Main.BatchCancelledDetail")
                    : Localize("Main.BatchCancelledRestoreDetail"),
                Localize("Main.BatchCancelledBadge"),
                StatusTone.Warning);
            return;
        }

        if (result.MacroPreflightFailure is { } macroPreflightFailure)
        {
            ClearBatchRetryState();
            SetStatus(
                Localize("Macro.PreflightFailureTitle"),
                Localize(macroPreflightFailure ==
                    SessionTemplateMacroPreflightFailureKind.InvalidAssignment
                        ? "Macro.PreflightInvalidAssignmentDetail"
                        : "Macro.PreflightUnavailableDetail"),
                Localize("Main.BatchErrorBadge"),
                StatusTone.Error);
            return;
        }

        if (result.Failures.Count == 0)
        {
            ClearBatchRetryState();
            SetStatus(
                result.Started == 1
                    ? Localize("Main.BatchCompleteTitleOne")
                    : Localize(
                        "Main.BatchCompleteTitleMany",
                        result.Started),
                result.AutomationWarning ??
                    (restoredOriginalProfile
                        ? Localize("Main.BatchCompleteDetail")
                        : Localize("Main.BatchCompleteRestoreDetail")),
                result.AutomationWarning is null
                    ? Localize("Main.BatchCompleteBadge")
                    : Localize("Main.BatchPartialBadge"),
                result.AutomationWarning is null
                    ? StatusTone.Success
                    : StatusTone.Warning);
            return;
        }

        var title = result.ClientsWereClosed
            ? result.Started == 1
                ? Localize("Main.BatchPartialTitleOne", result.Total)
                : Localize(
                    "Main.BatchPartialTitleMany",
                    result.Started,
                    result.Total)
            : Localize("Main.BatchNotStartedTitle");
        SetBatchRetryState(result, launchPlans);
        var failureDetail = string.Join(
            "; ",
            result.Failures
                .Select(failure => failure.Message)
                .Distinct(StringComparer.Ordinal));
        SetStatus(
            title,
            result.Failures.Count == 1
                ? restoredOriginalProfile
                    ? Localize("Main.BatchFailureDetailOne", failureDetail)
                    : Localize(
                        "Main.BatchFailureRestoreDetailOne",
                        failureDetail)
                : restoredOriginalProfile
                    ? Localize("Main.BatchFailureDetailMany", failureDetail)
                    : Localize(
                        "Main.BatchFailureRestoreDetailMany",
                        failureDetail),
            result.Started > 0
                ? Localize("Main.BatchPartialBadge")
                : Localize("Main.BatchErrorBadge"),
            result.Started > 0 ? StatusTone.Warning : StatusTone.Error);
    }

    private void SetBatchRetryState(
        BatchLaunchResult result,
        IReadOnlyList<BatchLaunchPlan> launchPlans)
    {
        var accountKeys = BatchLaunchPreferences.GetRetryAccountKeys(
            result.Failures.Select(failure => failure.AccountKey),
            _settings.Accounts);
        if (accountKeys.Count == 0)
        {
            ClearBatchRetryState();
            return;
        }

        var failedKeys = accountKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var effectiveDestinations = launchPlans
            .Where(plan => failedKeys.Contains(plan.Account.Key))
            .GroupBy(
                plan => plan.Account.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().LaunchInput.AccountDestination,
                StringComparer.OrdinalIgnoreCase);
        _batchRetryState = new BatchRetryState(
            accountKeys,
            effectiveDestinations);
        RetryFailedBatchButton.Visibility = Visibility.Visible;
        RetryFailedBatchButton.IsEnabled =
            !_operationBusy &&
            !IsAutoJoinWatchActive &&
            !_accountReorderInProgress;
        UpdateBatchRetryButtonPresentation(accountKeys.Count);
    }

    private void RefreshBatchRetryState()
    {
        if (_batchRetryState is null)
        {
            RetryFailedBatchButton.Visibility = Visibility.Collapsed;
            RetryFailedBatchButton.IsEnabled = false;
            return;
        }

        var currentKeys = BatchLaunchPreferences.GetRetryAccountKeys(
            _batchRetryState.AccountKeys,
            _settings.Accounts);
        if (currentKeys.Count == 0)
        {
            ClearBatchRetryState();
            return;
        }

        var effectiveDestinations = _batchRetryState.EffectiveDestinations
            .Where(pair => currentKeys.Contains(
                pair.Key,
                StringComparer.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        _batchRetryState = new BatchRetryState(
            currentKeys,
            effectiveDestinations);
        RetryFailedBatchButton.Visibility = Visibility.Visible;
        RetryFailedBatchButton.IsEnabled =
            !_operationBusy &&
            !IsAutoJoinWatchActive &&
            !_accountReorderInProgress;
        UpdateBatchRetryButtonPresentation(currentKeys.Count);
    }

    private void UpdateBatchRetryButtonPresentation(int accountCount) =>
        System.Windows.Automation.AutomationProperties.SetHelpText(
            RetryFailedBatchButton,
            accountCount == 1
                ? Localize("Main.BatchRetryHelpOne")
                : Localize("Main.BatchRetryHelpMany", accountCount));

    private void ClearBatchRetryState()
    {
        _batchRetryState = null;
        RetryFailedBatchButton.Visibility = Visibility.Collapsed;
        RetryFailedBatchButton.IsEnabled = false;
    }

    private async Task<WebSessionToken?> ActivateBatchAccountAsync(
        string accountKey,
        CancellationToken cancellationToken)
    {
        var pageLoaded = new TaskCompletionSource<WebSessionToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedTokens = new HashSet<WebSessionToken>();
        WebSessionToken? expectedToken = null;
        void PageLoadedHandler(object? sender, WebSessionEventArgs args)
        {
            if (!args.Token.AccountKey.Equals(
                    accountKey,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsCurrentWebSessionOwner(args.Token))
            {
                return;
            }

            if (expectedToken is { } token)
            {
                if (args.Token == token)
                    pageLoaded.TrySetResult(args.Token);
                return;
            }

            observedTokens.Add(args.Token);
        }

        _webSession.RobloxPageLoaded += PageLoadedHandler;
        var account = _settings.Accounts.FirstOrDefault(candidate =>
            candidate.Key.Equals(
                accountKey,
                StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            _webSession.RobloxPageLoaded -= PageLoadedHandler;
            return null;
        }

        try
        {
            // Batch switches are temporary. Keeping ActiveAccountKey unchanged
            // avoids redundant disk writes and preserves the user's selected
            // account even if the app exits during a batch.
            _activeProfile = account;
            _pendingProfile = null;
            _currentUser = null;

            cancellationToken.ThrowIfCancellationRequested();
            // This activation performs an awaited verification below. Skip only
            // its first automatic page-load check so the same Roblox request is
            // not serialized twice; later navigations remain observable.
            using var automaticCheckSuppression =
                _accountVerificationGate.SuppressNextAutomaticVerification(
                    account.Key);
            var initialization = InitializeBrowserAsync(
                account,
                showLogin: false,
                cancellationToken);
            if (!TryGetAffineWebSessionToken(account, out var sessionToken))
            {
                await initialization;
                return null;
            }
            expectedToken = sessionToken;
            if (observedTokens.Contains(sessionToken) &&
                IsCurrentWebSessionOwner(sessionToken))
            {
                pageLoaded.TrySetResult(sessionToken);
            }

            if (!await initialization ||
                !IsCurrentWebSessionOwner(sessionToken))
            {
                return null;
            }

            var sessionEnded = _webSession.GetSessionEndedTask(sessionToken);
            if (!await RobloxWebSessionService.WaitForSessionWorkAsync(
                    pageLoaded.Task,
                    sessionEnded,
                    TimeSpan.FromSeconds(20),
                    cancellationToken))
            {
                return null;
            }
            var loadedToken = await pageLoaded.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (loadedToken != sessionToken ||
                !IsCurrentWebSessionOwner(sessionToken))
            {
                return null;
            }

            await CheckAuthenticatedAccountAsync(
                sessionToken,
                skipIfBusy: false,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return IsCurrentWebSessionOwner(sessionToken) &&
                   _activeProfile?.Key.Equals(
                       account.Key,
                       StringComparison.OrdinalIgnoreCase) == true &&
                   _currentUser?.Id == account.UserId
                ? sessionToken
                : null;
        }
        catch (TimeoutException)
        {
            SetStatus(
                account is null
                    ? Localize("Main.BatchAccountLoadFailureTitle")
                    : Localize(
                        "Main.BatchNamedAccountLoadFailureTitle",
                        account.Username),
                Localize("Main.BatchAccountLoadTimeoutDetail"),
                Localize("Main.BatchAccountErrorBadge"),
                StatusTone.Error);
            return null;
        }
        finally
        {
            _webSession.RobloxPageLoaded -= PageLoadedHandler;
        }
    }

    private async Task<QueuedBatchLaunchResult> QueueBatchLaunchAsync(
        VerifiedBatchLaunchPlan verifiedPlan,
        string position,
        CancellationToken cancellationToken)
    {
        var plan = verifiedPlan.Plan;
        var account = _settings.Accounts.FirstOrDefault(candidate =>
            candidate.Key.Equals(
                plan.Account.Key,
                StringComparison.OrdinalIgnoreCase)) ?? plan.Account;
        SetStatus(
            Localize(
                "Main.BatchQueueingTitle",
                position,
                GetAccountDisplayName(account)),
            Localize("Main.BatchQueueingSessionDetail"),
            Localize("Main.BatchQueueBadge"),
            StatusTone.Neutral);

        try
        {
            var sessionToken = await ActivateBatchAccountAsync(
                account.Key,
                cancellationToken);
            if (sessionToken is null)
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSignInUnavailable",
                        account.Username));
            }

            var currentAccount = _settings.Accounts.FirstOrDefault(candidate =>
                candidate.Key.Equals(
                    account.Key,
                    StringComparison.OrdinalIgnoreCase));
            if (currentAccount is null ||
                !IsCurrentWebSessionOwner(sessionToken.Value) ||
                !TryGetCurrentWebSessionToken(
                    currentAccount,
                    out var currentToken) ||
                currentToken != sessionToken.Value)
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSessionUnavailable",
                        account.Username));
            }
            account = currentAccount;

            var target = verifiedPlan.Target;
            if (plan.LaunchInput.TrackedPlaceId is not null &&
                target.PlaceId != plan.LaunchInput.TrackedPlaceId)
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureTrackedServerChanged",
                        account.Username));
            }

            SetStatus(
                Localize(
                    "Main.BatchQueueingUsernameTitle",
                    position,
                    account.Username),
                Localize("Main.BatchPreparingDestinationDetail"),
                Localize("Main.BatchQueueBadge"),
                StatusTone.Neutral);
            if (!IsCurrentWebSessionOwner(sessionToken.Value))
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSessionUnavailable",
                        account.Username));
            }

            var nameTask = _webSession.GetExperienceNameAsync(
                target.PlaceId,
                sessionToken.Value,
                cancellationToken);
            var localeTask = _webSession.GetUserLocaleAsync(
                sessionToken.Value,
                cancellationToken);
            await Task.WhenAll(nameTask, localeTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(sessionToken.Value))
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSessionUnavailable",
                        account.Username));
            }

            var queued = new QueuedBatchLaunch(
                account,
                sessionToken.Value,
                plan.LaunchInput.Destination,
                target,
                plan.LaunchInput.ServerJobId,
                await nameTask,
                await localeTask);
            return new QueuedBatchLaunchResult(account, queued, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            WebSessionException.IsExpectedLifecycleFailure(ex))
        {
            Trace.WriteLine(
                $"Batch queueing failed for one account: {ex.GetType().Name}.");
            return QueuedBatchLaunchResult.Failed(
                account,
                Localize(
                    "Main.BatchFailurePreparation",
                    account.Username));
        }
    }

    private async Task<StartedBatchLaunchResult> StartQueuedBatchAccountAsync(
        QueuedBatchLaunch queued,
        string position,
        CancellationToken cancellationToken)
    {
        _launchInProgress = true;
        var account = queued.Account;
        var handedOffToCompletion = false;

        try
        {
            if (!IsCurrentWebSessionOwner(queued.SessionToken) ||
                !TryGetCurrentWebSessionToken(account, out var currentToken) ||
                currentToken != queued.SessionToken)
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSessionUnavailable",
                        account.Username));
            }

            SetStatus(
                Localize(
                    "Main.BatchPreparingAccountTitle",
                    position,
                    account.Username),
                Localize("Main.BatchRequestingTicketDetail"),
                Localize("Main.BatchTicketBadge"),
                StatusTone.Neutral);
            var ticket = await _webSession.GetAuthenticationTicketAsync(
                queued.SessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(queued.SessionToken))
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureSessionUnavailable",
                        account.Username));
            }
            if (string.IsNullOrWhiteSpace(ticket))
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureTicketUnavailable",
                        account.Username));
            }

            var recent = new RecentExperience
            {
                Destination = queued.Destination,
                PlaceId = queued.Target.PlaceId,
                Name = queued.ExperienceName,
                IsPrivateServer = queued.Target.IsPrivateServer,
                ServerJobId = queued.ServerJobId,
                AccountUserId = account.UserId,
                AccountUsername = account.Username,
                LastLaunchedAt = DateTimeOffset.UtcNow
            };
            SetStatus(
                Localize(
                    "Main.BatchLaunchingAccountTitle",
                    position,
                    account.Username),
                Localize("Main.BatchLaunchingDetail"),
                Localize("Main.BatchLaunchBadge"),
                StatusTone.Neutral);
            var launchStartedAt = DateTimeOffset.UtcNow;
            var result = await _robloxClient.LaunchAsync(
                RobloxLaunchUriBuilder.Build(
                    queued.Target,
                    ticket,
                    queued.ServerJobId,
                    queued.Locale),
                cancellationToken);
            TrackLaunchedClient(result.PlayerIdentity, account, recent);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not { Success: true, ProcessId: int processId })
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    Localize(
                        "Main.BatchFailureLaunch",
                        account.Username));
            }

            handedOffToCompletion = true;
            return new StartedBatchLaunchResult(
                account,
                new StartedBatchLaunch(
                    account,
                    recent,
                    processId,
                    result.PlayerIdentity,
                    launchStartedAt,
                    position),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            WebSessionException.IsExpectedLifecycleFailure(ex))
        {
            Trace.WriteLine(
                $"Batch ticket request failed for one account: {ex.GetType().Name}.");
            return StartedBatchLaunchResult.Failed(
                account,
                Localize(
                    "Main.BatchFailurePreparation",
                    account.Username));
        }
        finally
        {
            if (!handedOffToCompletion)
                _launchInProgress = false;
        }
    }

    private async Task CompleteStartedBatchLaunchAsync(
        StartedBatchLaunch started,
        CancellationToken cancellationToken)
    {
        try
        {
            await SaveRecentExperienceAsync(started.Recent);
            if (_settings.RecentExperiences.Contains(started.Recent))
            {
                BeginServerTracking(
                    started.Recent,
                    started.LaunchStartedAt);
            }
            SetStatus(
                Localize(
                    "Main.BatchHookTitle",
                    started.Position,
                    started.Account.Username),
                Localize("Main.BatchHookDetail"),
                Localize("Main.BatchHookBadge"),
                StatusTone.Neutral);
            await NotifyLaunchHookAsync(
                started.Recent,
                started.ProcessId,
                started.Account.Label,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _launchInProgress = false;
        }
    }

    private static string GetAccountDisplayName(AccountProfile account) =>
        account.Label is null
            ? $"@{account.Username}"
            : $"{account.Label} (@{account.Username})";

    private sealed record BatchPreflightResult(
        IReadOnlyList<VerifiedBatchLaunchPlan> Plans,
        IReadOnlyList<BatchFailure> Failures);

    private sealed record BatchFailure(
        string AccountKey,
        string Message);

    private sealed record VerifiedBatchLaunchPlan(
        BatchLaunchPlan Plan,
        LaunchTarget Target);

    private sealed record QueuedBatchLaunch(
        AccountProfile Account,
        WebSessionToken SessionToken,
        string Destination,
        LaunchTarget Target,
        string? ServerJobId,
        string? ExperienceName,
        string? Locale);

    private sealed record QueuedBatchLaunchResult(
        AccountProfile Account,
        QueuedBatchLaunch? Queued,
        string? Failure)
    {
        internal static QueuedBatchLaunchResult Failed(
            AccountProfile account,
            string failure) =>
            new(account, null, failure);
    }

    private sealed record StartedBatchLaunch(
        AccountProfile Account,
        RecentExperience Recent,
        int ProcessId,
        RobloxClientProcessIdentity? Identity,
        DateTimeOffset LaunchStartedAt,
        string Position);

    private sealed record StartedBatchLaunchResult(
        AccountProfile Account,
        StartedBatchLaunch? Started,
        string? Failure)
    {
        internal static StartedBatchLaunchResult Failed(
            AccountProfile account,
            string failure) =>
            new(account, null, failure);
    }

    private sealed record BatchAccountLaunchResult(
        string AccountKey,
        bool Started,
        string? Failure,
        RobloxClientProcessIdentity? Identity);

    private sealed record BatchLaunchResult(
        int Started,
        int Total,
        IReadOnlyList<BatchFailure> Failures,
        bool ClientsWereClosed,
        bool Cancelled,
        string? AutomationWarning,
        SessionTemplateMacroPreflightFailureKind? MacroPreflightFailure = null)
    {
        public static BatchLaunchResult CancelledResult(int total) =>
            new(
                0,
                total,
                [],
                ClientsWereClosed: false,
                Cancelled: true,
                AutomationWarning: null);
    }

    private sealed record BatchRetryState(
        IReadOnlyList<string> AccountKeys,
        IReadOnlyDictionary<string, string> EffectiveDestinations);
}
