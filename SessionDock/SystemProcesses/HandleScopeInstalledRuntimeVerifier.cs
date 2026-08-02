using System.IO;
using System.Security.Cryptography;
using SessionDock.ReleaseTrust;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeInstalledRuntimeVerifier
{
    bool IsAuthorized(string executablePath);
}

internal interface IHandleScopeRuntimeIdentityResolver :
    IHandleScopeInstalledRuntimeVerifier
{
    bool TryIdentify(
        string executablePath,
        out HandleScopeRuntimeIdentity? identity);
}

internal sealed class HandleScopeInstalledRuntimeVerifier :
    IHandleScopeRuntimeIdentityResolver
{
    internal const string SupportedVersion = "0.1.4";
    internal const long ExpectedExecutableSize = 50_275_061;
    internal const string ExpectedExecutableSha256 =
        "9925d032819750809d66f5e6f267606cb1d6ff419acadffc15d7bdbcb1402e95";

    private readonly IReadOnlyDictionary<long, IReadOnlyList<RuntimeCandidate>>
        _candidatesBySize;
    private readonly Func<IEnumerable<RuntimeCandidate>>? _reloadCandidates;

    internal HandleScopeInstalledRuntimeVerifier()
    {
        _reloadCandidates = LoadCatalogCandidatesOrEmpty;
        _candidatesBySize = CreateCandidateIndex(
            _reloadCandidates(),
            allowEmpty: true);
    }

    internal HandleScopeInstalledRuntimeVerifier(
        HandleScopeCompatibilityCatalogService catalogService)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        _reloadCandidates = () =>
            LoadCatalogCandidatesOrEmpty(catalogService);
        _candidatesBySize = CreateCandidateIndex(
            _reloadCandidates(),
            allowEmpty: true);
    }

    internal HandleScopeInstalledRuntimeVerifier(
        long expectedSize,
        ReadOnlySpan<byte> expectedSha256)
        : this(
            [
                new RuntimeCandidate(
                    expectedSize,
                    ValidateHash(expectedSha256),
                    new HandleScopeRuntimeIdentity(
                        new Version(SupportedVersion),
                        $"v{SupportedVersion}",
                        ["v1"],
                        [
                            "handlescope.http.v1",
                            "handlescope.plan.single-use.v1",
                            "handlescope.policy.roblox-singleton-event.v1"
                        ]))
            ])
    {
    }

    internal HandleScopeInstalledRuntimeVerifier(
        IEnumerable<HandleScopeRuntimeIdentityCandidate> candidates)
        : this(candidates.Select(candidate => new RuntimeCandidate(
            candidate.Size,
            ValidateHash(candidate.Sha256),
            candidate.Identity)))
    {
    }

    private HandleScopeInstalledRuntimeVerifier(
        IEnumerable<RuntimeCandidate> candidates)
    {
        _candidatesBySize = CreateCandidateIndex(candidates, allowEmpty: true);
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<RuntimeCandidate>>
        CreateCandidateIndex(
            IEnumerable<RuntimeCandidate> candidates,
            bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var materialized = candidates.ToArray();
        if (!allowEmpty && materialized.Length == 0 ||
            materialized.Any(candidate => candidate.Size <= 0) ||
            materialized.Select(candidate => candidate.Identity.Version).Distinct().Count() !=
                materialized.Length ||
            materialized
                .Select(candidate => $"{candidate.Size}:{Convert.ToHexString(candidate.Sha256)}")
                .Distinct(StringComparer.Ordinal)
                .Count() != materialized.Length)
        {
            throw new ArgumentException(
                "HandleScope runtime versions and executable identities must be unique.",
                nameof(candidates));
        }
        return materialized
            .GroupBy(candidate => candidate.Size)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RuntimeCandidate>)group.ToArray());
    }

    public bool IsAuthorized(string executablePath) =>
        TryIdentify(executablePath, out _);

    public bool TryIdentify(
        string executablePath,
        out HandleScopeRuntimeIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        try
        {
            var candidatesBySize = _reloadCandidates is not null
                ? CreateCandidateIndex(
                    _reloadCandidates(),
                    allowEmpty: true)
                : _candidatesBySize;
            if (candidatesBySize.Count == 0)
                return false;
            using var stream = new FileStream(
                Path.GetFullPath(executablePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (!candidatesBySize.TryGetValue(stream.Length, out var candidates))
                return false;

            var actualHash = SHA256.HashData(stream);
            var match = candidates.SingleOrDefault(candidate =>
                CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    candidate.Sha256));
            identity = match?.Identity;
            return identity is not null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                CryptographicException or InvalidOperationException or
                SessionDock.ReleaseTrust.ReleaseTrustException or
                HandleScopeCatalogException)
        {
            return false;
        }
    }

    private static IEnumerable<RuntimeCandidate> LoadCatalogCandidates()
    {
        using var service = new HandleScopeCompatibilityCatalogService();
        return LoadCatalogCandidates(service);
    }

    private static IEnumerable<RuntimeCandidate> LoadCatalogCandidates(
        HandleScopeCompatibilityCatalogService service)
    {
        var catalog = service.Load();
        return HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                catalog,
                HandleScopeCompatibilityRequirements.SessionDockVersion,
                HandleScopeCompatibilityRequirements.CompiledApiContracts,
                HandleScopeCompatibilityRequirements.RequiredCapabilities)
            .Select(release => new RuntimeCandidate(
                release.ApiExecutable.Size,
                Convert.FromHexString(release.ApiExecutable.Sha256),
                new HandleScopeRuntimeIdentity(
                    new Version(release.Version),
                    release.Tag,
                    release.ApiContracts,
                    release.Capabilities)))
            .ToArray();
    }

    private static IEnumerable<RuntimeCandidate> LoadCatalogCandidatesOrEmpty()
    {
        try
        {
            return LoadCatalogCandidates();
        }
        catch (Exception exception) when (
            exception is HandleScopeCatalogException or ReleaseTrustException)
        {
            return [];
        }
    }

    private static IEnumerable<RuntimeCandidate> LoadCatalogCandidatesOrEmpty(
        HandleScopeCompatibilityCatalogService service)
    {
        try
        {
            return LoadCatalogCandidates(service);
        }
        catch (Exception exception) when (
            exception is HandleScopeCatalogException or ReleaseTrustException)
        {
            return [];
        }
    }

    private static byte[] ValidateHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                "A SHA-256 digest must contain exactly 32 bytes.",
                nameof(hash));
        }
        return hash.ToArray();
    }

    private sealed record RuntimeCandidate(
        long Size,
        byte[] Sha256,
        HandleScopeRuntimeIdentity Identity);
}

internal sealed record HandleScopeRuntimeIdentity(
    Version Version,
    string Tag,
    IReadOnlyList<string> ApiContracts,
    IReadOnlyList<string> Capabilities);

internal sealed record HandleScopeRuntimeIdentityCandidate(
    long Size,
    byte[] Sha256,
    HandleScopeRuntimeIdentity Identity);
