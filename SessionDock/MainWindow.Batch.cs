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
                "No failed accounts remain",
                "The accounts from that batch are no longer saved, so there is nothing left to retry.",
                "RETRY CLEARED");
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
                    "Batch preferences could not be saved",
                    "BATCH SETTINGS ERROR",
                    "SessionDock could not confirm the preset or delay update, so it was rolled back. Make %LOCALAPPDATA%\\SessionDock writable, then retry."))
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
                "Batch destinations are not ready",
                planningError,
                "INVALID DESTINATION");
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
        CancelBatchButton.Visibility = Visibility.Visible;
        CancelBatchButton.IsEnabled = true;
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
            CancelBatchButton.IsEnabled = false;
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
                CancelBatchButton.Visibility = Visibility.Collapsed;
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

    private IReadOnlyList<AccountProfile> CreateRetryAccounts(
        BatchRetryState retryState)
    {
        var accounts = BatchLaunchPreferences.ResolveAccounts(
            retryState.AccountKeys,
            _settings.Accounts);
        return accounts.Select(account =>
        {
            var retryAccount = AppSettingsSnapshot.Clone(account);
            if (string.IsNullOrWhiteSpace(retryAccount.Destination) &&
                retryState.EffectiveDestinations.TryGetValue(
                    retryAccount.Key,
                    out var effectiveDestination))
            {
                retryAccount.Destination = effectiveDestination;
            }
            return retryAccount;
        }).ToArray();
    }

    private void CancelBatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchCancellation is null || _batchCancellation.IsCancellationRequested)
            return;

        CancelBatchButton.IsEnabled = false;
        SetStatus(
            "Cancelling batch launch",
            "SessionDock will stop before the next safe step. Clients already started remain open.",
            "BATCH CANCELLING");
        _batchCancellation.Cancel();
    }

    private async Task<BatchLaunchResult> RunBatchLaunchAsync(
        IReadOnlyList<BatchLaunchPlan> launchPlans,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
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
                Cancelled: false);
        }

        SetStatus(
            "Preparing batch launch",
            "Closing every running, verified Roblox Player instance…",
            "BATCH CLEANUP");
        RobloxClientService.ClosePlayersResult closeResult;
        try
        {
            closeResult = await _robloxClient.CloseAllPlayersAsync(
                cancellationToken);
            if (closeResult.Success)
                _runningClients.Clear();
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
                "Batch launch stopped",
                "SessionDock could not verify that all current clients were closed.",
                "BATCH ERROR");
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                launchPlans.Select(plan => new BatchFailure(
                    plan.Account.Key,
                    "Existing Roblox clients could not be verified as closed"))
                    .ToArray(),
                ClientsWereClosed: false,
                Cancelled: false);
        }

        if (!closeResult.Success)
        {
            var detail = closeResult.Unverified > 0
                ? $"{closeResult.Unverified} Roblox-named process(es) could not be safely verified and were left running."
                : $"{closeResult.Remaining} verified Roblox Player instance(s) could not be closed.";
            SetStatus(
                "Batch launch stopped",
                detail,
                "BATCH ERROR");
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                launchPlans.Select(plan => new BatchFailure(
                    plan.Account.Key,
                    detail))
                    .ToArray(),
                ClientsWereClosed: false,
                Cancelled: false);
        }

        Task cleanupSettled = Task.CompletedTask;
        if (closeResult.Closed > 0)
        {
            SetStatus(
                $"Closed {closeResult.Closed} Roblox Player instance(s)",
                "Roblox cleanup will settle while SessionDock prepares the first queued launch…",
                "BATCH CLEANUP");
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
                $"{index + 1} of {preflight.Plans.Count}",
                token),
            async (queued, index, token) =>
            {
                await cleanupSettled;
                token.ThrowIfCancellationRequested();
                return queued.Queued is null
                    ? StartedBatchLaunchResult.Failed(
                        queued.Account,
                        queued.Failure ??
                        $"@{queued.Account.Username}: launch preparation failed")
                    : await StartQueuedBatchAccountAsync(
                        queued.Queued,
                        $"{index + 1} of {preflight.Plans.Count}",
                        token);
            },
            async (started, index, hasNext, token) =>
            {
                if (started.Started is not null)
                    await CompleteStartedBatchLaunchAsync(started.Started, token);
                var launched = started.Started is not null;
                if (launched && hasNext)
                {
                    SetStatus(
                        $"Batch {index + 1} of {preflight.Plans.Count}: @{started.Account.Username} started",
                        $"The next account is queued. Waiting {delay.TotalSeconds:0} seconds so Roblox and local hooks can settle…",
                        "BATCH WAIT");
                    await Task.Delay(delay, token);
                }

                return launched
                    ? new BatchAccountLaunchResult(
                        started.Account.Key,
                        true,
                        null)
                    : new BatchAccountLaunchResult(
                        started.Account.Key,
                        false,
                        started.Failure ??
                        $"@{started.Account.Username}: launch failed");
            },
            cancellationToken);

        return new BatchLaunchResult(
            outcomes.Count(outcome => outcome.Started),
            launchPlans.Count,
            outcomes
                .Where(outcome => outcome.Failure is not null)
                .Select(outcome => new BatchFailure(
                    outcome.AccountKey,
                    outcome.Failure!))
                .ToArray(),
            ClientsWereClosed: true,
            Cancelled: false);
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
                $"Checking account {index + 1} of {launchPlans.Count}",
                $"Verifying {GetAccountDisplayName(account)} before any running client is closed…",
                "BATCH CHECK");
            try
            {
                var sessionToken = await ActivateBatchAccountAsync(
                    account.Key,
                    cancellationToken);
                if (sessionToken is null)
                {
                    unavailable.Add(new BatchFailure(
                        account.Key,
                        $"@{account.Username}: sign-in unavailable"));
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
                        $"@{plan.Account.Username}: account session unavailable"));
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
                            $"@{account.Username}: account session unavailable"));
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
                            $"@{account.Username}: account session unavailable"));
                        continue;
                    }
                    if (resolvedTarget is null ||
                        plan.LaunchInput.TrackedPlaceId is not null &&
                        resolvedTarget.PlaceId != plan.LaunchInput.TrackedPlaceId)
                    {
                        unavailable.Add(new BatchFailure(
                            account.Key,
                            $"@{account.Username}: private server unavailable"));
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
                    $"@{account.Username}: account check failed"));
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
            $"Restoring {GetAccountDisplayName(profile)}",
            "Returning the launcher to the account that was selected before the batch…",
            "BATCH RESTORE");
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
        var restoreDetail = restoredOriginalProfile
            ? string.Empty
            : " The original account needs to be signed in again.";
        if (result.Cancelled)
        {
            ClearBatchRetryState();
            SetStatus(
                "Batch launch cancelled",
                $"No further accounts will start. Started clients remain open; clients already closed during cleanup cannot be restored.{restoreDetail}",
                "BATCH CANCELLED");
            return;
        }

        if (result.Failures.Count == 0)
        {
            ClearBatchRetryState();
            SetStatus(
                $"Batch complete: {result.Started} clients started",
                $"Every selected account received its own launch ticket.{restoreDetail}",
                "BATCH COMPLETE");
            return;
        }

        var title = result.ClientsWereClosed
            ? $"Batch complete: {result.Started} of {result.Total} started"
            : "Batch not started";
        SetBatchRetryState(result, launchPlans);
        var failureDetail = string.Join(
            "; ",
            result.Failures
                .Select(failure => failure.Message)
                .Distinct(StringComparer.Ordinal));
        SetStatus(
            title,
            $"{failureDetail}.{restoreDetail} Choose Retry failed only to review just the failed account(s); retrying will close currently running Roblox clients.".Trim(),
            result.Started > 0 ? "BATCH PARTIAL" : "BATCH ERROR");
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
        System.Windows.Automation.AutomationProperties.SetHelpText(
            RetryFailedBatchButton,
            $"Review and retry {accountKeys.Count} failed account(s). Starting the retry closes currently running verified Roblox Player clients.");
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
    }

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
                    ? "Could not load the selected account"
                    : $"Could not load @{account.Username}",
                "Its Roblox session did not become ready within 20 seconds.",
                "BATCH ACCOUNT ERROR");
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
            $"Queueing batch {position}: {GetAccountDisplayName(account)}",
            "Loading this account's isolated session while the current launch settles…",
            "BATCH QUEUE");

        try
        {
            var sessionToken = await ActivateBatchAccountAsync(
                account.Key,
                cancellationToken);
            if (sessionToken is null)
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    $"@{account.Username}: sign-in unavailable");
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
                    $"@{account.Username}: account session unavailable");
            }
            account = currentAccount;

            var target = verifiedPlan.Target;
            if (plan.LaunchInput.TrackedPlaceId is not null &&
                target.PlaceId != plan.LaunchInput.TrackedPlaceId)
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    $"@{account.Username}: tracked server changed");
            }

            SetStatus(
                $"Queueing batch {position}: @{account.Username}",
                "Preparing destination details. Its launch ticket will be requested only when Roblox is ready to start.",
                "BATCH QUEUE");
            if (!IsCurrentWebSessionOwner(sessionToken.Value))
            {
                return QueuedBatchLaunchResult.Failed(
                    account,
                    $"@{account.Username}: account session unavailable");
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
                    $"@{account.Username}: account session unavailable");
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
                $"@{account.Username}: launch preparation failed");
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
                    $"@{account.Username}: account session unavailable");
            }

            SetStatus(
                $"Batch {position}: preparing @{account.Username}",
                "Requesting a fresh secure Roblox game-client ticket…",
                "BATCH TICKET");
            var ticket = await _webSession.GetAuthenticationTicketAsync(
                queued.SessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(queued.SessionToken))
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    $"@{account.Username}: account session unavailable");
            }
            if (string.IsNullOrWhiteSpace(ticket))
            {
                return StartedBatchLaunchResult.Failed(
                    account,
                    $"@{account.Username}: launch ticket unavailable");
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
                $"Batch {position}: launching @{account.Username}",
                "Handing this fresh account ticket directly to Roblox Player…",
                "BATCH LAUNCH");
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
                    $"@{account.Username}: launch failed");
            }

            handedOffToCompletion = true;
            return new StartedBatchLaunchResult(
                account,
                new StartedBatchLaunch(
                    account,
                    recent,
                    processId,
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
                $"@{account.Username}: launch preparation failed");
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
                $"Batch {started.Position}: @{started.Account.Username} started",
                "Waiting for optional local launch hooks to finish while the next account is prepared…",
                "BATCH HOOK");
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
        string? Failure);

    private sealed record BatchLaunchResult(
        int Started,
        int Total,
        IReadOnlyList<BatchFailure> Failures,
        bool ClientsWereClosed,
        bool Cancelled)
    {
        public static BatchLaunchResult CancelledResult(int total) =>
            new(0, total, [], ClientsWereClosed: false, Cancelled: true);
    }

    private sealed record BatchRetryState(
        IReadOnlyList<string> AccountKeys,
        IReadOnlyDictionary<string, string> EffectiveDestinations);
}
