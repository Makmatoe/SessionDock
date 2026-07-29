using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private CancellationTokenSource? _autoJoinWatchCancellation;
    private AutoJoinWatchSnapshot? _autoJoinWatch;
    private AutoJoinLaunchGate? _autoJoinLaunchGate;
    private long _autoJoinWatchEpoch;
    private string? _autoJoinLastAnnouncedState;
    private bool _autoJoinStopRequested;
    private bool _autoJoinReconnectFocusRequested;

    private bool IsAutoJoinWatchActive => _autoJoinWatch is not null;

    private async Task StartAutoJoinWatchAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await PrepareAutoJoinWatchAsync(cancellationToken);
        if (snapshot is null || _autoJoinWatchCancellation is not { } watchCts)
            return;
        LaunchButton.Focus();
        using var watchDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(watchCts.Token);
        watchDeadline.CancelAfter(JoinUserWatchPolicy.MaximumWatchDuration);

        JoinUserResolution? resolution = null;
        var explicitStop = false;
        var launchReserved = false;
        var canTrigger = false;
        try
        {
            resolution = await WaitForJoinableUserAsync(
                snapshot,
                watchDeadline.Token);
            if (resolution is not null)
            {
                launchReserved = await ReserveAutoJoinLaunchAsync(
                    snapshot,
                    watchDeadline.Token);
            }
        }
        catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
        {
            explicitStop = _autoJoinStopRequested;
        }
        catch (OperationCanceledException) when (
            watchDeadline.IsCancellationRequested)
        {
            ShowAutoJoinExpired(snapshot);
        }
        finally
        {
            canTrigger = resolution is not null &&
                         launchReserved &&
                         !watchCts.IsCancellationRequested &&
                         !watchDeadline.IsCancellationRequested &&
                         IsAutoJoinWatchCurrent(snapshot);
            CompleteAutoJoinWatch(snapshot.Epoch, watchCts);
            if (launchReserved && !canTrigger &&
                !_operationLifetime.IsShuttingDown)
            {
                SetOperationBusy(false);
            }
        }

        if (explicitStop && !_operationLifetime.IsShuttingDown)
        {
            SetStatus(
                "Auto-join stopped",
                "No Roblox Player launch was attempted.",
                "WATCH STOPPED");
        }
        if (!canTrigger || resolution is null)
            return;

        SetStatus(
            $"Found @{resolution.Username}'s current Roblox server",
            $"The watch stopped. Rechecking @{snapshot.AccountUsername} and preparing one Roblox Player launch. Player will make the final access check.",
            "SERVER FOUND");
        await LaunchButtonClickAsync(
            cancellationToken,
            resolution,
            snapshot.Context.AccountKey,
            snapshot.Context.AccountUserId,
            operationReserved: true);
    }

    private async Task<AutoJoinWatchSnapshot?> PrepareAutoJoinWatchAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy || _launchInProgress || IsAutoJoinWatchActive ||
            !_joinUserMode || AutoJoinUserCheckBox.IsChecked != true)
        {
            return null;
        }

        SetOperationBusy(true);
        try
        {
            var requestedText = PlaceIdBox.Text.Trim();
            if (!JoinUserDestination.TryParseInput(
                    requestedText,
                    out var requestedUser,
                    out var error))
            {
                SetStatus("User is not valid", error, "INVALID USER");
                return null;
            }
            if (!await FlushDestinationPersistenceAsync())
                return null;

            await CheckAuthenticatedAccountAsync(
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var currentUser = _currentUser;
            var profile = _activeProfile;
            if (currentUser is null || profile is null ||
                currentUser.Id != profile.UserId ||
                !TryGetCurrentWebSessionToken(profile, out var sessionToken))
            {
                return null;
            }

            var watchCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var snapshot = new AutoJoinWatchSnapshot(
                ++_autoJoinWatchEpoch,
                new AutoJoinWatchContext(
                    profile.Key,
                    currentUser.Id,
                    requestedText),
                currentUser.Name,
                sessionToken,
                requestedUser!,
                null);
            _autoJoinWatchCancellation = watchCts;
            _autoJoinWatch = snapshot;
            _autoJoinLaunchGate = new AutoJoinLaunchGate();
            _autoJoinLastAnnouncedState = null;
            _autoJoinStopRequested = false;
            _autoJoinReconnectFocusRequested = false;
            UpdateAutoJoinActionPresentation();
            AnnounceAutoJoinState(
                "armed",
                $"Watching {requestedUser!.DisplayValue}",
                $"Using @{currentUser.Name}'s Roblox session. SessionDock will check now, then usually about every 30 seconds, slowing down when Roblox asks or a check fails. Player starts once Roblox reports a usable current server and makes the final access check. The watch expires after four hours.",
                "WATCHING");
            return snapshot;
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private async Task<JoinUserResolution?> WaitForJoinableUserAsync(
        AutoJoinWatchSnapshot initialSnapshot,
        CancellationToken cancellationToken)
    {
        var policy = new JoinUserWatchPolicy();
        var startedAt = Stopwatch.GetTimestamp();
        var snapshot = initialSnapshot;
        JoinUserIdentity? identity = null;

        while (identity is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAutoJoinWatchCurrent(snapshot))
                return null;
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (policy.HasExpired(elapsed))
            {
                ShowAutoJoinExpired(snapshot);
                return null;
            }

            var lookup = await _webSession.ResolveJoinUserIdentityAsync(
                snapshot.RequestedUser,
                snapshot.SessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAutoJoinWatchCurrent(snapshot))
                return null;
            var decision = policy.ObserveIdentity(
                lookup,
                Stopwatch.GetElapsedTime(startedAt),
                Random.Shared.NextDouble());
            switch (decision.Action)
            {
                case JoinUserWatchAction.IdentityReady:
                    identity = lookup.Identity;
                    break;
                case JoinUserWatchAction.StopUserNotFound:
                    SetStatus(
                        "Roblox user was not found",
                        "The auto-join watch ended. Check the exact username, user ID, or profile URL before trying again.",
                        "USER NOT FOUND");
                    return null;
                case JoinUserWatchAction.StopSessionUnavailable:
                    ShowAutoJoinSessionUnavailable(snapshot);
                    return null;
                case JoinUserWatchAction.Expired:
                    ShowAutoJoinExpired(snapshot);
                    return null;
                default:
                    var identityDelay = policy.BoundDelayToExpiry(
                        decision.Delay,
                        Stopwatch.GetElapsedTime(startedAt));
                    if (identityDelay <= TimeSpan.Zero)
                    {
                        ShowAutoJoinExpired(snapshot);
                        return null;
                    }
                    ShowAutoJoinIdentityRetry(
                        snapshot,
                        lookup.Availability,
                        identityDelay);
                    await Task.Delay(identityDelay, cancellationToken);
                    break;
            }
        }

        if (identity.UserId == snapshot.Context.AccountUserId)
        {
            SetStatus(
                "Choose another Roblox user",
                "Auto-join cannot watch the same user as the selected account.",
                "INVALID USER");
            return null;
        }

        snapshot = snapshot with
        {
            Identity = identity,
            RequestedUser = new JoinUserIdentifier(
                identity.UserId,
                null,
                $"@{identity.Username}")
        };
        if (!ReplaceAutoJoinWatch(snapshot))
            return null;
        policy = new JoinUserWatchPolicy();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAutoJoinWatchCurrent(snapshot))
                return null;
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (policy.HasExpired(elapsed))
            {
                ShowAutoJoinExpired(snapshot);
                return null;
            }

            var lookup = await _webSession.GetJoinUserPresenceAsync(
                identity,
                snapshot.SessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAutoJoinWatchCurrent(snapshot))
                return null;
            var decision = policy.ObservePresence(
                lookup,
                Stopwatch.GetElapsedTime(startedAt),
                Random.Shared.NextDouble());
            switch (decision.Action)
            {
                case JoinUserWatchAction.Join:
                    return lookup.Resolution;
                case JoinUserWatchAction.StopUserNotFound:
                    SetStatus(
                        "Roblox user is no longer available",
                        "The auto-join watch ended without launching Player.",
                        "WATCH ENDED");
                    return null;
                case JoinUserWatchAction.StopSessionUnavailable:
                    ShowAutoJoinSessionUnavailable(snapshot);
                    return null;
                case JoinUserWatchAction.Expired:
                    ShowAutoJoinExpired(snapshot);
                    return null;
                default:
                    var presenceDelay = policy.BoundDelayToExpiry(
                        decision.Delay,
                        Stopwatch.GetElapsedTime(startedAt));
                    if (presenceDelay <= TimeSpan.Zero)
                    {
                        ShowAutoJoinExpired(snapshot);
                        return null;
                    }
                    ShowAutoJoinWaitingState(
                        snapshot,
                        lookup.Availability,
                        presenceDelay);
                    await Task.Delay(presenceDelay, cancellationToken);
                    break;
            }
        }
    }

    private void ShowAutoJoinIdentityRetry(
        AutoJoinWatchSnapshot snapshot,
        JoinUserIdentityAvailability availability,
        TimeSpan delay)
    {
        var rateLimited = availability ==
            JoinUserIdentityAvailability.RateLimited;
        AnnounceAutoJoinState(
            rateLimited ? "identity-rate-limit" : "identity-service",
            $"Roblox could not check {snapshot.RequestedUser.DisplayValue}",
            rateLimited
                ? $"The watch is still active. Roblox requested a slower rate, so SessionDock will retry in about {FormatDelay(delay)}."
                : $"The watch is still active. Roblox did not return a usable answer, so SessionDock will retry in about {FormatDelay(delay)}.",
            "WATCHING");
    }

    private void ShowAutoJoinWaitingState(
        AutoJoinWatchSnapshot snapshot,
        JoinUserAvailability availability,
        TimeSpan delay)
    {
        var target = snapshot.Identity is { } identity
            ? $"@{identity.Username}"
            : snapshot.RequestedUser.DisplayValue;
        var (state, title, detail) = availability switch
        {
            JoinUserAvailability.Offline => (
                "offline",
                $"Waiting for {target}",
                $"They are not joinable to @{snapshot.AccountUsername} right now. They may be offline, hiding activity, or unavailable. Checking again in about {FormatDelay(delay)}."),
            JoinUserAvailability.NotInExperience => (
                "not-in-experience",
                $"{target} is not in a Roblox experience yet",
                $"Still watching with @{snapshot.AccountUsername}. Checking again in about {FormatDelay(delay)}."),
            JoinUserAvailability.NotJoinable => (
                "not-joinable",
                $"{target} does not have a usable current server",
                $"Privacy, experience, or server details may be unavailable. Checking again in about {FormatDelay(delay)}."),
            JoinUserAvailability.RateLimited => (
                "presence-rate-limit",
                $"Roblox asked SessionDock to check less often",
                $"The watch is still active and will retry in about {FormatDelay(delay)}. No launch was attempted."),
            _ => (
                "presence-service",
                $"Roblox could not check {target}",
                $"The watch is still active. SessionDock will retry in about {FormatDelay(delay)}; no launch was attempted.")
        };
        AnnounceAutoJoinState(state, title, detail, "WATCHING");
    }

    private void ShowAutoJoinExpired(AutoJoinWatchSnapshot snapshot) =>
        SetStatus(
            "Auto-join watch expired",
            $"SessionDock stopped watching {snapshot.RequestedUser.DisplayValue} after four hours. Start a new watch if you still want to join automatically.",
            "WATCH EXPIRED");

    private void ShowAutoJoinSessionUnavailable(
        AutoJoinWatchSnapshot snapshot)
    {
        _currentUser = null;
        SetSignedOutState();
        SignInButtonLabel.Text = Localize("Main.Reconnect");
        AutomationProperties.SetName(
            SignInButton,
            Localize("Main.ReconnectName"));
        _autoJoinReconnectFocusRequested = true;
        SetStatus(
            "Auto-join watch ended",
            $"@{snapshot.AccountUsername}'s Roblox session is no longer available. Reconnect that account before starting another watch.",
            "SIGN-IN NEEDED");
    }

    private void AnnounceAutoJoinState(
        string state,
        string title,
        string detail,
        string badge)
    {
        AutoJoinWatchDetailText.Text = detail;
        if (string.Equals(
                _autoJoinLastAnnouncedState,
                state,
                StringComparison.Ordinal))
        {
            StatusDetail.Text = detail;
            return;
        }

        _autoJoinLastAnnouncedState = state;
        SetStatus(title, detail, badge);
    }

    private void AutoJoinUserCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (IsAutoJoinWatchActive)
            return;
        UpdateAutoJoinActionPresentation();
        RefreshLaunchAvailability();
    }

    private void RequestAutoJoinStop()
    {
        var cancellation = _autoJoinWatchCancellation;
        if (_autoJoinWatch is null || cancellation is null ||
            cancellation.IsCancellationRequested)
        {
            return;
        }

        _autoJoinLaunchGate?.TryStop();
        _autoJoinStopRequested = true;
        LaunchButtonLabel.Text = Localize("Main.AutoJoinStopping");
        LaunchButton.IsEnabled = false;
        AutomationProperties.SetName(
            LaunchButton,
            Localize("Main.AutoJoinStoppingName"));
        SetStatus(
            "Stopping auto-join",
            "SessionDock is cancelling the current check. No new check or launch will start.",
            "STOPPING WATCH");
        cancellation.Cancel();
    }

    private void CancelAutoJoinWatchSilently()
    {
        _autoJoinLaunchGate?.TryStop();
        _autoJoinStopRequested = false;
        try
        {
            _autoJoinWatchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning watcher completed between the state check and cancel.
        }
    }

    private bool ReplaceAutoJoinWatch(AutoJoinWatchSnapshot snapshot)
    {
        if (_autoJoinWatch?.Epoch != snapshot.Epoch)
            return false;
        _autoJoinWatch = snapshot;
        return IsAutoJoinWatchCurrent(snapshot);
    }

    private async Task<bool> ReserveAutoJoinLaunchAsync(
        AutoJoinWatchSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAutoJoinWatchCurrent(snapshot))
                return false;

            if (!_operationBusy && !_launchInProgress)
            {
                var claimed = false;
                SetOperationBusy(true);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsAutoJoinWatchCurrent(snapshot))
                        return false;
                    claimed = _autoJoinLaunchGate?.TryClaimLaunch() == true;
                    return claimed;
                }
                finally
                {
                    if (!claimed && !_operationLifetime.IsShuttingDown)
                        SetOperationBusy(false);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private bool IsAutoJoinWatchCurrent(AutoJoinWatchSnapshot snapshot) =>
        _autoJoinWatch is { } active &&
        active.Epoch == snapshot.Epoch &&
        _autoJoinWatchCancellation is
        {
            IsCancellationRequested: false
        } &&
        snapshot.Context.Matches(new AutoJoinWatchContextState(
            _activeProfile?.Key,
            _activeProfile?.UserId,
            _currentUser?.Id,
            PlaceIdBox.Text.Trim(),
            _joinUserMode,
            AutoJoinUserCheckBox.IsChecked == true,
            IsCurrentWebSessionOwner(snapshot.SessionToken)));

    private void CompleteAutoJoinWatch(
        long epoch,
        CancellationTokenSource cancellation)
    {
        var focusReconnect = false;
        if (_autoJoinWatch?.Epoch == epoch)
        {
            focusReconnect = _autoJoinReconnectFocusRequested;
            _autoJoinWatch = null;
            _autoJoinWatchCancellation = null;
            _autoJoinLaunchGate = null;
            _autoJoinLastAnnouncedState = null;
            _autoJoinStopRequested = false;
            _autoJoinReconnectFocusRequested = false;
        }
        cancellation.Dispose();
        AutoJoinWatchDetailText.Text = Localize("Main.AutoJoinHelp");
        SetOperationBusy(_operationBusy);
        if (focusReconnect && !_operationLifetime.IsShuttingDown)
            SignInButton.Focus();
    }

    private void UpdateAutoJoinActionPresentation()
    {
        if (AutoJoinUserPanel is null || LaunchButton is null)
            return;

        AutoJoinUserPanel.Visibility = _joinUserMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoJoinUserCheckBox.IsEnabled =
            _joinUserMode &&
            !_operationBusy &&
            !_launchInProgress &&
            !IsAutoJoinWatchActive;

        string label;
        string accessibleName;
        string tooltip;
        if (IsAutoJoinWatchActive)
        {
            label = _autoJoinWatchCancellation?.IsCancellationRequested == true
                ? Localize("Main.AutoJoinStopping")
                : Localize("Main.AutoJoinStop");
            accessibleName = _autoJoinWatchCancellation?.IsCancellationRequested == true
                ? Localize("Main.AutoJoinStoppingName")
                : Localize("Main.AutoJoinStopName");
            tooltip = Localize("Main.AutoJoinStopTooltip");
        }
        else if (_launchInProgress)
        {
            label = Localize("Main.Launching");
            accessibleName = Localize("Main.LaunchRoblox");
            tooltip = Localize("Main.LaunchTooltip");
        }
        else if (_joinUserMode && AutoJoinUserCheckBox.IsChecked == true)
        {
            label = Localize("Main.AutoJoinStart");
            accessibleName = Localize("Main.AutoJoinStartName");
            tooltip = Localize("Main.AutoJoinStartTooltip");
        }
        else if (_joinUserMode)
        {
            label = Localize("Main.JoinUserButton");
            accessibleName = Localize("Main.JoinRobloxUser");
            tooltip = Localize("Main.JoinThisUserTooltip");
        }
        else
        {
            label = Localize("Main.Launch");
            accessibleName = Localize("Main.LaunchRoblox");
            tooltip = Localize("Main.LaunchTooltip");
        }

        LaunchButtonLabel.Text = label;
        LaunchButton.ToolTip = tooltip;
        AutomationProperties.SetName(LaunchButton, accessibleName);
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1.5
            ? $"{Math.Ceiling(delay.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes"
            : $"{Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds";

    private sealed record AutoJoinWatchSnapshot(
        long Epoch,
        AutoJoinWatchContext Context,
        string AccountUsername,
        WebSessionToken SessionToken,
        JoinUserIdentifier RequestedUser,
        JoinUserIdentity? Identity);
}
