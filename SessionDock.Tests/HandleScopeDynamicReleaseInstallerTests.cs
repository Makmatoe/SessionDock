using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionDock.ReleaseTrust;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeDynamicReleaseInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.Dynamic.{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InstallAsync_RebuildsEveryAssetFromCurrentVerifiedCatalog()
    {
        var fixture = CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogHandler = new RejectingHandler();
        var catalogService = CreateCatalogService(
            fixture,
            catalogHandler,
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"));
        var callerControlled = fixture.Release with
        {
            Tag = "v9.9.9",
            Package = new("evil.zip", 1, new string('0', 64)),
            Checksums = new("evil.txt", 1, new string('1', 64)),
            Manifest = new("evil.json", 1, new string('2', 64)),
            ApiExecutable = new("evil.exe", 1, new string('3', 64)),
            ContractUrl = "https://evil.example/contract"
        };

        var result = await installer.InstallAsync(
            callerControlled,
            fixture.Catalog,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Release.Version, result.Version);
        Assert.Equal(2, processCount);
        Assert.Equal(0, catalogHandler.RequestCount);
        Assert.Equal(
            fixture.CreateAssets().Keys.OrderBy(uri => uri.AbsoluteUri),
            assetHandler.RequestUris.OrderBy(uri => uri.AbsoluteUri));
        Assert.DoesNotContain(
            assetHandler.RequestUris,
            uri => uri.Host.Equals("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_AllowsFuturePreferredV3WhenV2IsShared()
    {
        var fixture = CreateFixture(
            apiContracts: ["v1", "v2", "v3"],
            preferredApiVersion: "v3");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogService = CreateCatalogService(
            fixture,
            new RejectingHandler(),
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"));

        await installer.InstallAsync(
            fixture.Release,
            fixture.Catalog,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, processCount);
    }

    [Fact]
    public async Task InstallAsync_NativeSetupUsesOnlyManifestBoundDirectPhases()
    {
        var fixture = CreateFixture(
            version: "0.3.0",
            nativeSetup: true);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogService = CreateCatalogService(
            fixture,
            new RejectingHandler(),
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var invocations = new List<ProcessStartInfo>();
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            invocations.Add(startInfo);
            Assert.EndsWith(
                Path.Combine("api", "HandleScope.Setup.exe"),
                startInfo.FileName,
                StringComparison.OrdinalIgnoreCase);
            var apiRoot = startInfo.WorkingDirectory;
            var bundleRoot = Path.GetDirectoryName(apiRoot)!;
            var extractionRoot = Path.GetDirectoryName(bundleRoot)!;
            var operationRoot = Path.GetDirectoryName(extractionRoot)!;
            var archivePath = Path.Combine(
                operationRoot,
                fixture.Release.Package.Name);
            Assert.ThrowsAny<IOException>(() => File.Open(
                startInfo.FileName,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None));
            Assert.Throws<IOException>(() => File.Open(
                archivePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None));
            Assert.Throws<IOException>(() => File.WriteAllText(
                Path.Combine(bundleRoot, "CONTENTS.sha256"),
                "swapped manifest"));
            Assert.Throws<IOException>(() => File.WriteAllText(
                Path.Combine(apiRoot, "HandleScope.Api.exe"),
                "swapped runtime"));
            Assert.Throws<IOException>(() => Directory.Move(
                bundleRoot,
                bundleRoot + ".swapped"));
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"));

        await installer.InstallAsync(
            fixture.Release,
            fixture.Catalog,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, invocations.Count);
        Assert.Equal(["verify"], invocations[0].ArgumentList);
        Assert.Equal(
            ["install", "--start-now", "--enable-autostart"],
            invocations[1].ArgumentList);
        Assert.All(invocations, invocation =>
        {
            Assert.False(invocation.UseShellExecute);
            Assert.DoesNotContain(
                invocation.ArgumentList,
                argument => argument.Contains(
                    "ExecutionPolicy",
                    StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("schema", 2)]
    [InlineData("setup-missing", 2)]
    [InlineData("setup-path", 2)]
    [InlineData("setup-size", 3)]
    [InlineData("setup-hash", 3)]
    [InlineData("setup-extra", 2)]
    [InlineData("setup-inventory", 3)]
    [InlineData("bundle-missing", 3)]
    public async Task InstallAsync_NativeSetupRequiresStrictV2ManifestIdentity(
        string mutation,
        int expectedAssetRequests)
    {
        var fixture = CreateFixture(
            version: "0.3.0",
            nativeSetup: true,
            setupMutation: mutation);

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            fixture.Catalog);

        Assert.Equal(expectedAssetRequests, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            rejected.Exception.FailureKind);
    }

    [Fact]
    public async Task InstallAsync_LegacyManifestRejectsNativeSetupIdentity()
    {
        var fixture = CreateFixture(setupMutation: "legacy-extra");

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            fixture.Catalog);

        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            rejected.Exception.FailureKind);
    }

    [Fact]
    public async Task InstallAsync_NativeDetectsInventoryAdditionBetweenPhases()
    {
        var fixture = CreateFixture(
            version: "0.3.0",
            nativeSetup: true);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogService = CreateCatalogService(
            fixture,
            new RejectingHandler(),
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            File.WriteAllText(
                Path.Combine(
                    Path.GetDirectoryName(startInfo.WorkingDirectory)!,
                    "unexpected-after-verify.txt"),
                "untrusted addition");
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"));

        var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(
            () => installer.InstallAsync(
                fixture.Release,
                fixture.Catalog,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, processCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            exception.FailureKind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("contradictory")]
    public async Task InstallAsync_RejectsUnsupportedSetupAdapterBeforeNetwork(
        string mutation)
    {
        var fixture = CreateFixture(
            version: "0.3.0",
            nativeSetup: true);
        var capabilities = fixture.Release.Capabilities
            .Where(capability => capability !=
                HandleScopeCatalogInstallPolicy.NativeSetupCapability)
            .ToList();
        if (mutation is "unknown" or "contradictory")
            capabilities.Add("handlescope.setup.future.v2");
        if (mutation == "contradictory")
        {
            capabilities.Add(
                HandleScopeCatalogInstallPolicy.NativeSetupCapability);
        }
        var unauthorized = fixture.Release with
        {
            Capabilities = capabilities.Order(StringComparer.Ordinal).ToArray()
        };
        var catalog = CreateVerifiedCatalog(
            [unauthorized],
            unauthorized.Version,
            sessionDockVersion: "2.9.0");
        fixture = fixture with
        {
            Release = unauthorized,
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };

        var rejected = await RejectInstallAsync(
            fixture,
            unauthorized,
            catalog);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            rejected.Exception.FailureKind);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("identity")]
    [InlineData("api")]
    public async Task InstallAsync_EnforcesExternalManifestBinding(
        string mutation)
    {
        var fixture = CreateFixture(
            manifestMutation: mutation is "identity" or "api" ? mutation : null,
            includeManifestChecksum: mutation != "checksum");

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            fixture.Catalog);

        Assert.Equal(0, rejected.ProcessCount);
        Assert.True(rejected.AssetRequestCount >= 2);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            rejected.Exception.FailureKind);
    }

    [Fact]
    public async Task InstallAsync_RejectsExpiredDialogSnapshotBeforeNetwork()
    {
        var fixture = CreateFixture();
        var expired = fixture.Catalog with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            expired);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("incompatible")]
    public async Task InstallAsync_RejectsUnauthorizedSelectionBeforeNetwork(
        string reason)
    {
        var fixture = CreateFixture();
        var target = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.3",
            status: reason == "revoked" ? "revoked" : "supported",
            minimumSessionDockVersion: reason == "incompatible"
                ? "9.0.0"
                : "2.7.6");
        var catalog = CreateVerifiedCatalog(
            [fixture.Release, target],
            fixture.Release.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };

        var rejected = await RejectInstallAsync(fixture, target, catalog);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
    }

    [Fact]
    public async Task InstallAsync_RevocationDominatesSupportedRuntimeAlias()
    {
        var fixture = CreateFixture();
        var revokedAlias = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            status: "revoked",
            executableSize: fixture.Release.ApiExecutable.Size,
            executableSha256: fixture.Release.ApiExecutable.Sha256);
        var fallback = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.3");
        var catalog = CreateVerifiedCatalog(
            [revokedAlias, fixture.Release, fallback],
            fallback.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            catalog);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_UnknownInstalledRuntimeFailsClosedBeforeNetwork(
        bool sameSize)
    {
        var fixture = CreateFixture();
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        var length = checked((int)fixture.Release.ApiExecutable.Size) +
            (sameSize ? 0 : 1);
        File.WriteAllBytes(executablePath, Enumerable.Repeat((byte)0xA5, length).ToArray());

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            fixture.Catalog,
            localRoot);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.LocalEnvironment,
            rejected.Exception.FailureKind);
        Assert.Contains(
            "manually",
            rejected.Exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_KnownRevokedOlderRuntimeAllowsDistinctSupportedRemediation()
    {
        var fixture = CreateFixture();
        var revokedBytes = "known-revoked-runtime"u8.ToArray();
        var revoked = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            status: "revoked",
            executableSize: revokedBytes.LongLength,
            executableSha256: Hex(SHA256.HashData(revokedBytes)));
        var catalog = CreateVerifiedCatalog(
            [revoked, fixture.Release],
            fixture.Release.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, revokedBytes);

        var result = await InstallSuccessfullyAsync(
            fixture,
            fixture.Release,
            catalog,
            localRoot);

        Assert.Equal(3, result.AssetRequestCount);
        Assert.Equal(2, result.ProcessCount);
    }

    [Fact]
    public async Task InstallAsync_RevokedNewerRuntimeRejectsRemediationBeforeNetwork()
    {
        var fixture = CreateFixture();
        var revokedBytes = "known-revoked-newer-runtime"u8.ToArray();
        var revoked = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.3",
            status: "revoked",
            executableSize: revokedBytes.LongLength,
            executableSha256: Hex(SHA256.HashData(revokedBytes)));
        var catalog = CreateVerifiedCatalog(
            [fixture.Release, revoked],
            fixture.Release.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, revokedBytes);

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            catalog,
            localRoot);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.Installer,
            rejected.Exception.FailureKind);
        Assert.Contains(
            "strictly newer",
            rejected.Exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_AmbiguousRevokedRuntimeRejectsRemediationBeforeNetwork()
    {
        var fixture = CreateFixture();
        var revokedBytes = "ambiguous-revoked-runtime"u8.ToArray();
        var revokedHash = Hex(SHA256.HashData(revokedBytes));
        var firstRevoked = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.0",
            status: "revoked",
            executableSize: revokedBytes.LongLength,
            executableSha256: revokedHash);
        var secondRevoked = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            status: "revoked",
            executableSize: revokedBytes.LongLength,
            executableSha256: revokedHash);
        var catalog = CreateVerifiedCatalog(
            [firstRevoked, secondRevoked, fixture.Release],
            fixture.Release.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, revokedBytes);

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            catalog,
            localRoot);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.LocalEnvironment,
            rejected.Exception.FailureKind);
        Assert.Contains(
            "manually",
            rejected.Exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_KnownNewerRuntimeRefusesDowngradeBeforeNetwork()
    {
        var fixture = CreateFixture();
        var newerBytes = "known-newer-runtime"u8.ToArray();
        var newer = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.3",
            executableSize: newerBytes.LongLength,
            executableSha256: Hex(SHA256.HashData(newerBytes)));
        var catalog = CreateVerifiedCatalog(
            [fixture.Release, newer],
            fixture.Release.Version);
        fixture = fixture with
        {
            Catalog = catalog,
            CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                catalog.Catalog)
        };
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, newerBytes);

        var rejected = await RejectInstallAsync(
            fixture,
            fixture.Release,
            catalog,
            localRoot);

        Assert.Equal(0, rejected.AssetRequestCount);
        Assert.Equal(0, rejected.ProcessCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.Installer,
            rejected.Exception.FailureKind);
        Assert.Contains("downgrade", rejected.Exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_AllowsKnownSameOrOlderCatalogRuntime(
        bool sameVersion)
    {
        var fixture = CreateFixture();
        var installedBytes = sameVersion
            ? Encoding.UTF8.GetBytes($"runtime-{fixture.Release.Version}")
            : "known-older-runtime"u8.ToArray();
        var catalog = fixture.Catalog;
        if (!sameVersion)
        {
            var older = HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.2.1",
                executableSize: installedBytes.LongLength,
                executableSha256: Hex(SHA256.HashData(installedBytes)));
            catalog = CreateVerifiedCatalog(
                [older, fixture.Release],
                fixture.Release.Version);
            fixture = fixture with
            {
                Catalog = catalog,
                CatalogJson = HandleScopeCompatibilityCatalogPolicy.Serialize(
                    catalog.Catalog)
            };
        }
        var localRoot = Path.Combine(fixture.Root, "local");
        var executablePath = HandleScopeProcessVerifier
            .GetExpectedExecutablePath(localRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, installedBytes);

        var result = await InstallSuccessfullyAsync(
            fixture,
            fixture.Release,
            catalog,
            localRoot);

        Assert.Equal(3, result.AssetRequestCount);
        Assert.Equal(2, result.ProcessCount);
    }

    [Theory]
    [InlineData("revoked", false)]
    [InlineData("identity", false)]
    [InlineData("revoked", true)]
    [InlineData("identity", true)]
    public async Task InstallAsync_RechecksCurrentCatalogImmediatelyBeforeExecution(
        string change,
        bool nativeSetup)
    {
        var fixture = nativeSetup
            ? CreateFixture(version: "0.3.0", nativeSetup: true)
            : CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        HandleScopeCompatibleRelease changedRelease;
        IReadOnlyList<HandleScopeCompatibleRelease> updatedReleases;
        string recommended;
        if (change == "revoked")
        {
            changedRelease = fixture.Release with { Status = "revoked" };
            var fallback = HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: nativeSetup ? "0.3.1" : "0.2.3");
            updatedReleases = [changedRelease, fallback];
            recommended = fallback.Version;
        }
        else
        {
            changedRelease = fixture.Release with
            {
                MinimumSessionDockVersion = "2.7.5"
            };
            updatedReleases = [changedRelease];
            recommended = changedRelease.Version;
        }
        var generated = fixture.Catalog.GeneratedAt.AddMinutes(1);
        var unsignedUpdate = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            updatedReleases
                .OrderBy(release => new Version(release.Version))
                .ToArray(),
            recommended,
            sequence: fixture.Catalog.Catalog.Sequence + 1,
            generatedAt: generated,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));
        var signedUpdate = HandleScopeCompatibilityCatalogTestData.Sign(
            unsignedUpdate,
            key);
        var cachePath = Path.Combine(fixture.Root, "catalog.json");
        var catalogService = new HandleScopeCompatibilityCatalogService(
            new RejectingHandler(),
            cachePath,
            fixture.CatalogJson,
            key.ExportSubjectPublicKeyInfoPem());
        var packageUri = HandleScopeCatalogInstallPolicy.CreateCanonicalAssetUri(
            fixture.Release.Tag,
            fixture.Release.Package.Name);
        var updated = false;
        using var assetHandler = new AssetHandler(
            fixture.CreateAssets(),
            uri =>
            {
                if (updated || uri != packageUri)
                    return;
                updated = true;
                File.WriteAllText(
                    cachePath,
                    HandleScopeCompatibilityCatalogPolicy.Serialize(signedUpdate));
            });
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"));

        var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(
            () => installer.InstallAsync(
                fixture.Release,
                fixture.Catalog,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.True(updated);
        Assert.Equal(3, assetHandler.RequestUris.Count);
        Assert.Equal(0, processCount);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            exception.FailureKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InstallAsync_RechecksCatalogExpiryBeforeEachChildProcess(
        bool expiresAfterVerifyOnly,
        bool nativeSetup)
    {
        var fixture = nativeSetup
            ? CreateFixture(version: "0.3.0", nativeSetup: true)
            : CreateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogService = CreateCatalogService(
            fixture,
            new RejectingHandler(),
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var now = expiresAfterVerifyOnly
            ? fixture.Catalog.ExpiresAt.AddTicks(-1)
            : fixture.Catalog.ExpiresAt;
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            if (nativeSetup)
                Assert.Equal(["verify"], startInfo.ArgumentList);
            else
                Assert.Contains("-VerifyOnly", startInfo.ArgumentList);
            if (expiresAfterVerifyOnly)
                now = fixture.Catalog.ExpiresAt;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            Path.Combine(fixture.Root, "local"),
            () => now);

        var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(
            () => installer.InstallAsync(
                fixture.Release,
                fixture.Catalog,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(expiresAfterVerifyOnly ? 1 : 0, processCount);
        Assert.Equal(3, assetHandler.RequestUris.Count);
        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            exception.FailureKind);
        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReleaseFixture(
        string Root,
        HandleScopeCompatibleRelease Release,
        VerifiedHandleScopeCompatibilityCatalog Catalog,
        string CatalogJson,
        byte[] Package,
        byte[] Checksums,
        byte[] Manifest)
    {
        internal Dictionary<Uri, byte[]> CreateAssets() => new()
        {
            [HandleScopeCatalogInstallPolicy.CreateCanonicalAssetUri(
                Release.Tag,
                Release.Package.Name)] = Package,
            [HandleScopeCatalogInstallPolicy.CreateCanonicalAssetUri(
                Release.Tag,
                Release.Checksums.Name)] = Checksums,
            [HandleScopeCatalogInstallPolicy.CreateCanonicalAssetUri(
                Release.Tag,
                Release.Manifest!.Name)] = Manifest
        };
    }

    private ReleaseFixture CreateFixture(
        string version = "0.2.2",
        IReadOnlyList<string>? apiContracts = null,
        string preferredApiVersion = "v2",
        string? manifestMutation = null,
        bool includeManifestChecksum = true,
        bool nativeSetup = false,
        string? setupMutation = null)
    {
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var contracts = apiContracts ?? ["v1", "v2"];
        var capabilities = contracts
            .Select(contract => $"handlescope.http.{contract}")
            .Concat(
            [
                "handlescope.plan.single-use.v1",
                "handlescope.policy.roblox-singleton-event.v1"
            ])
            .Concat(nativeSetup
                ? [HandleScopeCatalogInstallPolicy.NativeSetupCapability]
                : [])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var executable = Encoding.UTF8.GetBytes($"runtime-{version}");
        var setupExecutable = Encoding.UTF8.GetBytes($"native-setup-{version}");
        var package = CreateBundle(
            version,
            executable,
            nativeSetup,
            setupMutation switch
            {
                "setup-inventory" => [.. setupExecutable, (byte)0x00],
                "bundle-missing" => [],
                _ => setupExecutable
            });
        var packageAsset = new HandleScopeCatalogAsset(
            $"HandleScope-{version}-win-x64.zip",
            package.LongLength,
            Hex(SHA256.HashData(package)));
        var runtime = new HandleScopeCatalogRuntime(
            "api/HandleScope.Api.exe",
            executable.LongLength,
            Hex(SHA256.HashData(executable)));
        var tag = $"v{version}";
        var manifestNode = new JsonObject
        {
            ["schemaVersion"] = nativeSetup ? 2 : 1,
            ["product"] = "HandleScope",
            ["repository"] = "Makmatoe/HandleScope",
            ["version"] = version,
            ["tag"] = tag,
            ["runtime"] = "win-x64",
            ["sourceRevision"] = new string('d', 40),
            ["sourceTimestamp"] = DateTimeOffset.UtcNow.AddMinutes(-5)
                .ToString("O"),
            ["discoveryApiVersion"] = "v1",
            ["supportedApiVersions"] = JsonSerializer.SerializeToNode(contracts),
            ["preferredApiVersion"] = preferredApiVersion,
            ["policies"] = new JsonArray("roblox-singleton-event-v1"),
            ["capabilities"] = JsonSerializer.SerializeToNode(capabilities),
            ["package"] = AssetNode(packageAsset),
            ["sbom"] = new JsonObject
            {
                ["name"] = $"HandleScope-{version}-win-x64.spdx.json",
                ["size"] = 1,
                ["sha256"] = new string('e', 64)
            },
            ["apiExecutable"] = RuntimeNode(runtime)
        };
        if (nativeSetup || setupMutation == "legacy-extra")
        {
            var setupRuntime = new HandleScopeCatalogRuntime(
                setupMutation == "setup-path"
                    ? "api/Evil.Setup.exe"
                    : "api/HandleScope.Setup.exe",
                setupExecutable.LongLength +
                    (setupMutation == "setup-size" ? 1 : 0),
                setupMutation == "setup-hash"
                    ? new string('f', 64)
                    : Hex(SHA256.HashData(setupExecutable)));
            manifestNode["setupExecutable"] = RuntimeNode(setupRuntime);
            if (setupMutation == "setup-extra")
            {
                ((JsonObject)manifestNode["setupExecutable"]!)["arguments"] =
                    "install --unsafe";
            }
        }
        if (setupMutation == "schema")
            manifestNode["schemaVersion"] = 1;
        if (setupMutation == "setup-missing")
            manifestNode.Remove("setupExecutable");
        if (manifestMutation == "identity")
            manifestNode["version"] = "9.9.9";
        else if (manifestMutation == "api")
            ((JsonObject)manifestNode["apiExecutable"]!)["sha256"] =
                new string('f', 64);
        var manifest = Encoding.UTF8.GetBytes(
            manifestNode.ToJsonString() + "\n");
        var manifestAsset = new HandleScopeCatalogAsset(
            $"HandleScope-{version}-win-x64.release.json",
            manifest.LongLength,
            Hex(SHA256.HashData(manifest)));
        var checksumText =
            $"{packageAsset.Sha256}  {packageAsset.Name}\n" +
            (includeManifestChecksum
                ? $"{manifestAsset.Sha256}  {manifestAsset.Name}\n"
                : string.Empty);
        var checksums = Encoding.UTF8.GetBytes(checksumText);
        var release = new HandleScopeCompatibleRelease(
            version,
            tag,
            "supported",
            "2.7.6",
            null,
            contracts,
            capabilities,
            packageAsset,
            new(
                "SHA256SUMS.txt",
                checksums.LongLength,
                Hex(SHA256.HashData(checksums))),
            manifestAsset,
            runtime,
            $"https://github.com/Makmatoe/HandleScope/blob/{tag}/" +
            "docs/integrations/sessiondock.md");
        var catalog = CreateVerifiedCatalog(
            [release],
            version,
            sessionDockVersion: nativeSetup ? "2.9.0" : "2.8.0");
        return new(
            root,
            release,
            catalog,
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog.Catalog),
            package,
            checksums,
            manifest);
    }

    private static HandleScopeCompatibilityCatalogService CreateCatalogService(
        ReleaseFixture fixture,
        HttpMessageHandler handler,
        string publicKeyPem) => new(
        handler,
        Path.Combine(fixture.Root, "catalog.json"),
        fixture.CatalogJson,
        publicKeyPem);

    private static async Task<RejectedInstall> RejectInstallAsync(
        ReleaseFixture fixture,
        HandleScopeCompatibleRelease selectedRelease,
        VerifiedHandleScopeCompatibilityCatalog expectedCatalog,
        string? localAppDataRoot = null,
        Action<Uri>? onAssetRequest = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogHandler = new RejectingHandler();
        var catalogService = CreateCatalogService(
            fixture,
            catalogHandler,
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(
            fixture.CreateAssets(),
            onAssetRequest);
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            localAppDataRoot ?? Path.Combine(fixture.Root, "local"));

        var exception = await Assert.ThrowsAsync<HandleScopeInstallException>(
            () => installer.InstallAsync(
                selectedRelease,
                expectedCatalog,
                progress: null,
                TestContext.Current.CancellationToken));
        return new(
            exception,
            assetHandler.RequestUris.Count,
            catalogHandler.RequestCount,
            processCount);
    }

    private static async Task<SuccessfulInstall> InstallSuccessfullyAsync(
        ReleaseFixture fixture,
        HandleScopeCompatibleRelease selectedRelease,
        VerifiedHandleScopeCompatibilityCatalog expectedCatalog,
        string localAppDataRoot)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogService = CreateCatalogService(
            fixture,
            new RejectingHandler(),
            key.ExportSubjectPublicKeyInfoPem());
        using var assetHandler = new AssetHandler(fixture.CreateAssets());
        var processCount = 0;
        Task<HandleScopeInstallerProcessResult> RunProcess(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            processCount++;
            return Task.FromResult(new HandleScopeInstallerProcessResult(0, null));
        }
        using var installer = new HandleScopeReleaseInstaller(
            assetHandler,
            Path.Combine(fixture.Root, "downloads"),
            RunProcess,
            release: null,
            catalogService,
            localAppDataRoot);

        await installer.InstallAsync(
            selectedRelease,
            expectedCatalog,
            progress: null,
            TestContext.Current.CancellationToken);
        return new(assetHandler.RequestUris.Count, processCount);
    }

    private static VerifiedHandleScopeCompatibilityCatalog CreateVerifiedCatalog(
        IReadOnlyList<HandleScopeCompatibleRelease> releases,
        string recommendedVersion,
        long sequence = 1,
        DateTimeOffset? generatedAt = null,
        string sessionDockVersion = "2.8.0")
    {
        var generated = generatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            releases.OrderBy(release => new Version(release.Version)).ToArray(),
            recommendedVersion,
            sequence,
            generated,
            DateTimeOffset.UtcNow.AddDays(30),
            sessionDockVersion);
        return HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog));
    }

    private static JsonObject AssetNode(HandleScopeCatalogAsset asset) => new()
    {
        ["name"] = asset.Name,
        ["size"] = asset.Size,
        ["sha256"] = asset.Sha256
    };

    private static JsonObject RuntimeNode(HandleScopeCatalogRuntime runtime) => new()
    {
        ["path"] = runtime.Path,
        ["size"] = runtime.Size,
        ["sha256"] = runtime.Sha256
    };

    private static byte[] CreateBundle(
        string version,
        byte[] executable,
        bool nativeSetup,
        byte[] setupExecutable)
    {
        var files = new List<(string Path, byte[] Contents)>
        {
            ("api/Install-HandleScopeApi.ps1", "synthetic installer"u8.ToArray()),
            ("api/HandleScope.Api.exe", executable),
            ("README.txt", "synthetic readme"u8.ToArray())
        };
        if (nativeSetup && setupExecutable.Length > 0)
        {
            files.Add(("api/HandleScope.Setup.exe", setupExecutable));
        }
        var manifest = string.Concat(files.Select(file =>
            $"{Hex(SHA256.HashData(file.Contents))}  {file.Path}\n"));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var file in files)
                WriteEntry(archive, $"HandleScope-{version}-win-x64/{file.Path}", file.Contents);
            WriteEntry(
                archive,
                $"HandleScope-{version}-win-x64/CONTENTS.sha256",
                Encoding.UTF8.GetBytes(manifest));
        }
        return output.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static string Hex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record RejectedInstall(
        HandleScopeInstallException Exception,
        int AssetRequestCount,
        int CatalogRequestCount,
        int ProcessCount);

    private sealed record SuccessfulInstall(
        int AssetRequestCount,
        int ProcessCount);

    private sealed class AssetHandler(
        IReadOnlyDictionary<Uri, byte[]> assets,
        Action<Uri>? onRequest = null) : HttpMessageHandler
    {
        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            RequestUris.Add(uri);
            onRequest?.Invoke(uri);
            if (!assets.TryGetValue(uri, out var contents))
                throw new InvalidOperationException($"Unexpected asset request: {uri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents)
            });
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Network access was not expected.");
        }
    }
}
