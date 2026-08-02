using System.Security.Cryptography;
using SessionDock.ReleaseTrust;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeCatalogInstallPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.InstallPolicy.{Guid.NewGuid():N}");

    [Fact]
    public void Select_UsesOnlyClosedReviewedSetupAdapters()
    {
        var legacy014 = CreateRelease(
            "0.1.4",
            "supported",
            "legacy-014"u8.ToArray());
        var legacy022 = CreateRelease(
            "0.2.2",
            "supported",
            "legacy-022"u8.ToArray());
        var native = CreateRelease(
            "0.3.0",
            "supported",
            "native-030"u8.ToArray());
        var catalog = VerifyCatalog(
            [legacy014, legacy022, native],
            native.Version,
            sessionDockVersion: "2.9.0");

        Assert.Equal(
            HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned,
            HandleScopeCatalogInstallPolicy.Select(
                legacy014,
                catalog,
                new Version(2, 9, 0)).SetupAdapter);
        Assert.Equal(
            HandleScopeSetupAdapter.LegacyPowerShellRemoteSigned,
            HandleScopeCatalogInstallPolicy.Select(
                legacy022,
                catalog,
                new Version(2, 9, 0)).SetupAdapter);
        Assert.Equal(
            HandleScopeSetupAdapter.NativeV1,
            HandleScopeCatalogInstallPolicy.Select(
                native,
                catalog,
                new Version(2, 9, 0)).SetupAdapter);
    }

    [Theory]
    [InlineData("0.1.4", "handlescope.setup.native.v1")]
    [InlineData("0.2.2", "handlescope.setup.native.v1")]
    [InlineData("0.3.0", null)]
    [InlineData("0.3.0", "handlescope.setup.future.v2")]
    [InlineData("0.3.0", "handlescope.setup.native.v1,handlescope.setup.future.v2")]
    public void Select_RejectsMissingUnknownOrContradictorySetupCapabilities(
        string version,
        string? setupCapabilitiesText)
    {
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: version);
        var setupCapabilities = setupCapabilitiesText?.Split(',') ?? [];
        release = release with
        {
            Capabilities = release.Capabilities
                .Where(capability => !capability.StartsWith(
                    "handlescope.setup.",
                    StringComparison.Ordinal))
                .Concat(setupCapabilities)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
        var catalog = VerifyCatalog(
            [release],
            release.Version,
            sessionDockVersion: "2.9.0");

        var exception = Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeCatalogInstallPolicy.Select(
                release,
                catalog,
                new Version(2, 9, 0)));

        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            exception.FailureKind);
        Assert.Contains("adapter", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_IgnoresCallerControlledSetupCapability()
    {
        var authorized = CreateRelease(
            "0.3.0",
            "supported",
            "native-runtime"u8.ToArray());
        var catalog = VerifyCatalog(
            [authorized],
            authorized.Version,
            sessionDockVersion: "2.9.0");
        var callerControlled = authorized with
        {
            Capabilities = authorized.Capabilities
                .Where(capability => capability !=
                    HandleScopeCatalogInstallPolicy.NativeSetupCapability)
                .Append("handlescope.setup.future.v2")
                .Order(StringComparer.Ordinal)
                .ToArray()
        };

        var selection = HandleScopeCatalogInstallPolicy.Select(
            callerControlled,
            catalog,
            new Version(2, 9, 0));

        Assert.Equal(HandleScopeSetupAdapter.NativeV1, selection.SetupAdapter);
        Assert.Equal(authorized.Version, selection.CatalogRelease?.Version);
        Assert.Equal(
            authorized.Capabilities,
            selection.CatalogRelease?.Capabilities);
        Assert.DoesNotContain(
            "handlescope.setup.future.v2",
            selection.CatalogRelease!.Capabilities);
    }

    [Fact]
    public void RefuseKnownDowngrade_AllowsExactRevokedOlderRuntimeRemediation()
    {
        var revokedBytes = "revoked-runtime"u8.ToArray();
        var targetBytes = "supported-runtime"u8.ToArray();
        var revoked = CreateRelease("0.2.1", "revoked", revokedBytes);
        var target = CreateRelease("0.2.2", "supported", targetBytes);
        var catalog = VerifyCatalog([revoked, target], target.Version);
        var selection = HandleScopeCatalogInstallPolicy.Select(
            target,
            catalog,
            new Version(2, 8, 0));
        WriteInstalledRuntime(revokedBytes);

        var exception = Record.Exception(() =>
            HandleScopeCatalogInstallPolicy.RefuseKnownDowngrade(
                selection,
                catalog,
                _root));

        Assert.Null(exception);
    }

    [Fact]
    public void RefuseKnownDowngrade_RejectsAmbiguousRevokedRuntimeIdentity()
    {
        var revokedBytes = "ambiguous-runtime"u8.ToArray();
        var first = CreateRelease("0.2.0", "revoked", revokedBytes);
        var second = CreateRelease("0.2.1", "revoked", revokedBytes);
        var target = CreateRelease(
            "0.2.2",
            "supported",
            "supported-runtime"u8.ToArray());
        var catalog = VerifyCatalog([first, second, target], target.Version);
        var selection = HandleScopeCatalogInstallPolicy.Select(
            target,
            catalog,
            new Version(2, 8, 0));
        WriteInstalledRuntime(revokedBytes);

        var exception = Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeCatalogInstallPolicy.RefuseKnownDowngrade(
                selection,
                catalog,
                _root));

        Assert.Equal(
            HandleScopeInstallFailureKind.LocalEnvironment,
            exception.FailureKind);
    }

    [Fact]
    public void RefuseKnownDowngrade_RejectsRevokedOrChangedSelectedIdentity()
    {
        var targetBytes = "supported-runtime"u8.ToArray();
        var target = CreateRelease("0.2.2", "supported", targetBytes);
        var catalog = VerifyCatalog([target], target.Version);
        var selection = HandleScopeCatalogInstallPolicy.Select(
            target,
            catalog,
            new Version(2, 8, 0));
        var invalidSelections = new[]
        {
            selection with
            {
                CatalogRelease = target with { Status = "revoked" }
            },
            selection with
            {
                CatalogRelease = target with
                {
                    ApiExecutable = target.ApiExecutable with
                    {
                        Sha256 = new string('f', 64)
                    }
                }
            }
        };

        foreach (var invalid in invalidSelections)
        {
            var exception = Assert.Throws<HandleScopeInstallException>(() =>
                HandleScopeCatalogInstallPolicy.RefuseKnownDowngrade(
                    invalid,
                    catalog,
                    _root));
            Assert.Equal(
                HandleScopeInstallFailureKind.ReleaseIntegrity,
                exception.FailureKind);
        }
    }

    [Fact]
    public void RefuseKnownDowngrade_RejectsSameVersionRevokedTarget()
    {
        var revokedBytes = "revoked-same-version"u8.ToArray();
        var revoked = CreateRelease("0.2.2", "revoked", revokedBytes);
        var fallback = CreateRelease(
            "0.2.3",
            "supported",
            "supported-fallback"u8.ToArray());
        var catalog = VerifyCatalog([revoked, fallback], fallback.Version);
        var fallbackSelection = HandleScopeCatalogInstallPolicy.Select(
            fallback,
            catalog,
            new Version(2, 8, 0));
        var revokedSelection = fallbackSelection with
        {
            Release = fallbackSelection.Release with
            {
                Version = revoked.Version,
                TagName = revoked.Tag
            },
            CatalogRelease = revoked
        };
        WriteInstalledRuntime(revokedBytes);

        var exception = Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeCatalogInstallPolicy.RefuseKnownDowngrade(
                revokedSelection,
                catalog,
                _root));

        Assert.Equal(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            exception.FailureKind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static HandleScopeCompatibleRelease CreateRelease(
        string version,
        string status,
        byte[] executable)
    {
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: version,
            status: status,
            executableSize: executable.LongLength,
            executableSha256: Convert.ToHexString(
                    SHA256.HashData(executable))
                .ToLowerInvariant());
        if (version is not ("0.1.4" or "0.2.2"))
        {
            release = release with
            {
                Capabilities = release.Capabilities
                    .Append(
                        HandleScopeCatalogInstallPolicy.NativeSetupCapability)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            };
        }
        return release;
    }

    private static VerifiedHandleScopeCompatibilityCatalog VerifyCatalog(
        IReadOnlyList<HandleScopeCompatibleRelease> releases,
        string recommendedVersion,
        string sessionDockVersion = "2.8.0")
    {
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            releases.OrderBy(release => new Version(release.Version)).ToArray(),
            recommendedVersion,
            sessionDockVersion: sessionDockVersion);
        return HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog),
            HandleScopeCompatibilityCatalogTestData.TestNow);
    }

    private void WriteInstalledRuntime(byte[] contents)
    {
        var executablePath = HandleScopeProcessVerifier.GetExpectedExecutablePath(
            _root);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, contents);
    }
}
