using System.Runtime.InteropServices;
using System.Text;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SupportDiagnosticsTests
{
    private const string SensitiveUser = "PrivateUserCanary";
    private const string SensitiveMachine = "PrivateMachineCanary";
    private const string SensitiveToken = "token-canary-9843";
    private const string SensitiveAccount = "AccountLabelCanary";
    private const string SensitiveDestination = "privateServerLinkCode=CanaryCode";

    [Fact]
    public void BuildDocument_IsDeterministicAndContainsOnlyApprovedFacts()
    {
        var snapshot = CreateSnapshot();

        var first = SupportDiagnosticsService.BuildDocument(snapshot);
        var second = SupportDiagnosticsService.BuildDocument(snapshot);

        Assert.Equal(first.Text, second.Text);
        Assert.DoesNotContain('\r', first.Text);
        Assert.Contains("- Version: 2.6.2", first.Text);
        Assert.Contains("- Windows version: 10.0.26100.0", first.Text);
        Assert.Contains("- .NET runtime: 10.0.10", first.Text);
        Assert.Contains("- Microsoft Edge WebView2 Runtime: Available (version 140.0.3485.54)", first.Text);
        Assert.Contains("- Roblox Player: Available and Windows-verified", first.Text);
        Assert.Contains("- Saved accounts: 3", first.Text);
        Assert.Contains("- Recent entries: 7", first.Text);
        Assert.Contains("- Favorites: 2", first.Text);
        Assert.Contains("- Roblox clients tracked this run: 1", first.Text);
        Assert.Contains("- Theme: Light", first.Text);
        Assert.Contains("- Interface sounds: Off", first.Text);
        Assert.True(first.Text.Length <= SupportDiagnosticsService.MaximumReportLength);
    }

    [Fact]
    public void Capture_DiscardsSensitiveProbePathsAndExceptionDetails()
    {
        var sensitivePath = Path.Combine(
            "C:" + Path.DirectorySeparatorChar,
            "Users",
            SensitiveUser,
            "AppData",
            "Local",
            "Roblox",
            SensitiveToken,
            "RobloxPlayerBeta.exe");
        var context = new SupportDiagnosticsContext(
            new Version(2, 6, 2),
            CanSelfUpdate: true,
            AccountCount: 1,
            RecentCount: 1,
            FavoriteCount: 1,
            TrackedRunningClientCount: 1,
            DiagnosticTheme.Dark,
            UiSoundsEnabled: true);

        var pathSnapshot = SupportDiagnosticsService.Capture(
            context,
            () => sensitivePath,
            () => $"140.0.0.0 {SensitiveMachine}");
        var pathDocument = SupportDiagnosticsService.BuildDocument(pathSnapshot);
        var exceptionSnapshot = SupportDiagnosticsService.Capture(
            context,
            () => throw new IOException(
                $"Could not inspect {sensitivePath}: {SensitiveToken}; {SensitiveAccount}; {SensitiveDestination}"),
            () => throw new InvalidOperationException(
                $"Stack at {sensitivePath}: {SensitiveToken}"));
        var exceptionDocument = SupportDiagnosticsService.BuildDocument(
            exceptionSnapshot);

        foreach (var report in new[]
                 {
                     pathDocument.Text,
                     exceptionDocument.Text
                 })
        {
            Assert.DoesNotContain(SensitiveUser, report);
            Assert.DoesNotContain(SensitiveMachine, report);
            Assert.DoesNotContain(SensitiveToken, report);
            Assert.DoesNotContain(SensitiveAccount, report);
            Assert.DoesNotContain(SensitiveDestination, report);
            Assert.DoesNotContain(sensitivePath, report);
            Assert.DoesNotContain("C:\\Users", report);
        }
        Assert.Contains(
            "Roblox Player: Available and Windows-verified",
            pathDocument.Text);
        Assert.Contains(
            "Microsoft Edge WebView2 Runtime: Could not be inspected",
            pathDocument.Text);
        Assert.Contains(
            "Roblox Player: Could not be inspected",
            exceptionDocument.Text);
    }

    [Fact]
    public void SnapshotSchema_HasNoFreeFormStringFields()
    {
        Assert.DoesNotContain(
            typeof(SupportDiagnosticsContext).GetProperties(),
            property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(
            typeof(SupportDiagnosticsSnapshot).GetProperties(),
            property => property.PropertyType == typeof(string));
    }

    [Fact]
    public void BuildDocument_BoundsEveryCount()
    {
        var snapshot = CreateSnapshot() with
        {
            AccountCount = -50,
            RecentCount = int.MaxValue,
            FavoriteCount = SupportDiagnosticsService.MaximumDisplayedCount,
            TrackedRunningClientCount = int.MaxValue
        };

        var document = SupportDiagnosticsService.BuildDocument(snapshot);

        Assert.DoesNotContain(SensitiveUser, document.Text);
        Assert.DoesNotContain(SensitiveToken, document.Text);
        Assert.DoesNotContain("C:\\Users", document.Text);
        Assert.Contains("- Saved accounts: 0", document.Text);
        Assert.Contains("- Recent entries: 10,000 or more", document.Text);
        Assert.Contains("- Favorites: 10,000 or more", document.Text);
        Assert.Contains(
            "- Roblox clients tracked this run: 10,000 or more",
            document.Text);
        Assert.True(document.Text.Length <= SupportDiagnosticsService.MaximumReportLength);
    }

    [Fact]
    public async Task ExportAsync_WritesExactPreviewWithoutBom()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-Diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var document = SupportDiagnosticsService.BuildDocument(
                CreateSnapshot());
            var destination = Path.Combine(
                directory,
                SupportDiagnosticsExporter.SuggestedFileName);

            await SupportDiagnosticsExporter.ExportAsync(
                destination,
                document,
                TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(
                destination,
                TestContext.Current.CancellationToken);
            Assert.Equal(document.Text, Encoding.UTF8.GetString(bytes));
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_UsesSafeNameAndRejectsNonTextDestination()
    {
        Assert.Matches(
            "^[A-Za-z0-9.-]+$",
            SupportDiagnosticsExporter.SuggestedFileName);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar,
            SupportDiagnosticsExporter.SuggestedFileName);
        Assert.DoesNotContain(
            Path.AltDirectorySeparatorChar,
            SupportDiagnosticsExporter.SuggestedFileName);

        var document = SupportDiagnosticsService.BuildDocument(
            CreateSnapshot());
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-Diagnostics-{Guid.NewGuid():N}.zip");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SupportDiagnosticsExporter.ExportAsync(
                destination,
                document,
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void UiContract_ProvidesDiscoverableAccessibleExactPreviewActions()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        var dialog = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "AboutDiagnosticsDialog.xaml"));
        var dialogCode = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "AboutDiagnosticsDialog.xaml.cs"));

        Assert.Contains("x:Name=\"AboutDiagnosticsButton\"", mainWindow);
        Assert.Contains(
            "AutomationProperties.Name=\"About SessionDock and diagnostics\"",
            mainWindow);
        Assert.Contains(
            "AutomationProperties.Name=\"Privacy-safe diagnostics preview\"",
            dialog);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", dialog);
        Assert.Contains("Copy diagnostics", dialog);
        Assert.Contains("Export text file", dialog);
        Assert.Contains("Clipboard.SetText(_document.Text)", dialogCode);
        Assert.Contains("_document);", dialogCode);
        Assert.DoesNotContain("settings.json", dialogCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.ReadAll", dialogCode, StringComparison.Ordinal);
    }

    private static SupportDiagnosticsSnapshot CreateSnapshot() =>
        new(
            new Version(2, 6, 2),
            new Version(10, 0, 26100, 0),
            new Version(10, 0, 10),
            Architecture.X64,
            Architecture.X64,
            DiagnosticDependencyState.Available,
            new Version(140, 0, 3485, 54),
            DiagnosticDependencyState.Available,
            CanSelfUpdate: true,
            AccountCount: 3,
            RecentCount: 7,
            FavoriteCount: 2,
            TrackedRunningClientCount: 1,
            DiagnosticTheme.Light,
            UiSoundsEnabled: false);

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
