using System.Diagnostics;
using System.IO;
using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    // Schema-v1 onboarding files used one monotonically increasing tutorial
    // version. Keep the Get Started version above every shipped legacy value so
    // the redesigned setup flow is shown once after migration. Advanced is a
    // separate, opt-in track.
    private const int CurrentGetStartedTutorialVersion = 6;
    private const int CurrentAdvancedTutorialVersion = 2;
    private const double HomeWidth = 620;
    private const double HomeHeight = 590;
    private const double HomeMinimumWidth = 520;
    private const double HomeMinimumHeight = 500;
    private const double SettingsWidth = 680;
    private const double SettingsHeight = 600;
    private const double SettingsMinimumWidth = 560;
    private const double SettingsMinimumHeight = 480;
    private const double AdvancedWidth = 1080;
    private const double AdvancedHeight = 720;
    private const double AdvancedMinimumWidth = 800;
    private const double AdvancedMinimumHeight = 520;
    private readonly OnboardingStateStore _onboardingStateStore = new();
    private Rect? _homeRestoreBounds;
    private MainWorkspacePage? _currentWorkspacePage;
    private GuidedTutorialKind? _activeTutorial;
    private bool _homeWorkspaceInitialized;

    private enum MainWorkspacePage
    {
        Home,
        Settings,
        Destinations,
        Accounts,
        Advanced
    }

    private enum GuidedTutorialKind
    {
        GetStarted,
        Advanced
    }

    private void InitializeHomeWorkspace()
    {
        if (_homeWorkspaceInitialized)
            return;

        _homeWorkspaceInitialized = true;
        HomeGuidedTour.Completed += HomeGuidedTour_Finished;
        HomeGuidedTour.Skipped += HomeGuidedTour_Finished;
        ShowHomeWorkspace(resizeWindow: true);
#if !SESSIONDOCK_SMOKE_HARNESS
        _ = _startupCompletion.Task.ContinueWith(
            _ => Dispatcher.BeginInvoke(TryStartFirstRunTutorial),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
#endif
    }

    private void HomeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowSettingsWorkspace();
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowHomeWorkspace(resizeWindow: true);
    }

    private async void SetupBackButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_currentWorkspacePage == MainWorkspacePage.Destinations &&
            (!await TryResolveDestinationEditorChangesAsync() ||
             _destinationCloseRequested))
        {
            return;
        }
        ShowHomeWorkspace(resizeWindow: true);
    }

    private void HomeDestinationsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToWorkspace(MainWorkspacePage.Destinations, resizeWindow: true);
    }

    private void HomeManageAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToWorkspace(MainWorkspacePage.Accounts, resizeWindow: true);
    }

    private void SettingsAdvancedWorkspaceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowAdvancedWorkspace();
    }

    private void HomeFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowHomeWorkspace(resizeWindow: true);
    }

    private void ReplayTutorialButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        StartGetStartedTutorial();
    }

    private void GetStartedTutorialButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        StartGetStartedTutorial();
    }

    private void AdvancedTutorialButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        StartAdvancedTutorial();
    }

    private async void SettingsThemeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ThemeToggleButton.IsChecked = ThemeToggleButton.IsChecked != true;
        await RunWindowOperationAsync(_ => ThemeToggleButtonClickAsync());
    }

    private void ShowSettingsWorkspace() =>
        NavigateToWorkspace(MainWorkspacePage.Settings, resizeWindow: true);

    private void ShowAdvancedWorkspace() =>
        NavigateToWorkspace(MainWorkspacePage.Advanced, resizeWindow: true);

    private void ShowHomeWorkspace(bool resizeWindow) =>
        NavigateToWorkspace(MainWorkspacePage.Home, resizeWindow);

    private void NavigateToWorkspace(
        MainWorkspacePage page,
        bool resizeWindow)
    {
        if (page != MainWorkspacePage.Advanced &&
            page != MainWorkspacePage.Accounts)
        {
            _returnToAccountsAfterBrowser = false;
        }
        if (page != MainWorkspacePage.Home && !HomeGuidedTour.IsRunning)
            HomeGuidedTour.Stop();
        if (HomeWorkspace.Visibility == Visibility.Visible &&
            page != MainWorkspacePage.Home)
        {
            _homeRestoreBounds = RestoreBounds;
        }

        HomeWorkspace.Visibility = page == MainWorkspacePage.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsHubWorkspace.Visibility = page == MainWorkspacePage.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        DestinationsWorkspace.Visibility = page == MainWorkspacePage.Destinations
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountsWorkspace.Visibility = page == MainWorkspacePage.Accounts
            ? Visibility.Visible
            : Visibility.Collapsed;
        AdvancedWorkspace.Visibility = page == MainWorkspacePage.Advanced
            ? Visibility.Visible
            : Visibility.Collapsed;
        HomeCaptionControls.Visibility = page == MainWorkspacePage.Advanced
            ? Visibility.Collapsed
            : Visibility.Visible;
        _currentWorkspacePage = page;

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

        switch (page)
        {
            case MainWorkspacePage.Home:
                MinWidth = HomeMinimumWidth;
                MinHeight = HomeMinimumHeight;
                if (resizeWindow)
                {
                    var restore = _homeRestoreBounds;
                    Width = restore is { Width: >= HomeMinimumWidth }
                        ? Math.Min(restore.Value.Width, HomeWidth)
                        : HomeWidth;
                    Height = restore is { Height: >= HomeMinimumHeight }
                        ? Math.Min(restore.Value.Height, HomeHeight)
                        : HomeHeight;
                    if (restore is not null)
                    {
                        Left = restore.Value.Left;
                        Top = restore.Value.Top;
                    }
                }
                break;
            case MainWorkspacePage.Settings:
            case MainWorkspacePage.Destinations:
            case MainWorkspacePage.Accounts:
                MinWidth = SettingsMinimumWidth;
                MinHeight = SettingsMinimumHeight;
                if (resizeWindow)
                {
                    Width = SettingsWidth;
                    Height = SettingsHeight;
                }
                break;
            case MainWorkspacePage.Advanced:
                MinWidth = AdvancedMinimumWidth;
                MinHeight = AdvancedMinimumHeight;
                if (resizeWindow)
                {
                    Width = Math.Max(AdvancedWidth, ActualWidth);
                    Height = Math.Max(AdvancedHeight, ActualHeight);
                }
                break;
            default:
                throw new InvalidOperationException(
                    "Unexpected main workspace page.");
        }

        WindowLayoutService.FitToWorkArea(this);
        switch (page)
        {
            case MainWorkspacePage.Home:
                HomeLaunchAccountsButton.Focus();
                break;
            case MainWorkspacePage.Settings:
                SettingsBackButton.Focus();
                break;
            case MainWorkspacePage.Destinations:
                RefreshDestinationsWorkspace();
                DestinationsBackButton.Focus();
                break;
            case MainWorkspacePage.Accounts:
                RefreshAccountsWorkspace();
                AccountsBackButton.Focus();
                break;
            case MainWorkspacePage.Advanced:
                HomeFromSettingsButton.Focus();
                break;
        }
    }

#if SESSIONDOCK_SMOKE_HARNESS
    internal void VerifyWorkspaceCaptionControlsForRuntimeSmoke()
    {
        Dispatcher.VerifyAccess();
        if (HomeWorkspace.Visibility != Visibility.Visible ||
            SettingsHubWorkspace.Visibility != Visibility.Collapsed ||
            DestinationsWorkspace.Visibility != Visibility.Collapsed ||
            AccountsWorkspace.Visibility != Visibility.Collapsed ||
            AdvancedWorkspace.Visibility != Visibility.Collapsed ||
            _currentWorkspacePage != MainWorkspacePage.Home)
        {
            throw new InvalidOperationException(
                "The runtime smoke did not start in the Home workspace.");
        }

        UpdateLayout();
        HomeCaptionControls.VerifyForRuntimeSmoke();

        ShowSettingsWorkspace();
        UpdateLayout();
        if (HomeWorkspace.Visibility != Visibility.Collapsed ||
            SettingsHubWorkspace.Visibility != Visibility.Visible ||
            DestinationsWorkspace.Visibility != Visibility.Collapsed ||
            AccountsWorkspace.Visibility != Visibility.Collapsed ||
            AdvancedWorkspace.Visibility != Visibility.Collapsed ||
            _currentWorkspacePage != MainWorkspacePage.Settings)
        {
            throw new InvalidOperationException(
                "The runtime smoke could not open the Settings hub.");
        }
        HomeCaptionControls.VerifyForRuntimeSmoke();

        ShowAdvancedWorkspace();
        UpdateLayout();
        CaptionControls.VerifyForRuntimeSmoke();

        ShowHomeWorkspace(resizeWindow: false);
        UpdateLayout();
        if (HomeWorkspace.Visibility != Visibility.Visible ||
            SettingsHubWorkspace.Visibility != Visibility.Collapsed ||
            DestinationsWorkspace.Visibility != Visibility.Collapsed ||
            AccountsWorkspace.Visibility != Visibility.Collapsed ||
            AdvancedWorkspace.Visibility != Visibility.Collapsed ||
            _currentWorkspacePage != MainWorkspacePage.Home)
        {
            throw new InvalidOperationException(
                "The runtime smoke could not restore the Home workspace.");
        }
        HomeCaptionControls.VerifyForRuntimeSmoke();
    }
#endif

    private void TryStartFirstRunTutorial()
    {
        if (_operationLifetime.IsShuttingDown || HomeGuidedTour.IsRunning)
            return;

        var state = _onboardingStateStore.Read();
        if (state.State.GetStartedTutorialVersion >=
            CurrentGetStartedTutorialVersion)
        {
            return;
        }
        StartGetStartedTutorial();
    }

    private void StartGetStartedTutorial()
    {
        if (_currentWorkspacePage != MainWorkspacePage.Home)
            ShowHomeWorkspace(resizeWindow: true);

        _activeTutorial = GuidedTutorialKind.GetStarted;
        HomeGuidedTour.Start(
            [
                new GuidedTourStep(
                    HomeManageAccountsButton,
                    Localize("Tutorial.AccountsTitle"),
                    Localize("Tutorial.AccountsBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    ManageAccountsAddButton,
                    Localize("Tutorial.AccountAddTitle"),
                    Localize("Tutorial.AccountAddBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Accounts)),
                new GuidedTourStep(
                    HomeDestinationsButton,
                    Localize("Tutorial.DestinationsTitle"),
                    Localize("Tutorial.DestinationsBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    DestinationNameBox,
                    Localize("Tutorial.DestinationDetailsTitle"),
                    Localize("Tutorial.DestinationDetailsBody"),
                    () => PrepareTutorialWorkspace(
                        MainWorkspacePage.Destinations)),
                new GuidedTourStep(
                    DestinationValueBox,
                    Localize("Tutorial.DestinationTargetTitle"),
                    Localize("Tutorial.DestinationTargetBody"),
                    () => PrepareTutorialWorkspace(
                        MainWorkspacePage.Destinations)),
                new GuidedTourStep(
                    DestinationAccountAssignmentsList,
                    Localize("Tutorial.DestinationAccountsTitle"),
                    Localize("Tutorial.DestinationAccountsBody"),
                    () => PrepareTutorialWorkspace(
                        MainWorkspacePage.Destinations)),
                new GuidedTourStep(
                    SaveDestinationButton,
                    Localize("Tutorial.DestinationAssignTitle"),
                    Localize("Tutorial.DestinationAssignBody"),
                    () => PrepareTutorialWorkspace(
                        MainWorkspacePage.Destinations)),
                new GuidedTourStep(
                    HomeLaunchAccountsButton,
                    Localize("Tutorial.LaunchTitle"),
                    Localize("Tutorial.LaunchBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    HomeRunTemplateButton,
                    Localize("Tutorial.RunTitle"),
                    Localize("Tutorial.RunBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    HomeRecordMacroButton,
                    Localize("Tutorial.RecordTitle"),
                    Localize("Tutorial.RecordBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    HomeSaveTemplateButton,
                    Localize("Tutorial.SaveTitle"),
                    Localize("Tutorial.SaveBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home)),
                new GuidedTourStep(
                    HomeSettingsButton,
                    Localize("Tutorial.SettingsTitle"),
                    Localize("Tutorial.SettingsBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Home))
            ],
            Localize("Tutorial.Progress"),
            Localize("Tutorial.Back"),
            Localize("Tutorial.Next"),
            Localize("Tutorial.Finish"),
            Localize("Tutorial.Skip"));
    }

    private void StartAdvancedTutorial()
    {
        if (_currentWorkspacePage != MainWorkspacePage.Settings)
            ShowSettingsWorkspace();

        _activeTutorial = GuidedTutorialKind.Advanced;
        HomeGuidedTour.Start(
            [
                new GuidedTourStep(
                    SettingsWindowLayoutButton,
                    Localize("Tutorial.AdvancedLayoutTitle"),
                    Localize("Tutorial.AdvancedLayoutBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsMacroLibraryButton,
                    Localize("Tutorial.AdvancedMacrosTitle"),
                    Localize("Tutorial.AdvancedMacrosBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsTemplatesButton,
                    Localize("Tutorial.AdvancedTemplatesTitle"),
                    Localize("Tutorial.AdvancedTemplatesBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsMetadataTransferButton,
                    Localize("Tutorial.AdvancedTransferTitle"),
                    Localize("Tutorial.AdvancedTransferBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsBatchAssignmentsButton,
                    Localize("Tutorial.AdvancedAssignmentsTitle"),
                    Localize("Tutorial.AdvancedAssignmentsBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsMacroControllerButton,
                    Localize("Tutorial.AdvancedControllerTitle"),
                    Localize("Tutorial.AdvancedControllerBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings)),
                new GuidedTourStep(
                    SettingsAdvancedWorkspaceButton,
                    Localize("Tutorial.AdvancedWorkspaceTitle"),
                    Localize("Tutorial.AdvancedWorkspaceBody"),
                    () => PrepareTutorialWorkspace(MainWorkspacePage.Settings))
            ],
            Localize("Tutorial.Progress"),
            Localize("Tutorial.Back"),
            Localize("Tutorial.Next"),
            Localize("Tutorial.Finish"),
            Localize("Tutorial.Skip"));
    }

    private void PrepareTutorialWorkspace(MainWorkspacePage page)
    {
        if (_currentWorkspacePage != page)
            NavigateToWorkspace(page, resizeWindow: true);
        UpdateLayout();
    }

    private void HomeGuidedTour_Finished(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        var completedTutorial = _activeTutorial;
        _activeTutorial = null;
        if (completedTutorial is null)
            return;

        try
        {
            var existing = _onboardingStateStore.Read().State;
            var updated = completedTutorial == GuidedTutorialKind.GetStarted
                ? existing with
                {
                    GetStartedTutorialVersion =
                        CurrentGetStartedTutorialVersion
                }
                : existing with
                {
                    AdvancedTutorialVersion = CurrentAdvancedTutorialVersion
                };
            _onboardingStateStore.Write(updated);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception) ||
            exception is ArgumentException or InvalidDataException)
        {
            Trace.WriteLine(
                $"Tutorial completion could not be saved: {exception.GetType().Name}.");
        }
        if (completedTutorial == GuidedTutorialKind.GetStarted)
        {
            if (_currentWorkspacePage != MainWorkspacePage.Home)
                ShowHomeWorkspace(resizeWindow: true);
            HomeManageAccountsButton.Focus();
        }
        else
        {
            if (_currentWorkspacePage != MainWorkspacePage.Settings)
                ShowSettingsWorkspace();
            SettingsAdvancedWorkspaceButton.Focus();
        }
    }

    private void HomeLaunchAccountsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        if (_settings.Accounts.Count < 2)
        {
            var minimumMessage = Localize("Batch.MinimumSelection");
            _homeStatusLiveRegion.Update(
                minimumMessage,
                minimumMessage,
                AccessibilityLiveRegionSeverity.Assertive);
            NavigateToWorkspace(
                MainWorkspacePage.Accounts,
                resizeWindow: true);
            RefreshAccountsWorkspace();
            ManageAccountsAddButton.Focus();
            return;
        }

        BatchLaunchButton_Click(BatchLaunchButton, e);
    }

    private void HomeRunTemplateButton_Click(
        object sender,
        RoutedEventArgs e) =>
        RunTemplateButtonClick(sender, e);

    private void HomeRecordMacroButton_Click(
        object sender,
        RoutedEventArgs e) =>
        MacroLibrarySettingsButton_Click(sender, e);

    private void HomeSaveTemplateButton_Click(
        object sender,
        RoutedEventArgs e) =>
        TemplateSettingsButton_Click(sender, e);
}
