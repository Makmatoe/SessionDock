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
    public void SupportedRuntimeIdentity_MatchesPublishedImmutableV014Asset()
    {
        Assert.Equal("0.1.4", HandleScopeInstalledRuntimeVerifier.SupportedVersion);
        Assert.Equal(
            50_275_061,
            HandleScopeInstalledRuntimeVerifier.ExpectedExecutableSize);
        Assert.Equal(
            "9925d032819750809d66f5e6f267606cb1d6ff419acadffc15d7bdbcb1402e95",
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
    public void IntegrationPanel_OffersCatalogSelectionAndVersionedOfficialGuide()
    {
        var startInfo = HandleScopeIntegrationDialog.CreateOfficialSetupStartInfo();

        Assert.Equal(
            "https://github.com/Makmatoe/HandleScope/blob/v0.3.0/docs/INSTALL.md",
            startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.Equal(
            "https://github.com/Makmatoe/HandleScope/blob/v0.2.0/docs/INSTALL.md",
            HandleScopeIntegrationDialog.CreateOfficialSetupUrl("0.2.0"));

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
        Assert.Contains("RuntimeVersionComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("ApiVersionComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckVersionsButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallLatestHandleScopeButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StartApiButton", xaml, StringComparison.Ordinal);

        var codeBehind = ReadProductionFile(
            "HandleScopeIntegrationDialog.xaml.cs");
        Assert.Contains(".InstallAsync(", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "Handle.VersionSummaryNoCompatible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot.CompatibleReleases.FirstOrDefault",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReloadVersionSnapshotAfterInstall();",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "_integrationService.InspectAsync(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("InstallPinnedAsync", codeBehind, StringComparison.Ordinal);
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
    public void ProductionBoundary_HasCatalogManagedInstallWithoutElevationOrOptIn()
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
        Assert.Contains("HandleScopeCatalogInstallPolicy", source, StringComparison.Ordinal);
        Assert.Contains("HandleScopeCompatibilityCatalogPolicy", source, StringComparison.Ordinal);
        Assert.Contains("Install-HandleScopeApi.ps1", source, StringComparison.Ordinal);
        Assert.Contains("HandleScope.Setup.exe", source, StringComparison.Ordinal);
        Assert.Contains("handlescope.setup.native.v1", source, StringComparison.Ordinal);
        Assert.Contains("--start-now", source, StringComparison.Ordinal);
        Assert.Contains("--enable-autostart", source, StringComparison.Ordinal);
        Assert.Contains("-StartNow", source, StringComparison.Ordinal);
        Assert.Contains("-EnableAutostart", source, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy", source, StringComparison.Ordinal);
        Assert.Contains("RemoteSigned", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Bypass", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrestricted", source, StringComparison.Ordinal);
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
    public void AllLocales_ExposeVersionSelectionAndKeepKeyParity()
    {
        var localizationRoot = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var files = Directory.GetFiles(localizationRoot, "Strings.*.xaml");
        Assert.Equal(5, files.Length);

        string[]? expectedKeys = null;
        var replacementDisclosureTerms = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["de-DE"] = "ersetzen",
            ["en-US"] = "replace",
            ["es-ES"] = "reemplazar",
            ["fr-FR"] = "remplacer",
            ["nl-NL"] = "vervangen"
        };
        var sessionDockUpdateTerms = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["de-DE"] = "Aktualisieren",
            ["en-US"] = "Update",
            ["es-ES"] = "Actualiza",
            ["fr-FR"] = "jour",
            ["nl-NL"] = "Werk SessionDock bij"
        };
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
            Assert.Contains("Handle.VersionAutomatic", keys);
            Assert.Contains("Handle.VersionAutomaticUnavailable", keys);
            Assert.Contains("Handle.VersionSummaryNoCompatible", keys);
            Assert.Contains("Handle.VersionKeepInstalled", keys);
            Assert.Contains("Handle.VersionExact", keys);
            Assert.Contains("Handle.ApiVersionV1", keys);
            Assert.Contains("Handle.ApiVersionV2", keys);
            Assert.Contains("Handle.CheckVersions", keys);
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
            Assert.Contains("{0}", strings["Handle.InstallVersion"], StringComparison.Ordinal);
            Assert.Contains("{0}", strings["Handle.InstallConfirm"], StringComparison.Ordinal);
            Assert.Contains("{0}", strings["Handle.VersionAutomatic"], StringComparison.Ordinal);
            var culture = Path.GetFileNameWithoutExtension(file)["Strings.".Length..];
            Assert.Contains(
                sessionDockUpdateTerms[culture],
                strings["Handle.VersionSummaryNoCompatible"],
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                replacementDisclosureTerms[culture],
                strings["Handle.InstallConfirm"],
                StringComparison.OrdinalIgnoreCase);
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
    public void CurrentDocumentation_StatesSignedCatalogManagedInstallBoundary()
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

        Assert.Contains("signed", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatibility catalog", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("handlescope-preferences.json", documentation, StringComparison.Ordinal);
        Assert.Contains("/v1/metadata", documentation, StringComparison.Ordinal);
        Assert.Contains("v2", documentation, StringComparison.Ordinal);
        Assert.Contains("Check versions", documentation, StringComparison.Ordinal);
        Assert.Contains("standard-user", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "handlescope.setup.native.v1",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "api/HandleScope.Setup.exe",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "release-manifest schema v2",
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "API running - integration disabled",
            documentation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "%LOCALAPPDATA%\\SessionDock\\handlescope.json",
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
            Assert.Contains("0.2.2", notes, StringComparison.Ordinal);
            Assert.Contains("0.3.0", notes, StringComparison.Ordinal);
            Assert.Contains("catalog", notes, StringComparison.OrdinalIgnoreCase);
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
