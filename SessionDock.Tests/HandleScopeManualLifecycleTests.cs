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
    public void SupportedRuntimeIdentity_MatchesPublishedImmutableV013Asset()
    {
        Assert.Equal("0.1.3", HandleScopeInstalledRuntimeVerifier.SupportedVersion);
        Assert.Equal(
            50_275_056,
            HandleScopeInstalledRuntimeVerifier.ExpectedExecutableSize);
        Assert.Equal(
            "ca273df4b3822e358658c43fd764c70661f9279b37d883d11a470cd363ad7852",
            HandleScopeInstalledRuntimeVerifier.ExpectedExecutableSha256);
        Assert.Equal(
            32,
            Convert.FromHexString(
                HandleScopeInstalledRuntimeVerifier.ExpectedExecutableSha256).Length);
    }

    [Fact]
    public void InstalledRuntimeVerifier_RequiresExactSizeAndSha256()
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

        File.WriteAllBytes(path, [.. expected, 0x00]);
        Assert.False(verifier.IsAuthorized(path));
        Assert.False(verifier.IsAuthorized(Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void IntegrationPanel_OffersPinnedInstallAndOfficialGuide()
    {
        var startInfo = HandleScopeIntegrationDialog.CreateOfficialSetupStartInfo();

        Assert.Equal(
            "https://github.com/Makmatoe/HandleScope/blob/v0.1.3/docs/INSTALL.md",
            startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));

        var xaml = ReadProductionFile("HandleScopeIntegrationDialog.xaml");
        Assert.Contains("InstallHandleScopeButton", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Handle.Install}", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenHandleScopeSetupButton", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Handle.SetupGuide}", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshButton", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableButton", xaml, StringComparison.Ordinal);
        Assert.Contains("DisableButton", xaml, StringComparison.Ordinal);
        Assert.Contains("TestConnectionButton", xaml, StringComparison.Ordinal);
        Assert.Contains("RepairButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallLatestHandleScopeButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StartApiButton", xaml, StringComparison.Ordinal);

        var codeBehind = ReadProductionFile(
            "HandleScopeIntegrationDialog.xaml.cs");
        Assert.Contains("InstallPinnedAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAsync(", codeBehind, StringComparison.Ordinal);

        var testAvailabilityStart = codeBehind.IndexOf(
            "TestConnectionButton.IsEnabled",
            StringComparison.Ordinal);
        Assert.True(testAvailabilityStart >= 0);
        var testAvailabilityEnd = codeBehind.IndexOf(
            ';',
            testAvailabilityStart);
        Assert.True(testAvailabilityEnd > testAvailabilityStart);
        var testAvailability = codeBehind[
            testAvailabilityStart..testAvailabilityEnd];
        Assert.Contains(
            "HandleScopeIntegrationState.InstalledStopped",
            testAvailability,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HandleScopeIntegrationState.RunningDisabled",
            testAvailability,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionBoundary_HasPinnedManagedInstallWithoutElevationOrOptIn()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemProcessesRoot = Path.Combine(
            repositoryRoot,
            "SessionDock",
            "SystemProcesses");
        var productionSources = Directory
            .EnumerateFiles(systemProcessesRoot, "HandleScope*.cs")
            .Select(File.ReadAllText)
            .Append(ReadProductionFile("HandleScopeIntegrationDialog.xaml.cs"))
            .ToArray();
        var source = string.Join('\n', productionSources);

        Assert.Contains("HandleScopeReleaseInstaller", source, StringComparison.Ordinal);
        Assert.Contains("HandleScopeReleasePolicy", source, StringComparison.Ordinal);
        Assert.Contains("Install-HandleScopeApi.ps1", source, StringComparison.Ordinal);
        Assert.Contains("-StartNow", source, StringComparison.Ordinal);
        Assert.Contains("-EnableAutostart", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-ExecutionPolicy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-EnableSessionDock", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-AllowDowngrade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAsync(", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            systemProcessesRoot,
            "HandleScopeReleaseInstaller.cs")));
        Assert.True(File.Exists(Path.Combine(
            systemProcessesRoot,
            "HandleScopeReleasePolicy.cs")));
        Assert.False(File.Exists(Path.Combine(
            systemProcessesRoot,
            "HandleScopeReleaseAuthorization.cs")));
    }

    [Fact]
    public void AllLocales_ExposePinnedInstallAndKeepKeyParity()
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

            Assert.Contains("Handle.SetupGuide", keys);
            Assert.Contains("Handle.SetupGuideName", keys);
            Assert.Contains("Handle.SetupGuideTooltip", keys);
            Assert.Contains("Handle.SetupGuideOpened", keys);
            Assert.Contains("Handle.SetupGuideFailed", keys);
            Assert.Contains("Handle.Install", keys);
            Assert.Contains("Handle.InstallName", keys);
            Assert.Contains("Handle.InstallTooltip", keys);
            Assert.Contains("Handle.InstallConfirm", keys);
            Assert.Contains("Handle.InstallSucceeded", keys);
            Assert.Contains("Handle.ProgressDownloading", keys);
            Assert.DoesNotContain("Handle.Start", keys);
            Assert.DoesNotContain("Handle.ActionStartChecked", keys);
            Assert.DoesNotContain("Handle.StateStartingTitle", keys);
            Assert.DoesNotContain("Handle.StateUntestedTitle", keys);

            var strings = document.Root!
                .Elements()
                .ToDictionary(
                    element => (string)element.Attribute(x + "Key")!,
                    element => element.Value,
                    StringComparer.Ordinal);
            Assert.Contains(
                HandleScopeReleaseInstaller.PinnedVersion,
                strings["Handle.Install"],
                StringComparison.Ordinal);
            Assert.Contains(
                HandleScopeReleaseInstaller.PinnedVersion,
                strings["Handle.InstallConfirm"],
                StringComparison.Ordinal);
            var disabledPresentation = string.Concat(
                strings["Handle.StateDisabledTitle"],
                " ",
                strings["Handle.StateDisabledDescription"]);
            foreach (var unsupportedClaim in new[]
                     {
                         "API running",
                         "API läuft",
                         "API en ejecución",
                         "API en cours d’exécution",
                         "API actief",
                         "answered",
                         "geantwortet",
                         "respondió",
                         "répondu",
                         "geantwoord"
                     })
            {
                Assert.DoesNotContain(
                    unsupportedClaim,
                    disabledPresentation,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CurrentDocumentation_StatesPinnedManagedInstallBoundary()
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
            paths.Select(path => File.ReadAllText(Path.Combine(
                repositoryRoot,
                path))));

        Assert.Contains("v0.1.3", documentation, StringComparison.Ordinal);
        Assert.Contains(
            HandleScopeInstalledRuntimeVerifier.ExpectedExecutableSha256,
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            HandleScopeIntegrationDialog.OfficialSetupUrl,
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install HandleScope v0.1.3",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            HandleScopeReleaseInstaller.PinnedPackageSha256,
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            HandleScopeReleaseInstaller.PinnedChecksumsSha256,
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100,839,933", documentation, StringComparison.Ordinal);
        Assert.Contains("standard-user", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "API running - integration disabled",
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "%LOCALAPPDATA%\\SessionDock\\HandleScopeAuthorization",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not inspect the process, discovery file, or API until",
            documentation,
            StringComparison.Ordinal);

        var releaseNotesRoot = Path.Combine(
            repositoryRoot,
            "SessionDock",
            "ReleaseNotes");
        var currentVersion = typeof(MainWindow).Assembly
            .GetName()
            .Version!
            .ToString(3);
        var currentNotes = Directory.GetFiles(
            releaseNotesRoot,
            $"{currentVersion}.*.md");
        Assert.Equal(5, currentNotes.Length);
        Assert.All(currentNotes, path =>
        {
            var notes = File.ReadAllText(path);
            Assert.Contains("v0.1.3", notes, StringComparison.Ordinal);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string ReadProductionFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
