using System.Diagnostics;
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
    private Func<(string Title, string Detail, string Badge, StatusTone Tone)>?
        _autoJoinStatusLocalizer;
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
                Localize("Main.AutoJoinStoppedTitle"),
                Localize("Main.AutoJoinStoppedDetail"),
                Localize("Main.AutoJoinWatchStoppedBadge"),
                StatusTone.Neutral);
        }
        if (!canTrigger || resolution is null)
            return;

        SetStatus(
            Localize("Main.AutoJoinServerFoundTitle", resolution.Username),
            Localize(
                "Main.AutoJoinServerFoundDetail",
                snapshot.AccountUsername),
            Localize("Main.AutoJoinServerFoundBadge"),
            StatusTone.Success);
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
                SetStatus(
                    Localize("Main.AutoJoinInvalidUserTitle"),
                    Localize(error),
                    Localize("Main.AutoJoinInvalidUserBadge"),
                    StatusTone.Error);
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
                () => (
                    Localize(
                        "Main.AutoJoinWatchingTitle",
                        requestedUser!.DisplayValue),
                    Localize(
                        "Main.AutoJoinWatchingDetail",
                        currentUser.Name),
                    Localize("Main.AutoJoinWatchingBadge"),
                    StatusTone.Neutral));
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
                        Localize("Main.AutoJoinUserNotFoundTitle"),
                        Localize("Main.AutoJoinUserNotFoundDetail"),
                        Localize("Main.AutoJoinUserNotFoundBadge"),
                        StatusTone.Error);
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
                Localize("Main.AutoJoinChooseAnotherUserTitle"),
                Localize("Main.AutoJoinChooseAnotherUserDetail"),
                Localize("Main.AutoJoinInvalidUserBadge"),
                StatusTone.Error);
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
                        Localize("Main.AutoJoinUserUnavailableTitle"),
                        Localize("Main.AutoJoinUserUnavailableDetail"),
                        Localize("Main.AutoJoinWatchEndedBadge"),
                        StatusTone.Warning);
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
            () => (
                Localize(
                    "Main.AutoJoinIdentityCheckFailedTitle",
                    snapshot.RequestedUser.DisplayValue),
                rateLimited
                    ? Localize(
                        "Main.AutoJoinIdentityRateLimitedDetail",
                        FormatDelay(delay))
                    : Localize(
                        "Main.AutoJoinIdentityRetryDetail",
                        FormatDelay(delay)),
                Localize("Main.AutoJoinWatchingBadge"),
                StatusTone.Warning));
    }

    private void ShowAutoJoinWaitingState(
        AutoJoinWatchSnapshot snapshot,
        JoinUserAvailability availability,
        TimeSpan delay)
    {
        var target = snapshot.Identity is { } identity
            ? $"@{identity.Username}"
            : snapshot.RequestedUser.DisplayValue;
        var state = availability switch
        {
            JoinUserAvailability.Offline => "offline",
            JoinUserAvailability.NotInExperience => "not-in-experience",
            JoinUserAvailability.NotJoinable => "not-joinable",
            JoinUserAvailability.RateLimited => "presence-rate-limit",
            _ => "presence-service"
        };
        AnnounceAutoJoinState(
            state,
            () =>
            {
                var (title, detail) = availability switch
                {
                    JoinUserAvailability.Offline => (
                        Localize("Main.AutoJoinWaitingTitle", target),
                        Localize(
                            "Main.AutoJoinOfflineDetail",
                            snapshot.AccountUsername,
                            FormatDelay(delay))),
                    JoinUserAvailability.NotInExperience => (
                        Localize(
                            "Main.AutoJoinNotInExperienceTitle",
                            target),
                        Localize(
                            "Main.AutoJoinNotInExperienceDetail",
                            snapshot.AccountUsername,
                            FormatDelay(delay))),
                    JoinUserAvailability.NotJoinable => (
                        Localize("Main.AutoJoinNotJoinableTitle", target),
                        Localize(
                            "Main.AutoJoinNotJoinableDetail",
                            FormatDelay(delay))),
                    JoinUserAvailability.RateLimited => (
                        Localize("Main.AutoJoinRateLimitedTitle"),
                        Localize(
                            "Main.AutoJoinRateLimitedDetail",
                            FormatDelay(delay))),
                    _ => (
                        Localize(
                            "Main.AutoJoinPresenceCheckFailedTitle",
                            target),
                        Localize(
                            "Main.AutoJoinPresenceRetryDetail",
                            FormatDelay(delay)))
                };
                return (
                    title,
                    detail,
                    Localize("Main.AutoJoinWatchingBadge"),
                    availability == JoinUserAvailability.RateLimited
                        ? StatusTone.Warning
                        : StatusTone.Neutral);
            });
    }

    private void ShowAutoJoinExpired(AutoJoinWatchSnapshot snapshot) =>
        SetStatus(
            Localize("Main.AutoJoinExpiredTitle"),
            Localize(
                "Main.AutoJoinExpiredDetail",
                snapshot.RequestedUser.DisplayValue),
            Localize("Main.AutoJoinWatchExpiredBadge"),
            StatusTone.Warning);

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
            Localize("Main.AutoJoinEndedTitle"),
            Localize(
                "Main.AutoJoinSessionUnavailableDetail",
                snapshot.AccountUsername),
            Localize("Main.SignInNeededBadge"),
            StatusTone.Error);
    }

    private void AnnounceAutoJoinState(
        string state,
        Func<(string Title, string Detail, string Badge, StatusTone Tone)>
            localizeStatus)
    {
        ArgumentNullException.ThrowIfNull(localizeStatus);
        _autoJoinStatusLocalizer = localizeStatus;
        var (title, detail, badge, tone) = localizeStatus();
        AutoJoinWatchDetailText.Text = detail;
        if (string.Equals(
                _autoJoinLastAnnouncedState,
                state,
                StringComparison.Ordinal))
        {
            RefreshAutoJoinStatusWithoutAnnouncement(
                title,
                detail,
                badge,
                tone);
            return;
        }

        _autoJoinLastAnnouncedState = state;
        SetStatus(title, detail, badge, tone);
    }

    private void RefreshAutoJoinLocalizedState()
    {
        if (!IsAutoJoinWatchActive || _autoJoinStatusLocalizer is null)
            return;

        var (title, detail, badge, tone) = _autoJoinStatusLocalizer();
        RefreshAutoJoinStatusWithoutAnnouncement(title, detail, badge, tone);
    }

    private void RefreshAutoJoinStatusWithoutAnnouncement(
        string title,
        string detail,
        string badge,
        StatusTone tone)
    {
        AutoJoinWatchDetailText.Text = detail;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        SessionBadge.Text = badge;
        AutomationProperties.SetName(
            StatusTitle,
            CreateStatusAnnouncement(title, detail, badge));
        AutomationProperties.SetLiveSetting(
            StatusTitle,
            tone is StatusTone.Error or StatusTone.Warning
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
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
            Localize("Main.AutoJoinStoppingTitle"),
            Localize("Main.AutoJoinStoppingDetail"),
            Localize("Main.AutoJoinStoppingBadge"),
            StatusTone.Neutral);
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
            _autoJoinStatusLocalizer = null;
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

    private string FormatDelay(TimeSpan delay)
    {
        var useMinutes = delay.TotalMinutes >= 1.5;
        var count = (int)Math.Ceiling(
            useMinutes ? delay.TotalMinutes : delay.TotalSeconds);
        if (useMinutes)
        {
            return count == 1
                ? Localize("Main.DurationMinuteOne")
                : Localize("Main.DurationMinuteMany", count);
        }

        return count == 1
            ? Localize("Main.DurationSecondOne")
            : Localize("Main.DurationSecondMany", count);
    }

    private sealed record AutoJoinWatchSnapshot(
        long Epoch,
        AutoJoinWatchContext Context,
        string AccountUsername,
        WebSessionToken SessionToken,
        JoinUserIdentifier RequestedUser,
        JoinUserIdentity? Identity);
}
