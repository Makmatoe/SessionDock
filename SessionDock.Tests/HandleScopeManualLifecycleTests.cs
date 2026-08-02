using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeManualLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.ManualLifecycle.{Guid.NewGuid():N}");

    [Fact]
    public void StandaloneRuntimeVerifier_StillRequiresExactIdentity()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "HandleScope.Api.exe");
        var expected = Encoding.UTF8.GetBytes("synthetic reviewed runtime");
        File.WriteAllBytes(path, expected);
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            expected.LongLength,
            SHA256.HashData(expected));

        Assert.True(verifier.IsAuthorized(path));
        expected[0] ^= 0x01;
        File.WriteAllBytes(path, expected);
        Assert.False(verifier.IsAuthorized(path));
        Assert.False(verifier.IsAuthorized(Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void IntegrationPanel_UsesBundledLifecycleInsteadOfInstallerWorkflow()
    {
        var xaml = ReadProductionFile("HandleScopeIntegrationDialog.xaml");
        Assert.Contains("IntegrationEnabledCheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("RuntimeSourceComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("StandaloneRuntimeVersionComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewedVersionsButton", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedOptionsExpander", xaml, StringComparison.Ordinal);
        Assert.Contains("ApiContractComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("RestartButton", xaml, StringComparison.Ordinal);
        Assert.Contains("RetryButton", xaml, StringComparison.Ordinal);
        Assert.Contains("RepairButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallHandleScopeButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenHandleScopeSetupButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckVersionsButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TestConnectionButton", xaml, StringComparison.Ordinal);

        var codeBehind = ReadProductionFile(
            "HandleScopeIntegrationDialog.xaml.cs");
        Assert.Contains("_coordinator.EnableAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_coordinator.DisableAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_coordinator.RestartAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetRuntimeSourceAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetRuntimeVersionAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewedVersionsAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewedVersionsButton.Visibility", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("AdvancedOptionsExpander.IsExpanded = true", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("SetApiContractAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleScopeReleaseInstaller", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionPolicy", codeBehind, StringComparison.Ordinal);

        var coordinator = ReadProductionFile(Path.Combine(
            "SystemProcesses",
            "HandleScopeRuntimeCoordinator.cs"));
        Assert.Contains("_catalogService.RefreshAsync", coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HandleScopeReleaseInstaller", coordinator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionBoundary_EmbedsParentOwnedSameExecutableWorker()
    {
        var repositoryRoot = FindRepositoryRoot();
        var program = ReadProductionFile("Program.cs");
        var runtime = ReadProductionFile(Path.Combine(
            "SystemProcesses",
            "BundledHandleScopeRuntime.cs"));
        var worker = ReadProductionFile(Path.Combine(
            "SystemProcesses",
            "HandleScopeWorkerCommand.cs"));
        var parentVerifier = ReadProductionFile(Path.Combine(
            "SystemProcesses",
            "WindowsProcessParentVerifier.cs"));
        var job = ReadProductionFile(Path.Combine(
            "SystemProcesses",
            "HandleScopeWorkerJob.cs"));
        var broker = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "SessionDock.HandleScope",
            "HandleScopeBroker.cs"));

        Assert.Contains(
            "HandleScopeWorkerCommand.IsInvocation",
            program,
            StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessPath", runtime, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", runtime, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = true", runtime, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = true", runtime, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardError = false", runtime, StringComparison.Ordinal);
        Assert.Contains("MaximumStartupAttempts = 2", runtime, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(StartupRetryDelay, cancellationToken)", runtime,
            StringComparison.Ordinal);
        Assert.Contains("stopDeadline.CancelAfter(ShutdownTimeout)", runtime, StringComparison.Ordinal);
        Assert.Contains("job.Assign(process)", runtime, StringComparison.Ordinal);
        Assert.Contains("JobObjectLimitKillOnJobClose", job, StringComparison.Ordinal);
        Assert.Contains("RuntimeSecurityPolicy", worker, StringComparison.Ordinal);
        Assert.Contains("StartSignalTimeout", worker, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsProcessParentVerifier.IsCurrentProcessCreatedBy",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("MaximumSnapshotEntries", parentVerifier, StringComparison.Ordinal);
        Assert.Contains("IPAddress.Loopback", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionFile", broker, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(
            repositoryRoot,
            "scripts",
            "Enable-HandleScope.ps1")));
        Assert.False(File.Exists(Path.Combine(
            repositoryRoot,
            "SessionDock",
            "SystemProcesses",
            "HandleScopeReleaseInstaller.cs")));
        Assert.False(File.Exists(Path.Combine(
            repositoryRoot,
            "SessionDock",
            "SystemProcesses",
            "HandleScopeReleasePolicy.cs")));
    }

    [Fact]
    public void RuntimeSmoke_ExercisesReadyAndUnsupportedFailClosedStates()
    {
        var app = ReadProductionFile("App.xaml.cs");

        Assert.Contains(
            "RuntimeSecurityPolicy.IsCurrentProcessSupported",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtimeSecurityContextSupported",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "? HandleScopeRuntimeState.Ready",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            ": HandleScopeRuntimeState.NeedsAttention",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "The bundled HandleScope worker did not fail closed",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AllLocales_ExposeBundledChoicesAndKeepKeyParity()
    {
        var localizationRoot = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var files = Directory.GetFiles(localizationRoot, "Strings.*.xaml");
        Assert.Equal(5, files.Length);

        string[]? expectedKeys = null;
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var keys = document.Root!
                .Elements()
                .Select(element => (string?)element.Attribute(x + "Key"))
                .Where(key => key is not null)
                .Select(key => key!)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            expectedKeys ??= keys;
            Assert.Equal(expectedKeys, keys);

            Assert.Contains("Handle.EnableSwitch", keys);
            Assert.Contains("Handle.RuntimeSourceBundled", keys);
            Assert.Contains("Handle.RuntimeSourceStandalone", keys);
            Assert.Contains("Handle.RuntimeVersionAutomatic", keys);
            Assert.Contains("Handle.RuntimeVersionKeepInstalled", keys);
            Assert.Contains("Handle.RuntimeVersionExact", keys);
            Assert.Contains("Handle.RuntimeVersionExactUnavailable", keys);
            Assert.Contains("Handle.RefreshVersions", keys);
            Assert.Contains("Handle.RefreshVersionsName", keys);
            Assert.Contains("Handle.ActionVersionsRefreshed", keys);
            Assert.Contains("Handle.ApiVersionAutomatic", keys);
            Assert.Contains("Handle.ApiVersionV2", keys);
            Assert.Contains("Handle.ApiVersionV1", keys);
            Assert.Contains("Handle.StateReadyBundledDescription", keys);
            Assert.Contains("Handle.StateStandaloneUnavailableTitle", keys);
            Assert.DoesNotContain("Handle.Install", keys);
            Assert.DoesNotContain("Handle.SetupGuide", keys);
            Assert.DoesNotContain("Handle.CheckVersions", keys);
        }
    }

    [Fact]
    public void CurrentDocumentation_ExplainsBundledAndStandaloneBoundaries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var paths = new[]
        {
            "README.md",
            "SECURITY.md",
            "THIRD_PARTY_NOTICES.md",
            Path.Combine("SessionDock", "README.md"),
            Path.Combine("SessionDock", "SystemProcesses", "README.md"),
            Path.Combine("docs", "PRIVACY.md"),
            Path.Combine("docs", "RELEASING.md")
        };
        var documentation = string.Join(
            "\n",
            paths.Where(path => File.Exists(Path.Combine(repositoryRoot, path)))
                .Select(path => File.ReadAllText(Path.Combine(
                    repositoryRoot,
                    path))));

        Assert.Contains("included", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standalone", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same SessionDock.exe", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.3.0", documentation, StringComparison.Ordinal);
        Assert.Contains("loopback", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inherited pipe", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provenance", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatibility catalog", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Refresh reviewed versions", documentation,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string ReadProductionFile(string relativePath) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            relativePath));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
