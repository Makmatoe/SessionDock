using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeReleaseInstallerTests
{
    private const string Version = "1.2.3";
    private const string TagName = "v1.2.3";
    private const string PackageName = "HandleScope-1.2.3-win-x64.zip";
    private const string ChecksumsName = "SHA256SUMS.txt";
    private const string DescriptorName = "handlescope-release.json";
    private static readonly Uri PackageUri = new(
        $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{PackageName}");
    private static readonly Uri ChecksumsUri = new(
        $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{ChecksumsName}");
    private static readonly Uri DescriptorUri = new(
        $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{DescriptorName}");

    [Fact]
    public void ParseLatestRelease_AcceptsImmutableStableReleaseWithoutDescriptor()
    {
        var packageHash = SHA256.HashData("package"u8);
        var checksumsHash = SHA256.HashData("checksums"u8);

        var release = HandleScopeReleasePolicy.ParseLatestRelease(
            CreateReleaseJson(
                packageHash,
                7,
                checksumsHash,
                9,
                includeDescriptor: false));

        Assert.Equal(Version, release.Version);
        Assert.Equal(TagName, release.TagName);
        Assert.Equal(PackageName, release.Package.Name);
        Assert.Equal(7, release.Package.Size);
        Assert.Equal(packageHash, release.Package.Sha256);
        Assert.Equal(PackageUri, release.Package.DownloadUri);
        Assert.Equal(ChecksumsName, release.Checksums.Name);
        Assert.Equal(9, release.Checksums.Size);
        Assert.Equal(checksumsHash, release.Checksums.Sha256);
        Assert.Equal(ChecksumsUri, release.Checksums.DownloadUri);
        Assert.Null(release.Descriptor);
    }

    [Fact]
    public void ParseLatestRelease_ValidatesOptInRealReleaseMetadata()
    {
        var metadataPath = Environment.GetEnvironmentVariable(
            "HANDLESCOPE_TEST_RELEASE_JSON");
        if (string.IsNullOrWhiteSpace(metadataPath))
            return;

        var release = HandleScopeReleasePolicy.ParseLatestRelease(
            File.ReadAllBytes(metadataPath));

        Assert.Equal($"v{release.Version}", release.TagName);
        Assert.Equal(
            $"HandleScope-{release.Version}-win-x64.zip",
            release.Package.Name);
        Assert.Equal(ChecksumsName, release.Checksums.Name);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ParseLatestRelease_RejectsMutableDraftOrPrerelease(
        bool immutable,
        bool draft,
        bool prerelease)
    {
        var json = CreateReleaseJson(
            SHA256.HashData("package"u8),
            7,
            SHA256.HashData("checksums"u8),
            9,
            immutable,
            draft,
            prerelease);

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleasePolicy.ParseLatestRelease(json));
    }

    [Fact]
    public void ParseLatestRelease_RejectsWrongPlatformAsset()
    {
        var json = CreateReleaseJson(
            SHA256.HashData("package"u8),
            7,
            SHA256.HashData("checksums"u8),
            9,
            packageName: "HandleScope-1.2.3-win-arm64.zip");

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleasePolicy.ParseLatestRelease(json));
    }

    [Fact]
    public void ParseLatestRelease_RejectsDuplicateAssetNames()
    {
        var json = CreateReleaseJson(
            SHA256.HashData("package"u8),
            7,
            SHA256.HashData("checksums"u8),
            9,
            duplicatePackage: true);

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleasePolicy.ParseLatestRelease(json));
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
            "Bypass",
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
        Assert.True(string.IsNullOrEmpty(startInfo.Verb));
        Assert.Equal(Path.GetDirectoryName(fullInstallerPath), startInfo.WorkingDirectory);
        Assert.EndsWith(
            Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
            startInfo.FileName,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(startInfo.ArgumentList, IsForbiddenInstallerArgument);
    }

    [Fact]
    public void IntegrationDialog_UsesExactInstallLatestReleaseLabel()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "HandleScopeIntegrationDialog.xaml"));

        Assert.Contains(
            "Text=\"{DynamicResource Handle.Install}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource Handle.InstallName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            ">Install Latest HandleScope release<",
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "SessionDock",
                "Localization",
                "Strings.en-US.xaml")),
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetHandleScopeButton", xaml, StringComparison.Ordinal);
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
    public async Task ExtractAndVerifyAsync_RejectsLinkedEntry()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "link.zip");
            using (var output = new FileStream(
                       archivePath,
                       FileMode.CreateNew,
                       FileAccess.Write))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(
                    $"HandleScope-{Version}-win-x64/api/HandleScope.Api.exe");
                entry.ExternalAttributes = 0xA000 << 16;
                using var stream = entry.Open();
                stream.Write("link"u8);
            }

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
    public async Task ExtractAndVerifyAsync_RejectsReservedWindowsPath()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "reserved.zip");
            WriteZip(
                archivePath,
                ($"HandleScope-{Version}-win-x64/api/CON.txt", "invalid"u8.ToArray()));

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
                "0.1.2",
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

    [Fact]
    public async Task InstallLatestAsync_VerifiesGitHubReleaseThenInstallsValidatedBundle()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var checksumHash = SHA256.HashData(checksumBytes);
            var metadata = CreateReleaseJson(
                packageHash,
                packageBytes.LongLength,
                checksumHash,
                checksumBytes.LongLength,
                includeDescriptor: false);
            using var handler = new FakeReleaseHandler(
                metadata,
                packageBytes,
                checksumBytes,
                descriptor: null);
            var invocations = new List<ProcessInvocation>();
            Task<int> RunProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                var arguments = startInfo.ArgumentList.ToArray();
                Assert.True(File.Exists(arguments[6]));
                invocations.Add(new(
                    startInfo.FileName,
                    arguments,
                    cancellationToken));
                return Task.FromResult(0);
            }

            var operationRoot = Path.Combine(root, "operations");
            var dataRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(operationRoot);
            Directory.CreateDirectory(dataRoot);
            var provider = new TestKeyProvider(string.Empty);
            var runtimeVerifier = new HandleScopeInstalledRuntimeVerifier(
                root,
                dataRoot,
                provider);
            using var installer = new HandleScopeReleaseInstaller(
                handler,
                operationRoot,
                RunProcess,
                provider,
                runtimeVerifier);
            var progress = new RecordingProgress();
            using var cancellation = new CancellationTokenSource();

            var result = await installer.InstallLatestAsync(
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
            Assert.Equal(5, handler.RequestUris.Count);
            Assert.Empty(Directory.EnumerateFileSystemEntries(operationRoot));
            Assert.True(File.Exists(Path.Combine(
                dataRoot,
                "HandleScopeAuthorization",
                "github-release-receipt.json")));
            Assert.False(File.Exists(Path.Combine(
                dataRoot,
                "HandleScopeAuthorization",
                DescriptorName)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallLatestAsync_WrapsProcessLaunchFailureAndCleansTemporaryFiles()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var packageHash = SHA256.HashData(packageBytes);
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(packageHash)}  {PackageName}\n");
            var manifestBytes = ReadBundleManifest(packageBytes);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var provider = new TestKeyProvider(signingKey.ExportSubjectPublicKeyInfoPem());
            var descriptorBytes = CreateSignedDescriptor(
                packageBytes,
                checksumBytes,
                manifestBytes,
                signingKey);
            using var handler = new FakeReleaseHandler(
                CreateReleaseJson(
                    packageHash,
                    packageBytes.LongLength,
                    SHA256.HashData(checksumBytes),
                    checksumBytes.LongLength,
                    descriptorHash: SHA256.HashData(descriptorBytes),
                    descriptorSize: descriptorBytes.LongLength),
                packageBytes,
                checksumBytes,
                descriptorBytes);
            static Task<int> RejectProcessStart(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken) =>
                throw new Win32Exception("Process creation was blocked.");

            var operationRoot = Path.Combine(root, "operations");
            var dataRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(operationRoot);
            Directory.CreateDirectory(dataRoot);
            using var installer = new HandleScopeReleaseInstaller(
                handler,
                operationRoot,
                RejectProcessStart,
                provider,
                new HandleScopeInstalledRuntimeVerifier(root, dataRoot, provider));

            var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallLatestAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.IsType<Win32Exception>(exception.InnerException);
            Assert.Contains("could not be installed safely", exception.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(operationRoot));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallLatestAsync_DoesNotExecuteBeforeDescriptorVerification()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var packageBytes = CreateValidBundle();
            var checksumBytes = Encoding.UTF8.GetBytes(
                $"{Hex(SHA256.HashData(packageBytes))}  {PackageName}\n");
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var provider = new TestKeyProvider(signingKey.ExportSubjectPublicKeyInfoPem());
            var descriptorBytes = CreateSignedDescriptor(
                packageBytes,
                checksumBytes,
                ReadBundleManifest(packageBytes),
                signingKey);
            descriptorBytes[^1] ^= 1;
            var metadata = CreateReleaseJson(
                SHA256.HashData(packageBytes),
                packageBytes.LongLength,
                SHA256.HashData(checksumBytes),
                checksumBytes.LongLength,
                descriptorHash: SHA256.HashData(descriptorBytes),
                descriptorSize: descriptorBytes.LongLength);
            using var handler = new FakeReleaseHandler(
                metadata,
                packageBytes,
                checksumBytes,
                descriptorBytes);
            var processRequests = 0;
            Task<int> RunProcess(
                ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                processRequests++;
                return Task.FromResult(0);
            }
            var operations = Path.Combine(root, "operations");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(operations);
            Directory.CreateDirectory(data);
            using var installer = new HandleScopeReleaseInstaller(
                handler,
                operations,
                RunProcess,
                provider,
                new HandleScopeInstalledRuntimeVerifier(root, data, provider));

            await Assert.ThrowsAsync<HandleScopeInstallException>(() =>
                installer.InstallLatestAsync(
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, processRequests);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static bool IsForbiddenInstallerArgument(string argument) =>
        argument.Equals("-EnableSessionDock", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-AllowDowngrade", StringComparison.OrdinalIgnoreCase);

    private static HandleScopeReleaseIdentity CreateIdentity(byte[] packageHash) =>
        new(
            Version,
            TagName,
            new HandleScopeReleaseAsset(
                PackageName,
                1,
                packageHash,
                PackageUri),
            new HandleScopeReleaseAsset(
                ChecksumsName,
                1,
                SHA256.HashData("checksums"u8),
                ChecksumsUri),
            new HandleScopeReleaseAsset(
                DescriptorName,
                1,
                SHA256.HashData("descriptor"u8),
                DescriptorUri));

    private static byte[] CreateReleaseJson(
        byte[] packageHash,
        long packageSize,
        byte[] checksumsHash,
        long checksumsSize,
        bool immutable = true,
        bool draft = false,
        bool prerelease = false,
        string packageName = PackageName,
        bool duplicatePackage = false,
        byte[]? descriptorHash = null,
        long descriptorSize = 10,
        bool includeDescriptor = true)
    {
        var assets = new List<Dictionary<string, object?>>
        {
            CreateAsset(packageName, packageSize, packageHash),
            CreateAsset(ChecksumsName, checksumsSize, checksumsHash)
        };
        if (includeDescriptor)
        {
            assets.Add(CreateAsset(
                DescriptorName,
                descriptorSize,
                descriptorHash ?? SHA256.HashData("descriptor"u8)));
        }
        if (duplicatePackage)
            assets.Add(CreateAsset(packageName, packageSize, packageHash));

        var release = new Dictionary<string, object?>
        {
            ["immutable"] = immutable,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["tag_name"] = TagName,
            ["assets"] = assets
        };
        return JsonSerializer.SerializeToUtf8Bytes(release);
    }

    private static Dictionary<string, object?> CreateAsset(
        string name,
        long size,
        byte[] hash) => new()
        {
            ["name"] = name,
            ["state"] = "uploaded",
            ["size"] = size,
            ["digest"] = $"sha256:{Hex(hash)}",
            ["browser_download_url"] =
            $"https://github.com/Makmatoe/HandleScope/releases/download/{TagName}/{Uri.EscapeDataString(name)}"
        };

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

    private static byte[] ReadBundleManifest(byte[] archiveBytes)
    {
        using var input = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = archive.GetEntry(
            $"HandleScope-{Version}-win-x64/CONTENTS.sha256")
            ?? throw new InvalidOperationException("Synthetic manifest missing.");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CreateSignedDescriptor(
        byte[] packageBytes,
        byte[] checksumBytes,
        byte[] manifestBytes,
        ECDsa signingKey)
    {
        var unsigned = new HandleScopeReleaseDescriptor(
            HandleScopeReleaseAuthorizationPolicy.SchemaVersion,
            HandleScopeReleaseAuthorizationPolicy.Product,
            HandleScopeReleaseAuthorizationPolicy.Repository,
            HandleScopeReleaseAuthorizationPolicy.Channel,
            "handlescope-release-2026-01",
            Version,
            TagName,
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero).ToString("O"),
            PackageName,
            packageBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(packageBytes)),
            ChecksumsName,
            checksumBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(checksumBytes)),
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            HandleScopeReleaseAuthorizationPolicy.Platform,
            HandleScopeReleaseAuthorizationPolicy.Architecture,
            string.Empty);
        var signature = signingKey.SignData(
            HandleScopeReleaseAuthorizationPolicy.CreateCanonicalPayload(unsigned),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var descriptor = unsigned with
        {
            Signature = Convert.ToBase64String(signature)
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            descriptor,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
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
        byte[] metadata,
        byte[] package,
        byte[] checksums,
        byte[]? descriptor)
        : HttpMessageHandler
    {
        private static readonly Uri PackageRedirect = new(
            "https://objects.githubusercontent.com/release/package?signature=test");
        private static readonly Uri ChecksumsRedirect = new(
            "https://release-assets.githubusercontent.com/release/checksums?signature=test");
        private static readonly Uri DescriptorRedirect = new(
            "https://objects.githubusercontent.com/release/descriptor?signature=test");

        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI is missing.");
            RequestUris.Add(uri);

            if (uri == HandleScopeReleaseInstaller.LatestReleaseUri)
                return Task.FromResult(Ok(metadata));
            if (uri == PackageUri)
                return Task.FromResult(Redirect(PackageRedirect));
            if (uri == ChecksumsUri)
                return Task.FromResult(Redirect(ChecksumsRedirect));
            if (uri == DescriptorUri)
                return Task.FromResult(Redirect(DescriptorRedirect));
            if (uri == PackageRedirect)
                return Task.FromResult(Ok(package));
            if (uri == ChecksumsRedirect)
                return Task.FromResult(Ok(checksums));
            if (uri == DescriptorRedirect)
            {
                if (descriptor is not null)
                    return Task.FromResult(Ok(descriptor));
            }
            throw new InvalidOperationException($"Unexpected request URI: {uri}");
        }

        private static HttpResponseMessage Ok(byte[] contents) => new(
            HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(contents)
        };

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }
    }

    private sealed class TestKeyProvider(string publicKeyPem) :
        IHandleScopeReleaseKeyProvider
    {
        public bool TryGetPublicKeyPem(string keyId, out string value)
        {
            value = publicKeyPem;
            return keyId == "handlescope-release-2026-01";
        }
    }
}
