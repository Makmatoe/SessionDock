using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SessionDock.ReleaseTrust;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeCompatibilityCatalogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.Catalog.{Guid.NewGuid():N}");

    [Fact]
    public void Load_MissingCacheUsesEmbeddedCatalogWithoutNetwork()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new CountingHandler();
        var cachePath = Path.Combine(_root, "catalog.json");
        using var service = new HandleScopeCompatibilityCatalogService(
            handler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog()),
            key.ExportSubjectPublicKeyInfoPem());

        var loaded = service.Load();

        Assert.Equal(1, loaded.Catalog.Sequence);
        Assert.Equal(new Version(0, 1, 4), loaded.RecommendedVersion);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public void Load_ExpiredEmbeddedCatalogWithoutAuthenticatedStateFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            generatedAt: now.AddDays(-30),
            expiresAt: now.AddMinutes(-1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = new HandleScopeCompatibilityCatalogService(
            new CountingHandler(),
            Path.Combine(_root, "expired-bootstrap.json"),
            HandleScopeCompatibilityCatalogPolicy.Serialize(expired),
            key.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeCatalogException>(() => service.Load());
    }

    [Fact]
    public void Load_InvalidCacheFailsClosedWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var cachePath = Path.Combine(_root, "catalog.json");
        File.WriteAllText(cachePath, "{ not valid catalog json }");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new CountingHandler();
        using var service = new HandleScopeCompatibilityCatalogService(
            handler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog()),
            key.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeCatalogException>(() => service.Load());
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Load_NewerSignedCacheWinsWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-2),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cached = HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                sequence: 2,
                generatedAt: now.AddMinutes(-1),
                expiresAt: now.AddDays(30)),
            key);
        var cachePath = Path.Combine(_root, "catalog.json");
        File.WriteAllText(
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(cached));
        using var handler = new CountingHandler();
        using var service = new HandleScopeCompatibilityCatalogService(
            handler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var loaded = service.Load();

        Assert.Equal(2, loaded.Catalog.Sequence);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(File.Exists(cachePath + ".floor"));
    }

    [Fact]
    public void GetCompatibleReleases_FiltersAllContractBoundariesAndSortsNewestFirst()
    {
        var required = new[]
        {
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        };
        var allCapabilities = new[]
        {
            "handlescope.http.v2",
            required[0],
            required[1]
        };
        var releases = new[]
        {
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.0",
                minimumSessionDockVersion: "2.7.0",
                maximumSessionDockVersionExclusive: "2.9.0"),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.1",
                status: "revoked"),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.2",
                minimumSessionDockVersion: "2.9.0"),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.3",
                maximumSessionDockVersionExclusive: "2.8.0"),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.4",
                apiContracts: ["v3"]),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.5",
                capabilities:
                [
                    "handlescope.http.v1",
                    "handlescope.plan.single-use.v1"
                ]),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.6",
                apiContracts: ["v2"],
                capabilities: allCapabilities)
        };
        var catalog = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                    releases,
                    recommendedVersion: "0.1.6")),
            HandleScopeCompatibilityCatalogTestData.TestNow);

        var compatible =
            HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                catalog,
                new Version(2, 8, 0),
                new HashSet<string>(["v1", "v2"], StringComparer.Ordinal),
                new HashSet<string>(required, StringComparer.Ordinal));

        Assert.Equal(["0.1.6", "0.1.0"], compatible.Select(x => x.Version));
    }

    [Fact]
    public void GetCompatibleReleases_RejectsDictionaryKeyVersionMismatch()
    {
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease();
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog();
        var verified = new VerifiedHandleScopeCompatibilityCatalog(
            catalog,
            new Version(2, 8, 0),
            new Version(0, 1, 4),
            HandleScopeCompatibilityCatalogTestData.TestNow.AddMinutes(-1),
            HandleScopeCompatibilityCatalogTestData.TestNow.AddDays(30),
            new Dictionary<Version, HandleScopeCompatibleRelease>
            {
                [new Version(9, 9, 9)] = release
            });

        var compatible =
            HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                verified,
                new Version(2, 8, 0),
                new HashSet<string>(["v1"], StringComparer.Ordinal),
                new HashSet<string>(
                    [
                        "handlescope.plan.single-use.v1",
                        "handlescope.policy.roblox-singleton-event.v1"
                    ],
                    StringComparer.Ordinal));

        Assert.Empty(compatible);
    }

    [Fact]
    public void GetCompatibleReleases_RejectsMismatchedApiCapabilityPair()
    {
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            apiContracts: ["v2"],
            capabilities:
            [
                "handlescope.http.v1",
                "handlescope.plan.single-use.v1",
                "handlescope.policy.roblox-singleton-event.v1"
            ]);
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            [release]);
        var verified = new VerifiedHandleScopeCompatibilityCatalog(
            catalog,
            new Version(2, 8, 0),
            new Version(0, 1, 4),
            HandleScopeCompatibilityCatalogTestData.TestNow.AddMinutes(-1),
            HandleScopeCompatibilityCatalogTestData.TestNow.AddDays(30),
            new Dictionary<Version, HandleScopeCompatibleRelease>
            {
                [new Version(0, 1, 4)] = release
            });

        var compatible =
            HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                verified,
                new Version(2, 8, 0),
                new HashSet<string>(["v1", "v2"], StringComparer.Ordinal),
                new HashSet<string>(
                    [
                        "handlescope.plan.single-use.v1",
                        "handlescope.policy.roblox-singleton-event.v1"
                    ],
                    StringComparer.Ordinal));

        Assert.Empty(compatible);
    }

    [Fact]
    public void GetCompatibleReleases_RevocationDominatesDuplicateRuntimeIdentity()
    {
        var sharedHash = new string('d', 64);
        var revoked = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            status: "revoked",
            executableSize: 500,
            executableSha256: sharedHash);
        var supported = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            executableSize: 500,
            executableSha256: sharedHash);
        var recommended = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.2");
        var catalog = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                    [revoked, supported, recommended],
                    recommendedVersion: "0.2.2")),
            HandleScopeCompatibilityCatalogTestData.TestNow);

        var compatible =
            HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                catalog,
                new Version(2, 8, 0),
                new HashSet<string>(["v1", "v2"], StringComparer.Ordinal),
                HandleScopeCompatibilityRequirements.RequiredCapabilities);

        Assert.Equal(["0.2.2"], compatible.Select(release => release.Version));
        Assert.True(
            HandleScopeCompatibilityCatalogService.IsRuntimeIdentityRevoked(
                catalog,
                supported));
    }

    [Fact]
    public async Task Refresh_StickyRevocationFloorBlocksCorruptCacheFallback()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-3),
            expiresAt: now.AddDays(30));
        var revokedLegacy =
            HandleScopeCompatibilityCatalogTestData.CreateRelease() with
            {
                Status = "revoked"
            };
        var modern = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var remote = HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [revokedLegacy, modern],
                recommendedVersion: "0.2.1",
                sequence: 2,
                generatedAt: now.AddMinutes(-2),
                expiresAt: now.AddDays(30)),
            key);
        var cachePath = Path.Combine(_root, "sticky-catalog.json");
        using (var service = new HandleScopeCompatibilityCatalogService(
                   new CatalogHandler(remote),
                   cachePath,
                   HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
                   key.ExportSubjectPublicKeyInfoPem()))
        {
            var refreshed = await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, refreshed.Catalog.Sequence);
        }

        var floorPath = cachePath + ".floor";
        Assert.True(File.Exists(floorPath));
        File.WriteAllText(cachePath, "{ corrupt cache }");
        using var fallbackHandler = new CountingHandler();
        using var fallback = new HandleScopeCompatibilityCatalogService(
            fallbackHandler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var recovered = fallback.Load();

        Assert.Equal(2, recovered.Catalog.Sequence);
        Assert.Equal(
            "revoked",
            recovered.Releases[new Version(0, 1, 4)].Status);
        Assert.Equal(0, fallbackHandler.RequestCount);
        var historicalFloor = HandleScopeCompatibilityCatalogPolicy.Verify(
            File.ReadAllText(floorPath),
            key.ExportSubjectPublicKeyInfoPem(),
            DateTimeOffset.Parse(
                remote.GeneratedAt,
                CultureInfo.InvariantCulture));
        Assert.Equal("revoked", historicalFloor.Releases[new Version(0, 1, 4)].Status);
    }

    [Fact]
    public void Load_CurrentFloorRecoversFromOlderValidCache()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-4),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cached = SignCatalog(key, 2, now.AddMinutes(-3));
        var floor = SignCatalog(key, 3, now.AddMinutes(-2));
        var cachePath = Path.Combine(_root, "partial-commit-catalog.json");
        File.WriteAllText(
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(cached));
        File.WriteAllText(
            cachePath + ".floor",
            HandleScopeCompatibilityCatalogPolicy.Serialize(floor));
        using var handler = new CountingHandler();
        using var service = new HandleScopeCompatibilityCatalogService(
            handler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var loaded = service.Load();

        Assert.Equal(3, loaded.Catalog.Sequence);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_InaccessibleCacheOrFloorFailsClosed(bool lockFloor)
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-3),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = SignCatalog(key, 2, now.AddMinutes(-2));
        var cachePath = Path.Combine(
            _root,
            lockFloor ? "locked-floor.json" : "locked-cache.json");
        var lockedPath = lockFloor ? cachePath + ".floor" : cachePath;
        File.WriteAllText(
            lockedPath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(current));
        using var inaccessible = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var service = new HandleScopeCompatibilityCatalogService(
            new CountingHandler(),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeCatalogException>(() => service.Load());
    }

    [Fact]
    public async Task Load_RetriesInterprocessLockContentionWithinBound()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            generatedAt: now.AddMinutes(-2),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cachePath = Path.Combine(_root, "contended-lock.json");
        using var externalLock = new FileStream(
            cachePath + ".floor.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var service = new HandleScopeCompatibilityCatalogService(
            new CountingHandler(),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var load = Task.Run(
            service.Load,
            TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);
        Assert.False(load.IsCompleted);
        externalLock.Dispose();

        var loaded = await load.WaitAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, loaded.Catalog.Sequence);
    }

    [Fact]
    public void Load_ExpiredAuthenticatedFloorFailsClosed()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddDays(-40),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expired = HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                sequence: 2,
                generatedAt: now.AddDays(-30),
                expiresAt: now.AddDays(-1)),
            key);
        var cachePath = Path.Combine(_root, "expired-catalog.json");
        var signedJson = HandleScopeCompatibilityCatalogPolicy.Serialize(expired);
        File.WriteAllText(cachePath, signedJson);
        File.WriteAllText(cachePath + ".floor", signedJson);
        using var service = new HandleScopeCompatibilityCatalogService(
            new CountingHandler(),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeCatalogException>(() => service.Load());
    }

    [Fact]
    public void Load_CorruptAuthenticatedFloorFailsClosedEvenWithValidCache()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-3),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = SignCatalog(key, 2, now.AddMinutes(-2));
        var cachePath = Path.Combine(_root, "corrupt-floor-catalog.json");
        File.WriteAllText(
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(current));
        File.WriteAllText(cachePath + ".floor", "{ corrupt floor }");
        using var service = new HandleScopeCompatibilityCatalogService(
            new CountingHandler(),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeCatalogException>(() => service.Load());
    }

    [Fact]
    public async Task AcquireCurrentCatalog_ReturnsLatestFloorUnderLease()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-3),
            expiresAt: now.AddDays(30));
        var expected = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            now);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var current = SignCatalog(key, 2, now.AddMinutes(-2));
        var cachePath = Path.Combine(_root, "leased-catalog.json");
        using var service = new HandleScopeCompatibilityCatalogService(
            new CatalogHandler(current),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());
        _ = await service.RefreshAsync(TestContext.Current.CancellationToken);

        using var lease = service.AcquireCurrentCatalog(
            expected,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, lease.Catalog.Catalog.Sequence);
        var expiredExpected = expected with { ExpiresAt = now.AddMinutes(-1) };
        Assert.Throws<HandleScopeCatalogException>(() =>
            service.AcquireCurrentCatalog(
                expiredExpected,
                TestContext.Current.CancellationToken));

        var expiringExpected = expected with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(250)
        };
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedAcquire = Task.Run(
            () =>
            {
                started.TrySetResult();
                using var ignored = service.AcquireCurrentCatalog(
                    expiringExpected,
                    TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(500),
            TestContext.Current.CancellationToken);
        lease.Dispose();
        await Assert.ThrowsAsync<HandleScopeCatalogException>(
            () => delayedAcquire);
    }

    [Fact]
    public async Task Refresh_CorruptCacheRecoversOnlyAtOrAboveSignedFloor()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-4),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var olderReplay = SignCatalog(key, 1, now.AddMinutes(-4));
        var second = SignCatalog(key, 2, now.AddMinutes(-3));
        var third = SignCatalog(key, 3, now.AddMinutes(-2));
        var cachePath = Path.Combine(_root, "recovery-catalog.json");
        using (var initial = new HandleScopeCompatibilityCatalogService(
                   new CatalogHandler(second),
                   cachePath,
                   HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
                   key.ExportSubjectPublicKeyInfoPem()))
        {
            _ = await initial.RefreshAsync(CancellationToken.None);
        }
        File.WriteAllText(cachePath, "{ corrupt cache }");

        using (var rejectedReplay = new HandleScopeCompatibilityCatalogService(
                   new CatalogHandler(olderReplay),
                   cachePath,
                   HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
                   key.ExportSubjectPublicKeyInfoPem()))
        {
            await Assert.ThrowsAsync<HandleScopeCatalogException>(async () =>
                await rejectedReplay.RefreshAsync(CancellationToken.None));
        }

        using (var replay = new HandleScopeCompatibilityCatalogService(
                   new CatalogHandler(second),
                   cachePath,
                   HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
                   key.ExportSubjectPublicKeyInfoPem()))
        {
            var recovered = await replay.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, recovered.Catalog.Sequence);
        }
        File.WriteAllText(cachePath, "{ corrupt again }");
        using var advance = new HandleScopeCompatibilityCatalogService(
            new CatalogHandler(third),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());
        var advanced = await advance.RefreshAsync(CancellationToken.None);

        Assert.Equal(3, advanced.Catalog.Sequence);
    }

    [Fact]
    public async Task Refresh_ConcurrentStaleWriterCannotOverwriteNewerFloor()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-4),
            expiresAt: now.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var olderHandler = new DeferredCatalogHandler(
            SignCatalog(key, 2, now.AddMinutes(-3)));
        var newerHandler = new DeferredCatalogHandler(
            SignCatalog(key, 3, now.AddMinutes(-2)));
        var cachePath = Path.Combine(_root, "concurrent-catalog.json");
        using var older = new HandleScopeCompatibilityCatalogService(
            olderHandler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());
        using var newer = new HandleScopeCompatibilityCatalogService(
            newerHandler,
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var olderRefresh = older.RefreshAsync(CancellationToken.None);
        await olderHandler.Started;
        var newerRefresh = newer.RefreshAsync(CancellationToken.None);
        await newerHandler.Started;
        newerHandler.Release();
        Assert.Equal(3, (await newerRefresh).Catalog.Sequence);
        olderHandler.Release();
        await Assert.ThrowsAsync<HandleScopeCatalogException>(async () =>
            await olderRefresh);

        var persisted = HandleScopeCompatibilityCatalogPolicy.Deserialize(
            File.ReadAllText(cachePath));
        Assert.Equal(3, persisted.Sequence);
        var floor = HandleScopeCompatibilityCatalogPolicy.Deserialize(
            File.ReadAllText(cachePath + ".floor"));
        Assert.Equal(3, floor.Sequence);
    }

    [Fact]
    public async Task Refresh_FutureOnlyCatalogAdvancesTrustButHasNoUsableRelease()
    {
        Directory.CreateDirectory(_root);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            sequence: 1,
            generatedAt: now.AddMinutes(-3),
            expiresAt: now.AddDays(30));
        var futureRelease = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            minimumSessionDockVersion: "2.9.0");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var future = HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [futureRelease],
                recommendedVersion: "0.2.1",
                sequence: 2,
                generatedAt: now.AddMinutes(-2),
                expiresAt: now.AddDays(30),
                sessionDockVersion: "2.9.0"),
            key);
        var cachePath = Path.Combine(_root, "future-catalog.json");
        using var service = new HandleScopeCompatibilityCatalogService(
            new CatalogHandler(future),
            cachePath,
            HandleScopeCompatibilityCatalogPolicy.Serialize(bootstrap),
            key.ExportSubjectPublicKeyInfoPem());

        var refreshed = await service.RefreshAsync(CancellationToken.None);
        var compatible =
            HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                refreshed,
                new Version(2, 8, 0),
                new HashSet<string>(["v1", "v2"], StringComparer.Ordinal),
                HandleScopeCompatibilityRequirements.RequiredCapabilities);

        Assert.Equal(2, refreshed.Catalog.Sequence);
        Assert.Empty(compatible);
        Assert.True(File.Exists(cachePath));
        Assert.True(File.Exists(cachePath + ".floor"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
        }
    }

    private static HandleScopeCompatibilityCatalog SignCatalog(
        ECDsa key,
        long sequence,
        DateTimeOffset generatedAt) =>
        HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                sequence: sequence,
                generatedAt: generatedAt,
                expiresAt: generatedAt.AddDays(30)),
            key);

    private sealed class CatalogHandler(
        HandleScopeCompatibilityCatalog catalog) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateResponse(catalog));
    }

    private sealed class DeferredCatalogHandler(
        HandleScopeCompatibilityCatalog catalog) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateResponse(catalog);
        }
    }

    private static HttpResponseMessage CreateResponse(
        HandleScopeCompatibilityCatalog catalog) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                HandleScopeCompatibilityCatalogPolicy.Serialize(catalog),
                Encoding.UTF8,
                "application/json")
        };
}
