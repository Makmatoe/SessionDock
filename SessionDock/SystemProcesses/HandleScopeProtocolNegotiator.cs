using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDock.SystemProcesses;

internal sealed record HandleScopeApiMetadata(
    Version ProductVersion,
    string DiscoveryApiVersion,
    IReadOnlyList<string> SupportedApiVersions,
    string PreferredApiVersion,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> Capabilities);

internal sealed record HandleScopeProtocolAdapter(
    string ApiVersion,
    string CloseEndpoint,
    string Capability,
    int PreferenceRank);

internal static partial class HandleScopeProtocolNegotiator
{
    internal const string MetadataEndpoint = "/v1/metadata";
    internal const string DiscoveryApiVersion = "v1";

    private const int MetadataSchemaVersion = 1;
    private const int MaximumApiVersions = 16;
    private const int MaximumPolicies = 16;
    private const int MaximumCapabilities = 64;
    private static readonly Version LegacyV014 = new(0, 1, 4);
    private static readonly IReadOnlySet<string> MetadataPropertyNames =
        new HashSet<string>(
            [
                "schemaVersion",
                "productVersion",
                "discoveryApiVersion",
                "supportedApiVersions",
                "preferredApiVersion",
                "policies",
                "capabilities"
            ],
            StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, HandleScopeProtocolAdapter>
        CompiledAdapters = new Dictionary<string, HandleScopeProtocolAdapter>(
            StringComparer.Ordinal)
        {
            ["v1"] = new(
                "v1",
                "/v1/handles/close",
                "handlescope.http.v1",
                PreferenceRank: 1),
            ["v2"] = new(
                "v2",
                "/v2/handles/close",
                "handlescope.http.v2",
                PreferenceRank: 2)
        };

    internal static HandleScopeProtocolAdapter LegacyV1Adapter =>
        CompiledAdapters["v1"];

    internal static bool TryParseMetadataDocument(
        JsonElement root,
        out HandleScopeApiMetadata? metadata)
    {
        metadata = null;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetPropertyCount() != 7 ||
            !HasExactUniqueProperties(root, MetadataPropertyNames) ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var parsedSchemaVersion) ||
            parsedSchemaVersion != MetadataSchemaVersion ||
            !TryGetRequiredString(root, "productVersion", out var productVersionText) ||
            !TryParseStableVersion(productVersionText, out var productVersion) ||
            !TryGetRequiredString(
                root,
                "discoveryApiVersion",
                out var discoveryApiVersion) ||
            discoveryApiVersion != DiscoveryApiVersion ||
            !TryGetSortedUniqueStrings(
                root,
                "supportedApiVersions",
                MaximumApiVersions,
                static value => ApiVersionPattern().IsMatch(value),
                out var supportedApiVersions) ||
            !TryGetRequiredString(
                root,
                "preferredApiVersion",
                out var preferredApiVersion) ||
            !ApiVersionPattern().IsMatch(preferredApiVersion) ||
            !supportedApiVersions.Contains(
                preferredApiVersion,
                StringComparer.Ordinal) ||
            !TryGetSortedUniqueStrings(
                root,
                "policies",
                MaximumPolicies,
                static value => PolicyPattern().IsMatch(value),
                out var policies) ||
            !TryGetSortedUniqueStrings(
                root,
                "capabilities",
                MaximumCapabilities,
                static value => CapabilityPattern().IsMatch(value),
                out var capabilities))
        {
            return false;
        }

        metadata = new(
            productVersion!,
            discoveryApiVersion,
            supportedApiVersions,
            preferredApiVersion,
            policies,
            capabilities);
        return true;
    }

    internal static bool TryNegotiate(
        HandleScopeApiMetadata metadata,
        HandleScopeRuntimeIdentity runtimeIdentity,
        HandleScopeSelection selection,
        out HandleScopeProtocolAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        ArgumentNullException.ThrowIfNull(selection);
        adapter = null;

        if (!MatchesSelectedVersion(selection, runtimeIdentity.Version) ||
            metadata.ProductVersion != runtimeIdentity.Version ||
            metadata.DiscoveryApiVersion != DiscoveryApiVersion ||
            !SetEquals(metadata.SupportedApiVersions, runtimeIdentity.ApiContracts) ||
            !SetEquals(metadata.Capabilities, runtimeIdentity.Capabilities) ||
            metadata.Policies.Count != 1 ||
            metadata.Policies[0] != HandleScopeApiBootstrapper.RequiredPolicy ||
            !HandleScopeCompatibilityRequirements.RequiredCapabilities.All(
                metadata.Capabilities.Contains))
        {
            return false;
        }

        var common = CompiledAdapters.Values
            .Where(candidate =>
                HandleScopeCompatibilityRequirements.CompiledApiContracts.Contains(
                    candidate.ApiVersion) &&
                metadata.SupportedApiVersions.Contains(
                    candidate.ApiVersion,
                    StringComparer.Ordinal) &&
                runtimeIdentity.ApiContracts.Contains(
                    candidate.ApiVersion,
                    StringComparer.Ordinal) &&
                metadata.Capabilities.Contains(
                    candidate.Capability,
                    StringComparer.Ordinal) &&
                runtimeIdentity.Capabilities.Contains(
                    candidate.Capability,
                    StringComparer.Ordinal))
            .ToArray();
        if (common.Length == 0)
            return false;

        if (selection.ExactApiContract is { } exactApiContract)
        {
            adapter = common.SingleOrDefault(candidate =>
                candidate.ApiVersion == exactApiContract);
            return adapter is not null;
        }

        adapter = common.SingleOrDefault(candidate =>
            candidate.ApiVersion == metadata.PreferredApiVersion) ??
            common.MaxBy(candidate => candidate.PreferenceRank);
        return adapter is not null;
    }

    internal static bool TryUseLegacyV014(
        HandleScopeRuntimeIdentity runtimeIdentity,
        HandleScopeSelection selection,
        out HandleScopeProtocolAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        ArgumentNullException.ThrowIfNull(selection);
        adapter = null;
        var legacy = LegacyV1Adapter;
        if (!MatchesSelectedVersion(selection, runtimeIdentity.Version) ||
            runtimeIdentity.Version != LegacyV014 ||
            selection.ExactApiContract is not (null or "v1") ||
            !runtimeIdentity.ApiContracts.Contains("v1", StringComparer.Ordinal) ||
            !runtimeIdentity.Capabilities.Contains(
                legacy.Capability,
                StringComparer.Ordinal) ||
            !HandleScopeCompatibilityRequirements.RequiredCapabilities.All(
                runtimeIdentity.Capabilities.Contains))
        {
            return false;
        }

        adapter = legacy;
        return true;
    }

    private static bool MatchesSelectedVersion(
        HandleScopeSelection selection,
        Version runtimeVersion) =>
        selection.VersionMode != HandleScopeVersionSelectionMode.Exact ||
        selection.ExactVersion == runtimeVersion;

    internal static bool IsCompiledAdapter(HandleScopeProtocolAdapter adapter) =>
        CompiledAdapters.TryGetValue(adapter.ApiVersion, out var expected) &&
        adapter == expected;

    private static bool HasExactUniqueProperties(
        JsonElement root,
        IReadOnlySet<string> expected)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                return false;
        }
        return actual.SetEquals(expected);
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return value.Length is > 0 and <= 256 &&
            !value.Any(char.IsControl);
    }

    private static bool TryGetSortedUniqueStrings(
        JsonElement root,
        string propertyName,
        int maximumCount,
        Func<string, bool> validator,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() is <= 0 ||
            property.GetArrayLength() > maximumCount)
        {
            return false;
        }

        var parsed = new List<string>(property.GetArrayLength());
        string? previous = null;
        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                return false;
            var value = element.GetString();
            if (string.IsNullOrEmpty(value) ||
                value.Length > 256 ||
                !validator(value) ||
                previous is not null &&
                string.CompareOrdinal(previous, value) >= 0)
            {
                return false;
            }
            parsed.Add(value);
            previous = value;
        }

        values = parsed;
        return true;
    }

    private static bool TryParseStableVersion(
        string value,
        out Version? version)
    {
        version = null;
        if (!StableVersionPattern().IsMatch(value) ||
            !Version.TryParse(value, out var parsed) ||
            parsed.Build < 0 || parsed.Revision >= 0 ||
            parsed.ToString(3) != value)
        {
            return false;
        }
        version = parsed;
        return true;
    }

    private static bool SetEquals(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right) =>
        left.Count == right.Count &&
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    [GeneratedRegex(
        @"^v[1-9][0-9]{0,2}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(
        @"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+){1,7}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PolicyPattern();

    [GeneratedRegex(
        @"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+){1,7}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex(
        @"^(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}
