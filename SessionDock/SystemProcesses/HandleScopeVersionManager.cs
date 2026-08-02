using System.IO;
using SessionDock.ReleaseTrust;

namespace SessionDock.SystemProcesses;

internal static class HandleScopeCompatibilityRequirements
{
    internal static IReadOnlySet<string> CompiledApiContracts { get; } =
        HandleScopeCompatibilityCatalogPolicy.SessionDockApiContracts;

    internal static IReadOnlySet<string> RequiredCapabilities { get; } =
        HandleScopeCompatibilityCatalogPolicy.SessionDockRequiredCapabilities;

    internal static Version SessionDockVersion
    {
        get
        {
            var assemblyVersion = typeof(HandleScopeCompatibilityRequirements)
                .Assembly
                .GetName()
                .Version ?? new Version(0, 0, 0);
            return new Version(
                assemblyVersion.Major,
                assemblyVersion.Minor,
                Math.Max(0, assemblyVersion.Build));
        }
    }
}

internal sealed record HandleScopeVersionSnapshot(
    VerifiedHandleScopeCompatibilityCatalog Catalog,
    IReadOnlyList<HandleScopeCompatibleRelease> CompatibleReleases,
    HandleScopeCompatibleRelease RecommendedRelease,
    HandleScopeSelection Selection,
    bool SelectionIsValid,
    HandleScopeRuntimeIdentity? InstalledRuntime,
    HandleScopeCompatibleRelease? SelectedRelease);

internal sealed class HandleScopeVersionManager : IDisposable
{
    private readonly HandleScopeCompatibilityCatalogService _catalogService;
    private readonly HandleScopeSelectionStore _selectionStore;
    private readonly IHandleScopeRuntimeIdentityResolver _runtimeResolver;
    private readonly string _installedExecutablePath;
    private bool _disposed;

    internal HandleScopeVersionManager()
        : this(
            new HandleScopeCompatibilityCatalogService(),
            new HandleScopeSelectionStore(),
            new HandleScopeInstalledRuntimeVerifier(),
            HandleScopeProcessVerifier.GetExpectedExecutablePath(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData)))
    {
    }

    internal HandleScopeVersionManager(
        HandleScopeCompatibilityCatalogService catalogService,
        HandleScopeSelectionStore selectionStore,
        IHandleScopeRuntimeIdentityResolver runtimeResolver,
        string installedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(runtimeResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedExecutablePath);
        _catalogService = catalogService;
        _selectionStore = selectionStore;
        _runtimeResolver = runtimeResolver;
        _installedExecutablePath = Path.GetFullPath(installedExecutablePath);
    }

    internal HandleScopeVersionSnapshot Load() =>
        CreateSnapshot(_catalogService.Load());

    internal async Task<HandleScopeVersionSnapshot> RefreshAsync(
        CancellationToken cancellationToken) =>
        CreateSnapshot(await _catalogService.RefreshAsync(cancellationToken));

    internal HandleScopeVersionSnapshot SaveSelection(
        HandleScopeSelection selection,
        VerifiedHandleScopeCompatibilityCatalog catalog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        var snapshot = CreateSnapshot(
            catalog,
            selection,
            selectionIsValid: true);
        if (!snapshot.SelectionIsValid)
        {
            throw new ArgumentException(
                "The selected HandleScope release or exact API contract is not compatible with this catalog.",
                nameof(selection));
        }

        _selectionStore.Write(selection);
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _catalogService.Dispose();
    }

    private HandleScopeVersionSnapshot CreateSnapshot(
        VerifiedHandleScopeCompatibilityCatalog catalog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stored = _selectionStore.Read();
        return CreateSnapshot(catalog, stored.Selection, stored.IsValid);
    }

    private HandleScopeVersionSnapshot CreateSnapshot(
        VerifiedHandleScopeCompatibilityCatalog catalog,
        HandleScopeSelection selection,
        bool selectionIsValid)
    {
        var compatible = GetCompatibleReleases(catalog);
        var recommended = compatible.FirstOrDefault(release =>
                release.Version == catalog.Catalog.RecommendedVersion)
            ?? (compatible.Count > 0
                ? compatible[0]
                : catalog.Releases[catalog.RecommendedVersion]);
        HandleScopeRuntimeIdentity? installed = null;
        _ = _runtimeResolver.TryIdentify(_installedExecutablePath, out installed);
        var selected = compatible.Count == 0
            ? null
            : ResolveSelection(
                selection,
                compatible,
                recommended,
                installed);
        var semanticallyValid = selectionIsValid && compatible.Count > 0 &&
            IsSelectionSemanticallyValid(selection, selected, installed);
        if (!semanticallyValid)
            selected = null;
        return new(
            catalog,
            compatible,
            recommended,
            selection,
            semanticallyValid,
            installed,
            selected);
    }

    private static IReadOnlyList<HandleScopeCompatibleRelease>
        GetCompatibleReleases(VerifiedHandleScopeCompatibilityCatalog catalog) =>
        HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
            catalog,
            HandleScopeCompatibilityRequirements.SessionDockVersion,
            HandleScopeCompatibilityRequirements.CompiledApiContracts,
            HandleScopeCompatibilityRequirements.RequiredCapabilities);

    private static HandleScopeCompatibleRelease? ResolveSelection(
        HandleScopeSelection selection,
        IReadOnlyList<HandleScopeCompatibleRelease> compatible,
        HandleScopeCompatibleRelease recommended,
        HandleScopeRuntimeIdentity? installed)
    {
        if (selection.VersionMode == HandleScopeVersionSelectionMode.Exact)
        {
            return compatible.FirstOrDefault(release =>
                new Version(release.Version) == selection.ExactVersion);
        }
        if (selection.VersionMode == HandleScopeVersionSelectionMode.KeepInstalled)
        {
            return installed is null
                ? null
                : compatible.FirstOrDefault(release =>
                    new Version(release.Version) == installed.Version);
        }
        return recommended;
    }

    private static bool IsSelectionSemanticallyValid(
        HandleScopeSelection selection,
        HandleScopeCompatibleRelease? selected,
        HandleScopeRuntimeIdentity? installed)
    {
        if (selected is null)
            return false;
        if (selection.ExactApiContract is not { } apiContract)
            return true;
        if (!SupportsApiContract(selected, apiContract))
            return false;
        return selection.VersionMode !=
                HandleScopeVersionSelectionMode.KeepInstalled ||
            installed is not null &&
            installed.ApiContracts.Contains(
                apiContract,
                StringComparer.Ordinal) &&
            installed.Capabilities.Contains(
                $"handlescope.http.{apiContract}",
                StringComparer.Ordinal);
    }

    private static bool SupportsApiContract(
        HandleScopeCompatibleRelease release,
        string apiContract) =>
        release.ApiContracts.Contains(apiContract, StringComparer.Ordinal) &&
        release.Capabilities.Contains(
            $"handlescope.http.{apiContract}",
            StringComparer.Ordinal);
}
