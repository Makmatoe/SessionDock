using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Models;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupProfileDeletionTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupOrphanProfileCleanupTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DestinationPersistenceDelay =
        TimeSpan.FromMilliseconds(450);
    private readonly SettingsService _settingsService = new();
    private readonly RobloxClientService _robloxClient = new();
    private readonly RunningClientRegistry _runningClients = new();
    private readonly RobloxServerTracker _serverTracker = new();
    private readonly RobloxWebSessionService _webSession = new();
    private readonly UiSoundService _soundService;
    private readonly CompositeLaunchHook _launchHook = new(
        new HandleScopeLaunchHook(),
        new LocalApiLaunchHook());
    private readonly WindowOperationLifetime _operationLifetime = new();
    private readonly SemaphoreSlim _accountCheckLock = new(1, 1);
    private readonly HashSet<string> _sessionImportedSoundFileNames = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<Exception?> _startupCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppSettings _settings;
    private readonly SerializedSettingsWriter _settingsWriter;
    private readonly SettingsMutationCoordinator _settingsMutations;
    private readonly DestinationPersistenceDebouncer _destinationPersistence;
    private readonly AccountVerificationGate _accountVerificationGate = new();
    private readonly AccessibilityLiveRegion _statusLiveRegion;
    private readonly AccessibilityLiveRegion? _destinationValidationLiveRegion;
    private string? _startupNotice;
    private AccountProfile? _activeProfile;
    private AccountProfile? _pendingProfile;
    private RobloxUser? _currentUser;
    private WebSessionToken? _webSessionToken;
    private WebSessionToken? _pendingAccountCheckToken;
    private CancellationTokenSource? _browserSwitchCancellation;
    private CancellationTokenSource? _batchCancellation;
    private bool _launchInProgress;
    private bool _operationBusy;
    private bool _destinationTrackingEnabled;
    private string? _destinationDraftAccountKey;
    private string? _destinationDraftValue;
    private string? _destinationPersistedValue;
    private long _destinationOwnerEpoch;
    private long _destinationRevision;
    private bool _destinationDraftDirty;
    private bool _destinationDraftValid = true;
    private bool _joinUserMode;
    private bool _updatingDestinationModeSelection;
    private bool _destinationModeAwaitingInput;
    private bool _webView2RecoveryPromptShown;
    private bool _shutdownComplete;

    internal Task<Exception?> StartupCompletion => _startupCompletion.Task;

    internal event Action<Exception?>? ShutdownCompleted;

    internal void VerifyThemeSwitchForRuntimeSmoke()
    {
        Dispatcher.VerifyAccess();
        var themeService = ((App)Application.Current).ThemeService;
        if (themeService.IsHighContrastActive)
            return;

        var originalPreference = themeService.UseLightThemePreference;
        var originalBackground = GetSolidColor(Background, "window background");
        try
        {
            themeService.ApplyPreference(!originalPreference);
            var expectedBackground = GetSolidColor(
                (Brush)FindResource("BackgroundBrush"),
                "active background resource");
            var switchedBackground = GetSolidColor(
                Background,
                "switched window background");
            if (switchedBackground != expectedBackground ||
                switchedBackground == originalBackground)
            {
                throw new InvalidOperationException(
                    "The existing main window did not adopt the switched theme.");
            }

            var generatedEmptyState = RecentExperiencesList.Children
                .OfType<TextBlock>()
                .FirstOrDefault();
            if (generatedEmptyState is not null)
            {
                var expectedSubtle = GetSolidColor(
                    (Brush)FindResource("SubtleBrush"),
                    "active subtle resource");
                var actualSubtle = GetSolidColor(
                    generatedEmptyState.Foreground,
                    "generated recent-list foreground");
                if (actualSubtle != expectedSubtle)
                {
                    throw new InvalidOperationException(
                        "A generated recent-list element retained the old theme.");
                }
            }
        }
        finally
        {
            themeService.ApplyPreference(originalPreference);
        }
    }

    internal void VerifyJoinUserUiForRuntimeSmoke()
    {
        Dispatcher.VerifyAccess();
        SelectWithAutomation(UserDestinationModeButton);
        if (!_joinUserMode ||
            LaunchButtonLabel.Text != Localize("Main.JoinUserButton") ||
            AutoJoinUserPanel.Visibility != Visibility.Visible ||
            AutoJoinUserCheckBox.IsChecked == true ||
            UserDestinationModeButton.IsChecked != true ||
            ExperienceDestinationModeButton.IsChecked == true ||
            AutomationProperties.GetName(PlaceIdBox) !=
            Localize("Main.JoinUserInputName"))
        {
            throw new InvalidOperationException(
                "The join-user destination mode did not become active.");
        }

        AutoJoinUserCheckBox.IsChecked = true;
        if (LaunchButtonLabel.Text != Localize("Main.AutoJoinStart") ||
            AutomationProperties.GetName(LaunchButton) !=
            Localize("Main.AutoJoinStartName"))
        {
            throw new InvalidOperationException(
                "The auto-join action did not become available.");
        }
        AutoJoinUserCheckBox.IsChecked = false;

        SelectWithAutomation(ExperienceDestinationModeButton);
        if (_joinUserMode ||
            LaunchButtonLabel.Text != Localize("Main.Launch") ||
            AutoJoinUserPanel.Visibility != Visibility.Collapsed ||
            ExperienceDestinationModeButton.IsChecked != true ||
            UserDestinationModeButton.IsChecked == true ||
            AutomationProperties.GetName(PlaceIdBox) !=
            Localize("Main.DestinationInputName"))
        {
            throw new InvalidOperationException(
                "The experience destination mode was not restored.");
        }
    }

    internal void VerifySemanticSelectorsForRuntimeSmoke()
    {
        Dispatcher.VerifyAccess();
        SelectWithAutomation(RecentTabButton);
        if (RecentTabPanel.Visibility != Visibility.Visible ||
            LaunchTabPanel.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException(
                "The UI Automation selection did not open Recent.");
        }

        SelectWithAutomation(PublicFilterButton);
        if (_recentTypeFilter != RecentTypeFilter.Public ||
            PublicFilterButton.IsChecked != true ||
            AllTypeFilterButton.IsChecked == true)
        {
            throw new InvalidOperationException(
                "The UI Automation selection did not apply the public filter.");
        }

        SelectWithAutomation(AllTypeFilterButton);
        SelectWithAutomation(LaunchTabButton);
        if (_recentTypeFilter != RecentTypeFilter.All ||
            LaunchTabPanel.Visibility != Visibility.Visible ||
            RecentTabPanel.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException(
                "The semantic selectors did not restore their default state.");
        }
    }

    private static void SelectWithAutomation(RadioButton selector)
    {
        var peer = new RadioButtonAutomationPeer(selector);
        var provider = peer.GetPattern(PatternInterface.SelectionItem) as
            ISelectionItemProvider ?? throw new InvalidOperationException(
                "A segmented selector did not expose SelectionItem.");
        provider.Select();
    }

    private static Color GetSolidColor(Brush brush, string description) =>
        brush is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color
            : throw new InvalidOperationException(
                $"The {description} is not a solid theme brush.");

    public MainWindow()
    {
        InitializeComponent();
        AttachSemanticSelectorHandlers();
        _statusLiveRegion = new AccessibilityLiveRegion(StatusTitle);
        _destinationValidationLiveRegion =
            new AccessibilityLiveRegion(DestinationValidationText);
        CaptionControls.AttachToWindow(this);
        var app = (App)Application.Current;
        _soundService = app.SoundService;
        _settings = _settingsService.Load();
        if (!WindowLayoutService.RestoreMainWindowPlacement(
                this,
                _settings.MainWindowPlacement))
        {
            WindowLayoutService.FitToWorkArea(this);
        }
        app.LocalizationService.ApplyPreference(_settings.Language);
        app.ThemeService.ApplyPreference(_settings.UseLightTheme);
        UpdateUpdateTooltip();
        UpdateThemeTogglePresentation();
        app.ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        app.LocalizationService.LanguageChanged +=
            LocalizationService_LanguageChanged;
        _settingsWriter = new SerializedSettingsWriter(_settingsService.Save);
        _settingsMutations = new SettingsMutationCoordinator(
            _settings,
            _settingsWriter);
        _destinationPersistence = new DestinationPersistenceDebouncer(
            DestinationPersistenceDelay,
            PersistDestinationRequestAsync);
        _startupNotice = _settingsService.LoadNotice;
        app.UiSoundsEnabled = _settings.UiSoundsEnabled;
        _webSession.RobloxPageLoaded += WebSession_RobloxPageLoaded;
        _webSession.SessionUnavailable += WebSession_SessionUnavailable;
        _activeProfile = FindActiveSavedProfile();
        ShowDestinationForProfile(_activeProfile);
        _destinationTrackingEnabled = true;
        RenderAccountList();
        RenderRecentExperiences();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void AttachSemanticSelectorHandlers()
    {
        LaunchTabButton.Checked += LaunchTabButton_Checked;
        RecentTabButton.Checked += RecentTabButton_Checked;
        ExperienceDestinationModeButton.Checked +=
            ExperienceDestinationModeButton_Checked;
        UserDestinationModeButton.Checked +=
            UserDestinationModeButton_Checked;
        AllTypeFilterButton.Checked += AllTypeFilterButton_Checked;
        PublicFilterButton.Checked += PublicFilterButton_Checked;
        PrivateFilterButton.Checked += PrivateFilterButton_Checked;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(MainWindowLoadedAsync);

    private async Task MainWindowLoadedAsync(CancellationToken cancellationToken)
    {
        SetOperationBusy(true);
        try
        {
            await RetryPendingProfileDeletionsAsync(cancellationToken);
            var orphanCleanup = await new BoundedOrphanProfileCleanup().RunAsync(
                cleanupCancellationToken =>
                    _settingsService.CleanupOrphanedSessionDirectories(
                        _settings,
                        cleanupCancellationToken),
                StartupOrphanProfileCleanupTimeout,
                cancellationToken);
            if (orphanCleanup.RemovedProfiles > 0)
            {
                AppendStartupNotice(
                    $"Removed {orphanCleanup.RemovedProfiles} incomplete local account profile(s) left by an interrupted sign-in.");
            }
            if (orphanCleanup.BudgetExpired)
            {
                AppendStartupNotice(
                    "SessionDock limited incomplete-profile cleanup during startup so the window could remain responsive. Any unfinished profile is preserved for another cleanup attempt on the next start.");
            }

            await ReconcileImportedSoundsAsync(cancellationToken);
            _soundService.PlayStartup(
                _settings.StartupSound,
                _settings.CustomStartupSoundFileName);
            if (!string.IsNullOrWhiteSpace(_startupNotice))
            {
                MessageBox.Show(
                    this,
                    _startupNotice,
                    "Local settings recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _settingsService.AcknowledgeLoadNotice();
            }
            if (_activeProfile is null)
            {
                SetSignedOutState();
            }
            else
            {
                await InitializeBrowserAsync(
                    _activeProfile,
                    showLogin: false,
                    cancellationToken);
            }

            _startupCompletion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            _startupCompletion.TrySetResult(exception);
            throw;
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private void AppendStartupNotice(string notice)
    {
        _startupNotice = string.IsNullOrWhiteSpace(_startupNotice)
            ? notice
            : $"{_startupNotice}{Environment.NewLine}{Environment.NewLine}{notice}";
    }

    private async Task RetryPendingProfileDeletionsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> journaledKeys;
        try
        {
            journaledKeys = await Task.Run(
                _settingsService.GetJournaledProfileDeletionKeys,
                cancellationToken);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            AppendStartupNotice(
                "SessionDock could not inspect its account-removal journal, so all browser profiles were left untouched.");
            return;
        }

        if (journaledKeys.Count == 0)
            return;

        var journaledSet = journaledKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var prepared = false;
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    _settings.PendingProfileDeletionKeys = journaledKeys
                        .Concat(_settings.PendingProfileDeletionKeys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(SettingsService.MaximumPendingProfileDeletions)
                        .ToList();
                    _settings.Accounts.RemoveAll(account =>
                        journaledSet.Contains(account.Key));
                    BatchLaunchPreferences.PrunePresetsForCurrentAccounts(
                        _settings);
                    if (_settings.ActiveAccountKey is not null &&
                        journaledSet.Contains(_settings.ActiveAccountKey))
                    {
                        _settings.ActiveAccountKey =
                            _settings.Accounts.FirstOrDefault()?.Key;
                    }
                    prepared = true;
                },
                "Pending account removal could not be restored",
                "CLEANUP WARNING",
                "SessionDock preserved the removal journal and left all browser profiles untouched. It will retry after local settings become writable.",
                onCommitted: () =>
                {
                    _activeProfile = FindActiveSavedProfile();
                    _pendingProfile = null;
                    _currentUser = null;
                    ShowDestinationForProfile(_activeProfile);
                    RenderAccountList();
                }) ||
            !prepared)
        {
            AppendStartupNotice(
                "One or more confirmed account removals could not yet be restored to local settings. Their browser data was preserved for a later retry.");
            return;
        }

        var replayResult = await new PendingProfileDeletionReplay().ReplayAsync(
            journaledKeys,
            (accountKey, replayCancellationToken) =>
                _settingsService.DeletePendingProfileOnceAsync(
                    accountKey,
                    _settings,
                    replayCancellationToken),
            StartupProfileDeletionTimeout,
            cancellationToken);
        var deletedKeys = replayResult.DeletedKeys;

        var journalClearFailed = false;
        if (deletedKeys.Count > 0 &&
            await AcknowledgePendingProfileDeletionsAsync(deletedKeys))
        {
            foreach (var accountKey in deletedKeys)
            {
                if (!await Task.Run(
                        () => _settingsService.ClearProfileDeletionJournal(
                            accountKey),
                        CancellationToken.None))
                {
                    journalClearFailed = true;
                }
            }

            AppendStartupNotice(
                $"Finished clearing {deletedKeys.Count} previously removed account profile(s).");
        }

        if (deletedKeys.Count < journaledKeys.Count || journalClearFailed ||
            _settings.PendingProfileDeletionKeys.Count > 0)
        {
            AppendStartupNotice(replayResult.BudgetExpired
                ? "SessionDock limited account-profile cleanup during startup so the window could remain responsive. Unfinished removals were preserved and will be retried on the next start."
                : "Some isolated browser data from a removed account is still locked or its cleanup acknowledgement could not be saved. SessionDock will retry on the next start.");
        }
    }

    private async Task<bool> AcknowledgePendingProfileDeletionsAsync(
        IReadOnlyCollection<string> accountKeys)
    {
        var keys = accountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acknowledged = 0;
        var committed = await TryCommitSettingsMutationAsync(
            () =>
            {
                if (_settings.Accounts.Any(account =>
                        keys.Contains(account.Key)))
                {
                    return;
                }

                acknowledged = _settings.PendingProfileDeletionKeys.RemoveAll(
                    key => keys.Contains(key));
            },
            "Account cleanup could not be confirmed",
            "CLEANUP WARNING",
            "The browser data was cleared, but SessionDock could not save that acknowledgement. It will safely retry on the next start.");
        return committed && acknowledged > 0;
    }

    private async Task ReconcileImportedSoundsAsync(
        CancellationToken cancellationToken)
    {
        var retention = await Task.Run(
            () => _settingsService.CaptureImportedSoundRetention(
                _settings.CustomStartupSoundFileName),
            cancellationToken);
        await Task.Run(
            () => _soundService.ReconcileImportedSounds(
                retention,
                _sessionImportedSoundFileNames.ToArray(),
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> InitializeBrowserAsync(
        AccountProfile profile,
        bool showLogin,
        CancellationToken cancellationToken = default)
    {
        CancelAutoJoinWatchSilently();
        _browserSwitchCancellation?.Cancel();
        _browserSwitchCancellation?.Dispose();
        _browserSwitchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _operationLifetime.Token,
            cancellationToken);
        var browserCancellationToken = _browserSwitchCancellation.Token;
        _currentUser = null;
        LaunchButton.IsEnabled = false;

        var session = _webSession.BeginBrowserReplacement(profile.Key);
        _webSessionToken = session.Token;
        var browser = session.Browser;
        BrowserHost.Children.Clear();
        BrowserHost.Children.Add(browser);

        try
        {
            return await _webSession.InitializeAsync(
                session,
                _settingsService.GetSessionDataDirectory(profile),
                showLogin,
                browserCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer account selection superseded this browser initialization.
            return false;
        }
        catch (WebSessionUnavailableException exception) when (
            exception.Reason == WebSessionUnavailableReason.Superseded ||
            _operationLifetime.IsShuttingDown)
        {
            return false;
        }
        catch (WebSessionUnavailableException exception)
        {
            PresentWebSessionFailure(
                "Web sign-in could not start",
                exception);
            return false;
        }
    }

    private void PresentWebSessionFailure(
        string title,
        WebSessionUnavailableException exception)
    {
        _webSessionToken = null;
        BrowserHost.Children.Clear();
        BrowserPanel.Visibility = Visibility.Collapsed;
        LauncherPanel.Visibility = Visibility.Visible;
        SetStatus(title, exception.Message, "SESSION ERROR");
        SignInButton.Visibility = Visibility.Visible;

        if (_webView2RecoveryPromptShown ||
            _operationLifetime.IsShuttingDown ||
            !WebSessionException.HasActionableRuntimeRecovery(exception.Reason))
        {
            return;
        }

        _webView2RecoveryPromptShown = true;
        var result = MessageBox.Show(
            this,
            $"{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            "Open Microsoft's official WebView2 download and repair page now?",
            "Microsoft WebView2 needs attention",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WebSessionException.OfficialWebView2DownloadUrl,
                UseShellExecute = true
            });
        }
        catch (Win32Exception startException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 help page could not be opened: {startException.NativeErrorCode}.");
            SetStatus(
                "WebView2 help page could not be opened",
                $"Open {WebSessionException.OfficialWebView2DownloadUrl} in your browser, install or repair WebView2, restart Windows, and try again.",
                "SESSION ERROR");
        }
    }

    private async void WebSession_RobloxPageLoaded(
        object? sender,
        WebSessionEventArgs e)
    {
        if (!_accountVerificationGate.ShouldRunAutomaticVerification(e.Token))
            return;

        await RunWindowOperationAsync(cancellationToken =>
            CheckAuthenticatedAccountAsync(
                e.Token,
                skipIfBusy: true,
                cancellationToken));
    }

    private async void WebSession_SessionUnavailable(
        object? sender,
        WebSessionUnavailableEventArgs e) =>
        await RunWindowOperationAsync(_ =>
            HandleWebSessionUnavailableAsync(e));

    private Task HandleWebSessionUnavailableAsync(
        WebSessionUnavailableEventArgs e)
    {
        if (!HasCurrentWebSessionAffinity(e.Token))
            return Task.CompletedTask;

        CancelAutoJoinWatchSilently();
        _currentUser = null;
        LaunchButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Visible;
        SignInButtonLabel.Text = Localize("Main.Reconnect");
        AutomationProperties.SetName(
            SignInButton,
            Localize("Main.ReconnectName"));
        SetStatus(
            "Roblox web session stopped",
            "The isolated browser process became unavailable. Reconnect this account before launching again.",
            "SESSION ERROR");
        return Task.CompletedTask;
    }

    private Task CheckAuthenticatedAccountAsync(
        bool skipIfBusy = false,
        CancellationToken cancellationToken = default) =>
        _webSessionToken is { } token
            ? CheckAuthenticatedAccountAsync(
                token,
                skipIfBusy,
                cancellationToken)
            : Task.CompletedTask;

    private async Task CheckAuthenticatedAccountAsync(
        WebSessionToken token,
        bool skipIfBusy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentWebSessionOwner(token))
            return;

        if (skipIfBusy)
        {
            if (!await _accountCheckLock.WaitAsync(0, cancellationToken))
            {
                _pendingAccountCheckToken = token;
                return;
            }
        }
        else
        {
            await _accountCheckLock.WaitAsync(cancellationToken);
        }

        try
        {
            var tokenToCheck = token;
            while (true)
            {
                _pendingAccountCheckToken = null;
                await CheckAuthenticatedAccountCoreAsync(
                    tokenToCheck,
                    cancellationToken);

                var pendingToken = _pendingAccountCheckToken;
                if (pendingToken is null ||
                    !IsCurrentWebSessionOwner(pendingToken.Value))
                {
                    return;
                }

                tokenToCheck = pendingToken.Value;
            }
        }
        finally
        {
            _accountCheckLock.Release();
        }
    }

    private async Task CheckAuthenticatedAccountCoreAsync(
        WebSessionToken token,
        CancellationToken cancellationToken)
    {
        RobloxUser? detectedUser;
        try
        {
            detectedUser = await _webSession.GetAuthenticatedUserAsync(
                token,
                cancellationToken);
        }
        catch (WebSessionUnavailableException exception) when (
            exception.Reason == WebSessionUnavailableReason.Superseded)
        {
            return;
        }
        catch (WebSessionUnavailableException exception)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Account verification failed safely: {exception.Reason}.");
            if (HasCurrentWebSessionAffinity(token))
                SetSignedOutState();
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentWebSessionOwner(token))
            return;

        _currentUser = detectedUser;
        if (detectedUser is null)
        {
            SetSignedOutState();
            return;
        }

        if (_pendingProfile is not null)
        {
            var pendingKey = _pendingProfile.Key;
            var promotionApplied = false;
            var duplicateDetected = false;
            if (!await TryCommitSettingsMutationAsync(
                    () =>
                    {
                        if (!IsCurrentWebSessionOwner(token) ||
                            _pendingProfile?.Key != pendingKey)
                        {
                            return;
                        }

                        if (_settings.Accounts.Any(account =>
                                account.UserId == detectedUser.Id))
                        {
                            duplicateDetected = true;
                            return;
                        }

                        _pendingProfile.UserId = detectedUser.Id;
                        _pendingProfile.Username = detectedUser.Name;
                        _settings.Accounts.Add(_pendingProfile);
                        _settings.ActiveAccountKey = pendingKey;
                        promotionApplied = true;
                    },
                    "Account could not be saved",
                    "ACCOUNT SAVE ERROR",
                    "The Roblox sign-in succeeded, but SessionDock could not save this account slot. The temporary slot was kept so you can retry after making %LOCALAPPDATA%\\SessionDock writable.",
                    onCommitted: () =>
                    {
                        if (!promotionApplied)
                            return;
                        _activeProfile = _settings.Accounts.FirstOrDefault(account =>
                            account.Key.Equals(
                                pendingKey,
                                StringComparison.OrdinalIgnoreCase));
                        _pendingProfile = null;
                        RenderAccountList();
                    }))
            {
                LaunchButton.IsEnabled = false;
                return;
            }

            if (!IsCurrentWebSessionOwner(token))
                return;

            if (duplicateDetected)
            {
                SetStatus(
                    "Account already added",
                    $"@{detectedUser.Name} already has a saved account slot. Sign out on the Roblox page and use a different account.",
                    "DUPLICATE ACCOUNT");
                LaunchButton.IsEnabled = false;
                return;
            }

            if (!promotionApplied)
                return;
        }

        if (!IsCurrentWebSessionOwner(token) ||
            _activeProfile is null ||
            _activeProfile.UserId != detectedUser.Id)
        {
            if (!IsCurrentWebSessionOwner(token))
                return;
            SetStatus(
                "Different account detected",
                $"This slot belongs to @{_activeProfile?.Username}. Sign out of @{detectedUser.Name} and reconnect the correct account.",
                "ACCOUNT BLOCKED");
            LaunchButton.IsEnabled = false;
            SignInButton.Visibility = Visibility.Visible;
            SignInButtonLabel.Text = Localize("Main.FixSignIn");
            AutomationProperties.SetName(
                SignInButton,
                Localize("Main.FixSignInName"));
            return;
        }

        BrowserPanel.Visibility = Visibility.Collapsed;
        LauncherPanel.Visibility = Visibility.Visible;
        SetReadyState();
    }

    private bool IsCurrentWebSessionOwner(WebSessionToken token)
    {
        return HasCurrentWebSessionAffinity(token) &&
            _webSession.IsUsable(token);
    }

    private bool HasCurrentWebSessionAffinity(WebSessionToken token)
    {
        var owner = _pendingProfile ?? _activeProfile;
        return _webSessionToken == token &&
            _webSession.IsCurrent(token) &&
            owner?.Key.Equals(
                token.AccountKey,
                StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool TryGetCurrentWebSessionToken(
        AccountProfile profile,
        out WebSessionToken token)
    {
        if (_webSessionToken is { } currentToken &&
            profile.Key.Equals(
                currentToken.AccountKey,
                StringComparison.OrdinalIgnoreCase) &&
            IsCurrentWebSessionOwner(currentToken))
        {
            token = currentToken;
            return true;
        }

        token = default;
        return false;
    }

    private bool TryGetAffineWebSessionToken(
        AccountProfile profile,
        out WebSessionToken token)
    {
        if (_webSessionToken is { } currentToken &&
            profile.Key.Equals(
                currentToken.AccountKey,
                StringComparison.OrdinalIgnoreCase) &&
            HasCurrentWebSessionAffinity(currentToken))
        {
            token = currentToken;
            return true;
        }

        token = default;
        return false;
    }

    private void SetSignedOutState()
    {
        CancelAutoJoinWatchSilently();
        var profile = _pendingProfile ?? _activeProfile;
        SetStatus(
            profile is null || _pendingProfile is not null
                ? "Connect a Roblox account"
                : $"Reconnect @{profile.Username}",
            _pendingProfile is not null
                ? "Sign in to the second Roblox account. It will keep its own isolated session."
                : profile is null
                    ? "Add an account to create the first isolated Roblox session."
                    : "This account slot's Roblox session has expired. Sign back into the same account.",
            "SIGN-IN NEEDED");
        LaunchButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Visible;
        SignInButtonLabel.Text = Localize("Main.SignIn");
        AutomationProperties.SetName(
            SignInButton,
            Localize("Main.SignInName"));
    }

    private void SetReadyState()
    {
        if (_currentUser is null || _activeProfile is null ||
            !TryGetCurrentWebSessionToken(_activeProfile, out _))
            return;

        SetStatus(
            $"Active account: @{_currentUser.Name}",
            "Ready. Roblox Player will be verified by Windows when you launch.",
            _launchInProgress ? "LAUNCHING" : "ACCOUNT VERIFIED");
        SignInButton.Visibility = Visibility.Collapsed;
        RefreshLaunchAvailability();
        UpdateAutoJoinActionPresentation();
    }

    private void SetStatus(string title, string detail, string badge)
    {
        var tone = StatusToneClassifier.Classify(badge);
        _statusLiveRegion.Update(
            title,
            CreateStatusAnnouncement(title, detail, badge),
            tone is StatusTone.Error or StatusTone.Warning
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        StatusDetail.Text = detail;
        SessionBadge.Text = badge;

        var foregroundResource = tone switch
        {
            StatusTone.Error => "ErrorTextBrush",
            StatusTone.Success => "SuccessTextBrush",
            StatusTone.Warning => "WarningTextBrush",
            _ => "InfoTextBrush"
        };
        var surfaceResource = tone switch
        {
            StatusTone.Error => "ErrorSurfaceBrush",
            StatusTone.Success => "SuccessSurfaceBrush",
            StatusTone.Warning => "WarningSurfaceBrush",
            _ => "InfoSurfaceBrush"
        };
        SessionBadge.SetResourceReference(
            TextBlock.ForegroundProperty,
            foregroundResource);
        SessionBadgeBorder.SetResourceReference(
            Border.BackgroundProperty,
            surfaceResource);
        StatusIconGlyph.Data = (Geometry)FindResource(
            tone == StatusTone.Error
                ? "IconError"
                : tone == StatusTone.Success
                    ? "IconCheck"
                    : "IconActivity");
        StatusIconGlyph.SetResourceReference(
            Shape.StrokeProperty,
            foregroundResource);
        StatusIconBorder.SetResourceReference(
            Border.BackgroundProperty,
            surfaceResource);
    }

    internal static string CreateStatusAnnouncement(
        string title,
        string detail,
        string badge) =>
        string.Join(
            " ",
            new[] { title, detail, badge }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));

    private Task RunWindowOperationAsync(
        Func<CancellationToken, Task> operation) =>
        _operationLifetime.RunAsync(
            operation,
            IsExpectedWindowOperationFailure,
            HandleExpectedWindowOperationFailure);

    private static bool IsExpectedWindowOperationFailure(Exception exception) =>
        LocalDataException.IsExpectedPersistenceFailure(exception) ||
        WebSessionException.IsExpectedLifecycleFailure(exception);

    private void HandleExpectedWindowOperationFailure(Exception exception)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Local operation failed safely: {exception.GetType().Name}.");
        if (exception is WebSessionUnavailableException webSessionFailure)
        {
            if (webSessionFailure.Reason ==
                WebSessionUnavailableReason.Superseded)
            {
                return;
            }
            PresentWebSessionFailure(
                "Roblox web session became unavailable",
                webSessionFailure);
            return;
        }
        SetStatus(
            "Local operation could not be completed",
            "SessionDock could not access a required local file or folder. Check that %LOCALAPPDATA%\\SessionDock is writable and not locked, then try again.",
            "LOCAL DATA ERROR");
    }

    private async Task<bool> TryCommitSettingsMutationAsync(
        Action mutation,
        string failureTitle,
        string failureBadge = "SETTINGS ERROR",
        string? failureDetail = null,
        bool showFailure = true,
        Action? onCommitted = null)
    {
        var result = await _settingsMutations.CommitAsync(
            mutation,
            onCommitted);
        if (result.Committed)
        {
            return true;
        }

        if (result.Closed)
            return false;

        System.Diagnostics.Trace.WriteLine(
            $"Settings update failed safely: {result.Failure!.GetType().Name}.");
        if (showFailure && !_operationLifetime.IsShuttingDown)
        {
            SetStatus(
                failureTitle,
                failureDetail ??
                    "SessionDock could not confirm the local settings update, so the in-memory change was rolled back. Check that %LOCALAPPDATA%\\SessionDock is writable and not locked, then try again.",
                failureBadge);
        }
        return false;
    }

    private void RenderAccountList()
    {
        var requestedFocusKey = _accountFocusRestoreKey;
        _accountFocusRestoreKey = null;
        var restoreKeyboardFocus =
            AccountsList.IsKeyboardFocusWithin || requestedFocusKey is not null;
        var focusedAccountKey = requestedFocusKey ??
            ((Keyboard.FocusedElement as Button)?.Tag as string);
        Button? focusedAccountButton = null;
        Button? activeAccountButton = null;
        var visibleAccounts = _settings.Accounts
            .Where(account => _accountSearch.MatchesAccount(
                account,
                account.Group))
            .ToList();

        AccountStripHintText.Text = _accountSearch.IsActive
            ? Localize("Main.ReorderPaused")
            : Localize("Main.DragToReorder");
        AutomationProperties.SetName(
            AccountStripHintText,
            _accountSearch.IsActive
                ? Localize("Main.ReorderPausedHelp")
                : Localize("Main.ReorderHelp"));
        AutomationProperties.SetItemStatus(
            AccountSearchBox,
            _accountSearch.IsActive
                ? Localize(
                    "Main.AccountFilteredCount",
                    visibleAccounts.Count,
                    _settings.Accounts.Count)
                : Localize("Main.AccountCount", _settings.Accounts.Count));

        AccountsList.Children.Clear();
        for (var index = 0; index < visibleAccounts.Count; index++)
        {
            var account = visibleAccounts[index];
            var isActive = account.Key == _settings.ActiveAccountKey;
            var accountButton = CreateAccountButton(
                account,
                isActive,
                positionInSet: index + 1,
                sizeOfSet: visibleAccounts.Count);
            AccountsList.Children.Add(accountButton);
            if (account.Key.Equals(
                    focusedAccountKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                focusedAccountButton = accountButton;
            }
            if (isActive)
                activeAccountButton = accountButton;
        }

        if (_pendingProfile is not null)
        {
            var pendingButton = CreateAccountButton(
                _pendingProfile,
                selected: true,
                pending: true);
            AccountsList.Children.Add(pendingButton);
            if (_pendingProfile.Key.Equals(
                    focusedAccountKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                focusedAccountButton = pendingButton;
            }
        }

        if (visibleAccounts.Count == 0 &&
            _pendingProfile is null &&
            _accountSearch.IsActive)
        {
            var emptyState = new TextBlock
            {
                Text = Localize("Main.NoAccountsMatch"),
                FontSize = 11,
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            emptyState.SetResourceReference(
                TextBlock.ForegroundProperty,
                "SubtleBrush");
            AutomationProperties.SetLiveSetting(
                emptyState,
                AutomationLiveSetting.Polite);
            AccountsList.Children.Add(emptyState);
        }

        UpdateAccountControlAvailability();
        RefreshBatchRetryState();

        if (restoreKeyboardFocus)
        {
            Control? accountFocusTarget =
                focusedAccountButton ?? activeAccountButton;
            if (accountFocusTarget is null && _accountSearch.IsActive)
                accountFocusTarget = AccountSearchBox;
            RestoreKeyboardFocus(accountFocusTarget);
        }
    }

    private static void RestoreKeyboardFocus(Control? control)
    {
        if (control is null)
            return;

        _ = control.Dispatcher.BeginInvoke(() =>
        {
            if (!control.IsVisible || !control.IsEnabled)
                return;
            control.Focus();
            control.BringIntoView();
        });
    }

    private Button CreateAccountButton(
        AccountProfile account,
        bool selected,
        bool pending = false,
        int positionInSet = 0,
        int sizeOfSet = 0)
    {
        var button = new Button
        {
            Tag = account.Key,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Width = 184,
            IsEnabled = !pending && _pendingProfile is null,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Click += AccountButton_Click;
        AutomationProperties.SetName(
            button,
            pending
                ? Localize("Main.NewAccountSignIn")
                : Localize(
                    "Main.SelectAccount",
                    account.Label ?? $"@{account.Username}"));
        AutomationProperties.SetItemStatus(
            button,
            selected
                ? Localize("Main.SelectedAccount")
                : Localize("Main.NotSelectedStatus"));
        if (!string.IsNullOrWhiteSpace(account.Group))
        {
            AutomationProperties.SetHelpText(
                button,
                Localize("Main.AccountGroup", account.Group));
        }
        if (!pending)
        {
            ConfigureAccountReordering(
                button,
                positionInSet,
                sizeOfSet);
        }

        var accountColor = (Color)ColorConverter.ConvertFromString(
            account.ColorHex ?? "#326FD1");

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        border.SetResourceReference(
            Border.BackgroundProperty,
            selected ? "CardSelectedBackgroundBrush" : "CardSurfaceBrush");
        border.SetResourceReference(
            Border.BorderBrushProperty,
            selected ? "CardSelectedBorderBrush" : "CardBorderBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var dot = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(accountColor)
        };
        dot.Child = CreateAccountIndicator(pending, selected, accountColor);

        var labels = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        var title = new TextBlock
        {
            Text = pending
                ? Localize("Main.NewAccount")
                : account.Label ?? $"@{account.Username}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        labels.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = pending
                ? Localize("Main.FinishSignIn")
                : account.Label is null
                    ? Localize("Main.UserId", account.UserId)
                    : Localize(
                        "Main.AccountIdentity",
                        account.Username,
                        account.UserId),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        subtitle.SetResourceReference(
            TextBlock.ForegroundProperty,
            "MutedBrush");
        labels.Children.Add(subtitle);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(dot);
        grid.Children.Add(labels);
        border.Child = grid;
        button.Content = border;
        return button;
    }

    private async void EditAccountButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => EditAccountButtonClickAsync());

    private async Task EditAccountButtonClickAsync()
    {
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null ||
            _activeProfile is null)
            return;

        var editedProfile = _activeProfile;
        var profileKey = editedProfile.Key;
        var dialog = new AccountAppearanceDialog(editedProfile) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var accountLabel = dialog.AccountLabel;
        var accountGroup = dialog.AccountGroup;
        var selectedColor = dialog.SelectedColor;
        var mutationApplied = false;
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    var profile = _settings.Accounts.FirstOrDefault(account =>
                        account.Key.Equals(
                            profileKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (profile is null)
                        return;
                    profile.Label = accountLabel;
                    profile.Group = accountGroup;
                    profile.ColorHex = selectedColor;
                    mutationApplied = true;
                },
                "Account details could not be saved",
                onCommitted: () =>
                {
                    if (mutationApplied)
                        RenderAccountList();
                }))
        {
            return;
        }
        if (!mutationApplied)
            return;
    }

    private async void ThemeToggleButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => ThemeToggleButtonClickAsync());

    private async Task ThemeToggleButtonClickAsync()
    {
        var useLightTheme = ThemeToggleButton.IsChecked != true;
        UpdateThemeTogglePresentation();
        if (_operationBusy || useLightTheme == _settings.UseLightTheme)
            return;

        SetOperationBusy(true);
        try
        {
            await TryCommitSettingsMutationAsync(
                () => _settings.UseLightTheme = useLightTheme,
                "Theme could not be saved",
                onCommitted: () =>
                {
                    ((App)Application.Current).ThemeService.ApplyPreference(
                        useLightTheme);
                    UpdateThemeTogglePresentation();
                });
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private void UpdateThemeTogglePresentation()
    {
        var isDarkTheme = !_settings.UseLightTheme;
        ThemeToggleButton.IsChecked = isDarkTheme;
        ThemeToggleIcon.Data = (Geometry)FindResource(
            isDarkTheme ? "IconMoon" : "IconSun");

        var highContrastSuffix =
            ((App)Application.Current).ThemeService.IsHighContrastActive
                ? Localize("Main.ThemeHighContrastSuffix")
                : string.Empty;
        var description = Localize(
            isDarkTheme
                ? "Main.ThemeDarkToLight"
                : "Main.ThemeLightToDark") + highContrastSuffix;
        ThemeToggleButton.ToolTip = description;
        AutomationProperties.SetName(ThemeToggleButton, description);
        AutomationProperties.SetHelpText(
            ThemeToggleButton,
            Localize("Main.ThemeHelp"));
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e) =>
        UpdateThemeTogglePresentation();

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        var app = (App)Application.Current;
        app.ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
        app.LocalizationService.LanguageChanged -=
            LocalizationService_LanguageChanged;
    }

    private async void SoundSettingsButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(SoundSettingsButtonClickAsync);

    private async Task SoundSettingsButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var dialog = new SoundSettingsDialog(
            _soundService,
            _settings.UiSoundsEnabled,
            _settings.StartupSound,
            _settings.CustomStartupSoundFileName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        SetOperationBusy(true);
        string? selectedCustomFileName = null;
        var uiSoundsEnabled = dialog.UiSoundsEnabled;
        var startupSound = dialog.StartupSound;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dialog.PendingCustomSourcePath is not null)
            {
                _soundService.StopPreview();
                selectedCustomFileName = await Task.Run(() =>
                    _soundService.ImportStartupSound(
                        dialog.PendingCustomSourcePath),
                    cancellationToken);
                _sessionImportedSoundFileNames.Add(selectedCustomFileName);
            }

            if (!await TryCommitSettingsMutationAsync(
                    () =>
                    {
                        _settings.UiSoundsEnabled = uiSoundsEnabled;
                        _settings.StartupSound = startupSound;
                        _settings.CustomStartupSoundFileName =
                            selectedCustomFileName ??
                            _settings.CustomStartupSoundFileName;
                    },
                    "Sound settings could not be saved",
                    onCommitted: () =>
                        ((App)Application.Current).UiSoundsEnabled =
                            uiSoundsEnabled))
            {
                return;
            }
            SetStatus(
                "Sound settings saved",
                "Your interface and startup sound choices stay on this PC.",
                "SETTINGS SAVED");
        }
        catch (Exception ex) when (IsExpectedSoundImportFailure(ex))
        {
            SetStatus(
                "Sound settings could not be saved",
                ex.Message,
                "SETTINGS ERROR");
        }
        finally
        {
            try
            {
                await ReconcileImportedSoundsAsync(cancellationToken);
            }
            finally
            {
                if (!_operationLifetime.IsShuttingDown)
                    SetOperationBusy(false);
            }
        }
    }

    internal static bool IsExpectedSoundImportFailure(Exception exception) =>
        exception is System.IO.IOException or UnauthorizedAccessException or
            System.IO.InvalidDataException or ArgumentException;

    private UIElement CreateAccountIndicator(
        bool pending,
        bool selected,
        Color accountColor)
    {
        var foreground = new SolidColorBrush(
            GetContrastingAccountForeground(accountColor));
        foreground.Freeze();
        if (pending)
        {
            return new Path
            {
                Data = (Geometry)FindResource("IconAdd"),
                Stroke = foreground,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(7)
            };
        }

        if (!selected)
        {
            return new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = foreground,
                Opacity = 0.78,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new Path
        {
            Data = (Geometry)FindResource("IconCheck"),
            Stroke = foreground,
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(6)
        };
    }

    internal static Color GetContrastingAccountForeground(Color background)
    {
        var luminance = GetRelativeLuminance(background);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;
        return whiteContrast >= blackContrast ? Colors.White : Colors.Black;
    }

    private static double GetRelativeLuminance(Color color)
    {
        static double Linearize(byte component)
        {
            var channel = component / 255d;
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }

    private void AccountsScrollViewer_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer ||
            scrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        var targetOffset = CalculateHorizontalWheelOffset(
            scrollViewer.HorizontalOffset,
            scrollViewer.ScrollableWidth,
            e.Delta);
        if (targetOffset == scrollViewer.HorizontalOffset)
            return;

        scrollViewer.ScrollToHorizontalOffset(targetOffset);
        e.Handled = true;
    }

    internal static double CalculateHorizontalWheelOffset(
        double currentOffset,
        double scrollableWidth,
        int wheelDelta) =>
        Math.Clamp(
            currentOffset - wheelDelta / 3d,
            0,
            Math.Max(0, scrollableWidth));

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountDragInProgress ||
            _accountReorderInProgress ||
            sender is Button { Tag: string key } &&
            _suppressedAccountClickKey?.Equals(
                key,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        await RunWindowOperationAsync(cancellationToken =>
            AccountButtonClickAsync(sender, cancellationToken));
    }

    private async Task AccountButtonClickAsync(
        object sender,
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;
        if (sender is not Button { Tag: string key })
            return;
        var profile = _settings.Accounts.FirstOrDefault(account => account.Key == key);
        if (profile is null || profile == _activeProfile)
            return;

        if (_destinationDraftDirty && !_destinationDraftValid)
        {
            SetStatus(
                "Account was not switched",
                "Enter a valid Roblox destination or restore the saved value before switching accounts.",
                "INVALID DESTINATION");
            return;
        }

        var outgoingDestination = _destinationDraftDirty
            ? CreateDestinationPersistenceRequest()
            : null;
        _destinationPersistence.Cancel();
        var mutationApplied = false;
        AccountProfile? committedProfile = null;
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    if (outgoingDestination is not null &&
                        IsCurrentDestinationRequest(outgoingDestination))
                    {
                        var outgoing = _settings.Accounts.FirstOrDefault(account =>
                            account.Key.Equals(
                                outgoingDestination.AccountKey,
                                StringComparison.OrdinalIgnoreCase));
                        if (outgoing is not null)
                        {
                            outgoing.Destination =
                                outgoingDestination.Destination;
                        }
                    }

                    if (!_settings.Accounts.Any(account => account.Key == key))
                        return;
                    _settings.ActiveAccountKey = key;
                    mutationApplied = true;
                },
                $"Could not switch to @{profile.Username}",
                "ACCOUNT SWITCH ERROR",
                onCommitted: () =>
                {
                    if (!mutationApplied)
                        return;
                    committedProfile = _settings.Accounts.First(account =>
                        account.Key.Equals(
                            key,
                            StringComparison.OrdinalIgnoreCase));
                    _activeProfile = committedProfile;
                    _pendingProfile = null;
                    ShowDestinationForProfile(committedProfile);
                    RenderAccountList();
                }))
        {
            return;
        }
        if (!mutationApplied)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (committedProfile is null ||
            !string.Equals(
                _activeProfile?.Key,
                key,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        SetStatus($"Switching to @{profile.Username}", "Loading its isolated Roblox session…", "SWITCHING");
        await InitializeBrowserAsync(
            committedProfile,
            showLogin: false,
            cancellationToken);
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            AddAccountButtonClickAsync(cancellationToken));

    private async Task AddAccountButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;

        if (!await FlushDestinationPersistenceAsync())
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;

        var key = Guid.NewGuid().ToString("N");
        _pendingProfile = new AccountProfile
        {
            Key = key,
            SessionFolder = $@"Profiles\{key}",
            Destination = GetMostRecentDestination()
        };
        _activeProfile = _pendingProfile;
        _currentUser = null;
        ShowDestinationForProfile(_pendingProfile);
        RenderAccountList();
        LauncherPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        await InitializeBrowserAsync(
            _pendingProfile,
            showLogin: true,
            cancellationToken);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            SignInButtonClickAsync(cancellationToken));

    private async Task SignInButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;
        if (_activeProfile is null)
        {
            await AddAccountButtonClickAsync(cancellationToken);
            return;
        }

        LauncherPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        if (!TryGetCurrentWebSessionToken(_activeProfile, out var token) ||
            !_webSession.IsReady)
        {
            await InitializeBrowserAsync(
                _activeProfile,
                showLogin: true,
                cancellationToken);
        }
        else
        {
            _webSession.NavigateToLogin(token);
        }
    }

    private async void BrowserBackButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(BrowserBackButtonClickAsync);

    private async Task BrowserBackButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingProfile is null)
        {
            BrowserPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            return;
        }

        if (!await _accountCheckLock.WaitAsync(0, cancellationToken))
        {
            SetStatus(
                "Account verification is still finishing",
                "Wait for the current sign-in check to finish before discarding this temporary session.",
                "ACCOUNT CHECK PENDING");
            return;
        }

        AccountProfile? nextProfile = null;
        try
        {
            if (_pendingProfile is null)
            {
                BrowserPanel.Visibility = Visibility.Collapsed;
                LauncherPanel.Visibility = Visibility.Visible;
                return;
            }

            if (!await ClearCurrentBrowserProfileAsync(cancellationToken))
            {
                SetStatus(
                    "Temporary session could not be cleared",
                    "Keep this window open and try Back again before closing SessionDock.",
                    "CLEANUP ERROR");
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            BrowserPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            _pendingProfile = null;
            _activeProfile = FindActiveSavedProfile();
            nextProfile = _activeProfile;
            ShowDestinationForProfile(nextProfile);
            RenderAccountList();
        }
        finally
        {
            _accountCheckLock.Release();
        }

        if (nextProfile is not null)
            await InitializeBrowserAsync(
                nextProfile,
                showLogin: false,
                cancellationToken);
        else
            SetSignedOutState();
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsAutoJoinWatchActive)
        {
            RequestAutoJoinStop();
            return;
        }

        await RunWindowOperationAsync(
            _joinUserMode && AutoJoinUserCheckBox.IsChecked == true
                ? StartAutoJoinWatchAsync
                : cancellationToken => LaunchButtonClickAsync(
                    cancellationToken));
    }

    private async Task LaunchButtonClickAsync(
        CancellationToken cancellationToken,
        JoinUserResolution? preResolvedJoinUser = null,
        string? expectedAccountKey = null,
        long? expectedAccountUserId = null,
        bool operationReserved = false)
    {
        if (IsAutoJoinWatchActive ||
            !operationReserved && _operationBusy)
            return;
        if (!operationReserved)
            SetOperationBusy(true);
        try
        {
            await LaunchAsync(
                cancellationToken,
                preResolvedJoinUser: preResolvedJoinUser,
                expectedAccountKey: expectedAccountKey,
                expectedAccountUserId: expectedAccountUserId);
        }
        finally
        {
            _launchInProgress = false;
            if (!_operationLifetime.IsShuttingDown)
            {
                SetOperationBusy(false);
                UpdateAutoJoinActionPresentation();
            }
        }
    }

    private async Task LaunchAsync(
        CancellationToken cancellationToken,
        ExternalRobloxLink? externalLink = null,
        JoinUserResolution? preResolvedJoinUser = null,
        string? expectedAccountKey = null,
        long? expectedAccountUserId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountDestination = externalLink?.Destination ??
            PlaceIdBox.Text.Trim();
        JoinUserIdentifier? joinUser = null;
        string destination;
        LaunchTarget? target;
        string? serverJobId;
        long? trackedPlaceId;
        string parseError;
        if (externalLink is not null)
        {
            destination = externalLink.Destination;
            target = externalLink.Target;
            serverJobId = null;
            trackedPlaceId = null;
            parseError = string.Empty;
        }
        else if (_joinUserMode)
        {
            if (!JoinUserDestination.TryParseInput(
                    accountDestination,
                    out joinUser,
                    out parseError))
            {
                SetStatus("User is not valid", parseError, "INVALID USER");
                return;
            }

            destination = string.Empty;
            target = null;
            serverJobId = null;
            trackedPlaceId = null;
        }
        else if (!TryResolveLaunchInput(
                     accountDestination,
                     out destination,
                     out target,
                     out serverJobId,
                     out trackedPlaceId,
                     out parseError))
        {
            SetStatus("Destination is not valid", parseError, "INVALID DESTINATION");
            return;
        }

        if (externalLink is null &&
            !await FlushDestinationPersistenceAsync())
            return;

        await CheckAuthenticatedAccountAsync(
            cancellationToken: cancellationToken);
        var currentUser = _currentUser;
        var activeProfile = _activeProfile;
        if (currentUser is null || activeProfile is null ||
            currentUser.Id != activeProfile.UserId ||
            !TryGetCurrentWebSessionToken(activeProfile, out var sessionToken))
            return;
        if (expectedAccountKey is not null &&
            (!string.Equals(
                 activeProfile.Key,
                 expectedAccountKey,
                 StringComparison.Ordinal) ||
             expectedAccountUserId != currentUser.Id))
        {
            SetStatus(
                "Auto-join watch ended",
                "The selected account changed before Roblox Player could start. No launch was attempted.",
                "WATCH ENDED");
            return;
        }

        _launchInProgress = true;
        SetReadyState();

        JoinUserResolution? joinUserResolution = null;
        if (joinUser is not null)
        {
            if (preResolvedJoinUser is not null &&
                DoesJoinUserResolutionMatch(
                    joinUser,
                    preResolvedJoinUser))
            {
                joinUserResolution = preResolvedJoinUser;
            }
            else
            {
                SetStatus(
                    "Checking whether this user can be joined",
                    $"Resolving {joinUser.DisplayValue} through @{currentUser.Name}'s isolated Roblox session…",
                    "CHECKING USER");
                LaunchButton.IsEnabled = false;
                var lookup = await _webSession.ResolveJoinUserAsync(
                    joinUser,
                    sessionToken,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentWebSessionOwner(sessionToken))
                    return;
                if (lookup.Resolution is null)
                {
                    _launchInProgress = false;
                    ShowJoinUserUnavailable(joinUser, lookup.Availability);
                    return;
                }

                joinUserResolution = lookup.Resolution;
            }

            target = new LaunchTarget(
                joinUserResolution.PlaceId,
                null,
                null);
            destination = joinUserResolution.PlaceId.ToString(
                CultureInfo.InvariantCulture);
            serverJobId = joinUserResolution.ServerJobId;
        }

        if (expectedAccountKey is not null &&
            (!string.Equals(
                 _activeProfile?.Key,
                 expectedAccountKey,
                 StringComparison.Ordinal) ||
             _currentUser?.Id != expectedAccountUserId ||
             !IsCurrentWebSessionOwner(sessionToken)))
        {
            _launchInProgress = false;
            SetStatus(
                "Auto-join watch ended",
                "The selected account or Roblox session changed before a ticket was requested. No launch was attempted.",
                "WATCH ENDED");
            return;
        }

        if (target!.ShareCode is not null)
        {
            SetStatus(
                "Resolving private-server link",
                "Looking up the hidden experience and private-server link code…",
                "RESOLVING SERVER");
            LaunchButton.IsEnabled = false;
            target = await _webSession.ResolvePrivateServerAsync(
                target.ShareCode,
                sessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(sessionToken))
                return;
            if (target is null)
            {
                _launchInProgress = false;
                SetStatus(
                    "Private-server link could not be resolved",
                    "The code may be invalid, expired, or unavailable to the selected account.",
                    "SERVER LINK ERROR");
                LaunchButtonLabel.Text = Localize("Main.Launch");
                LaunchButton.IsEnabled = true;
                return;
            }
        }

        if (trackedPlaceId is not null && target.PlaceId != trackedPlaceId)
        {
            _launchInProgress = false;
            SetStatus(
                "Tracked server does not match its experience",
                "The saved server record is inconsistent and was not launched.",
                "SERVER RECORD ERROR");
            LaunchButtonLabel.Text = Localize("Main.Launch");
            return;
        }

        SetStatus(
            joinUserResolution is not null
                ? $"Joining @{joinUserResolution.Username} as @{currentUser.Name}"
                : serverJobId is null
                ? $"Preparing @{currentUser.Name}"
                : $"Rejoining server as @{currentUser.Name}",
            joinUserResolution is not null
                ? "Requesting a fresh game-client ticket for the selected account and the user's current server…"
                : serverJobId is null
                ? "Requesting a secure Roblox game-client ticket for the selected account…"
                : $"Targeting tracked server {serverJobId[..8]}… with a fresh account ticket.",
            "GETTING TICKET");
        LaunchButton.IsEnabled = false;

        var ticketTask = _webSession.GetAuthenticationTicketAsync(
            sessionToken,
            cancellationToken);
        var nameTask = TryGetExperienceNameAsync(
            target.PlaceId,
            sessionToken,
            cancellationToken);
        var localeTask = _webSession.GetUserLocaleAsync(
            sessionToken,
            cancellationToken);
        await Task.WhenAll(ticketTask, nameTask, localeTask);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentWebSessionOwner(sessionToken))
            return;
        var ticket = ticketTask.Result;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            _launchInProgress = false;
            SetStatus(
                "Roblox did not issue a launch ticket",
                "Refresh this account slot by signing in again, then retry.",
                "TICKET ERROR");
            SignInButton.Visibility = Visibility.Visible;
            SignInButtonLabel.Text = Localize("Main.RefreshSignIn");
            AutomationProperties.SetName(
                SignInButton,
                Localize("Main.RefreshSignInName"));
            LaunchButtonLabel.Text = Localize("Main.Launch");
            return;
        }

        var recent = new RecentExperience
        {
            Destination = destination,
            PlaceId = target.PlaceId,
            Name = nameTask.Result,
            IsPrivateServer = target.IsPrivateServer,
            ServerJobId = serverJobId,
            AccountUserId = currentUser.Id,
            AccountUsername = currentUser.Name,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };
        var locale = localeTask.Result;
        if (!IsCurrentWebSessionOwner(sessionToken))
            return;
        var launchUri = joinUserResolution is not null
            ? RobloxLaunchUriBuilder.BuildFollowUser(
                joinUserResolution.UserId,
                ticket,
                locale)
            : RobloxLaunchUriBuilder.Build(target, ticket, serverJobId, locale);
        await LaunchClientAsync(
            launchUri,
            activeProfile,
            recent,
            cancellationToken,
            saveToHistory:
                ExternalRobloxLinkPolicy.ShouldSaveToHistory(externalLink));
    }

    internal static bool DoesJoinUserResolutionMatch(
        JoinUserIdentifier requestedUser,
        JoinUserResolution resolution) =>
        requestedUser.UserId is long userId
            ? userId == resolution.UserId
            : string.Equals(
                requestedUser.Username,
                resolution.Username,
                StringComparison.OrdinalIgnoreCase);

    private void ShowJoinUserUnavailable(
        JoinUserIdentifier requestedUser,
        JoinUserAvailability availability)
    {
        var (title, detail, badge) = availability switch
        {
            JoinUserAvailability.UserNotFound => (
                "Roblox user was not found",
                "Check the exact username, user ID, or profile URL and try again.",
                "USER NOT FOUND"),
            JoinUserAvailability.Offline => (
                $"{requestedUser.DisplayValue} is not joinable right now",
                "The user is offline, hiding their activity, or unavailable to the selected account.",
                "USER OFFLINE"),
            JoinUserAvailability.NotInExperience => (
                $"{requestedUser.DisplayValue} is not in an experience",
                "They may only be online on the website or using Roblox Studio.",
                "NOT IN GAME"),
            JoinUserAvailability.NotJoinable => (
                $"{requestedUser.DisplayValue} has no usable current server",
                "Privacy, experience, or current server details may be unavailable.",
                "JOINS UNAVAILABLE"),
            JoinUserAvailability.RateLimited => (
                "Roblox asked SessionDock to check less often",
                "Wait a moment before trying to join this user again.",
                "USER CHECK LIMITED"),
            JoinUserAvailability.SessionUnavailable => (
                "The selected Roblox session is unavailable",
                "Reconnect this account before trying to join a user.",
                "SIGN-IN NEEDED"),
            _ => (
                "Roblox could not check this user",
                "The Roblox user or presence service did not return a usable result. Try again shortly.",
                "USER CHECK ERROR")
        };
        if (availability == JoinUserAvailability.SessionUnavailable)
        {
            _currentUser = null;
            SetSignedOutState();
            SignInButtonLabel.Text = Localize("Main.Reconnect");
            AutomationProperties.SetName(
                SignInButton,
                Localize("Main.ReconnectName"));
        }
        SetStatus(title, detail, badge);
        if (availability != JoinUserAvailability.SessionUnavailable)
        {
            LaunchButtonLabel.Text = Localize("Main.JoinUserButton");
            LaunchButton.IsEnabled = true;
        }
    }

    private async Task<string?> TryGetExperienceNameAsync(
        long placeId,
        WebSessionToken sessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _webSession.GetExperienceNameAsync(
                placeId,
                sessionToken,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebSessionUnavailableException exception) when (
            exception.Reason != WebSessionUnavailableReason.Superseded)
        {
            return null;
        }
    }

    private async Task LaunchClientAsync(
        string launchUri,
        AccountProfile account,
        RecentExperience recent,
        CancellationToken cancellationToken,
        bool saveToHistory = true)
    {
        SetStatus(
            $"Launching as @{_currentUser?.Name}",
            "Handing this destination directly to Roblox Player…",
            "STARTING CLIENT");
        LaunchButton.IsEnabled = false;

        var launchStartedAt = DateTimeOffset.UtcNow;
        var result = await _robloxClient.LaunchAsync(
            launchUri,
            cancellationToken);
        TrackLaunchedClient(result.PlayerIdentity, account, recent);
        cancellationToken.ThrowIfCancellationRequested();
        _launchInProgress = false;
        if (result is { Success: true, ProcessId: int processId })
        {
            if (saveToHistory)
                await SaveRecentExperienceAsync(recent);
            if (saveToHistory && _settings.RecentExperiences.Contains(recent))
                BeginServerTracking(recent, launchStartedAt);
            SetStatus(
                "Roblox Player started",
                _launchHook.IsConfigured
                    ? "Running configured local launch integrations…"
                    : "Checking optional local launch integrations…",
                "CLIENT STARTED");
            var accountLabel = _activeProfile?.Label;
            await NotifyLaunchHookAsync(
                recent,
                processId,
                accountLabel,
                cancellationToken);
            SetStatus(
                "Roblox Player started",
                _launchHook.IsConfigured
                    ? "Configured local integrations finished their bounded attempt. They never control launch success."
                    : "No local launch integration is configured, so that step was skipped.",
                "CLIENT STARTED");
            RefreshLaunchAvailability();
            LaunchButtonLabel.Text = Localize("Main.Launch");
            return;
        }

        LaunchButtonLabel.Text = Localize("Main.Launch");
        LaunchButton.IsEnabled = true;
        SetStatus("Roblox Player is unavailable", result.Error!, "CLIENT ERROR");
    }

    private async Task NotifyLaunchHookAsync(
        RecentExperience recent,
        int processId,
        string? accountLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await _launchHook.NotifyLaunchAsync(new LaunchHookEvent(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                processId,
                recent.PlaceId,
                recent.CustomName ?? recent.Name,
                recent.IsPrivateServer,
                recent.AccountUserId,
                recent.AccountUsername ?? string.Empty,
                accountLabel),
                cancellationToken);
        }
        catch
        {
            // A custom local integration must never change launch success.
        }
    }

    private void BeginServerTracking(
        RecentExperience recent,
        DateTimeOffset launchStartedAt)
    {
        var trackingTask = TrackJoinedServerAsync(recent, launchStartedAt);
        _ = trackingTask.ContinueWith(
            completed => System.Diagnostics.Trace.WriteLine(
                $"Roblox server tracking faulted: {completed.Exception?.GetBaseException().GetType().Name}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task TrackJoinedServerAsync(
        RecentExperience recent,
        DateTimeOffset launchStartedAt)
    {
        try
        {
            var serverJobId = await _serverTracker.FindJoinedServerAsync(
                recent.AccountUserId,
                recent.PlaceId,
                launchStartedAt,
                _operationLifetime.Token);
            _operationLifetime.Token.ThrowIfCancellationRequested();
            if (serverJobId is null ||
                !_settings.RecentExperiences.Contains(recent))
            {
                return;
            }

            await SaveRecentMetadataAsync(
                () =>
                {
                    if (_settings.RecentExperiences.Contains(recent))
                        recent.ServerJobId = serverJobId;
                },
                showError: false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown cancels optional local server detection.
        }
        catch (Exception ex) when (
            LocalDataException.IsExpectedPersistenceFailure(ex))
        {
            System.Diagnostics.Trace.WriteLine(
                $"Roblox server detection failed: {ex.GetType().Name}.");
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(ResetButtonClickAsync);

    private async Task ResetButtonClickAsync(CancellationToken cancellationToken)
    {
        if (_operationBusy || _accountReorderInProgress)
            return;
        var profile = _activeProfile;
        if (profile is null || _pendingProfile is not null)
            return;

        var result = MessageBox.Show(
            $"Remove @{profile.Username} from SessionDock? Its isolated sign-in will be cleared. Recent and Favorites entries for this account will remain until you remove or clear them.",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        _destinationPersistence.Cancel();
        SetOperationBusy(true);
        try
        {
            await Task.Run(
                () => _settingsService.StageProfileDeletion(profile.Key),
                cancellationToken);
            if (!await TryCommitSettingsMutationAsync(
                    () =>
                    {
                        _settings.PendingProfileDeletionKeys = new[]
                            {
                                profile.Key
                            }
                            .Concat(_settings.PendingProfileDeletionKeys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(SettingsService.MaximumPendingProfileDeletions)
                            .ToList();
                        _settings.Accounts.RemoveAll(
                            account => account.Key == profile.Key);
                        BatchLaunchPreferences.PrunePresetsForCurrentAccounts(
                            _settings);
                        _settings.ActiveAccountKey =
                            _settings.Accounts.FirstOrDefault()?.Key;
                    },
                    "Account removal could not be saved",
                    "ACCOUNT SAVE ERROR",
                    "SessionDock could not save the account removal, so the account and its isolated sign-in data were left unchanged. Make %LOCALAPPDATA%\\SessionDock writable, then retry."))
            {
                if (!await Task.Run(
                        () => _settingsService.ClearProfileDeletionJournal(
                            profile.Key),
                        CancellationToken.None))
                {
                    SetStatus(
                        "Account removal is queued",
                        "SessionDock could not cancel the durable removal request. It will safely finish removing this account after local settings become writable.",
                        "CLEANUP WARNING");
                }
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_operationLifetime.IsShuttingDown)
                return;

            var profileWasCleared = false;
            try
            {
                profileWasCleared = await ClearBrowserProfileAsync(
                    profile,
                    cancellationToken,
                    requireDeletionIntent: true);
            }
            finally
            {
                _activeProfile = _settings.Accounts.FirstOrDefault();
                ShowDestinationForProfile(_activeProfile);
                _currentUser = null;
                RenderAccountList();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var cleanupAcknowledged = profileWasCleared &&
                await AcknowledgePendingProfileDeletionsAsync([profile.Key]);
            var journalCleared = cleanupAcknowledged &&
                await Task.Run(
                    () => _settingsService.ClearProfileDeletionJournal(
                        profile.Key),
                    CancellationToken.None);
            if (_activeProfile is not null)
                await InitializeBrowserAsync(
                    _activeProfile,
                    showLogin: false,
                    cancellationToken);
            else
                SetSignedOutState();

            if (!profileWasCleared || !cleanupAcknowledged || !journalCleared)
            {
                SetStatus(
                    "Account removed; local cleanup is pending",
                    "The account removal was saved, but some isolated browser files are still in use or the cleanup acknowledgement could not be saved. SessionDock will retry this exact removal on the next start.",
                    "CLEANUP WARNING");
            }
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private async Task<bool> ClearCurrentBrowserProfileAsync(
        CancellationToken cancellationToken)
    {
        var profile = _pendingProfile ?? _activeProfile;
        if (profile is null)
            return true;

        return await ClearBrowserProfileAsync(profile, cancellationToken);
    }

    private async Task<bool> ClearBrowserProfileAsync(
        AccountProfile profile,
        CancellationToken cancellationToken,
        bool requireDeletionIntent = false)
    {
        if (TryGetAffineWebSessionToken(profile, out var sessionToken))
        {
            try
            {
                await _webSession.ClearProfileAsync(
                    sessionToken,
                    cancellationToken);
            }
            catch (WebSessionUnavailableException)
            {
                // A failed current browser still owns profile resources and
                // must be released before the exact deletion is retried.
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (HasCurrentWebSessionAffinity(sessionToken))
            {
                _webSession.ReleaseBrowser();
                _webSessionToken = null;
                BrowserHost.Children.Clear();
            }
        }
        var directoryRemoved = requireDeletionIntent
            ? await _settingsService.DeletePendingProfileAsync(
                profile.Key,
                _settings,
                cancellationToken)
            : await _settingsService.DeleteSessionDataAsync(
                profile,
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return directoryRemoved;
    }

    private void PlaceIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_destinationTrackingEnabled)
            UpdateDestinationDraftFromText();
        if (LaunchButton is not null)
            RefreshLaunchAvailability();
    }

    private void ExperienceDestinationModeButton_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_updatingDestinationModeSelection)
            ChangeDestinationMode(joinUser: false);
    }

    private void UserDestinationModeButton_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_updatingDestinationModeSelection)
            ChangeDestinationMode(joinUser: true);
    }

    private void ChangeDestinationMode(bool joinUser)
    {
        if (_operationBusy || _launchInProgress || IsAutoJoinWatchActive ||
            _joinUserMode == joinUser)
            return;

        _destinationPersistence.Cancel();
        var trackingWasEnabled = _destinationTrackingEnabled;
        _destinationTrackingEnabled = false;
        _joinUserMode = joinUser;
        _destinationModeAwaitingInput = true;
        PlaceIdBox.Text = string.Empty;
        _destinationTrackingEnabled = trackingWasEnabled;
        UpdateDestinationModePresentation();
        _destinationRevision++;
        _destinationDraftValue = _destinationPersistedValue;
        _destinationDraftValid = true;
        _destinationDraftDirty = false;
        RefreshLaunchAvailability();
        PlaceIdBox.Focus();
    }

    private void UpdateDestinationModePresentation()
    {
        _updatingDestinationModeSelection = true;
        try
        {
            ExperienceDestinationModeButton.IsChecked = !_joinUserMode;
            UserDestinationModeButton.IsChecked = _joinUserMode;
        }
        finally
        {
            _updatingDestinationModeSelection = false;
        }
        DestinationHelpText.Text = _joinUserMode
            ? Localize("Main.JoinUserHelp")
            : Localize("Main.DestinationHelp");
        PlaceIdBox.ToolTip = _joinUserMode
            ? Localize("Main.JoinUserTooltip")
            : Localize("Main.DestinationInputTooltip");
        AutomationProperties.SetName(
            PlaceIdBox,
            _joinUserMode
                ? Localize("Main.JoinUserInputName")
                : Localize("Main.DestinationInputName"));
        SetDestinationForAllButton.ToolTip = _joinUserMode
            ? Localize("Main.SetUserForAllTooltip")
            : Localize("Main.SetForAllTooltip");
        BatchLaunchButton.ToolTip = _joinUserMode
            ? Localize("Main.BatchJoinUserTooltip")
            : Localize("Main.BatchTooltip");
        if (!IsAutoJoinWatchActive)
            AutoJoinWatchDetailText.Text = Localize("Main.AutoJoinHelp");
        UpdateAutoJoinActionPresentation();
    }

    private async void PlaceIdBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        await RunWindowOperationAsync(_ =>
            FlushDestinationPersistenceAsync(showInvalidError: false));

    private void PlaceIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !LaunchButton.IsEnabled)
            return;

        e.Handled = true;
        LaunchButton_Click(LaunchButton, new RoutedEventArgs());
    }

    private async void SetDestinationForAllButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => SetDestinationForAllButtonClickAsync());

    private async Task SetDestinationForAllButtonClickAsync()
    {
        if (_operationBusy || _launchInProgress || IsAutoJoinWatchActive ||
            _pendingProfile is not null)
            return;

        if (_settings.Accounts.Count == 0)
        {
            SetStatus(
                "Destination was not changed",
                "Add an account before setting a shared destination.",
                "INVALID DESTINATION");
            RefreshLaunchAvailability();
            return;
        }
        if (!TryNormalizeCurrentDestinationInput(
                PlaceIdBox.Text,
                out var storedDestination,
                out _,
                out var error))
        {
            SetStatus("Destination was not changed", error, "INVALID DESTINATION");
            RefreshLaunchAvailability();
            return;
        }

        _destinationPersistence.Cancel();
        var assignedCount = _settings.Accounts.Count;
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    foreach (var account in _settings.Accounts)
                        account.Destination = storedDestination;
                },
                "Shared destination could not be saved",
                "DESTINATION SAVE ERROR",
                onCommitted: () =>
                {
                    ShowDestinationForProfile(_activeProfile);
                    RefreshLaunchAvailability();
                }))
        {
            ShowDestinationForProfile(_activeProfile);
            RefreshLaunchAvailability();
            return;
        }
        SetStatus(
            "Destination set for all accounts",
            $"Saved this {(_joinUserMode ? "user" : "experience")} destination for {assignedCount} account{(assignedCount == 1 ? string.Empty : "s")}.",
            "DESTINATION SAVED");
    }

    private bool TryResolveLaunchInput(
        string input,
        out string destination,
        out LaunchTarget? target,
        out string? serverJobId,
        out long? trackedPlaceId,
        out string error)
    {
        if (LaunchInputResolver.TryResolve(
                input,
                _settings.RecentExperiences,
                out var resolved,
                out error))
        {
            destination = resolved!.Destination;
            target = resolved.Target;
            serverJobId = resolved.ServerJobId;
            trackedPlaceId = resolved.TrackedPlaceId;
            return true;
        }

        destination = input.Trim();
        target = null;
        serverJobId = null;
        trackedPlaceId = null;
        return false;
    }

    private void UpdateDestinationDraftFromText()
    {
        _destinationPersistence.Cancel();
        var profile = _activeProfile;
        if (profile is null ||
            _pendingProfile is not null ||
            !string.Equals(
                _destinationDraftAccountKey,
                profile.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _destinationRevision++;
        var input = PlaceIdBox.Text.Trim();
        if (input.Length == 0 && _destinationModeAwaitingInput)
        {
            _destinationDraftValue = _destinationPersistedValue;
            _destinationDraftValid = true;
        }
        else if (input.Length == 0)
        {
            _destinationDraftValue = null;
            _destinationDraftValid = true;
        }
        else if (TryNormalizeCurrentDestinationInput(
                     input,
                     out var storedDestination,
                     out _,
                     out _))
        {
            _destinationModeAwaitingInput = false;
            _destinationDraftValue = storedDestination;
            _destinationDraftValid = true;
        }
        else
        {
            _destinationDraftValue = null;
            _destinationDraftValid = false;
        }

        _destinationDraftDirty = !_destinationDraftValid ||
            !string.Equals(
                _destinationPersistedValue,
                _destinationDraftValue,
                StringComparison.Ordinal);
        if (_destinationDraftValid && _destinationDraftDirty)
        {
            var request = CreateDestinationPersistenceRequest();
            PersistDestinationAfterDelay(request);
        }
    }

    private async void PersistDestinationAfterDelay(
        DestinationPersistenceRequest request) =>
        await RunWindowOperationAsync(_ =>
            _destinationPersistence.ScheduleAsync(request));

    private async Task<bool> FlushDestinationPersistenceAsync(
        bool showInvalidError = true)
    {
        _destinationPersistence.Cancel();
        if (_activeProfile is null || _pendingProfile is not null ||
            !_destinationDraftDirty)
        {
            return true;
        }
        if (!_destinationDraftValid)
        {
            if (showInvalidError && !_operationLifetime.IsShuttingDown)
            {
                SetStatus(
                    "Destination was not saved",
                    "Enter a valid Roblox destination before leaving this account.",
                    "INVALID DESTINATION");
            }
            return false;
        }

        return await _destinationPersistence.FlushAsync(
            CreateDestinationPersistenceRequest());
    }

    private async Task<bool> PersistDestinationRequestAsync(
        DestinationPersistenceRequest request)
    {
        var applied = false;
        var stale = false;
        var committed = await TryCommitSettingsMutationAsync(
            () =>
            {
                if (!IsCurrentDestinationRequest(request))
                {
                    stale = true;
                    return;
                }

                var profile = _settings.Accounts.FirstOrDefault(account =>
                    account.Key.Equals(
                        request.AccountKey,
                        StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    stale = true;
                    return;
                }
                profile.Destination = request.Destination;
                applied = true;
            },
            "Destination could not be saved",
            "DESTINATION SAVE ERROR",
            onCommitted: () =>
            {
                if (!applied ||
                    _pendingProfile is not null ||
                    _activeProfile is null ||
                    request.OwnerEpoch != _destinationOwnerEpoch ||
                    !request.AccountKey.Equals(
                        _destinationDraftAccountKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    !request.AccountKey.Equals(
                        _activeProfile.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _destinationPersistedValue = request.Destination;
                _destinationDraftDirty = !_destinationDraftValid ||
                    !string.Equals(
                        _destinationPersistedValue,
                        _destinationDraftValue,
                        StringComparison.Ordinal);
            });
        if (!committed || stale || !applied)
            return false;
        return true;
    }

    private DestinationPersistenceRequest CreateDestinationPersistenceRequest() =>
        new(
            _destinationDraftAccountKey!,
            _destinationOwnerEpoch,
            _destinationRevision,
            _destinationDraftValue);

    private bool IsCurrentDestinationRequest(
        DestinationPersistenceRequest request) =>
        _pendingProfile is null &&
        _activeProfile is not null &&
        request.OwnerEpoch == _destinationOwnerEpoch &&
        request.Revision == _destinationRevision &&
        _destinationDraftValid &&
        string.Equals(
            request.AccountKey,
            _destinationDraftAccountKey,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            request.AccountKey,
            _activeProfile.Key,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            request.Destination,
            _destinationDraftValue,
            StringComparison.Ordinal);

    private void ShowDestinationForProfile(AccountProfile? profile)
    {
        _destinationPersistence.Cancel();
        var trackingWasEnabled = _destinationTrackingEnabled;
        _destinationTrackingEnabled = false;
        var storedDestination = profile?.Destination;
        _joinUserMode = JoinUserDestination.TryParseStored(
            storedDestination,
            out var joinUser,
            out _);
        PlaceIdBox.Text = _joinUserMode
            ? joinUser!.DisplayValue
            : storedDestination ?? string.Empty;
        _destinationOwnerEpoch++;
        _destinationRevision++;
        _destinationDraftAccountKey = profile?.Key;
        _destinationDraftValue = profile?.Destination;
        _destinationPersistedValue = profile?.Destination;
        _destinationDraftValid = true;
        _destinationDraftDirty = false;
        _destinationModeAwaitingInput = false;
        _destinationTrackingEnabled = trackingWasEnabled;
        UpdateDestinationModePresentation();
        ResetDestinationViewport();
    }

    private DestinationPersistenceRequest? CaptureShutdownDestinationRequest()
    {
        if (!_destinationDraftValid || !_destinationDraftDirty ||
            _destinationDraftAccountKey is null)
        {
            return null;
        }

        return new DestinationPersistenceRequest(
            _destinationDraftAccountKey,
            _destinationOwnerEpoch,
            _destinationRevision,
            _destinationDraftValue);
    }

    private void ResetDestinationViewport()
    {
        PlaceIdBox.CaretIndex = 0;
        PlaceIdBox.ScrollToHome();
        PlaceIdBox.Dispatcher.BeginInvoke(() =>
        {
            PlaceIdBox.CaretIndex = 0;
            PlaceIdBox.ScrollToHome();
        });
    }

    private string? GetMostRecentDestination() =>
        _settings.RecentExperiences
            .OrderByDescending(item => item.LastLaunchedAt)
            .Select(item => item.Destination)
            .FirstOrDefault();

    private bool TryNormalizeCurrentDestinationInput(
        string input,
        out string? storedDestination,
        out ResolvedLaunchInput? resolvedExperience,
        out string error)
    {
        if (_joinUserMode)
        {
            resolvedExperience = null;
            if (!JoinUserDestination.TryParseInput(
                    input,
                    out var joinUser,
                    out error))
            {
                storedDestination = null;
                return false;
            }

            storedDestination = JoinUserDestination.CreateStoredValue(joinUser!);
            return true;
        }

        if (!LaunchInputResolver.TryResolve(
                input,
                _settings.RecentExperiences,
                out resolvedExperience,
                out error))
        {
            storedDestination = null;
            return false;
        }

        storedDestination = resolvedExperience!.AccountDestination;
        return true;
    }

    private void RefreshLaunchAvailability()
    {
        if (LaunchButton is null)
            return;

        var destination = PlaceIdBox.Text.Trim();
        var destinationIsValid = TryNormalizeCurrentDestinationInput(
            destination,
            out _,
            out var resolvedInput,
            out var validationError);
        if (DestinationValidationText is not null)
        {
            if (_destinationValidationLiveRegion is { } liveRegion)
            {
                liveRegion.Update(
                    validationError,
                    severity: AccessibilityLiveRegionSeverity.Assertive);
            }
            else
            {
                DestinationValidationText.Text = validationError;
            }
            DestinationValidationText.Visibility =
                destination.Length > 0 && !destinationIsValid
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        if (ServerJobIdWarningPanel is not null)
        {
            ServerJobIdWarningPanel.Visibility =
                destinationIsValid && resolvedInput?.ServerJobId is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        SetDestinationForAllButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            !IsAutoJoinWatchActive &&
            _pendingProfile is null &&
            _settings.Accounts.Count >= 2 &&
            destinationIsValid;

        LaunchButton.IsEnabled = IsAutoJoinWatchActive
            ? _autoJoinWatchCancellation is
            {
                IsCancellationRequested: false
            }
            : !_operationBusy &&
              !_launchInProgress &&
              _currentUser?.Id == _activeProfile?.UserId &&
              _activeProfile is not null &&
              TryGetCurrentWebSessionToken(_activeProfile, out _) &&
              destinationIsValid;
        BatchLaunchButton.IsEnabled =
            !_operationBusy &&
            !_accountReorderInProgress &&
            !_launchInProgress &&
            !IsAutoJoinWatchActive &&
            _pendingProfile is null &&
            _settings.Accounts.Count >= 2;
    }

    private void SetOperationBusy(bool busy)
    {
        _operationBusy = busy;
        var watchLocksContext = IsAutoJoinWatchActive;
        var auxiliaryActionsEnabled = !busy && !watchLocksContext;
        UpdateAccountControlAvailability();
        RunningClientsButton.IsEnabled = auxiliaryActionsEnabled;
        SignInButton.IsEnabled = !busy && !watchLocksContext;
        PlaceIdBox.IsEnabled = !busy && !watchLocksContext;
        ExperienceDestinationModeButton.IsEnabled = !busy && !watchLocksContext;
        UserDestinationModeButton.IsEnabled = !busy && !watchLocksContext;
        LaunchTabButton.IsEnabled = !busy && !watchLocksContext;
        RecentTabButton.IsEnabled = !busy && !watchLocksContext;
        RecentExperiencesList.IsEnabled = !busy && !watchLocksContext;
        UpdateClearHistoryButton();
        BatchLaunchButton.IsEnabled = !busy && !watchLocksContext;
        RetryFailedBatchButton.IsEnabled =
            !busy &&
            !watchLocksContext &&
            !_accountReorderInProgress &&
            _batchRetryState is not null;
        ThemeToggleButton.IsEnabled = auxiliaryActionsEnabled;
        SoundSettingsButton.IsEnabled = auxiliaryActionsEnabled;
        LanguageSettingsButton.IsEnabled = auxiliaryActionsEnabled;
        MetadataTransferButton.IsEnabled = auxiliaryActionsEnabled;
        IntegrationsButton.IsEnabled = auxiliaryActionsEnabled;
        AboutDiagnosticsButton.IsEnabled = auxiliaryActionsEnabled;
        ReleaseNotesButton.IsEnabled = auxiliaryActionsEnabled;
        InstallUpdateButton.IsEnabled = auxiliaryActionsEnabled;
        UpdateAutoJoinActionPresentation();
        RefreshLaunchAvailability();
    }

    private AccountProfile? FindActiveSavedProfile() =>
        _settings.Accounts.FirstOrDefault(
            account => account.Key == _settings.ActiveAccountKey)
        ?? _settings.Accounts.FirstOrDefault();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        var shutdownBudget = new ShutdownTimeBudget(ShutdownTimeout);
        e.Cancel = true;
        if (!_operationLifetime.BeginShutdown())
            return;
        var shutdownWatchdog = ShutdownExitWatchdog.Start(ShutdownTimeout);
        DisarmWatchdogOnApplicationExit(shutdownWatchdog);

        var destinationRequest = CaptureShutdownDestinationRequest();
        var mainWindowPlacement =
            WindowLayoutService.CaptureMainWindowPlacement(this);
        var shutdownFailures = new List<Exception>();
        var settingsCompletion = _settingsMutations.CompleteAsync(() =>
            ShutdownSettingsSnapshot.Create(
                _settings,
                destinationRequest,
                CaptureShutdownDestinationRequest(),
                mainWindowPlacement));
        ObserveShutdownSettingsCompletion(settingsCompletion);
        _destinationTrackingEnabled = false;
        IsEnabled = false;

        // Always leave the original Closing event before Close is requested
        // again, even when there are no active operations to await.
        await Task.Yield();
        try
        {
            await CompleteShutdownAsync(
                shutdownBudget,
                settingsCompletion,
                shutdownFailures);
        }
        catch (Exception exception)
        {
            // A shutdown failure must not keep the process and mutex alive.
            shutdownFailures.Add(exception);
            System.Diagnostics.Trace.WriteLine(
                $"Window shutdown failed safely: {exception.GetType().Name}.");
        }
        finally
        {
            _shutdownComplete = true;
            try
            {
                ShutdownCompleted?.Invoke(shutdownFailures.FirstOrDefault());
            }
            catch (Exception exception)
            {
                // A smoke observer must not keep the production window open.
                System.Diagnostics.Trace.WriteLine(
                    $"Shutdown completion observer failed: {exception.GetType().Name}.");
            }
            Close();
        }
    }

    private static void DisarmWatchdogOnApplicationExit(
        ShutdownExitWatchdog shutdownWatchdog)
    {
        ArgumentNullException.ThrowIfNull(shutdownWatchdog);
        var application = Application.Current;
        if (application is null)
            return;

        ExitEventHandler? handler = null;
        handler = (_, _) =>
        {
            application.Exit -= handler;
            shutdownWatchdog.Dispose();
        };
        application.Exit += handler;
    }

    private async Task CompleteShutdownAsync(
        ShutdownTimeBudget shutdownBudget,
        Task settingsCompletion,
        ICollection<Exception> shutdownFailures)
    {
        ArgumentNullException.ThrowIfNull(shutdownFailures);
        var browserCancellation =
            CancelScopedOperation(_browserSwitchCancellation);
        var batchCancellation =
            CancelScopedOperation(_batchCancellation);
        _destinationPersistence.Cancel();
        var operationsDrained =
            shutdownBudget.TryGetRemaining(out var drainTimeout) &&
            await _operationLifetime.DrainAsync(drainTimeout);
        if (!operationsDrained)
        {
            shutdownFailures.Add(new TimeoutException(
                "Active window operations did not drain before shutdown."));
        }
        var incompleteProfile = _pendingProfile;
        var finalSettingsSaved = false;

        try
        {
            if (settingsCompletion.IsCompletedSuccessfully)
            {
                finalSettingsSaved = true;
            }
            else if (shutdownBudget.TryGetRemaining(out var saveTimeout))
            {
                finalSettingsSaved =
                    await BoundedSettingsPersistence.TrySaveAsync(
                        () => settingsCompletion,
                        saveTimeout);
            }
        }
        catch (Exception exception)
        {
            // Closing must continue even if local settings are unavailable.
            shutdownFailures.Add(exception);
            System.Diagnostics.Trace.WriteLine(
                $"Shutdown settings persistence failed: {exception.GetType().Name}.");
        }
        if (!finalSettingsSaved)
        {
            shutdownFailures.Add(new TimeoutException(
                "Final settings persistence did not finish before shutdown."));
        }

        _webSession.RobloxPageLoaded -= WebSession_RobloxPageLoaded;
        _webSession.SessionUnavailable -= WebSession_SessionUnavailable;
        try
        {
            BrowserHost.Children.Clear();
        }
        catch (Exception exception)
        {
            // Native browser teardown continues below.
            shutdownFailures.Add(exception);
            System.Diagnostics.Trace.WriteLine(
                $"Browser host teardown failed: {exception.GetType().Name}.");
        }
        DisposeDuringShutdown(_webSession, shutdownFailures);
        _webSessionToken = null;
        if (PendingProfileCleanup.CanDelete(
                operationsDrained,
                finalSettingsSaved,
                incompleteProfile,
                _pendingProfile,
                _settings))
        {
            if (shutdownBudget.TryGetRemaining(out var cleanupTimeout))
            {
                await PendingProfileCleanup.TryDeleteAsync(
                    cancellationToken => _settingsService.DeleteSessionDataAsync(
                        incompleteProfile!,
                        cancellationToken),
                    cleanupTimeout);
            }
        }

        DisposeCancellationAfterCallbacks(
            _browserSwitchCancellation,
            browserCancellation);
        DisposeCancellationAfterCallbacks(
            _batchCancellation,
            batchCancellation);
        DisposeDuringShutdown(_launchHook, shutdownFailures);
        DisposeDuringShutdown(_updateService, shutdownFailures);
        DisposeDuringShutdown(_destinationPersistence, shutdownFailures);
        DisposeDuringShutdown(_operationLifetime, shutdownFailures);
    }

    private static void ObserveShutdownSettingsCompletion(Task completion)
    {
        _ = completion.ContinueWith(
            completed => System.Diagnostics.Trace.WriteLine(
                $"Shutdown settings completion later failed: {completed.Exception?.GetBaseException().GetType().Name}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static Task CancelScopedOperation(
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return Task.CompletedTask;

        try
        {
            var cancellationTask = cancellation.CancelAsync();
            _ = cancellationTask.ContinueWith(
                completed => System.Diagnostics.Trace.WriteLine(
                    $"A scoped shutdown cancellation callback failed: {completed.Exception?.GetBaseException().GetType().Name}."),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                    TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return cancellationTask;
        }
        catch (ObjectDisposedException)
        {
            // The operation completed while shutdown was taking its snapshot.
            return Task.CompletedTask;
        }
    }

    private static void DisposeCancellationAfterCallbacks(
        CancellationTokenSource? cancellation,
        Task cancellationTask)
    {
        if (cancellation is null)
            return;
        if (cancellationTask.IsCompleted)
        {
            DisposeDuringShutdown(cancellation);
            return;
        }

        _ = cancellationTask.ContinueWith(
            _ => DisposeDuringShutdown(cancellation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposeDuringShutdown(
        IDisposable? disposable,
        ICollection<Exception>? shutdownFailures = null)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception exception)
        {
            // One teardown failure must not prevent the remaining releases.
            shutdownFailures?.Add(exception);
            System.Diagnostics.Trace.WriteLine(
                $"Shutdown disposal failed: {exception.GetType().Name}.");
        }
    }

}
