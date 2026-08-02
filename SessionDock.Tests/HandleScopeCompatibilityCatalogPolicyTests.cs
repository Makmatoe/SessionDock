using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SessionDock.ReleaseTrust;

namespace SessionDock.Tests;

public sealed class HandleScopeCompatibilityCatalogPolicyTests
{
    [Fact]
    public void VerifyEmbedded_ValidCatalogBuildsTypedVersionIndex()
    {
        var releases = new[]
        {
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.1.3",
                status: "revoked"),
            HandleScopeCompatibilityCatalogTestData.CreateRelease()
        };
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            releases);

        var verified = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog),
            HandleScopeCompatibilityCatalogTestData.TestNow);

        Assert.Equal(new Version(2, 8, 0), verified.SessionDockVersion);
        Assert.Equal(new Version(0, 1, 4), verified.RecommendedVersion);
        Assert.Equal(2, verified.Releases.Count);
        Assert.Equal(
            "v0.1.3",
            verified.Releases[new Version(0, 1, 3)].Tag);
        Assert.Equal(
            "supported",
            verified.Releases[new Version(0, 1, 4)].Status);
    }

    [Fact]
    public void Verify_AuthenticRemoteCatalogPassesAndTamperingFails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = HandleScopeCompatibilityCatalogTestData.Sign(
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(),
            key);

        var verified = HandleScopeCompatibilityCatalogPolicy.Verify(
            HandleScopeCompatibilityCatalogPolicy.Serialize(signed),
            key.ExportSubjectPublicKeyInfoPem(),
            HandleScopeCompatibilityCatalogTestData.TestNow);

        Assert.Equal(new Version(0, 1, 4), verified.RecommendedVersion);

        var release = signed.Releases[0];
        var tampered = signed with
        {
            Releases =
            [
                release with
                {
                    ApiExecutable = release.ApiExecutable with
                    {
                        Sha256 = new string('e', 64)
                    }
                }
            ]
        };
        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.Verify(
                HandleScopeCompatibilityCatalogPolicy.Serialize(tampered),
                key.ExportSubjectPublicKeyInfoPem(),
                HandleScopeCompatibilityCatalogTestData.TestNow));
    }

    [Fact]
    public void VerifyEmbedded_RejectsNonCanonicalOrSignedBootstrapMetadata()
    {
        var valid = HandleScopeCompatibilityCatalogTestData.CreateCatalog();
        var release = valid.Releases[0];
        var invalidCatalogs = new[]
        {
            valid with { Signature = "packaged catalogs are unsigned" },
            valid with
            {
                Releases = [release with { Tag = "v9.9.9" }]
            },
            valid with
            {
                Releases =
                [
                    release with
                    {
                        Capabilities = release.Capabilities.Reverse().ToArray()
                    }
                ]
            },
            valid with { RecommendedVersion = "0.1.5" }
        };

        foreach (var invalid in invalidCatalogs)
        {
            Assert.Throws<ReleaseTrustException>(() =>
                HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                    HandleScopeCompatibilityCatalogPolicy.Serialize(invalid),
                    HandleScopeCompatibilityCatalogTestData.TestNow));
        }
    }

    [Fact]
    public void Deserialize_RejectsUnknownFieldsAndUtf8Oversize()
    {
        var json = JsonNode.Parse(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog()))!
            .AsObject();
        json["unexpected"] = true;

        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.Deserialize(
                json.ToJsonString()));

        var oversized = $$"""
            { "value": "{{new string('\u00e9', HandleScopeCompatibilityCatalogPolicy.MaximumCatalogBytes)}}" }
            """;
        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.Deserialize(oversized));
    }

    [Fact]
    public void VerifyEmbedded_NullRequiredShapesFailAsReleaseTrustErrors()
    {
        var mutations = new Action<JsonObject>[]
        {
            root => root["product"] = null,
            root => root["repository"] = null,
            root => root["keyId"] = null,
            root => root["generatedAt"] = null,
            root => root["expiresAt"] = null,
            root => root["sessionDockVersion"] = null,
            root => root["recommendedVersion"] = null,
            root => root["releases"] = null,
            root => ((JsonArray)root["releases"]!)[0] = null,
            root => Release(root)["version"] = null,
            root => Release(root)["tag"] = null,
            root => Release(root)["status"] = null,
            root => Release(root)["minimumSessionDockVersion"] = null,
            root => Release(root)["apiContracts"] = null,
            root => ((JsonArray)Release(root)["apiContracts"]!)[0] = null,
            root => Release(root)["capabilities"] = null,
            root => ((JsonArray)Release(root)["capabilities"]!)[0] = null,
            root => Release(root)["package"] = null,
            root => ((JsonObject)Release(root)["package"]!)["name"] = null,
            root => ((JsonObject)Release(root)["package"]!)["sha256"] = null,
            root => Release(root)["checksums"] = null,
            root => ((JsonObject)Release(root)["checksums"]!)["name"] = null,
            root => ((JsonObject)Release(root)["checksums"]!)["sha256"] = null,
            root => ((JsonObject)Release(root)["manifest"]!)["name"] = null,
            root => ((JsonObject)Release(root)["manifest"]!)["sha256"] = null,
            root => Release(root)["apiExecutable"] = null,
            root => ((JsonObject)Release(root)["apiExecutable"]!)["path"] = null,
            root => ((JsonObject)Release(root)["apiExecutable"]!)["sha256"] = null,
            root => Release(root)["contractUrl"] = null,
            root => root["signature"] = null
        };

        foreach (var mutate in mutations)
        {
            var root = JsonNode.Parse(
                HandleScopeCompatibilityCatalogPolicy.Serialize(
                    HandleScopeCompatibilityCatalogTestData.CreateCatalog()))!
                .AsObject();
            mutate(root);

            var exception = Record.Exception(() =>
                HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                    root.ToJsonString(),
                    HandleScopeCompatibilityCatalogTestData.TestNow));

            Assert.IsType<ReleaseTrustException>(exception);
        }
    }

    [Fact]
    public void VerifyEmbedded_RequiresMatchingApiContractCapabilities()
    {
        var required = new[]
        {
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        };
        var mismatches = new[]
        {
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                apiContracts: ["v2"],
                capabilities:
                [
                    "handlescope.http.v1",
                    required[0],
                    required[1]
                ]),
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                apiContracts: ["v1"],
                capabilities:
                [
                    "handlescope.http.v1",
                    "handlescope.http.v2",
                    required[0],
                    required[1]
                ])
        };

        foreach (var release in mismatches)
        {
            Assert.Throws<ReleaseTrustException>(() =>
                HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                    HandleScopeCompatibilityCatalogPolicy.Serialize(
                        HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                            [release])),
                    HandleScopeCompatibilityCatalogTestData.TestNow));
        }
    }

    [Fact]
    public void VerifyEmbedded_AllowsOnlyLegacy014WithoutExternalManifest()
    {
        var legacy = HandleScopeCompatibilityCatalogTestData.CreateRelease() with
        {
            Manifest = null
        };
        var verified = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog([legacy])),
            HandleScopeCompatibilityCatalogTestData.TestNow);

        Assert.Null(verified.Releases[new Version(0, 1, 4)].Manifest);

        var modern = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1") with
        {
            Manifest = null
        };
        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                HandleScopeCompatibilityCatalogPolicy.Serialize(
                    HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                        [modern],
                        recommendedVersion: "0.2.1")),
                HandleScopeCompatibilityCatalogTestData.TestNow));
    }

    [Fact]
    public void VerifyEmbedded_RejectsDuplicateSupportedRuntimeIdentity()
    {
        var sharedHash = new string('d', 64);
        var legacy = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            executableSize: 500,
            executableSha256: sharedHash);
        var modern = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            executableSize: 500,
            executableSha256: sharedHash);
        var duplicateCatalog =
            HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                [legacy, modern],
                recommendedVersion: "0.2.1");

        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                HandleScopeCompatibilityCatalogPolicy.Serialize(
                    duplicateCatalog),
                HandleScopeCompatibilityCatalogTestData.TestNow));

        var revokedLegacy = legacy with { Status = "revoked" };
        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                HandleScopeCompatibilityCatalogPolicy.Serialize(
                    HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                        [revokedLegacy, modern],
                        recommendedVersion: "0.2.1")),
                HandleScopeCompatibilityCatalogTestData.TestNow));

        var recommended = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.2");
        var allowed = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(
                HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                    [revokedLegacy, modern, recommended],
                    recommendedVersion: "0.2.2")),
            HandleScopeCompatibilityCatalogTestData.TestNow);
        Assert.Equal(3, allowed.Releases.Count);
    }

    [Fact]
    public void VerifyEmbedded_RecommendationMustSupportPublishingSessionDock()
    {
        var futureOnly = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            minimumSessionDockVersion: "2.9.0");
        var ended = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: "0.2.1",
            maximumSessionDockVersionExclusive: "2.8.0");
        var missingRequiredCapability =
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.2.1",
                capabilities:
                [
                    "handlescope.http.v1",
                    "handlescope.policy.roblox-singleton-event.v1"
                ]);
        var uncompiledApi =
            HandleScopeCompatibilityCatalogTestData.CreateRelease(
                version: "0.2.1",
                apiContracts: ["v3"]);

        foreach (var release in new[]
                 {
                     futureOnly,
                     ended,
                     missingRequiredCapability,
                     uncompiledApi
                 })
        {
            Assert.Throws<ReleaseTrustException>(() =>
                HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                    HandleScopeCompatibilityCatalogPolicy.Serialize(
                        HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                            [release],
                            recommendedVersion: "0.2.1")),
                    HandleScopeCompatibilityCatalogTestData.TestNow));
        }

        var futurePublisher =
            HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
                HandleScopeCompatibilityCatalogPolicy.Serialize(
                    HandleScopeCompatibilityCatalogTestData.CreateCatalog(
                        [futureOnly],
                        recommendedVersion: "0.2.1",
                        sessionDockVersion: "2.9.0")),
                HandleScopeCompatibilityCatalogTestData.TestNow);
        Assert.Equal(new Version(2, 9, 0), futurePublisher.SessionDockVersion);
    }

    [Fact]
    public void Verify_ExpiredRemoteIsRejectedWhileEmbeddedPolicyIsIndependent()
    {
        var now = HandleScopeCompatibilityCatalogTestData.TestNow;
        var expired = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            generatedAt: now.AddDays(-20),
            expiresAt: now.AddDays(-1));

        var embedded = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(expired),
            now);

        Assert.Equal(new Version(0, 1, 4), embedded.RecommendedVersion);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = HandleScopeCompatibilityCatalogTestData.Sign(expired, key);
        Assert.Throws<ReleaseTrustException>(() =>
            HandleScopeCompatibilityCatalogPolicy.Verify(
                HandleScopeCompatibilityCatalogPolicy.Serialize(signed),
                key.ExportSubjectPublicKeyInfoPem(),
                now));
    }

    private static JsonObject Release(JsonObject root) =>
        (JsonObject)((JsonArray)root["releases"]!)[0]!;
}
