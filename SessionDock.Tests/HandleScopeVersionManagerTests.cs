using System.Net;
using System.Security.Cryptography;
using SessionDock.ReleaseTrust;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeVersionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.VersionManager.{Guid.NewGuid():N}");

    [Fact]
    public void RuntimeRequirementsUseReleasePolicyContractDefinitions()
    {
        Assert.Same(
            HandleScopeCompatibilityCatalogPolicy.SessionDockApiContracts,
            HandleScopeCompatibilityRequirements.CompiledApiContracts);
        Assert.Same(
            HandleScopeCompatibilityCatalogPolicy.SessionDockRequiredCapabilities,
            HandleScopeCompatibilityRequirements.RequiredCapabilities);
    }

    [Fact]
    public void Load_CatalogApiChangeInvalidatesPersistedChoiceWithoutRewritingIt()
    {
        Directory.CreateDirectory(_root);
        var selectionPath = Path.Combine(_root, "selection.json");
        var selectionStore = new HandleScopeSelectionStore(selectionPath);
        var persisted = new HandleScopeSelection(
            HandleScopeVersionSelectionMode.Automatic,
            null,
            "v2");
        selectionStore.Write(persisted);
        var v2Release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            apiContracts: ["v2"]);
        var v1Release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.2",
            apiContracts: ["v1"]);

        using (var initial = CreateManager(
                   HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                       [v2Release],
                       recommendedVersion: "0.2.1"),
                   selectionPath,
                   "api-change-initial.json"))
        {
            var valid = initial.Load();
            Assert.True(valid.SelectionIsValid);
            Assert.Equal("0.2.1", valid.SelectedRelease?.Version);
        }

        using var changed = CreateManager(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [v1Release],
                recommendedVersion: "0.2.2",
                sequence: 2),
            selectionPath,
            "api-change-current.json");
        var invalid = changed.Load();

        Assert.False(invalid.SelectionIsValid);
        Assert.Null(invalid.SelectedRelease);
        Assert.Equal(persisted, invalid.Selection);
        Assert.Equal(persisted, selectionStore.Read().Selection);
    }

    [Fact]
    public void Load_RemovedExactRuntimeInvalidatesPersistedChoice()
    {
        Directory.CreateDirectory(_root);
        var selectionPath = Path.Combine(_root, "exact-selection.json");
        var selection = new HandleScopeSelection(
            HandleScopeVersionSelectionMode.Exact,
            new Version(0, 2, 1),
            "v2");
        new HandleScopeSelectionStore(selectionPath).Write(selection);
        var replacement = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.2",
            apiContracts: ["v2"]);
        using var manager = CreateManager(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [replacement],
                recommendedVersion: "0.2.2"),
            selectionPath,
            "exact-removed.json");

        var snapshot = manager.Load();

        Assert.False(snapshot.SelectionIsValid);
        Assert.Null(snapshot.SelectedRelease);
        Assert.Equal(selection, snapshot.Selection);
    }

    [Fact]
    public void Load_KeepInstalledWithoutInstalledRuntimeIsInvalid()
    {
        Directory.CreateDirectory(_root);
        var selectionPath = Path.Combine(_root, "missing-installed-selection.json");
        var selection = new HandleScopeSelection(
            HandleScopeVersionSelectionMode.KeepInstalled,
            null,
            null);
        new HandleScopeSelectionStore(selectionPath).Write(selection);
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1");
        using var manager = CreateManager(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [release],
                recommendedVersion: "0.2.1"),
            selectionPath,
            "missing-installed.json");

        var snapshot = manager.Load();

        Assert.False(snapshot.SelectionIsValid);
        Assert.Null(snapshot.SelectedRelease);
        Assert.Equal(selection, snapshot.Selection);
    }

    [Fact]
    public void SaveSelection_RejectsApiUnsupportedByResolvedRelease()
    {
        Directory.CreateDirectory(_root);
        var selectionPath = Path.Combine(_root, "rejected-selection.json");
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            apiContracts: ["v1"]);
        var catalog = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                    [release],
                    recommendedVersion: "0.2.1")),
            HandleScopeCompatibilityCatalogTestData.TestNow);
        using var manager = CreateManager(
            catalog.Catalog,
            selectionPath,
            "save-rejected.json");
        var selection = new HandleScopeSelection(
            HandleScopeVersionSelectionMode.Automatic,
            null,
            "v2");

        Assert.Throws<ArgumentException>(() =>
            manager.SaveSelection(selection, catalog));
        Assert.False(File.Exists(selectionPath));
    }

    [Fact]
    public void Load_FutureOnlyCatalogProducesEmptyFailClosedSnapshot()
    {
        var future = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            minimumSessionDockVersion: "3.0.0");
        using var manager = CreateManager(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [future],
                recommendedVersion: "0.2.1",
                sessionDockVersion: "3.0.0"),
            Path.Combine(_root, "future-selection.json"),
            "future-only.json");

        var snapshot = manager.Load();

        Assert.Empty(snapshot.CompatibleReleases);
        Assert.False(snapshot.SelectionIsValid);
        Assert.Null(snapshot.SelectedRelease);
        Assert.Equal("0.2.1", snapshot.RecommendedRelease.Version);
    }

    [Fact]
    public void Load_OlderSessionDockFallsBackToNewestCompatibleRecommendation()
    {
        var legacy = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.2",
            minimumSessionDockVersion: "2.8.0");
        var native = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.3.0",
            minimumSessionDockVersion: "2.9.0");
        native = native with
        {
            Capabilities = native.Capabilities
                .Append(HandleScopeCatalogInstallPolicy.NativeSetupCapability)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            [legacy, native],
            recommendedVersion: "0.3.0",
            sessionDockVersion: "2.9.0");
        var selectionPath = Path.Combine(_root, "older-selection.json");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = new HandleScopeCompatibilityCatalogService(
            new NoNetworkHandler(),
            Path.Combine(_root, "older-catalog.json"),
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog),
            key.ExportSubjectPublicKeyInfoPem());
        using var manager = new HandleScopeVersionManager(
            service,
            new HandleScopeSelectionStore(selectionPath),
            new MissingRuntimeResolver(),
            Path.Combine(_root, "missing.exe"),
            new Version(2, 8, 0));

        var snapshot = manager.Load();

        Assert.Equal(["0.2.2"], snapshot.CompatibleReleases
            .Select(release => release.Version));
        Assert.Equal("0.2.2", snapshot.RecommendedRelease.Version);
        Assert.Equal("0.2.2", snapshot.SelectedRelease?.Version);
        Assert.True(snapshot.SelectionIsValid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private HandleScopeVersionManager CreateManager(
        HandleScopeCompatibilityCatalog catalog,
        string selectionPath,
        string cacheName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service = new HandleScopeCompatibilityCatalogService(
            new NoNetworkHandler(),
            Path.Combine(_root, cacheName),
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog),
            key.ExportSubjectPublicKeyInfoPem());
        return new(
            service,
            new HandleScopeSelectionStore(selectionPath),
            new MissingRuntimeResolver(),
            Path.Combine(_root, "HandleScope.Api.exe"));
    }

    private sealed class MissingRuntimeResolver : IHandleScopeRuntimeIdentityResolver
    {
        public bool IsAuthorized(string executablePath) => false;

        public bool TryIdentify(
            string executablePath,
            out HandleScopeRuntimeIdentity? identity)
        {
            identity = null;
            return false;
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
    }
}
