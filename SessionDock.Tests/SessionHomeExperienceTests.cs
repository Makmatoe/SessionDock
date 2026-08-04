using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class SessionHomeExperienceTests
{
    [Fact]
    public void HomeWorkspace_ExposesCompactActionsSettingsAndTourOverlay()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("HomeWorkspace", names);
        Assert.Contains("HomeLaunchAccountsButton", names);
        Assert.Contains("HomeRunTemplateButton", names);
        Assert.Contains("HomeRecordMacroButton", names);
        Assert.Contains("HomeSaveTemplateButton", names);
        Assert.Contains("HomeDestinationsButton", names);
        Assert.Contains("HomeManageAccountsButton", names);
        Assert.Contains("HomeSettingsButton", names);
        Assert.Contains("HomeCancelBatchButton", names);
        Assert.Contains("HomeGuidedTour", names);
        Assert.Contains("SettingsHubWorkspace", names);
        Assert.Contains("DestinationsWorkspace", names);
        Assert.Contains("AccountsWorkspace", names);

        var homeWorkspace = document.Descendants(presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "HomeWorkspace");
        Assert.Contains(
            homeWorkspace.Descendants(presentation + "ScrollViewer"),
            scrollViewer =>
                (string?)scrollViewer.Attribute(
                    "VerticalScrollBarVisibility") == "Auto");

        var advanced = document.Descendants(presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "AdvancedWorkspace");
        Assert.Equal(
            "Collapsed",
            (string?)advanced.Attribute("Visibility"));

        var settingsHub = document.Descendants(presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "SettingsHubWorkspace");
        Assert.Equal(
            "Collapsed",
            (string?)settingsHub.Attribute("Visibility"));

        foreach (var workspaceName in new[]
                 {
                     "DestinationsWorkspace",
                     "AccountsWorkspace"
                 })
        {
            var workspace = document.Descendants(presentation + "Grid")
                .Single(element =>
                    (string?)element.Attribute(xaml + "Name") ==
                    workspaceName);
            Assert.Equal(
                "Collapsed",
                (string?)workspace.Attribute("Visibility"));
        }

        var homeButtons = document.Descendants(presentation + "Button")
            .Where(element => element.Attribute(xaml + "Name") is not null)
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Name")!,
                StringComparer.Ordinal);
        Assert.Equal(
            "HomeRecordMacroButton_Click",
            (string?)homeButtons["HomeRecordMacroButton"].Attribute("Click"));
        Assert.Equal(
            "HomeSaveTemplateButton_Click",
            (string?)homeButtons["HomeSaveTemplateButton"].Attribute("Click"));
        Assert.Equal(
            "HomeDestinationsButton_Click",
            (string?)homeButtons["HomeDestinationsButton"].Attribute("Click"));
        Assert.Equal(
            "HomeManageAccountsButton_Click",
            (string?)homeButtons["HomeManageAccountsButton"].Attribute("Click"));

        var source = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Home.cs"));
        Assert.Contains(
            "MacroLibrarySettingsButton_Click(sender, e)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateSettingsButton_Click(sender, e)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsHub_ProvidesFocusedRoutesAndExplicitAdvancedAccess()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var settingsHub = document.Descendants(presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "SettingsHubWorkspace");
        var expectedRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SettingsThemeButton"] = "SettingsThemeButton_Click",
            ["SettingsSoundButton"] = "SoundSettingsButton_Click",
            ["SettingsLanguageButton"] = "LanguageSettingsButton_Click",
            ["SettingsWindowLayoutButton"] =
                "WindowLayoutSettingsButton_Click",
            ["SettingsMacroLibraryButton"] =
                "MacroLibrarySettingsButton_Click",
            ["SettingsTemplatesButton"] =
                "TemplateSettingsButton_Click",
            ["SettingsBatchAssignmentsButton"] =
                "CurrentBatchAssignmentsButton_Click",
            ["SettingsMacroControllerButton"] =
                "OpenMacroControllerButton_Click",
            ["SettingsIntegrationsButton"] = "IntegrationsButton_Click",
            ["SettingsMetadataTransferButton"] =
                "MetadataTransferButton_Click",
            ["SettingsReplayTutorialButton"] =
                "GetStartedTutorialButton_Click",
            ["SettingsAdvancedTutorialButton"] =
                "AdvancedTutorialButton_Click",
            ["SettingsAboutButton"] = "AboutDiagnosticsButton_Click",
            ["SettingsReleaseNotesButton"] = "ReleaseNotesButton_Click",
            ["SettingsUpdateButton"] = "InstallUpdateButton_Click",
            ["SettingsAdvancedWorkspaceButton"] =
                "SettingsAdvancedWorkspaceButton_Click"
        };

        var buttons = settingsHub.Descendants(presentation + "Button")
            .Where(element => element.Attribute(xaml + "Name") is not null)
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Name")!,
                StringComparer.Ordinal);
        foreach (var (name, clickHandler) in expectedRoutes)
        {
            Assert.True(buttons.ContainsKey(name), name);
            var button = buttons[name];
            Assert.Equal(clickHandler, (string?)button.Attribute("Click"));
            Assert.NotNull(button.Attribute("Content"));
        }
        foreach (var name in new[]
                 {
                     "SettingsWindowLayoutButton",
                     "SettingsMacroLibraryButton",
                     "SettingsTemplatesButton"
                 })
        {
            Assert.NotNull(
                buttons[name].Attribute("AutomationProperties.Name"));
            Assert.NotNull(buttons[name].Attribute("AutomationProperties.HelpText"));
        }

        Assert.Equal(
            "False",
            (string?)buttons["SettingsBatchAssignmentsButton"]
                .Attribute("IsEnabled"));
        Assert.Equal(
            "False",
            (string?)buttons["SettingsMacroControllerButton"]
                .Attribute("IsEnabled"));
        Assert.DoesNotContain(
            settingsHub.Descendants(),
            element =>
                (string?)element.Attribute(xaml + "Name") ==
                "RunningClientsButton");

        var source = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Home.cs"));
        var homeSettingsHandler = source[
            source.IndexOf(
                "private void HomeSettingsButton_Click",
                StringComparison.Ordinal)..source.IndexOf(
                "private void SettingsBackButton_Click",
                StringComparison.Ordinal)];
        Assert.Contains("ShowSettingsWorkspace();", homeSettingsHandler);
        Assert.DoesNotContain("ShowAdvancedWorkspace();", homeSettingsHandler);
        Assert.Contains("MainWorkspacePage.Settings", source);
    }

    [Fact]
    public void GuidedTours_SeparateGetStartedFromAdvancedRoutes()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Home.cs"));
        var normalizedSource = source.ReplaceLineEndings();

        foreach (var target in new[]
                 {
                     "HomeLaunchAccountsButton",
                     "HomeRunTemplateButton",
                     "HomeRecordMacroButton",
                     "HomeSaveTemplateButton",
                     "HomeDestinationsButton",
                     "HomeManageAccountsButton",
                     "ManageAccountsAddButton",
                     "DestinationNameBox",
                     "DestinationValueBox",
                     "DestinationAccountAssignmentsList",
                     "SaveDestinationButton",
                     "HomeSettingsButton",
                     "SettingsWindowLayoutButton",
                     "SettingsMacroLibraryButton",
                     "SettingsTemplatesButton",
                     "SettingsMetadataTransferButton",
                     "SettingsBatchAssignmentsButton",
                     "SettingsMacroControllerButton",
                     "SettingsAdvancedWorkspaceButton"
                 })
        {
            Assert.Contains(
                $"new GuidedTourStep({Environment.NewLine}                    {target}",
                normalizedSource,
                StringComparison.Ordinal);
        }
        Assert.Contains("ReplayTutorialButton_Click", source);
        Assert.Contains("GetStartedTutorialButton_Click", source);
        Assert.Contains("AdvancedTutorialButton_Click", source);
        Assert.Contains("StartGetStartedTutorial()", source);
        Assert.Contains("StartAdvancedTutorial()", source);
        Assert.Contains("OnboardingStateStore", source);
        Assert.Contains("PrepareTutorialWorkspace(MainWorkspacePage.Settings)", source);
        Assert.Contains("CurrentGetStartedTutorialVersion = 6", source);
        Assert.Contains("CurrentAdvancedTutorialVersion = 2", source);
        Assert.Contains("GetStartedTutorialVersion =", source);
        Assert.Contains("AdvancedTutorialVersion =", source);
        Assert.DoesNotContain("CurrentTutorialVersion", source);
    }

    [Fact]
    public void FocusedSetupPages_UseNamedDestinationsAndExistingAccountFlows()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Setup.cs"));
        var saveStart = source.IndexOf(
            "private async Task<bool> SaveDestinationAsync",
            StringComparison.Ordinal);
        var saveEnd = source.IndexOf(
            "private async void DeleteDestinationButton_Click",
            saveStart,
            StringComparison.Ordinal);
        Assert.True(saveStart >= 0 && saveEnd > saveStart);
        var saveBlock = source[saveStart..saveEnd];
        Assert.Contains(
            "await FlushDestinationPersistenceAsync()",
            saveBlock,
            StringComparison.Ordinal);
        Assert.True(
            saveBlock.IndexOf(
                "await FlushDestinationPersistenceAsync()",
                StringComparison.Ordinal) <
            saveBlock.IndexOf(
                "NamedDestinationPolicy.TryUpsert",
                StringComparison.Ordinal));

        var signInStart = source.IndexOf(
            "private async void ManageAccountsSignInButton_Click",
            StringComparison.Ordinal);
        var signInEnd = source.IndexOf(
            "private string AccountDestinationSummary",
            signInStart,
            StringComparison.Ordinal);
        Assert.True(signInStart >= 0 && signInEnd > signInStart);
        var signInBlock = source[signInStart..signInEnd];
        var switchIndex = signInBlock.IndexOf(
            "AccountButtonClickAsync(",
            StringComparison.Ordinal);
        var loginIndex = signInBlock.IndexOf(
            "SignInButtonClickAsync(",
            StringComparison.Ordinal);
        Assert.True(switchIndex >= 0);
        Assert.True(loginIndex > switchIndex);
        Assert.Contains(
            "_activeProfile?.Key",
            signInBlock,
            StringComparison.Ordinal);

        foreach (var marker in new[]
                 {
                     "HasDestinationEditorChanges()",
                     "TryResolveDestinationEditorChangesAsync()",
                     "DestinationUnsavedSaveButton_Click",
                     "DestinationUnsavedDiscardButton_Click",
                     "DestinationUnsavedCancelButton_Click",
                     "DestinationEditorBaseline"
                 })
        {
            Assert.Contains(marker, source, StringComparison.Ordinal);
        }
        Assert.Contains(
            "NamedDestinationValidationLiveRegion.Update(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccessibilityLiveRegionSeverity.Assertive",
            source,
            StringComparison.Ordinal);

        var homeSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Home.cs"));
        var launchStart = homeSource.IndexOf(
            "private void HomeLaunchAccountsButton_Click",
            StringComparison.Ordinal);
        var launchEnd = homeSource.IndexOf(
            "private void HomeRunTemplateButton_Click",
            launchStart,
            StringComparison.Ordinal);
        Assert.True(launchStart >= 0 && launchEnd > launchStart);
        var launchBlock = homeSource[launchStart..launchEnd];
        Assert.Contains("Batch.MinimumSelection", launchBlock);
        Assert.Contains("MainWorkspacePage.Accounts", launchBlock);
        Assert.Contains("RefreshAccountsWorkspace();", launchBlock);
        Assert.Contains("ManageAccountsAddButton.Focus();", launchBlock);
        Assert.DoesNotContain("ShowAdvancedWorkspace", launchBlock);
        Assert.Contains(
            "!await TryResolveDestinationEditorChangesAsync()",
            homeSource,
            StringComparison.Ordinal);

        var mainSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml.cs"));
        Assert.Contains(
            "HomeLaunchAccountsButton.IsEnabled = auxiliaryActionsEnabled;",
            mainSource,
            StringComparison.Ordinal);

        var mainDocument = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var setupNames = mainDocument.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DestinationsEmptyText", setupNames);
        Assert.Contains("AccountsEmptyText", setupNames);
        Assert.Contains("DestinationUnsavedOverlay", setupNames);
        Assert.Contains("DestinationEditorHeadingText", setupNames);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var assignmentList = mainDocument
            .Descendants(presentation + "ListBox")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "DestinationAccountAssignmentsList");
        var assignmentCheckBox = assignmentList
            .Descendants(presentation + "CheckBox")
            .Single();
        Assert.Equal(
            "{Binding DestinationSummary}",
            (string?)assignmentCheckBox.Attribute(
                "AutomationProperties.HelpText"));
    }

    [Fact]
    public void AutomationSettings_RoutesOnePreservedComponentSetAtATime()
    {
        var root = FindRepositoryRoot();
        var dialog = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "SessionAutomationSettingsDialog.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = dialog.Root!;
        Assert.True(double.Parse(
            (string)window.Attribute("Width")!,
            System.Globalization.CultureInfo.InvariantCulture) <= 680);

        var names = dialog.Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("WindowLayoutSection", names);
        Assert.Contains("TemplatesSection", names);
        Assert.Contains("MacroLibrarySection", names);
        Assert.Contains("RecordMacroButton", names);

        var recordButton = dialog
            .Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "RecordMacroButton");
        Assert.Equal(
            "RecordMacroButton_Click",
            (string?)recordButton.Attribute("Click"));

        var dialogSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "SessionAutomationSettingsDialog.xaml.cs"));
        Assert.Contains("SessionAutomationSettingsRoute.WindowLayout", dialogSource);
        Assert.Contains("SessionAutomationSettingsRoute.MacroLibrary", dialogSource);
        Assert.Contains("SessionAutomationSettingsRoute.Templates", dialogSource);
        Assert.Contains("LibrarySectionsGrid.Visibility", dialogSource);
        Assert.Contains("Grid.SetColumnSpan(TemplatesSection, 3)", dialogSource);
        Assert.Contains("Grid.SetColumnSpan(MacroLibrarySection, 3)", dialogSource);
        Assert.Contains("SaveCurrentSessionButton", names);
        var saveCurrentButton = dialog
            .Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "SaveCurrentSessionButton");
        Assert.Equal(
            "SaveCurrentSessionButton_Click",
            (string?)saveCurrentButton.Attribute("Click"));
        Assert.Contains(
            "SessionAutomationSettingsDialogAction.SaveCurrentTemplate",
            dialogSource,
            StringComparison.Ordinal);

        var templatesSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Templates.cs"));
        var settingsStart = templatesSource.IndexOf(
            "private async Task SessionAutomationSettingsButtonClickAsync(",
            StringComparison.Ordinal);
        var settingsEnd = templatesSource.IndexOf(
            "private async Task RunTemplateButtonClickAsync(",
            settingsStart,
            StringComparison.Ordinal);
        Assert.True(settingsStart >= 0 && settingsEnd > settingsStart);
        var settingsBlock = templatesSource[settingsStart..settingsEnd];
        Assert.Contains(
            "SessionAutomationSettingsDialogAction.SaveCurrentTemplate",
            settingsBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "await SaveTemplateButtonClickAsync(cancellationToken);",
            settingsBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RunTemplateButtonClickAsync",
            settingsBlock,
            StringComparison.Ordinal);

        var firstRunGuide = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "GETTING_STARTED.md"));
        Assert.Contains(
            "**Open automation settings**",
            firstRunGuide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuidedTour_NextBackAndFinishAdvanceTheVisibleStep()
    {
        RunOnSta(() =>
        {
            var overlay = new GuidedTourOverlay();
            var completed = 0;
            var prepared = new List<string>();
            overlay.Completed += (_, _) => completed++;
            overlay.Start(
                [
                    new GuidedTourStep(
                        new Button(),
                        "First",
                        "First body",
                        () => prepared.Add("first")),
                    new GuidedTourStep(
                        new Button(),
                        "Second",
                        "Second body",
                        () => prepared.Add("second")),
                    new GuidedTourStep(
                        new Button(),
                        "Third",
                        "Third body",
                        () => prepared.Add("third"))
                ],
                "{0} of {1}",
                "Back",
                "Next",
                "Finish",
                "Skip");

            Assert.Equal("1 of 3", overlay.ProgressText.Text);
            Assert.Equal("First", overlay.TitleText.Text);
            Assert.Equal(new[] { "first" }, prepared);

            overlay.NextButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2 of 3", overlay.ProgressText.Text);
            Assert.Equal("Second", overlay.TitleText.Text);
            Assert.Equal(new[] { "first", "second" }, prepared);

            overlay.BackButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1 of 3", overlay.ProgressText.Text);
            Assert.Equal(
                new[] { "first", "second", "first" },
                prepared);

            overlay.NextButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            overlay.NextButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Finish", overlay.NextButton.Content);
            overlay.NextButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, completed);
            Assert.False(overlay.IsRunning);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        });
    }

    [Fact]
    public void HomeBatchStop_IsHiddenByDefaultAndTracksBothBatchPaths()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var stopButton = document.Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "HomeCancelBatchButton");

        Assert.Equal("Collapsed", (string?)stopButton.Attribute("Visibility"));
        Assert.Equal("False", (string?)stopButton.Attribute("IsEnabled"));
        Assert.Equal(
            "CancelBatchButton_Click",
            (string?)stopButton.Attribute("Click"));
        Assert.Equal(
            "{DynamicResource Macro.Stop}",
            (string?)stopButton.Attribute("Content"));
        Assert.Equal(
            "{DynamicResource Home.StopAutomationHelp}",
            (string?)stopButton.Attribute("AutomationProperties.HelpText"));

        var batchSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Batch.cs"));
        var templateSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Templates.cs"));
        Assert.Contains(
            "HomeCancelBatchButton.Visibility = visibility",
            batchSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "HomeCancelBatchButton.IsEnabled = active && enabled",
            batchSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetBatchCancellationControls(active: true, enabled: true)",
            batchSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetBatchCancellationControls(active: true, enabled: true)",
            templateSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuidedTour_AnnouncementIncludesProgressTitleAndBody()
    {
        Assert.Equal(
            "2 of 5 Record macro. Keep the run harmless.",
            GuidedTourOverlay.CreateAnnouncement(
                " 2 of 5 ",
                " Record macro. ",
                " Keep the run harmless. "));
        Assert.Equal(
            "2 of 5 Guided preview. Record macro. Keep the run harmless.",
            GuidedTourOverlay.CreateAnnouncement(
                " 2 of 5 ",
                " Record macro. ",
                " Keep the run harmless. ",
                " Guided preview. "));

        var overlaySource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "GuidedTourOverlay.xaml.cs"));
        Assert.Contains("step.Target.BringIntoView();", overlaySource);
        Assert.Contains("PreviewText.Text", overlaySource);

        var overlayDocument = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "GuidedTourOverlay.xaml"));
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var preview = overlayDocument.Descendants()
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "PreviewText");
        Assert.Equal(
            "{DynamicResource Tutorial.Preview}",
            (string?)preview.Attribute("Text"));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception currentException)
            {
                exception = currentException;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(15)),
            "The guided-tour STA test did not finish within 15 seconds.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
