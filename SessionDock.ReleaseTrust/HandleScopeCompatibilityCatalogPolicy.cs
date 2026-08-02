using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SessionDock.ReleaseTrust;

public static partial class HandleScopeCompatibilityCatalogPolicy
{
    public const int SchemaVersion = 1;
    public const string Product = "SessionDock.HandleScopeCompatibility";
    public const string Repository = "Makmatoe/SessionDock";
    public const string KeyId = ReleaseDescriptorPolicy.KeyId;
    public const string FileName = "sessiondock-handlescope-compatibility.json";
    public const int MaximumCatalogBytes = 256 * 1024;
    public const int MaximumReleases = 32;
    public const int MaximumApiContracts = 8;
    public const int MaximumCapabilities = 32;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    public const long MaximumSmallAssetBytes = 1024L * 1024;
    public const long MaximumExecutableBytes = 256L * 1024 * 1024;
    private static readonly Version LegacyManifestlessVersion = new(0, 1, 4);
    public static IReadOnlySet<string> SessionDockApiContracts { get; } =
        new[] { "v1", "v2" }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> SessionDockRequiredCapabilities { get; } =
        new[]
        {
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        WriteIndented = true
    };

    public static string Serialize(HandleScopeCompatibilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return JsonSerializer.Serialize(catalog, JsonOptions) + "\n";
    }

    public static HandleScopeCompatibilityCatalog Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumCatalogBytes)
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility catalog is too large.");
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<HandleScopeCompatibilityCatalog>(
                    json,
                    JsonOptions)
                ?? throw new ReleaseTrustException(
                    "The HandleScope compatibility catalog is empty.");
            ValidateReferenceShape(catalog);
            return catalog;
        }
        catch (JsonException exception)
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility catalog is malformed or contains unsupported fields.",
                exception);
        }
    }

    public static VerifiedHandleScopeCompatibilityCatalog Verify(
        string json,
        string publicKeyPem,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        var catalog = Deserialize(json);
        var verified = Validate(catalog, now ?? DateTimeOffset.UtcNow, remote: true);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(catalog.Signature);
        }
        catch (FormatException exception)
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility signature is malformed.",
                exception);
        }

        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (publicKey.KeySize != 256 ||
                signature.Length != 64 ||
                !publicKey.VerifyData(
                    CreateCanonicalPayload(catalog),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new ReleaseTrustException(
                    "The HandleScope compatibility catalog was not signed by the trusted SessionDock release key.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException)
        {
            throw new ReleaseTrustException(
                "The trusted SessionDock release key could not verify the HandleScope compatibility catalog.",
                exception);
        }

        return verified;
    }

    public static VerifiedHandleScopeCompatibilityCatalog VerifyEmbedded(
        string json,
        DateTimeOffset? now = null)
    {
        var catalog = Deserialize(json);
        if (!string.IsNullOrEmpty(catalog.Signature))
        {
            throw new ReleaseTrustException(
                "The embedded HandleScope catalog must use the application package as its trust boundary.");
        }

        return Validate(catalog, now ?? DateTimeOffset.UtcNow, remote: false);
    }

    public static byte[] CreateCanonicalPayload(
        HandleScopeCompatibilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ValidateReferenceShape(catalog);
        var lines = new List<string>
        {
            "sessiondock-handlescope-compatibility/v1",
            catalog.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            catalog.Product,
            catalog.Repository,
            catalog.KeyId,
            catalog.Sequence.ToString(CultureInfo.InvariantCulture),
            catalog.GeneratedAt,
            catalog.ExpiresAt,
            catalog.SessionDockVersion,
            catalog.RecommendedVersion,
            catalog.Releases.Count.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var release in catalog.Releases)
        {
            lines.Add(release.Version);
            lines.Add(release.Tag);
            lines.Add(release.Status);
            lines.Add(release.MinimumSessionDockVersion);
            lines.Add(release.MaximumSessionDockVersionExclusive ?? string.Empty);
            lines.Add(string.Join(',', release.ApiContracts));
            lines.Add(string.Join(',', release.Capabilities));
            AddAsset(lines, release.Package);
            AddAsset(lines, release.Checksums);
            if (release.Manifest is null)
            {
                lines.Add(string.Empty);
                lines.Add("0");
                lines.Add(string.Empty);
            }
            else
            {
                AddAsset(lines, release.Manifest);
            }
            lines.Add(release.ApiExecutable.Path);
            lines.Add(release.ApiExecutable.Size.ToString(CultureInfo.InvariantCulture));
            lines.Add(release.ApiExecutable.Sha256);
            lines.Add(release.ContractUrl);
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    private static VerifiedHandleScopeCompatibilityCatalog Validate(
        HandleScopeCompatibilityCatalog catalog,
        DateTimeOffset now,
        bool remote)
    {
        if (catalog.SchemaVersion != SchemaVersion ||
            catalog.Product != Product ||
            catalog.Repository != Repository ||
            catalog.KeyId != KeyId ||
            catalog.Sequence <= 0 ||
            catalog.Releases.Count is <= 0 or > MaximumReleases)
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility catalog identity is invalid.");
        }

        ValidateLine(catalog.Product, nameof(catalog.Product));
        ValidateLine(catalog.Repository, nameof(catalog.Repository));
        ValidateLine(catalog.KeyId, nameof(catalog.KeyId));
        var generatedAt = ParseUtc(catalog.GeneratedAt, "generation");
        var expiresAt = ParseUtc(catalog.ExpiresAt, "expiry");
        if (generatedAt > now.AddHours(24) ||
            expiresAt <= generatedAt ||
            expiresAt > generatedAt.AddDays(400) ||
            (remote && expiresAt <= now))
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility catalog validity window is invalid or expired.");
        }

        var sessionDockVersion = ParseStableVersion(
            catalog.SessionDockVersion,
            "SessionDock catalog");
        var recommendedVersion = ParseStableVersion(
            catalog.RecommendedVersion,
            "recommended HandleScope");
        var releases = new SortedDictionary<Version, HandleScopeCompatibleRelease>();
        var supportedRuntimeIdentities = new HashSet<string>(StringComparer.Ordinal);
        Version? previous = null;
        foreach (var release in catalog.Releases)
        {
            var version = ValidateRelease(release);
            if (previous is not null && version <= previous)
            {
                throw new ReleaseTrustException(
                    "HandleScope compatibility releases must be unique and sorted by version.");
            }
            previous = version;
            if (release.Status == "supported" &&
                !supportedRuntimeIdentities.Add(
                    release.ApiExecutable.Size.ToString(
                        CultureInfo.InvariantCulture) + ":" +
                    release.ApiExecutable.Sha256))
            {
                throw new ReleaseTrustException(
                    "Supported HandleScope releases must have unique API executable identities.");
            }
            releases.Add(version, release);
        }

        if (!releases.TryGetValue(recommendedVersion, out var recommended) ||
            recommended.Status != "supported")
        {
            throw new ReleaseTrustException(
                "The recommended HandleScope release is unavailable or revoked.");
        }
        var recommendedMinimum = ParseStableVersion(
            recommended.MinimumSessionDockVersion,
            "recommended minimum SessionDock");
        if (sessionDockVersion < recommendedMinimum ||
            recommended.MaximumSessionDockVersionExclusive is { } maximumText &&
            sessionDockVersion >= ParseStableVersion(
                maximumText,
                "recommended maximum SessionDock") ||
            !recommended.ApiContracts.Any(contract =>
                SessionDockApiContracts.Contains(contract) &&
                recommended.Capabilities.Contains(
                    $"handlescope.http.{contract}",
                    StringComparer.Ordinal)) ||
            !SessionDockRequiredCapabilities.All(
                recommended.Capabilities.Contains))
        {
            throw new ReleaseTrustException(
                "The recommended HandleScope release is not usable by its publishing SessionDock version.");
        }
        if (releases.Values.Any(release =>
                release.Status == "revoked" &&
                release.ApiExecutable.Size == recommended.ApiExecutable.Size &&
                release.ApiExecutable.Sha256 == recommended.ApiExecutable.Sha256))
        {
            throw new ReleaseTrustException(
                "The recommended HandleScope release uses a revoked executable identity.");
        }

        if (remote)
        {
            if (string.IsNullOrWhiteSpace(catalog.Signature) ||
                catalog.Signature.Length > 1024)
            {
                throw new ReleaseTrustException(
                    "The HandleScope compatibility signature is missing or invalid.");
            }
        }
        else if (!string.IsNullOrEmpty(catalog.Signature))
        {
            throw new ReleaseTrustException(
                "The embedded HandleScope catalog signature field is invalid.");
        }

        return new(
            catalog,
            sessionDockVersion,
            recommendedVersion,
            generatedAt,
            expiresAt,
            releases);
    }

    private static Version ValidateRelease(HandleScopeCompatibleRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var version = ParseStableVersion(release.Version, "HandleScope");
        if (release.Tag != $"v{release.Version}" ||
            release.Status is not ("supported" or "revoked"))
        {
            throw new ReleaseTrustException(
                "A HandleScope compatibility release has invalid version metadata.");
        }
        if (release.Manifest is null && version != LegacyManifestlessVersion)
        {
            throw new ReleaseTrustException(
                "Only the immutable HandleScope 0.1.4 legacy release may omit its external manifest.");
        }

        var minimum = ParseStableVersion(
            release.MinimumSessionDockVersion,
            "minimum SessionDock");
        if (release.MaximumSessionDockVersionExclusive is { } maximumText &&
            ParseStableVersion(maximumText, "maximum SessionDock") <= minimum)
        {
            throw new ReleaseTrustException(
                "A HandleScope compatibility release has an invalid SessionDock range.");
        }

        ValidateUniqueValues(
            release.ApiContracts,
            MaximumApiContracts,
            ApiContractPattern(),
            "API contracts");
        ValidateUniqueValues(
            release.Capabilities,
            MaximumCapabilities,
            CapabilityPattern(),
            "capabilities");
        var expectedHttpCapabilities = release.ApiContracts
            .Select(contract => $"handlescope.http.{contract}")
            .ToHashSet(StringComparer.Ordinal);
        var declaredHttpCapabilities = release.Capabilities
            .Where(capability => capability.StartsWith(
                "handlescope.http.",
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedHttpCapabilities.SetEquals(declaredHttpCapabilities))
        {
            throw new ReleaseTrustException(
                "A HandleScope compatibility release has inconsistent API contracts and HTTP capabilities.");
        }
        ValidateAsset(
            release.Package,
            $"HandleScope-{release.Version}-win-x64.zip",
            MaximumPackageBytes);
        ValidateAsset(release.Checksums, "SHA256SUMS.txt", MaximumSmallAssetBytes);
        if (release.Manifest is not null)
        {
            ValidateAsset(
                release.Manifest,
                $"HandleScope-{release.Version}-win-x64.release.json",
                MaximumSmallAssetBytes);
        }
        if (release.ApiExecutable.Path != "api/HandleScope.Api.exe" ||
            release.ApiExecutable.Size is <= 0 or > MaximumExecutableBytes)
        {
            throw new ReleaseTrustException(
                "A HandleScope API executable identity is invalid.");
        }
        _ = ParseSha256(release.ApiExecutable.Sha256, "API executable");

        var expectedContract =
            $"https://github.com/Makmatoe/HandleScope/blob/{release.Tag}/" +
            "docs/integrations/sessiondock.md";
        if (release.ContractUrl != expectedContract)
        {
            throw new ReleaseTrustException(
                "A HandleScope compatibility contract URL is not canonical.");
        }

        return version;
    }

    private static void ValidateAsset(
        HandleScopeCatalogAsset asset,
        string expectedName,
        long maximumSize)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Name != expectedName ||
            Path.GetFileName(asset.Name) != asset.Name ||
            asset.Size is <= 0 || asset.Size > maximumSize)
        {
            throw new ReleaseTrustException(
                $"The HandleScope asset identity for {expectedName} is invalid.");
        }
        _ = ParseSha256(asset.Sha256, expectedName);
    }

    private static void ValidateUniqueValues(
        IReadOnlyList<string> values,
        int maximum,
        Regex pattern,
        string description)
    {
        if (values.Count is <= 0 || values.Count > maximum)
        {
            throw new ReleaseTrustException(
                $"The HandleScope {description} list is outside its boundary.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var value in values)
        {
            if (!pattern.IsMatch(value) ||
                !seen.Add(value) ||
                previous is not null &&
                string.CompareOrdinal(previous, value) >= 0)
            {
                throw new ReleaseTrustException(
                    $"The HandleScope {description} list is invalid or unsorted.");
            }
            previous = value;
        }
    }

    private static Version ParseStableVersion(string value, string description)
    {
        ValidateLine(value, description);
        if (!Version.TryParse(value, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != value)
        {
            throw new ReleaseTrustException(
                $"The {description} version is invalid.");
        }
        return version;
    }

    private static DateTimeOffset ParseUtc(string value, string description)
    {
        ValidateLine(value, description);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw new ReleaseTrustException(
                $"The HandleScope catalog {description} time is invalid.");
        }
        return timestamp;
    }

    private static byte[] ParseSha256(string value, string description)
    {
        ValidateLine(value, description);
        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new ReleaseTrustException(
                $"The HandleScope {description} SHA-256 value is invalid.");
        }
        return Convert.FromHexString(value);
    }

    private static void ValidateLine(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            value.Contains('\r') || value.Contains('\n') ||
            value.Any(char.IsControl))
        {
            throw new ReleaseTrustException(
                $"The HandleScope catalog {description} field is invalid.");
        }
    }

    private static void ValidateReferenceShape(
        HandleScopeCompatibilityCatalog catalog)
    {
        if (catalog.Product is null ||
            catalog.Repository is null ||
            catalog.KeyId is null ||
            catalog.GeneratedAt is null ||
            catalog.ExpiresAt is null ||
            catalog.SessionDockVersion is null ||
            catalog.RecommendedVersion is null ||
            catalog.Releases is null ||
            catalog.Signature is null)
        {
            throw new ReleaseTrustException(
                "The HandleScope compatibility catalog contains a null required field.");
        }

        foreach (var release in catalog.Releases)
        {
            if (release is null ||
                release.Version is null ||
                release.Tag is null ||
                release.Status is null ||
                release.MinimumSessionDockVersion is null ||
                release.ApiContracts is null ||
                release.Capabilities is null ||
                release.Package is null ||
                release.Checksums is null ||
                release.ApiExecutable is null ||
                release.ContractUrl is null ||
                release.ApiContracts.Any(contract => contract is null) ||
                release.Capabilities.Any(capability => capability is null) ||
                HasNullRequiredField(release.Package) ||
                HasNullRequiredField(release.Checksums) ||
                release.Manifest is not null &&
                    HasNullRequiredField(release.Manifest) ||
                release.ApiExecutable.Path is null ||
                release.ApiExecutable.Sha256 is null)
            {
                throw new ReleaseTrustException(
                    "A HandleScope compatibility release contains a null required field.");
            }
        }
    }

    private static bool HasNullRequiredField(HandleScopeCatalogAsset asset) =>
        asset.Name is null || asset.Sha256 is null;

    private static void AddAsset(
        List<string> lines,
        HandleScopeCatalogAsset asset)
    {
        lines.Add(asset.Name);
        lines.Add(asset.Size.ToString(CultureInfo.InvariantCulture));
        lines.Add(asset.Sha256);
    }

    [GeneratedRegex(@"^v[1-9][0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiContractPattern();

    [GeneratedRegex(
        @"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+){1,7}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();
}
