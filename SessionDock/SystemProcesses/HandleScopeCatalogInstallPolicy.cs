using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SessionDock.ReleaseTrust;

namespace SessionDock.SystemProcesses;

internal sealed record HandleScopeCatalogInstallSelection(
    HandleScopeReleaseIdentity Release,
    HandleScopeReleaseAsset? Manifest,
    HandleScopeCompatibleRelease? CatalogRelease,
    HandleScopeSetupAdapter SetupAdapter);

internal enum HandleScopeSetupAdapter
{
    LegacyPowerShellRemoteSigned,
    NativeV1
}

internal sealed record HandleScopeSetupExecutableIdentity(
    string Path,
    long Size,
    string Sha256);

internal static partial class HandleScopeCatalogInstallPolicy
{
    internal const string NativeSetupCapability =
        "handlescope.setup.native.v1";
    internal static readonly string LegacySetupRelativePath = Path.Combine(
        "api",
        "Install-HandleScopeApi.ps1");
    internal static readonly string NativeSetupRelativePath = Path.Combine(
        "api",
        "HandleScope.Setup.exe");

    private const string Repository = "Makmatoe/HandleScope";
    private const string Runtime = "win-x64";
    private const string DiscoveryApiVersion = "v1";
    private const string RequiredPolicy = "roblox-singleton-event-v1";
    private static readonly Version LegacyV014 = new(0, 1, 4);
    private static readonly Version LegacyV022 = new(0, 2, 2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static HandleScopeCatalogInstallSelection Select(
        HandleScopeCompatibleRelease selectedRelease,
        VerifiedHandleScopeCompatibilityCatalog verifiedCatalog,
        Version sessionDockVersion)
    {
        ArgumentNullException.ThrowIfNull(selectedRelease);
        ArgumentNullException.ThrowIfNull(verifiedCatalog);
        ArgumentNullException.ThrowIfNull(sessionDockVersion);

        if (!TryParseStableVersion(selectedRelease.Version, out var selectedVersion) ||
            !verifiedCatalog.Releases.TryGetValue(selectedVersion, out var authorized) ||
            !HandleScopeCompatibilityCatalogService.IsCompatible(
                selectedVersion,
                authorized,
                sessionDockVersion,
                HandleScopeCompatibilityRequirements.CompiledApiContracts,
                HandleScopeCompatibilityRequirements.RequiredCapabilities) ||
            verifiedCatalog.Releases.Values.Any(release =>
                release.Status == "revoked" &&
                HasSameRuntimeIdentity(
                    release.ApiExecutable,
                    authorized.ApiExecutable)) ||
            authorized.Package.Size > HandleScopeReleasePolicy.MaximumPackageBytes ||
            authorized.Checksums.Size > HandleScopeReleasePolicy.MaximumChecksumBytes ||
            authorized.Manifest?.Size > HandleScopeReleasePolicy.MaximumMetadataBytes)
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.ReleaseIntegrity,
                "The selected HandleScope release is not authorized for this SessionDock version.");
        }

        // The selected object supplies only its version. Every executable input
        // is rebuilt from the entry inside the already verified signed catalog.
        var release = new HandleScopeReleaseIdentity(
            authorized.Version,
            authorized.Tag,
            CreateAsset(authorized.Tag, authorized.Package),
            CreateAsset(authorized.Tag, authorized.Checksums));
        var manifest = authorized.Manifest is null
            ? null
            : CreateAsset(authorized.Tag, authorized.Manifest);
        var setupAdapter = SelectSetupAdapter(
            selectedVersion,
            authorized.Capabilities);
        if (setupAdapter == HandleScopeSetupAdapter.NativeV1 &&
            manifest is null)
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.ReleaseIntegrity,
                "The native HandleScope setup release has no catalog-authorized release manifest.");
        }
        return new(
            release,
            manifest,
            authorized,
            setupAdapter);
    }

    internal static string GetSetupRelativePath(
        HandleScopeSetupAdapter adapter) => adapter switch
        {
            HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned =>
                LegacySetupRelativePath,
            HandleScopeSetupAdapter.NativeV1 => NativeSetupRelativePath,
            _ => throw UnsupportedSetupAdapter()
        };

    internal static Uri CreateCanonicalAssetUri(string tag, string assetName)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length < 2 ||
            !Version.TryParse(tag.AsSpan(1), out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            tag != $"v{version.ToString(3)}" ||
            string.IsNullOrWhiteSpace(assetName) ||
            Path.GetFileName(assetName) != assetName ||
            assetName.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not ('.' or '_' or '-')))
        {
            throw new HandleScopeInstallException(
                "The HandleScope catalog contains a non-canonical release asset.");
        }

        return new Uri(
            $"https://github.com/{Repository}/releases/download/{tag}/{assetName}");
    }

    internal static void VerifyManifestChecksumEntry(
        ReadOnlySpan<byte> checksumContents,
        HandleScopeReleaseAsset manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string text;
        try
        {
            text = StrictUtf8.GetString(checksumContents);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope checksum file is not valid UTF-8.",
                exception);
        }

        byte[]? publishedHash = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            var match = ChecksumLinePattern().Match(line);
            if (!match.Success)
            {
                throw new HandleScopeInstallException(
                    "The HandleScope checksum file is malformed.");
            }
            if (!match.Groups["name"].Value.Equals(
                    manifest.Name,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (publishedHash is not null)
            {
                throw new HandleScopeInstallException(
                    "The HandleScope checksum file contains duplicate entries.");
            }
            publishedHash = Convert.FromHexString(match.Groups["hash"].Value);
        }

        if (publishedHash is null ||
            !CryptographicOperations.FixedTimeEquals(
                publishedHash,
                manifest.Sha256))
        {
            throw new HandleScopeInstallException(
                "The HandleScope release manifest does not match its published checksum.");
        }
    }

    internal static HandleScopeSetupExecutableIdentity? VerifyExternalManifest(
        ReadOnlySpan<byte> contents,
        HandleScopeCompatibleRelease catalogRelease,
        HandleScopeSetupAdapter setupAdapter)
    {
        ArgumentNullException.ThrowIfNull(catalogRelease);
        string json;
        try
        {
            json = StrictUtf8.GetString(contents);
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (!HasUniquePropertiesRecursively(document.RootElement))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope release manifest contains ambiguous fields.");
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidManifest(exception);
        }
        catch (JsonException exception)
        {
            throw InvalidManifest(exception);
        }

        ExternalReleaseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExternalReleaseManifest>(
                    json,
                    JsonOptions)
                ?? throw new JsonException("The release manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidManifest(exception);
        }

        var setupExecutable = ValidateSetupExecutable(
            manifest,
            setupAdapter);
        if (manifest.SchemaVersion != ExpectedManifestSchema(setupAdapter) ||
            manifest.Product != "HandleScope" ||
            manifest.Repository != Repository ||
            manifest.Version != catalogRelease.Version ||
            manifest.Tag != catalogRelease.Tag ||
            manifest.Runtime != Runtime ||
            manifest.DiscoveryApiVersion != DiscoveryApiVersion ||
            !ApiVersionPattern().IsMatch(
                manifest.PreferredApiVersion ?? string.Empty) ||
            manifest.SupportedApiVersions is null ||
            !manifest.SupportedApiVersions.Contains(
                manifest.PreferredApiVersion,
                StringComparer.Ordinal) ||
            !manifest.SupportedApiVersions.SequenceEqual(
                catalogRelease.ApiContracts,
                StringComparer.Ordinal) ||
            manifest.Policies is null ||
            !manifest.Policies.SequenceEqual(
                new[] { RequiredPolicy },
                StringComparer.Ordinal) ||
            manifest.Capabilities is null ||
            !manifest.Capabilities.SequenceEqual(
                catalogRelease.Capabilities,
                StringComparer.Ordinal) ||
            !SourceRevisionPattern().IsMatch(manifest.SourceRevision ?? string.Empty) ||
            !TryParseSourceTimestamp(manifest.SourceTimestamp) ||
            !MatchesAsset(manifest.Package, catalogRelease.Package) ||
            !IsValidSbom(manifest.Sbom, catalogRelease.Version) ||
            !MatchesRuntime(manifest.ApiExecutable, catalogRelease.ApiExecutable))
        {
            throw InvalidManifest();
        }
        return setupExecutable;
    }

    internal static bool HasSameAuthorizedIdentity(
        HandleScopeCatalogInstallSelection expected,
        HandleScopeCatalogInstallSelection current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);
        return expected.SetupAdapter == current.SetupAdapter &&
            SameReleaseIdentity(expected.Release, current.Release) &&
            SameAsset(expected.Manifest, current.Manifest) &&
            SameCatalogRelease(expected.CatalogRelease, current.CatalogRelease);
    }

    internal static void RefuseKnownDowngrade(
        HandleScopeCatalogInstallSelection selection,
        VerifiedHandleScopeCompatibilityCatalog catalog,
        string localAppDataRoot)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        if (!Version.TryParse(selection.Release.Version, out var selectedVersion) ||
            selection.CatalogRelease is not { } selectedSnapshotRelease ||
            !Version.TryParse(
                selectedSnapshotRelease.Version,
                out var selectedSnapshotVersion) ||
            selectedSnapshotVersion != selectedVersion ||
            selectedSnapshotRelease.Status != "supported" ||
            !catalog.Releases.TryGetValue(
                selectedVersion,
                out var selectedCatalogRelease) ||
            selectedCatalogRelease.Status != "supported" ||
            !HasSameRuntimeIdentity(
                selectedSnapshotRelease.ApiExecutable,
                selectedCatalogRelease.ApiExecutable) ||
            HandleScopeCompatibilityCatalogService.IsRuntimeIdentityRevoked(
                catalog,
                selectedCatalogRelease))
        {
            throw UnauthorizedSelectedRuntime();
        }

        var normalizedRoot = Path.GetFullPath(localAppDataRoot);
        var executablePath = HandleScopeProcessVerifier.GetExpectedExecutablePath(
            normalizedRoot);
        if (!File.Exists(executablePath))
            return;
        if (!HandleScopePathSecurity.IsSafeExistingPath(
                normalizedRoot,
                executablePath,
                targetMustExist: true))
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.LocalEnvironment,
                "The existing HandleScope installation path is not safe to replace.");
        }

        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var candidates = catalog.Releases
                .Where(pair => pair.Value.ApiExecutable.Size == stream.Length)
                .ToArray();
            if (candidates.Length == 0)
                throw UnknownInstalledRuntime();
            var actualHash = SHA256.HashData(stream);
            var matched = candidates
                .Where(pair => CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    Convert.FromHexString(pair.Value.ApiExecutable.Sha256)))
                .ToArray();
            if (matched.Length != 1)
                throw UnknownInstalledRuntime();
            var installed = matched[0];
            if (installed.Value.Status is not ("supported" or "revoked"))
                throw UnknownInstalledRuntime();
            if (installed.Value.Status == "revoked")
            {
                if (selectedVersion <= installed.Key ||
                    HasSameRuntimeIdentity(
                        selectedCatalogRelease.ApiExecutable,
                        installed.Value.ApiExecutable))
                {
                    throw new HandleScopeInstallException(
                        HandleScopeInstallFailureKind.Installer,
                        $"SessionDock refused to replace revoked HandleScope {installed.Key.ToString(3)} except with a strictly newer supported release that has a distinct authorized runtime.");
                }
                return;
            }
            if (installed.Key > selectedVersion)
            {
                throw new HandleScopeInstallException(
                    HandleScopeInstallFailureKind.Installer,
                    $"SessionDock refused to downgrade HandleScope from {installed.Key.ToString(3)} to {selectedVersion.ToString(3)}.");
            }
        }
        catch (HandleScopeInstallException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.LocalEnvironment,
                "The existing HandleScope installation could not be checked safely before replacement.",
                exception);
        }
    }

    private static HandleScopeInstallException UnknownInstalledRuntime() => new(
        HandleScopeInstallFailureKind.LocalEnvironment,
        "The existing HandleScope executable is not a known signed-catalog runtime, so SessionDock refused to replace it automatically. Remove or repair the existing per-user HandleScope installation manually, then retry.");

    private static HandleScopeInstallException UnauthorizedSelectedRuntime() => new(
        HandleScopeInstallFailureKind.ReleaseIntegrity,
        "The selected HandleScope runtime is revoked or is not an authorized supported catalog release.");

    private static HandleScopeSetupAdapter SelectSetupAdapter(
        Version version,
        IReadOnlyList<string> capabilities)
    {
        var setupCapabilities = capabilities
            .Where(capability => capability.StartsWith(
                "handlescope.setup.",
                StringComparison.Ordinal))
            .ToArray();
        if (version == LegacyV014 || version == LegacyV022)
        {
            if (setupCapabilities.Length == 0)
                return HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned;
            throw UnsupportedSetupAdapter();
        }

        if (setupCapabilities.Length == 1 &&
            setupCapabilities[0] == NativeSetupCapability)
        {
            return HandleScopeSetupAdapter.NativeV1;
        }
        throw UnsupportedSetupAdapter();
    }

    private static HandleScopeInstallException UnsupportedSetupAdapter() => new(
        HandleScopeInstallFailureKind.ReleaseIntegrity,
        "The selected HandleScope release does not use a setup adapter compiled into this SessionDock version.");

    private static bool SameReleaseIdentity(
        HandleScopeReleaseIdentity expected,
        HandleScopeReleaseIdentity current) =>
        expected.Version == current.Version &&
        expected.TagName == current.TagName &&
        SameAsset(expected.Package, current.Package) &&
        SameAsset(expected.Checksums, current.Checksums);

    private static bool SameAsset(
        HandleScopeReleaseAsset? expected,
        HandleScopeReleaseAsset? current)
    {
        if (expected is null || current is null)
            return expected is null && current is null;
        return expected.Name == current.Name &&
            expected.Size == current.Size &&
            expected.DownloadUri == current.DownloadUri &&
            CryptographicOperations.FixedTimeEquals(
                expected.Sha256,
                current.Sha256);
    }

    private static bool SameCatalogRelease(
        HandleScopeCompatibleRelease? expected,
        HandleScopeCompatibleRelease? current)
    {
        if (expected is null || current is null)
            return expected is null && current is null;
        return expected.Version == current.Version &&
            expected.Tag == current.Tag &&
            expected.Status == current.Status &&
            expected.MinimumSessionDockVersion ==
                current.MinimumSessionDockVersion &&
            expected.MaximumSessionDockVersionExclusive ==
                current.MaximumSessionDockVersionExclusive &&
            expected.ApiContracts.SequenceEqual(
                current.ApiContracts,
                StringComparer.Ordinal) &&
            expected.Capabilities.SequenceEqual(
                current.Capabilities,
                StringComparer.Ordinal) &&
            expected.Package == current.Package &&
            expected.Checksums == current.Checksums &&
            expected.Manifest == current.Manifest &&
            expected.ApiExecutable == current.ApiExecutable &&
            expected.ContractUrl == current.ContractUrl;
    }

    private static bool HasSameRuntimeIdentity(
        HandleScopeCatalogRuntime expected,
        HandleScopeCatalogRuntime current) =>
        expected.Size == current.Size &&
        expected.Sha256 == current.Sha256;

    private static HandleScopeReleaseAsset CreateAsset(
        string tag,
        HandleScopeCatalogAsset asset) => new(
            asset.Name,
            asset.Size,
            Convert.FromHexString(asset.Sha256),
            CreateCanonicalAssetUri(tag, asset.Name));

    private static bool TryParseStableVersion(string value, out Version version)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Version.TryParse(value, out var parsed) &&
            parsed.Build >= 0 && parsed.Revision < 0 &&
            parsed.ToString(3) == value)
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static bool HasUniquePropertiesRecursively(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    !HasUniquePropertiesRecursively(property.Value))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!HasUniquePropertiesRecursively(item))
                    return false;
            }
        }
        return true;
    }

    private static bool MatchesAsset(
        ManifestAsset? manifest,
        HandleScopeCatalogAsset catalog) =>
        manifest is not null &&
        manifest.Name == catalog.Name &&
        manifest.Size == catalog.Size &&
        manifest.Sha256 == catalog.Sha256;

    private static bool MatchesRuntime(
        ManifestRuntime? manifest,
        HandleScopeCatalogRuntime catalog) =>
        manifest is not null &&
        manifest.Path == catalog.Path &&
        manifest.Size == catalog.Size &&
        manifest.Sha256 == catalog.Sha256;

    private static int ExpectedManifestSchema(
        HandleScopeSetupAdapter setupAdapter) => setupAdapter switch
        {
            HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned => 1,
            HandleScopeSetupAdapter.NativeV1 => 2,
            _ => throw UnsupportedSetupAdapter()
        };

    private static HandleScopeSetupExecutableIdentity? ValidateSetupExecutable(
        ExternalReleaseManifest manifest,
        HandleScopeSetupAdapter setupAdapter)
    {
        if (setupAdapter ==
            HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned)
        {
            if (manifest.SetupExecutable is not null)
                throw InvalidManifest();
            return null;
        }
        if (setupAdapter != HandleScopeSetupAdapter.NativeV1 ||
            manifest.SetupExecutable is not { } setup ||
            setup.Path != "api/HandleScope.Setup.exe" ||
            setup.Size is <= 0 or >
                HandleScopeCompatibilityCatalogPolicy.MaximumExecutableBytes ||
            !Sha256Pattern().IsMatch(setup.Sha256 ?? string.Empty))
        {
            throw InvalidManifest();
        }
        return new(setup.Path, setup.Size, setup.Sha256!);
    }

    private static bool IsValidSbom(ManifestAsset? sbom, string version) =>
        sbom is not null &&
        sbom.Name == $"HandleScope-{version}-win-x64.spdx.json" &&
        Path.GetFileName(sbom.Name) == sbom.Name &&
        sbom.Size is > 0 and <= HandleScopeReleasePolicy.MaximumMetadataBytes &&
        Sha256Pattern().IsMatch(sbom.Sha256 ?? string.Empty);

    private static bool TryParseSourceTimestamp(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        !value.Any(char.IsControl) &&
        SourceTimestampPattern().IsMatch(value) &&
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);

    private static HandleScopeInstallException InvalidManifest(
        Exception? innerException = null) => innerException is null
        ? new HandleScopeInstallException(
            "The HandleScope release manifest does not match the signed compatibility catalog.")
        : new HandleScopeInstallException(
            "The HandleScope release manifest does not match the signed compatibility catalog.",
            innerException);

    [GeneratedRegex(
        @"^(?<hash>[0-9a-f]{64})  (?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,255})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceRevisionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(
        @"^v[1-9][0-9]{0,2}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SourceTimestampPattern();

    private sealed class ExternalReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string? Product { get; set; }
        public string? Repository { get; set; }
        public string? Version { get; set; }
        public string? Tag { get; set; }
        public string? Runtime { get; set; }
        public string? SourceRevision { get; set; }
        public string? SourceTimestamp { get; set; }
        public string? DiscoveryApiVersion { get; set; }
        public string[]? SupportedApiVersions { get; set; }
        public string? PreferredApiVersion { get; set; }
        public string[]? Policies { get; set; }
        public string[]? Capabilities { get; set; }
        public ManifestAsset? Package { get; set; }
        public ManifestAsset? Sbom { get; set; }
        public ManifestRuntime? ApiExecutable { get; set; }
        public ManifestRuntime? SetupExecutable { get; set; }
    }

    private sealed class ManifestAsset
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class ManifestRuntime
    {
        public string? Path { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
