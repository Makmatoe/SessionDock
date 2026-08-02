namespace SessionDock.ReleaseTrust;

public sealed record HandleScopeCompatibilityCatalog(
    int SchemaVersion,
    string Product,
    string Repository,
    string KeyId,
    long Sequence,
    string GeneratedAt,
    string ExpiresAt,
    string SessionDockVersion,
    string RecommendedVersion,
    IReadOnlyList<HandleScopeCompatibleRelease> Releases,
    string Signature);

public sealed record HandleScopeCompatibleRelease(
    string Version,
    string Tag,
    string Status,
    string MinimumSessionDockVersion,
    string? MaximumSessionDockVersionExclusive,
    IReadOnlyList<string> ApiContracts,
    IReadOnlyList<string> Capabilities,
    HandleScopeCatalogAsset Package,
    HandleScopeCatalogAsset Checksums,
    HandleScopeCatalogAsset? Manifest,
    HandleScopeCatalogRuntime ApiExecutable,
    string ContractUrl);

public sealed record HandleScopeCatalogAsset(
    string Name,
    long Size,
    string Sha256);

public sealed record HandleScopeCatalogRuntime(
    string Path,
    long Size,
    string Sha256);

public sealed record VerifiedHandleScopeCompatibilityCatalog(
    HandleScopeCompatibilityCatalog Catalog,
    Version SessionDockVersion,
    Version RecommendedVersion,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<Version, HandleScopeCompatibleRelease> Releases);
