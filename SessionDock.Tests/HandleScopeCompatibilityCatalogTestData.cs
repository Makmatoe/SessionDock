using System.Security.Cryptography;
using System.Text;
using SessionDock.ReleaseTrust;

namespace SessionDock.Tests;

internal static class HandleScopeCompatibilityCatalogTestData
{
    internal static readonly DateTimeOffset TestNow =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    internal static HandleScopeCompatibilityCatalog CreateCatalog(
        IReadOnlyList<HandleScopeCompatibleRelease>? releases = null,
        string recommendedVersion = "0.1.4",
        long sequence = 1,
        DateTimeOffset? generatedAt = null,
        DateTimeOffset? expiresAt = null,
        string sessionDockVersion = "2.8.0")
    {
        var generated = generatedAt ?? TestNow.AddMinutes(-1);
        return new(
            HandleScopeCompatibilityCatalogPolicy.SchemaVersion,
            HandleScopeCompatibilityCatalogPolicy.Product,
            HandleScopeCompatibilityCatalogPolicy.Repository,
            HandleScopeCompatibilityCatalogPolicy.KeyId,
            sequence,
            generated.ToString("O"),
            (expiresAt ?? generated.AddDays(30)).ToString("O"),
            sessionDockVersion,
            recommendedVersion,
            releases ?? [CreateRelease()],
            string.Empty);
    }

    internal static HandleScopeCompatibleRelease CreateRelease(
        string version = "0.1.4",
        string status = "supported",
        string minimumSessionDockVersion = "2.7.6",
        string? maximumSessionDockVersionExclusive = null,
        IReadOnlyList<string>? apiContracts = null,
        IReadOnlyList<string>? capabilities = null,
        long executableSize = 50,
        string? executableSha256 = null)
    {
        var tag = $"v{version}";
        var contracts = apiContracts ?? ["v1"];
        return new(
            version,
            tag,
            status,
            minimumSessionDockVersion,
            maximumSessionDockVersionExclusive,
            contracts,
            capabilities ??
                contracts
                    .Select(contract => $"handlescope.http.{contract}")
                    .Concat(
                    [
                        "handlescope.plan.single-use.v1",
                        "handlescope.policy.roblox-singleton-event.v1"
                    ])
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            new(
                $"HandleScope-{version}-win-x64.zip",
                100,
                new string('a', 64)),
            new("SHA256SUMS.txt", 100, new string('b', 64)),
            new(
                $"HandleScope-{version}-win-x64.release.json",
                100,
                new string('c', 64)),
            new(
                "api/HandleScope.Api.exe",
                executableSize,
                executableSha256 ?? Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(version)))
                    .ToLowerInvariant()),
            $"https://github.com/Makmatoe/HandleScope/blob/{tag}/" +
            "docs/integrations/sessiondock.md");
    }

    internal static HandleScopeCompatibilityCatalog Sign(
        HandleScopeCompatibilityCatalog catalog,
        ECDsa key)
    {
        var unsigned = catalog with { Signature = string.Empty };
        return unsigned with
        {
            Signature = Convert.ToBase64String(key.SignData(
                HandleScopeCompatibilityCatalogPolicy.CreateCanonicalPayload(
                    unsigned),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };
    }
}
