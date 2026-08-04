using System.IO;

namespace SessionDock.Tests;

public sealed class AccessibilityStatusIntegrationTests
{
    [Theory]
    [InlineData("Ready", "Player verified.", "ACCOUNT VERIFIED",
        "Ready Player verified. ACCOUNT VERIFIED")]
    [InlineData("  Ready  ", "", " VERIFIED ", "Ready VERIFIED")]
    [InlineData("", "Working", "", "Working")]
    public void MainStatusAnnouncement_OmitsBlankSegmentsAndTrimsValues(
        string title,
        string detail,
        string badge,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindow.CreateStatusAnnouncement(title, detail, badge));
    }

    [Fact]
    public void ApplicationStatuses_UseTheCentralLiveRegionHelper()
    {
        var mainSource = ReadProductionFile("MainWindow.xaml.cs");
        var metadataSource = ReadProductionFile(
            "MetadataTransferDialog.xaml.cs");
        var linkSource = ReadProductionFile(
            "RobloxLinkIntegrationDialog.xaml.cs");
        var aboutSource = ReadProductionFile(
            "AboutDiagnosticsDialog.xaml.cs");
        var batchSource = ReadProductionFile("BatchLaunchDialog.xaml.cs");
        var handleScopeSource = ReadProductionFile(
            "HandleScopeIntegrationDialog.xaml.cs");
        var runningClientsSource = ReadProductionFile(
            "RunningClientsDialog.xaml.cs");
        var soundSource = ReadProductionFile("SoundSettingsDialog.xaml.cs");
        var tourSource = ReadProductionFile("GuidedTourOverlay.xaml.cs");
        var templateEditorSource = ReadProductionFile(
            "TemplateEditorDialog.xaml.cs");
        var templateRunSource = ReadProductionFile(
            "RunTemplateDialog.xaml.cs");
        var macroRecorderSource = ReadProductionFile(
            "MacroRecorderDialog.xaml.cs");
        var automationSettingsSource = ReadProductionFile(
            "SessionAutomationSettingsDialog.xaml.cs");
        var localizationSource = ReadProductionFile(
            "MainWindow.Localization.cs");
        var mainXaml = ReadProductionFile("MainWindow.xaml");

        Assert.Contains(
            "new AccessibilityLiveRegion(StatusTitle)",
            mainSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(HomeStatusText)",
            mainSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccessibilityLiveRegionSeverity.Assertive",
            mainSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ExportStatusText)",
            metadataSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ImportStatusText)",
            metadataSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(StateTitleText)",
            linkSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ActionStatusText)",
            linkSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ActionStatusText)",
            aboutSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            batchSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(StateTitleText)",
            handleScopeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(StatusText)",
            runningClientsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(WarningText)",
            runningClientsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "accessibleAnnouncement: baseStatus",
            runningClientsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "accessibleAnnouncement: completedActionStatus",
            runningClientsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            soundSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(TitleText)",
            tourSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            templateEditorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(TemplateSummaryText)",
            templateRunSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            templateRunSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(StatusText)",
            macroRecorderSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AccessibilityLiveRegion(ValidationText)",
            automationSettingsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetValidation(Localize(\"Sound.ImportCancelled\"))",
            soundSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshLaunchAvailability(announceValidation: false)",
            localizationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetReadyState(announceStatus: false)",
            localizationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetSignedOutState(announceStatus: false)",
            localizationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"StatusTitle\"",
            mainXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            mainXaml,
            StringComparison.Ordinal);
    }

    private static string ReadProductionFile(string fileName)
    {
        return File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            fileName));
    }

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
