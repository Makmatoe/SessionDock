namespace SessionDock.Tests;

public sealed class SessionUiReadinessTests
{
    [Fact]
    public void GuidedTour_KeepsKeyboardLiveRegionAndBoundedCalloutContracts()
    {
        var xaml = ReadProductionFile("GuidedTourOverlay.xaml");
        var source = ReadProductionFile("GuidedTourOverlay.xaml.cs");

        Assert.Contains(
            "PreviewKeyDown=\"Overlay_PreviewKeyDown\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyboardNavigation.TabNavigation=\"Cycle\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalScrollBarVisibility=\"Auto\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Assertive\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(TitleText)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GuidedTourPlacementPolicy.Calculate(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Callout.MaxHeight = placement.Bounds.Height",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClipToBounds=\"True\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetCompactNavigation(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NewDialogs_KeepEscapeDefaultFocusAndSpokenStatusContracts()
    {
        var templateXaml = ReadProductionFile("TemplateEditorDialog.xaml");
        var templateSource = ReadProductionFile(
            "TemplateEditorDialog.xaml.cs");
        var runXaml = ReadProductionFile("RunTemplateDialog.xaml");
        var runSource = ReadProductionFile("RunTemplateDialog.xaml.cs");
        var recorderXaml = ReadProductionFile("MacroRecorderDialog.xaml");
        var recorderSource = ReadProductionFile("MacroRecorderDialog.xaml.cs");
        var settingsXaml = ReadProductionFile(
            "SessionAutomationSettingsDialog.xaml");
        var settingsSource = ReadProductionFile(
            "SessionAutomationSettingsDialog.xaml.cs");

        foreach (var xaml in new[]
                 {
                     templateXaml,
                     runXaml,
                     recorderXaml,
                     settingsXaml
                 })
        {
            Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
            Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains(
            "FocusValidationTarget(focusTarget)",
            templateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsTabStop=\"False\"",
            templateXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(TemplateSummaryText)",
            runSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Assertive\"",
            runXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "StopButton.IsDefault =",
            recorderSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpText=\"{DynamicResource Macro.ModeClientHelp}\"",
            recorderXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LabeledBy=",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Loaded += (_, _) => FocusRouteEntryPoint()",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutoArrangeCheckBox.Focus()",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecordMacroButton.Focus()",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacroListBox.Focus()",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SaveCurrentSessionButton.Focus()",
            settingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateListBox.Focus()",
            settingsSource,
            StringComparison.Ordinal);
    }

    private static string ReadProductionFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
