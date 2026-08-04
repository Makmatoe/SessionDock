using System.Diagnostics;
using System.Text.Json;
using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelProvenanceTests
{
    private const string SourceCommit =
        "14fef76e6639bf291c87a83db7043b91e1c3daa8";

    private const string CanonicalInventorySha256 =
        "2368564b533bc5b762bceefb5b27ed4273ebdf7deee6d63a2fe7108d2659405e";

    private const string ProjectGitBlob =
        "07fe8f9ec14088750f6d2a0d835c86b678a0f76e";

    private const string ProjectSha256 =
        "76e3be05eea91e5526965d05da043219da67afdc52a423b07707b63fdfaa1841";

    private const string ManifestSha256 =
        "de707b6b4b234a08c42c73282d1941acbe724b4c89f10b0e3000359e907fa19c";

    [Fact]
    public void EmbeddedProvenance_PinsRepositoryNativeMitSourceIdentity()
    {
        using var stream = typeof(ExactWheelSession).Assembly
            .GetManifestResourceStream("SessionDock.ExactWheel.Provenance.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Assert.Equal(19, root.EnumerateObject().Count());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ExactWheel", root.GetProperty("component").GetString());
        Assert.Equal("1.1.0", root.GetProperty("componentVersion").GetString());
        Assert.Equal(1, root.GetProperty("macroFormatVersion").GetInt32());
        Assert.Equal("immutable-git", root.GetProperty("sourceState").GetString());
        Assert.Equal(
            "SessionDock.ExactWheel",
            root.GetProperty("sourcePathHint").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceTag").ValueKind);
        Assert.Equal(SourceCommit, root.GetProperty("sourceCommit").GetString());
        Assert.Equal(14, root.GetProperty("sourceFileCount").GetInt32());
        Assert.Equal(172_994, root.GetProperty("sourceBytes").GetInt32());
        Assert.Equal(
            CanonicalInventorySha256,
            root.GetProperty("canonicalManifestSha256").GetString());
        Assert.Equal(
            "SessionDock.ExactWheel/SessionDock.ExactWheel.csproj",
            root.GetProperty("buildDefinitionPath").GetString());
        Assert.Equal(1_311, root.GetProperty("buildDefinitionBytes").GetInt32());
        Assert.Equal(
            ProjectGitBlob,
            root.GetProperty("buildDefinitionGitBlob").GetString());
        Assert.Equal(
            ProjectSha256,
            root.GetProperty("buildDefinitionSha256").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        Assert.False(root.GetProperty("releaseBlockedPendingLicense").GetBoolean());
        Assert.Equal(
            "managed-compatible-port",
            root.GetProperty("integrationKind").GetString());
    }

    [Fact]
    public void TagRelease_GatesProvenanceBeforeBuildAndStagesImmutableInput()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "release.yml"));
        var gateIndex = workflow.IndexOf(
            "- name: Enforce ExactWheel release provenance",
            StringComparison.Ordinal);
        var buildIndex = workflow.IndexOf(
            "- name: Build, test, and publish the application",
            StringComparison.Ordinal);

        Assert.True(gateIndex >= 0);
        Assert.True(buildIndex > gateIndex);
        Assert.Contains(
            "./scripts/Verify-ExactWheelReleaseProvenance.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-StagedManifestOnly",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item SessionDock.ExactWheel/exactwheel-provenance.json " +
            "artifacts/release-input/exactwheel-provenance.json",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item scripts/Verify-ExactWheelReleaseProvenance.ps1 " +
            "artifacts/release-input/scripts/Verify-ExactWheelReleaseProvenance.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-BundledExactWheelManifest " +
            "./release-input/exactwheel-provenance.json",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseGate_PinsSourceBuildDefinitionAndLicenseCryptographically()
    {
        var root = FindRepositoryRoot();
        var gate = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-ExactWheelReleaseProvenance.ps1"));
        var sbom = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "New-ReleaseSbom.ps1"));
        var assetVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Assets.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "scripts", "Build.ps1"));
        var ci = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "ci.yml"));
        var ciBuildJobStart = ci.IndexOf(
            "  build-and-test:",
            StringComparison.Ordinal);
        var ciDependencyJobStart = ci.IndexOf(
            "  dependency-review:",
            StringComparison.Ordinal);
        Assert.True(ciBuildJobStart >= 0);
        Assert.True(ciDependencyJobStart > ciBuildJobStart);
        var ciBuildJob = ci[ciBuildJobStart..ciDependencyJobStart];

        Assert.Contains("releaseBlockedPendingLicense is true", gate);
        Assert.Contains("license is missing", gate);
        Assert.Contains("sourceState is not immutable-git", gate);
        Assert.Contains("sourceCommit is missing", gate);
        Assert.Contains("[0-9a-f]{40}|[0-9a-f]{64}", gate);
        Assert.Contains(SourceCommit, gate, StringComparison.Ordinal);
        Assert.Contains(CanonicalInventorySha256, gate, StringComparison.Ordinal);
        Assert.Contains(ProjectGitBlob, gate, StringComparison.Ordinal);
        Assert.Contains(ProjectSha256, gate, StringComparison.Ordinal);
        Assert.Contains(ManifestSha256, gate, StringComparison.Ordinal);
        Assert.Contains("exact reviewed bytes", gate, StringComparison.Ordinal);
        Assert.Contains(
            "5944250b546861e4e616de520b7d06513fec435a5651fc49d83ae92d3cf14bf2",
            gate,
            StringComparison.Ordinal);
        Assert.Contains("Get-GitBlobBytes", gate, StringComparison.Ordinal);
        Assert.Contains("Get-FileSha256Hex", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", gate, StringComparison.Ordinal);
        Assert.Contains("hash-object", gate, StringComparison.Ordinal);
        Assert.Contains("requires complete Git history", gate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("is-shallow-repository", gate,
            StringComparison.Ordinal);
        Assert.Contains("sourceTag must be null", gate, StringComparison.Ordinal);
        Assert.Contains("SPDXRef-Package-ExactWheel", sbom);
        Assert.Contains("Verify-ExactWheelReleaseProvenance.ps1", sbom);
        Assert.Contains("-StagedManifestOnly", sbom);
        Assert.Contains("relationshipType = 'CONTAINS'", sbom);
        Assert.Contains("Repository-native tagless ExactWheel source", sbom);
        Assert.Contains("buildDefinitionGitBlob", sbom);
        Assert.Contains(
            "https://github.com/Makmatoe/SessionDock/archive/" +
            "$($exactWheelManifest.sourceCommit).tar.gz",
            sbom,
            StringComparison.Ordinal);
        Assert.Contains("copyrightText = 'Copyright (c) 2026 Makmatoe'", sbom,
            StringComparison.Ordinal);
        Assert.Contains("supplier = 'Person: Makmatoe'", sbom,
            StringComparison.Ordinal);
        Assert.Contains(SourceCommit, assetVerifier, StringComparison.Ordinal);
        Assert.Contains(CanonicalInventorySha256, assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains(ProjectGitBlob, assetVerifier, StringComparison.Ordinal);
        Assert.Contains(ProjectSha256, assetVerifier, StringComparison.Ordinal);
        Assert.Contains("$exactWheelDownloadLocation", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Person: Makmatoe", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("SPDXRef-Package-ExactWheel", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Release SBOM must model bundled ExactWheel", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("ExactWheel runtime relationship", assetVerifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain("source tag $($exactWheelManifest.sourceTag)", sbom);
        Assert.DoesNotContain("Verify-ExactWheelReleaseProvenance.ps1", build);
        Assert.DoesNotContain("Verify-ExactWheelReleaseProvenance.ps1", ci);
        Assert.Contains("fetch-depth: 0", ciBuildJob, StringComparison.Ordinal);
    }

    [Fact]
    public void StagedManifestGate_RejectsDuplicateKeysAndNumericStrings()
    {
        var root = FindRepositoryRoot();
        var original = File.ReadAllText(Path.Combine(
            root,
            "SessionDock.ExactWheel",
            "exactwheel-provenance.json"));
        var mutations = new[]
        {
            original.Replace(
                "\"component\": \"ExactWheel\",",
                "\"component\": \"ExactWheel\",\n  " +
                "\"component\": \"ExactWheel\",",
                StringComparison.Ordinal),
            original.Replace(
                "\"sourceFileCount\": 14,",
                "\"sourceFileCount\": \"14\",",
                StringComparison.Ordinal)
        };

        foreach (var mutation in mutations)
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"sessiondock-exactwheel-rejected-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var scriptPath = Path.Combine(
                    temporaryDirectory,
                    "Verify-ExactWheelReleaseProvenance.ps1");
                var manifestPath = Path.Combine(
                    temporaryDirectory,
                    "exactwheel-provenance.json");
                File.Copy(
                    Path.Combine(
                        root,
                        "scripts",
                        "Verify-ExactWheelReleaseProvenance.ps1"),
                    scriptPath);
                File.WriteAllText(manifestPath, mutation);

                var startInfo = CreateStagedVerifierStartInfo(
                    temporaryDirectory,
                    scriptPath,
                    manifestPath);
                using var process = Process.Start(startInfo);
                Assert.NotNull(process);
                var standardOutput = process.StandardOutput.ReadToEnd();
                var standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains(
                    "exact reviewed bytes",
                    standardOutput + standardError,
                    StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void StagedManifestGate_WorksWithoutARepositoryCheckout()
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sessiondock-exactwheel-staged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var scriptPath = Path.Combine(
                temporaryDirectory,
                "Verify-ExactWheelReleaseProvenance.ps1");
            var manifestPath = Path.Combine(
                temporaryDirectory,
                "exactwheel-provenance.json");
            File.Copy(
                Path.Combine(root, "scripts", "Verify-ExactWheelReleaseProvenance.ps1"),
                scriptPath);
            File.Copy(
                Path.Combine(
                    root,
                    "SessionDock.ExactWheel",
                    "exactwheel-provenance.json"),
                manifestPath);

            var startInfo = CreateStagedVerifierStartInfo(
                temporaryDirectory,
                scriptPath,
                manifestPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Staged provenance gate failed.{Environment.NewLine}" +
                $"{standardOutput}{Environment.NewLine}{standardError}");
            Assert.Contains(
                "Verified staged ExactWheel 1.1.0 manifest",
                standardOutput,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateStagedVerifierStartInfo(
        string workingDirectory,
        string scriptPath,
        string manifestPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ManifestPath");
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.ArgumentList.Add("-StagedManifestOnly");
        return startInfo;
    }

    [Fact]
    public void ReleaseGate_VerifiesTheCheckedOutRepositoryIdentity()
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(
            root,
            "scripts",
            "Verify-ExactWheelReleaseProvenance.ps1"));
        startInfo.ArgumentList.Add("-ManifestPath");
        startInfo.ArgumentList.Add(Path.Combine(
            root,
            "SessionDock.ExactWheel",
            "exactwheel-provenance.json"));

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"Provenance gate failed.{Environment.NewLine}{standardOutput}" +
            $"{Environment.NewLine}{standardError}");
        Assert.Contains(
            "Verified release-ready repository-native ExactWheel 1.1.0",
            standardOutput,
            StringComparison.Ordinal);
    }

    private static string ResolvePowerShellExecutable()
    {
        var powerShell7 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        return File.Exists(powerShell7) ? powerShell7 : "powershell.exe";
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
}
