using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeReleaseInstallerTests
{
    private const string Version = "1.2.3";
    private const string TagName = "v1.2.3";
    private const string PackageName = "HandleScope-1.2.3-win-x64.zip";
    private const string ChecksumsName = "SHA256SUMS.txt";
    private static readonly Uri PackageUri = new(
        $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{PackageName}");
    private static readonly Uri ChecksumsUri = new(
        $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{ChecksumsName}");

    [Fact]
    public void PinnedRelease_MatchesSupportedImmutableV014Assets()
    {
        var release = HandleScopeReleaseInstaller.CreatePinnedRelease();

        Assert.Equal("0.1.4", release.Version);
        Assert.Equal("v0.1.4", release.TagName);
        Assert.Equal(
            HandleScopeInstalledRuntimeVerifier.SupportedVersion,
            release.Version);
        Assert.Equal(
            "HandleScope-0.1.4-win-x64.zip",
            release.Package.Name);
        Assert.Equal(100_841_616, release.Package.Size);
        Assert.Equal(
            "b06bfe850b8334b6be86d9037ea43e7210845420e7473cf7c17d030277c06622",
            HandleScopeReleaseInstaller.PinnedPackageSha256);
        Assert.Equal(
            HandleScopeReleaseInstaller.PinnedPackageSha256,
            Hex(release.Package.Sha256));
        Assert.Equal("SHA256SUMS.txt", release.Checksums.Name);
        Assert.Equal(198, release.Checksums.Size);
        Assert.Equal(
            "860bcd77e7cd83693a87b15a1f464908e6dbe43195b0ed0572684e009b1e6ccf",
            HandleScopeReleaseInstaller.PinnedChecksumsSha256);
        Assert.Equal(
            HandleScopeReleaseInstaller.PinnedChecksumsSha256,
            Hex(release.Checksums.Sha256));
        Assert.StartsWith(
            "https://github.com/Makmatoe/HandleScope/releases/download/v0.1.4/",
            release.Package.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "https://github.com/Makmatoe/HandleScope/releases/download/v0.1.4/",
            release.Checksums.DownloadUri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/latest/",
            release.Package.DownloadUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "/latest/",
            release.Checksums.DownloadUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyChecksumManifest_AcceptsMatchingPackageHash()
    {
        var packageHash = SHA256.HashData("package"u8);
        var release = CreateIdentity(packageHash);
        var manifest = Encoding.UTF8.GetBytes(
            $"{Hex(packageHash)}  {PackageName}\n");

        HandleScopeReleasePolicy.VerifyChecksumManifest(manifest, release);
    }

    [Fact]
    public void VerifyChecksumManifest_RejectsMismatchedPackageHash()
    {
        var release = CreateIdentity(SHA256.HashData("expected"u8));
        var otherHash = SHA256.HashData("different"u8);
        var manifest = Encoding.UTF8.GetBytes(
            $"{Hex(otherHash)}  {PackageName}\n");

        var exception = Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleasePolicy.VerifyChecksumManifest(manifest, release));

        Assert.Contains("does not match", exception.Message);
    }

    [Theory]
    [InlineData(
        "https://github.com/Makmatoe/HandleScope/releases/download/v1.2.3/HandleScope-1.2.3-win-x64.zip",
        true)]
    [InlineData(
        "https://release-assets.githubusercontent.com/github-production-release-asset/file?sig=abc",
        true)]
    [InlineData("https://objects.githubusercontent.com/release/file?sig=abc", true)]
    [InlineData("https://example.com/file", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/file", false)]
    [InlineData("http://release-assets.githubusercontent.com/file", false)]
    [InlineData("https://user@release-assets.githubusercontent.com/file", false)]
    [InlineData("https://release-assets.githubusercontent.com:444/file", false)]
    [InlineData("https://release-assets.githubusercontent.com/file#fragment", false)]
    public void IsAllowedAssetUri_OnlyAllowsExactInitialOrGithubObjectHosts(
        string candidate,
        bool expected)
    {
        var actual = HandleScopeReleasePolicy.IsAllowedAssetUri(
            new Uri(candidate),
            PackageUri);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateInstallerStartInfo_UsesOnlyReviewedPerUserArguments(
        bool verifyOnly)
    {
        var installerPath = Path.Combine(
            Path.GetTempPath(),
            "HandleScopeReleaseTests",
            "api",
            "Install-HandleScopeApi.ps1");
        var fullInstallerPath = Path.GetFullPath(installerPath);

        var startInfo = HandleScopeReleaseInstaller.CreateInstallerStartInfo(
            installerPath,
            verifyOnly);

        var expectedArguments = new List<string>
        {
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "RemoteSigned",
            "-File",
            fullInstallerPath
        };
        if (verifyOnly)
        {
            expectedArguments.Add("-VerifyOnly");
        }
        else
        {
            expectedArguments.Add("-StartNow");
            expectedArguments.Add("-EnableAutostart");
        }

        Assert.Equal(expectedArguments, startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(string.IsNullOrEmpty(startInfo.Verb));
        Assert.Equal(Path.GetDirectoryName(fullInstallerPath), startInfo.WorkingDirectory);
        Assert.EndsWith(
            Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
            startInfo.FileName,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(startInfo.ArgumentList, IsForbiddenInstallerArgument);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateInstallerStartInfo_NativeUsesNoShellOrPowerShellPolicy(
        bool verifyOnly)
    {
        var installerPath = Path.Combine(
            Path.GetTempPath(),
            "HandleScopeReleaseTests",
            "api",
            "HandleScope.Setup.exe");
        var fullInstallerPath = Path.GetFullPath(installerPath);

        var startInfo = HandleScopeReleaseInstaller.CreateInstallerStartInfo(
            installerPath,
            HandleScopeSetupAdapter.NativeV1,
            verifyOnly);

        Assert.Equal(fullInstallerPath, startInfo.FileName);
        Assert.Equal(
            verifyOnly
                ? ["verify"]
                : new[] { "install", "--start-now", "--enable-autostart" },
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.Equal(Path.GetDirectoryName(fullInstallerPath), startInfo.WorkingDirectory);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains(
                "ExecutionPolicy",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(startInfo.ArgumentList, IsForbiddenInstallerArgument);
    }

    [Theory]
    [InlineData("Install-HandleScopeApi.ps1", 1)]
    [InlineData("HandleScope.Setup.exe", 0)]
    public void CreateInstallerStartInfo_RejectsPathAdapterMismatch(
        string fileName,
        int adapterValue)
    {
        var path = Path.Combine(Path.GetTempPath(), "api", fileName);

        Assert.Throws<ArgumentException>(() =>
            HandleScopeReleaseInstaller.CreateInstallerStartInfo(
                path,
                (HandleScopeSetupAdapter)adapterValue,
                verifyOnly: true));
    }

    [Fact]
    public async Task CreateInstallerStartInfo_RunsVerifiedScriptFromRestrictedProcessPolicy()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var installerPath = Path.Combine(
                root,
                "api",
                "Install-HandleScopeApi.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            await File.WriteAllTextAsync(
                installerPath,
                "param([switch]$VerifyOnly)\nif (-not $VerifyOnly) { exit 9 }\nexit 0\n",
                TestContext.Current.CancellationToken);
            var startInfo = HandleScopeReleaseInstaller.CreateInstallerStartInfo(
                installerPath,
                verifyOnly: true);
            startInfo.Environment["PSExecutionPolicyPreference"] = "Restricted";

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start());
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunProcessAsync_PreservesBoundedVerifiedInstallerFailureReason()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var installerPath = Path.Combine(
                root,
                "api",
                "Install-HandleScopeApi.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            await File.WriteAllTextAsync(
                installerPath,
                "param([switch]$VerifyOnly)\nthrow 'reviewed installer failure'\n",
                TestContext.Current.CancellationToken);
            var startInfo = HandleScopeReleaseInstaller.CreateInstallerStartInfo(
                installerPath,
                verifyOnly: true);

            var result = await HandleScopeReleaseInstaller.RunProcessAsync(
                startInfo,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            var failureReason = Assert.IsType<string>(result.FailureReason);
            Assert.Contains(
                "reviewed installer failure",
                failureReason,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExtractProcessFailureReason_DecodesBoundedPowerShellCliXml()
    {
        const string cliXml = """
            #< CLIXML
            <Objs xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj><MS><S S="progress">ignored</S><S S="Error">reviewed installer failure_x000D__x000A_At C:\temp\installer.ps1:1 char:1</S></MS></Obj></Objs>
            """;

        var reason = HandleScopeReleaseInstaller.ExtractProcessFailureReason(cliXml);

        Assert.Equal("reviewed installer failure", reason);
        Assert.Null(HandleScopeReleaseInstaller.ExtractProcessFailureReason(
            "#< CLIXML\n<Objs><S S=\"Error\">truncated"));
    }

    [Fact]
    public async Task RunProcessAsync_DrainsOutputAfterRetentionLimit()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var installerPath = Path.Combine(
                root,
                "api",
                "Install-HandleScopeApi.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            await File.WriteAllTextAsync(
                installerPath,
                "param([switch]$VerifyOnly)\n[Console]::Error.WriteLine('bounded failure')\n$chunk = 'x' * 1024\n1..128 | ForEach-Object { [Console]::Out.WriteLine($chunk); [Console]::Error.WriteLine($chunk) }\nexit 23\n",
                TestContext.Current.CancellationToken);
            var startInfo = HandleScopeReleaseInstaller.CreateInstallerStartInfo(
                installerPath,
                verifyOnly: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var result = await HandleScopeReleaseInstaller.RunProcessAsync(
                startInfo,
                timeout.Token);

            Assert.Equal(23, result.ExitCode);
            Assert.Equal("bounded failure", result.FailureReason);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void IntegrationDialog_UsesLocalizedCatalogInstallAction()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "SessionDock",
            "HandleScopeIntegrationDialog.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "SessionDock",
            "HandleScopeIntegrationDialog.xaml.cs"));
        var normalizedCodeBehind = codeBehind.ReplaceLineEndings("\n");

        Assert.Contains(
            "x:Name=\"InstallHandleScopeButton\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{DynamicResource Handle.Install}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource Handle.InstallName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetHandleScopeButton", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "LocalizeInstallFailureReason(exception.FailureKind)",
            normalizedCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("CurrentSetupUrl", normalizedCodeBehind, StringComparison.Ordinal);
        Assert.Contains("_releaseInstaller.InstallAsync(", normalizedCodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAndVerifyAsync_RejectsTraversalEntry()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "traversal.zip");
            WriteZip(
                archivePath,
                ($"HandleScope-{Version}-win-x64/../outside.txt", "escape"u8.ToArray()));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                HandleScopeReleasePolicy.ExtractAndVerifyAsync(
                    archivePath,
                    Path.Combine(root, "extracted"),
                    Version,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExtractAndVerifyAsync_RejectsCaseCollidingEntries()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = $"HandleScope-{Version}-win-x64";
            var archivePath = Path.Combine(root, "collision.zip");
            WriteZip(
                archivePath,
                ($"{bundle}/api/tool.txt", "one"u8.ToArray()),
                ($"{bundle}/API/tool.txt", "two"u8.ToArray()));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                HandleScopeReleasePolicy.ExtractAndVerifyAsync(
                    archivePath,
                    Path.Combine(root, "extracted"),
                    Version,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExtractAndVerifyAsync_AcceptsValidSyntheticBundle()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, PackageName);
            var archiveBytes = CreateValidBundle();
            await File.WriteAllBytesAsync(
                archivePath,
                archiveBytes,
                TestContext.Current.CancellationToken);

            var installerPath = await HandleScopeReleasePolicy.ExtractAndVerifyAsync(
                archivePath,
                Path.Combine(root, "extracted"),
                Version,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                Path.Combine(
                    root,
                    "extracted",
                    $"HandleScope-{Version}-win-x64",
                    "api",
                    "Install-HandleScopeApi.ps1"),
                installerPath);
            Assert.Equal(
                "synthetic installer",
                await File.ReadAllTextAsync(
                    installerPath,
                    TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(installerPath)!,
                "HandleScope.Api.exe")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExtractAndVerifyAsync_ValidatesOptInRealReleaseArchive()
    {
        var archivePath = Environment.GetEnvironmentVariable(
            "HANDLESCOPE_TEST_ARCHIVE");
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        Assert.True(
            File.Exists(archivePath),
            $"HANDLESCOPE_TEST_ARCHIVE does not exist: {archivePath}");
        var root = CreateTemporaryRoot();
        try
        {
            var installerPath = await HandleScopeReleasePolicy.ExtractAndVerifyAsync(
                archivePath,
                Path.Combine(root, "extracted"),
                HandleScopeReleaseInstaller.PinnedVersion,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(installerPath));
            Assert.EndsWith(
                Path.Combine("api", "Install-HandleScopeApi.ps1"),
                installerPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallPinnedAsync_VerifiesThenInstallsValidatedFakeHttpBundle(
        bool includeContentLength)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes,
                includeContentLength);
            var invocations = new List<ProcessInvocation>();
            Task<HandleScopeInstallerProcessResult> RunProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                var arguments = startInfo.ArgumentList.ToArray();
                Assert.True(File.Exists(arguments[6]));
                invocations.Add(new(
                    startInfo.FileName,
                    arguments,
                    cancellationToken));
                return Task.FromResult(
                    new HandleScopeInstallerProcessResult(0, FailureReason: null));
            }

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                RunProcess,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));
            var progress = new RecordingProgress();
            using var cancellation = new CancellationTokenSource();

            var result = await installer.InstallPinnedAsync(
                progress,
                cancellation.Token);

            Assert.Equal(Version, result.Version);
            Assert.Equal(2, invocations.Count);
            Assert.Equal("-VerifyOnly", Assert.Single(
                invocations[0].Arguments.Skip(7)));
            Assert.Equal(
                new[] { "-StartNow", "-EnableAutostart" },
                invocations[1].Arguments.Skip(7));
            Assert.Equal(cancellation.Token, invocations[0].CancellationToken);
            Assert.Equal(CancellationToken.None, invocations[1].CancellationToken);
            Assert.Equal(invocations[0].FileName, invocations[1].FileName);
            Assert.DoesNotContain(
                invocations.SelectMany(invocation => invocation.Arguments),
                IsForbiddenInstallerArgument);
            Assert.Equal(
                new[]
                {
                    HandleScopeReleaseInstallStage.CheckingRelease,
                    HandleScopeReleaseInstallStage.DownloadingPackage,
                    HandleScopeReleaseInstallStage.DownloadingPackage,
                    HandleScopeReleaseInstallStage.VerifyingPackage,
                    HandleScopeReleaseInstallStage.InstallingPackage
                },
                progress.Values.Select(value => value.Stage));
            Assert.Equal(4, handler.RequestUris.Count);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallPinnedAsync_PreservesFailedInstallerPhaseGuidance(
        bool failVerification)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes);
            var invocationCount = 0;
            Task<HandleScopeInstallerProcessResult> RunProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                invocationCount++;
                var shouldFail = failVerification
                    ? invocationCount == 1
                    : invocationCount == 2;
                return Task.FromResult(new HandleScopeInstallerProcessResult(
                    shouldFail ? 17 : 0,
                    shouldFail ? "reviewed phase failure" : null));
            }

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                RunProcess,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                HandleScopeInstallFailureKind.Installer,
                exception.FailureKind);
            Assert.Contains(
                failVerification ? "Nothing was installed" : "Refresh the status",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "reviewed phase failure",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(failVerification ? 1 : 2, invocationCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallPinnedAsync_RejectsContradictoryDeclaredLength(
        bool mismatchChecksumLength)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes,
                packageDeclaredLength: mismatchChecksumLength
                    ? null
                    : packageBytes.LongLength + 1,
                checksumsDeclaredLength: mismatchChecksumLength
                    ? checksumBytes.LongLength + 1
                    : null);
            static Task<HandleScopeInstallerProcessResult> UnexpectedProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken) =>
                throw new Xunit.Sdk.XunitException(
                    "A package with a contradictory declared length was executed.");

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                UnexpectedProcess,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InstallPinnedAsync_RejectsInvalidHeaderlessPackageBody(
        int lengthDelta)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var expectedPackage = CreateValidBundle();
            byte[] servedPackage = lengthDelta switch
            {
                -1 => expectedPackage[..^1],
                0 => expectedPackage.ToArray(),
                1 => [.. expectedPackage, 0],
                _ => throw new Xunit.Sdk.XunitException("Unexpected test case.")
            };
            if (lengthDelta == 0)
                servedPackage[0] ^= 0xFF;
            var packageHash = SHA256.HashData(expectedPackage);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                servedPackage,
                checksumBytes,
                includeContentLength: false);
            static Task<HandleScopeInstallerProcessResult> UnexpectedProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken) =>
                throw new Xunit.Sdk.XunitException(
                    "An invalid headerless package was executed.");

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                UnexpectedProcess,
                CreateIdentity(
                    packageHash,
                    expectedPackage.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InstallPinnedAsync_RejectsInvalidHeaderlessChecksumBody(
        int lengthDelta)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var expectedChecksums = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            byte[] servedChecksums = lengthDelta switch
            {
                -1 => expectedChecksums[..^1],
                0 => expectedChecksums.ToArray(),
                1 => [.. expectedChecksums, 0],
                _ => throw new Xunit.Sdk.XunitException("Unexpected test case.")
            };
            if (lengthDelta == 0)
                servedChecksums[0] ^= 0x01;
            var checksumHash = SHA256.HashData(expectedChecksums);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                servedChecksums,
                includeContentLength: false);
            static Task<HandleScopeInstallerProcessResult> UnexpectedProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken) =>
                throw new Xunit.Sdk.XunitException(
                    "An invalid headerless checksum was executed.");

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                UnexpectedProcess,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    expectedChecksums.LongLength));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallPinnedAsync_WrapsProcessLaunchFailureAndCleansTemporaryFiles()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes);
            var checksumHash = SHA256.HashData(checksumBytes);
            static Task<HandleScopeInstallerProcessResult> RejectProcessStart(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken) =>
                throw new Win32Exception("Process creation was blocked.");

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                RejectProcessStart,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.IsType<Win32Exception>(exception.InnerException);
            Assert.Contains("could not be installed safely", exception.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallPinnedAsync_KeepsArchiveAndVerifiedBundleLockedThroughBothProcesses()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes);
            var invocationCount = 0;
            Task<HandleScopeInstallerProcessResult> ProbeLockedInputs(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                invocationCount++;
                var installerPath = startInfo.ArgumentList[6];
                var apiRoot = Path.GetDirectoryName(installerPath)!;
                var bundleRoot = Path.GetDirectoryName(apiRoot)!;
                var extractionRoot = Path.GetDirectoryName(bundleRoot)!;
                var operationRoot = Path.GetDirectoryName(extractionRoot)!;
                var archivePath = Path.Combine(operationRoot, PackageName);
                var manifestPath = Path.Combine(bundleRoot, "CONTENTS.sha256");
                var executablePath = Path.Combine(
                    apiRoot,
                    "HandleScope.Api.exe");

                Assert.Throws<IOException>(() => File.Open(
                    archivePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None));
                Assert.Throws<IOException>(() =>
                    File.WriteAllText(installerPath, "swapped installer"));
                Assert.Throws<IOException>(() =>
                    File.WriteAllText(manifestPath, "swapped manifest"));
                Assert.Throws<IOException>(() =>
                    File.WriteAllText(executablePath, "swapped executable"));
                Assert.Throws<IOException>(() => Directory.Move(
                    bundleRoot,
                    bundleRoot + ".swapped"));
                return Task.FromResult(
                    new HandleScopeInstallerProcessResult(0, null));
            }

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                ProbeLockedInputs,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            await installer.InstallPinnedAsync(
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, invocationCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallPinnedAsync_DetectsInventoryAdditionBetweenProcessPhases()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            using var handler = new FakeReleaseHandler(
                packageBytes,
                checksumBytes);
            var invocationCount = 0;
            Task<HandleScopeInstallerProcessResult> AddUnverifiedFile(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                invocationCount++;
                File.WriteAllText(
                    Path.Combine(startInfo.WorkingDirectory, "unverified.ps1"),
                    "unverified");
                return Task.FromResult(
                    new HandleScopeInstallerProcessResult(0, null));
            }

            using var installer = new HandleScopeReleaseInstaller(
                handler,
                root,
                AddUnverifiedFile,
                CreateIdentity(
                    packageHash,
                    packageBytes.LongLength,
                    checksumHash,
                    checksumBytes.LongLength));

            var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(
                () => installer.InstallPinnedAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, invocationCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static bool IsForbiddenInstallerArgument(string argument) =>
        argument.Equals("-EnableSessionDock", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-AllowDowngrade", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("Bypass", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase);

    private static HandleScopeReleaseIdentity CreateIdentity(byte[] packageHash) =>
        CreateIdentity(
            packageHash,
            1,
            SHA256.HashData("checksums"u8),
            1);

    private static HandleScopeReleaseIdentity CreateIdentity(
        byte[] packageHash,
        long packageSize,
        byte[] checksumsHash,
        long checksumsSize) =>
        new(
            Version,
            TagName,
            new HandleScopeReleaseAsset(
                PackageName,
                packageSize,
                packageHash,
                PackageUri),
            new HandleScopeReleaseAsset(
                ChecksumsName,
                checksumsSize,
                checksumsHash,
                ChecksumsUri));

    private static byte[] CreateValidBundle()
    {
        var files = new (string Path, byte[] Contents)[]
        {
            ("api/Install-HandleScopeApi.ps1", Encoding.UTF8.GetBytes("synthetic installer")),
            ("api/HandleScope.Api.exe", "synthetic executable"u8.ToArray()),
            ("README.txt", "synthetic readme"u8.ToArray())
        };
        var manifest = string.Concat(files.Select(file =>
            $"{Hex(SHA256.HashData(file.Contents))}  {file.Path}\n"));
        var bundle = $"HandleScope-{Version}-win-x64";

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
                WriteEntry(archive, $"{bundle}/{file.Path}", file.Contents);
            WriteEntry(
                archive,
                $"{bundle}/CONTENTS.sha256",
                Encoding.UTF8.GetBytes(manifest));
        }
        return output.ToArray();
    }

    private static void WriteZip(
        string path,
        params (string Path, byte[] Contents)[] entries)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var entry in entries)
            WriteEntry(archive, entry.Path, entry.Contents);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(contents);
    }

    private static string Hex(byte[] value) =>
        Convert.ToHexString(value).ToLowerInvariant();

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-HandleScopeRelease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
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
            "The SessionDock repository root could not be located for source validation.");
    }

    private sealed class RecordingProgress
        : IProgress<HandleScopeReleaseInstallProgress>
    {
        internal List<HandleScopeReleaseInstallProgress> Values { get; } = [];

        public void Report(HandleScopeReleaseInstallProgress value) =>
            Values.Add(value);
    }

    private sealed record ProcessInvocation(
        string FileName,
        string[] Arguments,
        CancellationToken CancellationToken);

    private sealed class FakeReleaseHandler(
        byte[] package,
        byte[] checksums,
        bool includeContentLength = true,
        long? packageDeclaredLength = null,
        long? checksumsDeclaredLength = null)
        : HttpMessageHandler
    {
        private static readonly Uri PackageRedirect = new(
            "https://objects.githubusercontent.com/release/package?signature=test");
        private static readonly Uri ChecksumsRedirect = new(
            "https://release-assets.githubusercontent.com/release/checksums?signature=test");

        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI is missing.");
            RequestUris.Add(uri);

            if (uri == PackageUri)
                return Task.FromResult(Redirect(PackageRedirect));
            if (uri == ChecksumsUri)
                return Task.FromResult(Redirect(ChecksumsRedirect));
            if (uri == PackageRedirect)
                return Task.FromResult(Ok(package, packageDeclaredLength));
            if (uri == ChecksumsRedirect)
                return Task.FromResult(Ok(checksums, checksumsDeclaredLength));
            throw new InvalidOperationException($"Unexpected request URI: {uri}");
        }

        private HttpResponseMessage Ok(byte[] contents, long? declaredLength)
        {
            HttpContent content = includeContentLength
                ? new ByteArrayContent(contents)
                : new HeaderlessByteArrayContent(contents);
            if (declaredLength is not null)
                content.Headers.ContentLength = declaredLength;
            Assert.Equal(
                declaredLength ??
                    (includeContentLength ? contents.LongLength : null),
                content.Headers.ContentLength);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        }

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }
    }

    private sealed class HeaderlessByteArrayContent(byte[] contents)
        : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(contents, 0, contents.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
