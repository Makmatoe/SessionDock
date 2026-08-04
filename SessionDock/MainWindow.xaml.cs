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
    private const double CompactLayoutBreakpoint = 900;
    private const double CompactHeaderHeight = 112;
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
    private readonly HandleScopeRuntimeCoordinator
        _handleScopeRuntimeCoordinator;
    private readonly CompositeLaunchHook _launchHook;
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
    private readonly AccessibilityLiveRegion _homeStatusLiveRegion;
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
    private bool _closingDestinationPromptInProgress;
    private bool _compactLayoutActive;

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

    internal void VerifyCompactLayoutForRuntimeSmoke()
    {
        Dispatcher.VerifyAccess();
        var original = _compactLayoutActive;
        try
        {
            ApplyCompactLayout(compact: true);
            if (HeaderRow.Height.Value != CompactHeaderHeight ||
                Grid.GetRow(HeaderTextPanel) != 1 ||
                Grid.GetColumnSpan(HeaderUtilityPanel) != 2 ||
                Grid.GetRow(DestinationSavedText) != 1 ||
                Grid.GetColumnSpan(DestinationSavedText) != 3 ||
                Grid.GetRow(LaunchPrimaryActionsPanel) != 1 ||
                Grid.GetColumnSpan(NotAffiliatedText) != 2)
            {
                throw new InvalidOperationException(
                    "The compact main-window layout was not applied.");
            }

            ApplyCompactLayout(compact: false);
            if (HeaderRow.Height.Value != 64 ||
                Grid.GetRow(HeaderTextPanel) != 0 ||
                Grid.GetColumn(HeaderUtilityPanel) != 1 ||
                Grid.GetRow(DestinationSavedText) != 0 ||
                Grid.GetColumn(DestinationSavedText) != 2 ||
                Grid.GetRow(LaunchPrimaryActionsPanel) != 0 ||
                Grid.GetColumn(NotAffiliatedText) != 1)
            {
                throw new InvalidOperationException(
                    "The regular main-window layout was not restored.");
            }
        }
        finally
        {
            ApplyCompactLayout(original);
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
        var app = (App)Application.Current;
        _handleScopeRuntimeCoordinator =
            app.HandleScopeRuntimeCoordinator;
        _launchHook = new CompositeLaunchHook(
            new HandleScopeLaunchHook(_handleScopeRuntimeCoordinator),
            new LocalApiLaunchHook());
        InitializeComponent();
        AttachSemanticSelectorHandlers();
        _statusLiveRegion = new AccessibilityLiveRegion(StatusTitle);
        _homeStatusLiveRegion = new AccessibilityLiveRegion(HomeStatusText);
        _destinationValidationLiveRegion =
            new AccessibilityLiveRegion(DestinationValidationText);
        CaptionControls.AttachToWindow(this);
        HomeCaptionControls.AttachToWindow(this);
        _soundService = app.SoundService;
        _settings = _settingsService.Load();
        if (!WindowLayoutService.RestoreMainWindowPlacement(
                this,
                _settings.MainWindowPlacement))
        {
            WindowLayoutService.FitToWorkArea(this);
        }
        InitializeHomeWorkspace();
        InitializeMacroSessionUi();
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
        _startupNotice = LocalizeSettingsLoadNotice(
            _settingsService.LoadNotice);
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

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        ApplyCompactLayout(ShouldUseCompactLayout(e.NewSize.Width));
    }

    internal static bool ShouldUseCompactLayout(double width) =>
        double.IsFinite(width) &&
        width > 0 &&
        width < CompactLayoutBreakpoint;

    private void ApplyCompactLayout(bool compact)
    {
        if (_compactLayoutActive == compact)
            return;

        _compactLayoutActive = compact;
        HeaderRow.Height = new GridLength(compact ? CompactHeaderHeight : 64);

        Grid.SetRow(HeaderTextPanel, compact ? 1 : 0);
        Grid.SetColumn(HeaderTextPanel, 0);
        Grid.SetColumnSpan(HeaderTextPanel, compact ? 2 : 1);
        HeaderTextPanel.Margin = compact
            ? new Thickness(0, 4, 0, 0)
            : new Thickness(0);

        Grid.SetRow(HeaderUtilityPanel, 0);
        Grid.SetColumn(HeaderUtilityPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(HeaderUtilityPanel, compact ? 2 : 1);
        HeaderUtilityPanel.HorizontalAlignment = compact
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;

        Grid.SetRow(DestinationSavedText, compact ? 1 : 0);
        Grid.SetColumn(DestinationSavedText, compact ? 0 : 2);
        Grid.SetColumnSpan(DestinationSavedText, compact ? 3 : 1);
        DestinationSavedText.HorizontalAlignment = compact
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
        DestinationSavedText.Margin = compact
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);

        Grid.SetRow(LaunchPrimaryActionsPanel, compact ? 1 : 0);
        Grid.SetColumn(LaunchPrimaryActionsPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(LaunchPrimaryActionsPanel, compact ? 2 : 1);
        LaunchPrimaryActionsPanel.HorizontalAlignment = compact
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
        LaunchPrimaryActionsPanel.Margin = compact
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);

        Grid.SetRow(RequirementsText, 0);
        Grid.SetColumn(RequirementsText, 0);
        Grid.SetColumnSpan(RequirementsText, compact ? 2 : 1);
        RequirementsText.TextTrimming = compact
            ? TextTrimming.None
            : TextTrimming.CharacterEllipsis;
        RequirementsText.TextWrapping = compact
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;

        Grid.SetRow(NotAffiliatedText, compact ? 1 : 0);
        Grid.SetColumn(NotAffiliatedText, compact ? 0 : 1);
        Grid.SetColumnSpan(NotAffiliatedText, compact ? 2 : 1);
        NotAffiliatedText.Margin = compact
            ? new Thickness(0, 2, 0, 0)
            : new Thickness(14, 0, 0, 0);
        NotAffiliatedText.TextWrapping = compact
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(MainWindowLoadedAsync);

    private async Task MainWindowLoadedAsync(CancellationToken cancellationToken)
    {
        SetOperationBusy(true);
        try
        {
            var handleScopeStartup =
                _handleScopeRuntimeCoordinator.InspectAsync(cancellationToken);
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
                    Localize(
                        orphanCleanup.RemovedProfiles == 1
                            ? "Main.StartupOrphanRemovedOne"
                            : "Main.StartupOrphanRemovedMany",
                        orphanCleanup.RemovedProfiles));
            }
            if (orphanCleanup.BudgetExpired)
            {
                AppendStartupNotice(
                    Localize("Main.StartupOrphanCleanupDeferred"));
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
                    Localize("Main.LocalSettingsRecoveryTitle"),
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

            _ = await handleScopeStartup;

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

    private string? LocalizeSettingsLoadNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
            return null;

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            notice.Split(
                    [Environment.NewLine + Environment.NewLine],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part switch
                {
                    "SessionDock recovered your accounts and history from the local settings backup. The unreadable file was preserved for diagnosis." =>
                        Localize("Main.SettingsRecoveredPreserved"),
                    "SessionDock recovered your accounts and history from the local settings backup after the primary file was missing." =>
                        Localize("Main.SettingsRecoveredMissing"),
                    "SessionDock could not read the local settings or its backup. The unreadable files were preserved, and browser profiles were left untouched." =>
                        Localize("Main.SettingsUnreadablePreserved"),
                    "SessionDock found separate or conflicting current and legacy RobloxOne data. Conflicting files were left untouched, and automatic browser-profile cleanup is paused. Resolve the preserved legacy data before deleting migration-conflict.txt." =>
                        Localize("Main.SettingsMigrationConflict"),
                    "A legacy RobloxOne data migration did not finish cleanly. Some files may exist in either data directory, so automatic browser-profile cleanup is paused. Reconcile both directories before deleting migration-in-progress.txt." =>
                        Localize("Main.SettingsMigrationIncomplete"),
                    "Automatic browser-profile cleanup remains paused while recovered sessions are being validated. Your account records are available; the pause prevents unreferenced browser profiles from being deleted." =>
                        Localize("Main.SettingsRecoveredValidation"),
                    "Automatic browser-profile cleanup is paused to protect sessions whose account metadata could not be recovered." =>
                        Localize("Main.SettingsCleanupPaused"),
                    "Your accounts and browser profiles were recovered, but a conflicting optional sound or local integration file remains only in the preserved RobloxOne folder. Keep that folder until any optional configuration you still need has been reviewed." =>
                        Localize("Main.SettingsOptionalLegacyData"),
                    _ => Localize("Main.SettingsRecoveryGeneric")
                }));
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
                Localize("Main.RemovalJournalInspectFailure"));
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
                    AccountRemovalSettingsPolicy.RemoveAccounts(
                        _settings,
                        journaledSet);
                    prepared = true;
                },
                Localize("Main.PendingRemovalRestoreFailureTitle"),
                Localize("Main.CleanupWarningBadge"),
                Localize("Main.PendingRemovalRestoreFailureDetail"),
                failureTone: StatusTone.Warning,
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
                Localize("Main.PendingRemovalPreserved"));
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
                Localize(
                    deletedKeys.Count == 1
                        ? "Main.PendingRemovalClearedOne"
                        : "Main.PendingRemovalClearedMany",
                    deletedKeys.Count));
        }

        if (deletedKeys.Count < journaledKeys.Count || journalClearFailed ||
            _settings.PendingProfileDeletionKeys.Count > 0)
        {
            AppendStartupNotice(replayResult.BudgetExpired
                ? Localize("Main.PendingRemovalCleanupDeferred")
                : Localize("Main.PendingRemovalCleanupPending"));
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
            Localize("Main.CleanupAcknowledgeFailureTitle"),
            Localize("Main.CleanupWarningBadge"),
            Localize("Main.CleanupAcknowledgeFailureDetail"),
            failureTone: StatusTone.Warning);
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
                Localize("Main.WebSignInStartFailureTitle"),
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
        var failureDetail = Localize(
            WebSessionException.GetLocalizationKey(exception.Reason));
        SetStatus(
            title,
            failureDetail,
            Localize("Main.SessionErrorBadge"),
            StatusTone.Error);
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
            Localize("Main.WebView2RecoveryPrompt", failureDetail),
            Localize("Main.WebView2RecoveryTitle"),
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
                Localize("Main.WebView2HelpOpenFailureTitle"),
                Localize(
                    "Main.WebView2HelpOpenFailureDetail",
                    WebSessionException.OfficialWebView2DownloadUrl),
                Localize("Main.SessionErrorBadge"),
                StatusTone.Error);
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
            Localize("Main.WebSessionStoppedTitle"),
            Localize("Main.WebSessionStoppedDetail"),
            Localize("Main.SessionErrorBadge"),
            StatusTone.Error);
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
                    Localize("Main.AccountSaveFailureTitle"),
                    Localize("Main.AccountSaveErrorBadge"),
                    Localize("Main.AccountSaveFailureDetail"),
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
                    Localize("Main.AccountAlreadyAddedTitle"),
                    Localize(
                        "Main.AccountAlreadyAddedDetail",
                        detectedUser.Name),
                    Localize("Main.DuplicateAccountBadge"),
                    StatusTone.Warning);
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
                Localize("Main.DifferentAccountTitle"),
                Localize(
                    "Main.DifferentAccountDetail",
                    _activeProfile?.Username,
                    detectedUser.Name),
                Localize("Main.AccountBlockedBadge"),
                StatusTone.Warning);
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
        ReturnToAccountsAfterBrowserIfRequested(_activeProfile.Key);
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

    private void SetSignedOutState(bool announceStatus = true)
    {
        CancelAutoJoinWatchSilently();
        var profile = _pendingProfile ?? _activeProfile;
        SetStatus(
            profile is null || _pendingProfile is not null
                ? Localize("Main.ConnectAccountTitle")
                : Localize("Main.ReconnectAccountTitle", profile.Username),
            _pendingProfile is not null
                ? Localize("Main.PendingAccountSignInDetail")
                : profile is null
                    ? Localize("Main.NoAccountSignInDetail")
                    : Localize("Main.ExpiredAccountSignInDetail"),
            Localize("Main.SignInNeededBadge"),
            StatusTone.Warning,
            announceStatus);
        LaunchButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Visible;
        SignInButtonLabel.Text = Localize("Main.SignIn");
        AutomationProperties.SetName(
            SignInButton,
            Localize("Main.SignInName"));
    }

    private void SetReadyState(bool announceStatus = true)
    {
        if (_currentUser is null || _activeProfile is null ||
            !TryGetCurrentWebSessionToken(_activeProfile, out _))
            return;

        SetStatus(
            Localize("Main.ActiveAccountTitle", _currentUser.Name),
            Localize("Main.AccountReadyDetail"),
            _launchInProgress
                ? Localize("Main.LaunchingBadge")
                : Localize("Main.AccountVerifiedBadge"),
            _launchInProgress ? StatusTone.Neutral : StatusTone.Success,
            announceStatus);
        SignInButton.Visibility = Visibility.Collapsed;
        RefreshLaunchAvailability(announceValidation: announceStatus);
        UpdateAutoJoinActionPresentation();
    }

    private void SetStatus(
        string title,
        string detail,
        string badge,
        StatusTone tone,
        bool announceChanges = true)
    {
        var announcement = CreateStatusAnnouncement(title, detail, badge);
        var severity = tone is StatusTone.Error or StatusTone.Warning
            ? AccessibilityLiveRegionSeverity.Assertive
            : AccessibilityLiveRegionSeverity.Polite;
        var advancedIsVisible = AdvancedWorkspace.Visibility ==
            Visibility.Visible;
        _statusLiveRegion.Update(
            title,
            announcement,
            severity,
            announceChanges && advancedIsVisible);
        StatusDetail.Text = detail;
        SessionBadge.Text = badge;
        _homeStatusLiveRegion.Update(
            announcement,
            announcement,
            severity,
            announceChanges && !advancedIsVisible);

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
        HomeStatusIndicator.SetResourceReference(
            Border.BackgroundProperty,
            foregroundResource);
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
                Localize("Main.WebSessionUnavailableTitle"),
                webSessionFailure);
            return;
        }
        SetStatus(
            Localize("Main.LocalOperationFailureTitle"),
            Localize("Main.LocalOperationFailureDetail"),
            Localize("Main.LocalDataErrorBadge"),
            StatusTone.Error);
    }

    private async Task<bool> TryCommitSettingsMutationAsync(
        Action mutation,
        string failureTitle,
        string? failureBadge = null,
        string? failureDetail = null,
        StatusTone failureTone = StatusTone.Error,
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
                    Localize("Main.SettingsRollbackDetail"),
                failureBadge ?? Localize("Main.SettingsErrorBadge"),
                failureTone);
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
        if (AccountsWorkspace.Visibility == Visibility.Visible)
            RefreshAccountsWorkspace();
        if (DestinationsWorkspace.Visibility == Visibility.Visible)
            RefreshDestinationsWorkspace();
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

    private Task EditAccountButtonClickAsync() =>
        EditAccountProfileAsync(_activeProfile);

    private async Task EditAccountProfileAsync(AccountProfile? requestedProfile)
    {
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null ||
            requestedProfile is null)
            return;

        var editedProfile = _settings.Accounts.FirstOrDefault(account =>
            account.Key.Equals(
                requestedProfile.Key,
                StringComparison.OrdinalIgnoreCase));
        if (editedProfile is null)
            return;
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
                Localize("Main.AccountDetailsSaveFailureTitle"),
                onCommitted: () =>
                {
                    if (mutationApplied)
                    {
                        RenderAccountList();
                        RefreshAccountsWorkspace();
                    }
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
                Localize("Main.ThemeSaveFailureTitle"),
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
                            UiSoundService.ResolveCustomStartupSoundFileName(
                                startupSound,
                                selectedCustomFileName,
                                _settings.CustomStartupSoundFileName);
                    },
                    Localize("Main.SoundSaveFailureTitle"),
                    onCommitted: () =>
                        ((App)Application.Current).UiSoundsEnabled =
                            uiSoundsEnabled))
            {
                return;
            }
            SetStatus(
                Localize("Main.SoundSavedTitle"),
                Localize("Main.SoundSavedDetail"),
                Localize("Main.SettingsSavedBadge"),
                StatusTone.Success);
        }
        catch (Exception ex) when (IsExpectedSoundImportFailure(ex))
        {
            SetStatus(
                Localize("Main.SoundSaveFailureTitle"),
                Localize("Main.SoundSaveFailureDetail"),
                Localize("Main.SettingsErrorBadge"),
                StatusTone.Error);
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
        await AccountButtonClickAsync(key, cancellationToken);
    }

    private async Task AccountButtonClickAsync(
        string key,
        CancellationToken cancellationToken)
    {
        if (_operationBusy ||
            _accountReorderInProgress ||
            _pendingProfile is not null)
            return;
        var profile = _settings.Accounts.FirstOrDefault(account => account.Key == key);
        if (profile is null || profile == _activeProfile)
            return;

        if (_destinationDraftDirty && !_destinationDraftValid)
        {
            SetStatus(
                Localize("Main.AccountSwitchInvalidTitle"),
                Localize("Main.AccountSwitchInvalidDetail"),
                Localize("Main.InvalidDestinationBadge"),
                StatusTone.Error);
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
                            NamedDestinationPolicy.SetAccountDestination(
                                _settings,
                                outgoing.Key,
                                outgoingDestination.Destination);
                        }
                    }

                    if (!_settings.Accounts.Any(account => account.Key == key))
                        return;
                    _settings.ActiveAccountKey = key;
                    mutationApplied = true;
                },
                Localize("Main.AccountSwitchFailureTitle", profile.Username),
                Localize("Main.AccountSwitchErrorBadge"),
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
        SetStatus(
            Localize("Main.AccountSwitchingTitle", profile.Username),
            Localize("Main.AccountSwitchingDetail"),
            Localize("Main.SwitchingBadge"),
            StatusTone.Neutral);
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
            ReturnToAccountsAfterBrowserIfRequested(_activeProfile?.Key);
            return;
        }

        if (!await _accountCheckLock.WaitAsync(0, cancellationToken))
        {
            SetStatus(
                Localize("Main.AccountCheckPendingTitle"),
                Localize("Main.AccountCheckPendingDetail"),
                Localize("Main.AccountCheckPendingBadge"),
                StatusTone.Warning);
            return;
        }

        AccountProfile? nextProfile = null;
        try
        {
            if (_pendingProfile is null)
            {
                BrowserPanel.Visibility = Visibility.Collapsed;
                LauncherPanel.Visibility = Visibility.Visible;
                ReturnToAccountsAfterBrowserIfRequested(_activeProfile?.Key);
                return;
            }

            if (!await ClearCurrentBrowserProfileAsync(cancellationToken))
            {
                SetStatus(
                    Localize("Main.TemporarySessionCleanupFailureTitle"),
                    Localize("Main.TemporarySessionCleanupFailureDetail"),
                    Localize("Main.CleanupErrorBadge"),
                    StatusTone.Error);
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
        ReturnToAccountsAfterBrowserIfRequested(nextProfile?.Key);
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
                SetStatus(
                    Localize("Main.InvalidUserTitle"),
                    Localize(parseError),
                    Localize("Main.InvalidUserBadge"),
                    StatusTone.Error);
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
            SetStatus(
                Localize("Main.InvalidDestinationTitle"),
                Localize(parseError),
                Localize("Main.InvalidDestinationBadge"),
                StatusTone.Error);
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
                Localize("Main.AutoJoinEndedTitle"),
                Localize("Main.AutoJoinAccountChangedDetail"),
                Localize("Main.WatchEndedBadge"),
                StatusTone.Warning);
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
                    Localize("Main.JoinUserCheckingTitle"),
                    Localize(
                        "Main.JoinUserResolvingDetail",
                        joinUser.DisplayValue,
                        currentUser.Name),
                    Localize("Main.CheckingUserBadge"),
                    StatusTone.Neutral);
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
                Localize("Main.AutoJoinEndedTitle"),
                Localize("Main.AutoJoinSessionChangedDetail"),
                Localize("Main.WatchEndedBadge"),
                StatusTone.Warning);
            return;
        }

        if (target!.ShareCode is not null)
        {
            SetStatus(
                Localize("Main.PrivateServerResolvingTitle"),
                Localize("Main.PrivateServerResolvingDetail"),
                Localize("Main.ResolvingServerBadge"),
                StatusTone.Neutral);
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
                    Localize("Main.PrivateServerResolveFailureTitle"),
                    Localize("Main.PrivateServerResolveFailureDetail"),
                    Localize("Main.ServerLinkErrorBadge"),
                    StatusTone.Error);
                LaunchButtonLabel.Text = Localize("Main.Launch");
                LaunchButton.IsEnabled = true;
                return;
            }
        }

        if (trackedPlaceId is not null && target.PlaceId != trackedPlaceId)
        {
            _launchInProgress = false;
            SetStatus(
                Localize("Main.TrackedServerMismatchTitle"),
                Localize("Main.TrackedServerMismatchDetail"),
                Localize("Main.ServerRecordErrorBadge"),
                StatusTone.Error);
            LaunchButtonLabel.Text = Localize("Main.Launch");
            return;
        }

        SetStatus(
            joinUserResolution is not null
                ? Localize(
                    "Main.JoiningUserAsTitle",
                    joinUserResolution.Username,
                    currentUser.Name)
                : serverJobId is null
                ? Localize("Main.PreparingAccountTitle", currentUser.Name)
                : Localize("Main.RejoiningAsTitle", currentUser.Name),
            joinUserResolution is not null
                ? Localize("Main.JoinUserTicketDetail")
                : serverJobId is null
                ? Localize("Main.LaunchTicketDetail")
                : Localize("Main.TrackedServerTicketDetail", serverJobId[..8]),
            Localize("Main.GettingTicketBadge"),
            StatusTone.Neutral);
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
                Localize("Main.TicketFailureTitle"),
                Localize("Main.TicketFailureDetail"),
                Localize("Main.TicketErrorBadge"),
                StatusTone.Error);
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
        var (title, detail, badge, tone) = availability switch
        {
            JoinUserAvailability.UserNotFound => (
                Localize("Main.JoinUserNotFoundTitle"),
                Localize("Main.JoinUserNotFoundDetail"),
                Localize("Main.UserNotFoundBadge"),
                StatusTone.Error),
            JoinUserAvailability.Offline => (
                Localize(
                    "Main.JoinUserOfflineTitle",
                    requestedUser.DisplayValue),
                Localize("Main.JoinUserOfflineDetail"),
                Localize("Main.UserOfflineBadge"),
                StatusTone.Warning),
            JoinUserAvailability.NotInExperience => (
                Localize(
                    "Main.JoinUserNotInExperienceTitle",
                    requestedUser.DisplayValue),
                Localize("Main.JoinUserNotInExperienceDetail"),
                Localize("Main.NotInGameBadge"),
                StatusTone.Warning),
            JoinUserAvailability.NotJoinable => (
                Localize(
                    "Main.JoinUserUnavailableTitle",
                    requestedUser.DisplayValue),
                Localize("Main.JoinUserUnavailableDetail"),
                Localize("Main.JoinsUnavailableBadge"),
                StatusTone.Warning),
            JoinUserAvailability.RateLimited => (
                Localize("Main.JoinUserRateLimitedTitle"),
                Localize("Main.JoinUserRateLimitedDetail"),
                Localize("Main.UserCheckLimitedBadge"),
                StatusTone.Warning),
            JoinUserAvailability.SessionUnavailable => (
                Localize("Main.JoinUserSessionUnavailableTitle"),
                Localize("Main.JoinUserSessionUnavailableDetail"),
                Localize("Main.SignInNeededBadge"),
                StatusTone.Warning),
            _ => (
                Localize("Main.JoinUserCheckFailureTitle"),
                Localize("Main.JoinUserCheckFailureDetail"),
                Localize("Main.UserCheckErrorBadge"),
                StatusTone.Error)
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
        SetStatus(title, detail, badge, tone);
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
            Localize("Main.LaunchingAsTitle", _currentUser?.Name),
            Localize("Main.StartingClientDetail"),
            Localize("Main.StartingClientBadge"),
            StatusTone.Neutral);
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
                Localize("Main.ClientStartedTitle"),
                _launchHook.IsConfigured
                    ? Localize("Main.IntegrationsRunningDetail")
                    : Localize("Main.IntegrationsCheckingDetail"),
                Localize("Main.ClientStartedBadge"),
                StatusTone.Success);
            var accountLabel = _activeProfile?.Label;
            await NotifyLaunchHookAsync(
                recent,
                processId,
                accountLabel,
                cancellationToken);
            SetStatus(
                Localize("Main.ClientStartedTitle"),
                _launchHook.IsConfigured
                    ? Localize("Main.IntegrationsFinishedDetail")
                    : Localize("Main.IntegrationsSkippedDetail"),
                Localize("Main.ClientStartedBadge"),
                StatusTone.Success);
            RefreshLaunchAvailability();
            LaunchButtonLabel.Text = Localize("Main.Launch");
            return;
        }

        LaunchButtonLabel.Text = Localize("Main.Launch");
        LaunchButton.IsEnabled = true;
        SetStatus(
            Localize("Main.ClientUnavailableTitle"),
            Localize(result.Error!),
            Localize("Main.ClientErrorBadge"),
            StatusTone.Error);
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

    private Task ResetButtonClickAsync(CancellationToken cancellationToken) =>
        RemoveAccountAsync(_activeProfile, cancellationToken);

    private async Task RemoveAccountAsync(
        AccountProfile? requestedProfile,
        CancellationToken cancellationToken)
    {
        if (_operationBusy || _accountReorderInProgress)
            return;
        var profile = requestedProfile is null
            ? null
            : _settings.Accounts.FirstOrDefault(account => account.Key.Equals(
                requestedProfile.Key,
                StringComparison.OrdinalIgnoreCase));
        if (profile is null || _pendingProfile is not null)
            return;

        var result = MessageBox.Show(
            Localize("Main.RemoveAccountConfirm", profile.Username),
            Localize("Main.RemoveAccountTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
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
                        AccountRemovalSettingsPolicy.RemoveAccounts(
                            _settings,
                            [profile.Key]);
                    },
                    Localize("Main.AccountRemovalSaveFailureTitle"),
                    Localize("Main.AccountSaveErrorBadge"),
                    Localize("Main.AccountRemovalSaveFailureDetail")))
            {
                if (!await Task.Run(
                        () => _settingsService.ClearProfileDeletionJournal(
                            profile.Key),
                        CancellationToken.None))
                {
                    SetStatus(
                        Localize("Main.AccountRemovalQueuedTitle"),
                        Localize("Main.AccountRemovalQueuedDetail"),
                        Localize("Main.CleanupWarningBadge"),
                        StatusTone.Warning);
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
                _activeProfile = FindActiveSavedProfile();
                ShowDestinationForProfile(_activeProfile);
                _currentUser = null;
                RenderAccountList();
                RefreshAccountsWorkspace();
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
                    Localize("Main.AccountRemovalCleanupPendingTitle"),
                    Localize("Main.AccountRemovalCleanupPendingDetail"),
                    Localize("Main.CleanupWarningBadge"),
                    StatusTone.Warning);
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
                Localize("Main.DestinationNotChangedTitle"),
                Localize("Main.DestinationNoAccountsDetail"),
                Localize("Main.InvalidDestinationBadge"),
                StatusTone.Error);
            RefreshLaunchAvailability();
            return;
        }
        if (!TryNormalizeCurrentDestinationInput(
                PlaceIdBox.Text,
                out var storedDestination,
                out _,
                out var error))
        {
            SetStatus(
                Localize("Main.DestinationNotChangedTitle"),
                Localize(error),
                Localize("Main.InvalidDestinationBadge"),
                StatusTone.Error);
            RefreshLaunchAvailability();
            return;
        }

        _destinationPersistence.Cancel();
        var assignedCount = _settings.Accounts.Count;
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    foreach (var account in _settings.Accounts.ToArray())
                    {
                        NamedDestinationPolicy.SetAccountDestination(
                            _settings,
                            account.Key,
                            storedDestination);
                    }
                },
                Localize("Main.SharedDestinationSaveFailureTitle"),
                Localize("Main.DestinationSaveErrorBadge"),
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
            Localize("Main.DestinationSetForAllTitle"),
            Localize(
                (_joinUserMode, assignedCount == 1) switch
                {
                    (true, true) => "Main.UserDestinationSavedOne",
                    (true, false) => "Main.UserDestinationSavedMany",
                    (false, true) => "Main.ExperienceDestinationSavedOne",
                    _ => "Main.ExperienceDestinationSavedMany"
                },
                assignedCount),
            Localize("Main.DestinationSavedBadge"),
            StatusTone.Success);
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
                    Localize("Main.DestinationNotSavedTitle"),
                    Localize("Main.DestinationNotSavedDetail"),
                    Localize("Main.InvalidDestinationBadge"),
                    StatusTone.Error);
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
                NamedDestinationPolicy.SetAccountDestination(
                    _settings,
                    profile.Key,
                    request.Destination);
                applied = true;
            },
            Localize("Main.DestinationSaveFailureTitle"),
            Localize("Main.DestinationSaveErrorBadge"),
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

                var committedProfile = _settings.Accounts.FirstOrDefault(
                    account => account.Key.Equals(
                        request.AccountKey,
                        StringComparison.OrdinalIgnoreCase));
                ShowDestinationForProfile(committedProfile);
                if (DestinationsWorkspace.Visibility == Visibility.Visible)
                    RefreshDestinationsWorkspace();
                if (AccountsWorkspace.Visibility == Visibility.Visible)
                    RefreshAccountsWorkspace();
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

    private void RefreshLaunchAvailability(bool announceValidation = true)
    {
        if (LaunchButton is null)
            return;

        var destination = PlaceIdBox.Text.Trim();
        var destinationIsValid = TryNormalizeCurrentDestinationInput(
            destination,
            out _,
            out var resolvedInput,
            out var validationError);
        var localizedValidationError = destinationIsValid ||
                                       string.IsNullOrWhiteSpace(validationError)
            ? string.Empty
            : Localize(validationError);
        if (DestinationValidationText is not null)
        {
            if (_destinationValidationLiveRegion is { } liveRegion)
            {
                liveRegion.Update(
                    localizedValidationError,
                    severity: AccessibilityLiveRegionSeverity.Assertive,
                    announceChanges: announceValidation);
            }
            else
            {
                DestinationValidationText.Text = localizedValidationError;
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
        ReplayTutorialButton.IsEnabled = auxiliaryActionsEnabled;
        SessionAutomationSettingsButton.IsEnabled = auxiliaryActionsEnabled;
        MetadataTransferButton.IsEnabled = auxiliaryActionsEnabled;
        IntegrationsButton.IsEnabled = auxiliaryActionsEnabled;
        AboutDiagnosticsButton.IsEnabled = auxiliaryActionsEnabled;
        ReleaseNotesButton.IsEnabled = auxiliaryActionsEnabled;
        InstallUpdateButton.IsEnabled = auxiliaryActionsEnabled;
        HomeSettingsButton.IsEnabled = auxiliaryActionsEnabled;
        HomeLaunchAccountsButton.IsEnabled = auxiliaryActionsEnabled;
        HomeRunTemplateButton.IsEnabled = auxiliaryActionsEnabled;
        HomeRecordMacroButton.IsEnabled = auxiliaryActionsEnabled;
        HomeSaveTemplateButton.IsEnabled = auxiliaryActionsEnabled;
        HomeDestinationsButton.IsEnabled = auxiliaryActionsEnabled;
        HomeManageAccountsButton.IsEnabled = auxiliaryActionsEnabled;
        NewDestinationButton.IsEnabled = auxiliaryActionsEnabled;
        SaveDestinationButton.IsEnabled = auxiliaryActionsEnabled;
        DeleteDestinationButton.IsEnabled = auxiliaryActionsEnabled &&
            _editingDestinationId is not null;
        UpdateManageAccountActions();
        UpdateAutoJoinActionPresentation();
        RefreshLaunchAvailability();
        UpdateCurrentMacroActions();
    }

    private AccountProfile? FindActiveSavedProfile() =>
        _settings.Accounts.FirstOrDefault(
            account => account.Key == _settings.ActiveAccountKey)
        ?? _settings.Accounts.FirstOrDefault();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        if (_closingDestinationPromptInProgress)
            return;

        _destinationCloseRequested = true;
        if (_currentWorkspacePage == MainWorkspacePage.Destinations &&
            HasDestinationEditorChanges())
        {
            _closingDestinationPromptInProgress = true;
            try
            {
                if (!await TryResolveDestinationEditorChangesAsync())
                {
                    _destinationCloseRequested = false;
                    return;
                }
            }
            finally
            {
                _closingDestinationPromptInProgress = false;
            }
        }

        var shutdownBudget = new ShutdownTimeBudget(ShutdownTimeout);
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
        CancelCurrentMacroPlayback();
        _destinationPersistence.Cancel();
        if (!_macroPlaybackCompletion.IsCompleted)
        {
            if (shutdownBudget.TryGetRemaining(out var macroTimeout))
            {
                try
                {
                    await _macroPlaybackCompletion.WaitAsync(macroTimeout);
                }
                catch (TimeoutException exception)
                {
                    shutdownFailures.Add(exception);
                }
            }
            else
            {
                shutdownFailures.Add(new TimeoutException(
                    "Macro playback did not stop before the shutdown budget expired."));
            }
        }
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
        await ShutdownHandleScopeAsync(shutdownBudget, shutdownFailures);
        DisposeDuringShutdown(_updateService, shutdownFailures);
        DisposeDuringShutdown(_destinationPersistence, shutdownFailures);
        DisposeDuringShutdown(_operationLifetime, shutdownFailures);
    }

    private async Task ShutdownHandleScopeAsync(
        ShutdownTimeBudget shutdownBudget,
        ICollection<Exception> shutdownFailures)
    {
        if (!shutdownBudget.TryGetRemaining(out var timeout))
        {
            shutdownFailures.Add(new TimeoutException(
                "The bundled HandleScope worker did not receive a shutdown window."));
            return;
        }

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await _handleScopeRuntimeCoordinator.ShutdownAsync(
                cancellation.Token);
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested)
        {
            shutdownFailures.Add(new TimeoutException(
                "The bundled HandleScope worker did not stop before the shutdown deadline."));
        }
        catch (Exception exception)
        {
            shutdownFailures.Add(exception);
            System.Diagnostics.Trace.WriteLine(
                $"Bundled HandleScope shutdown failed safely: {exception.GetType().Name}.");
        }
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
