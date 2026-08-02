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
        byte[] executable) =>
        HandleScopeCompatibilityCatalogTestData.CreateRelease(
            version: version,
            status: status,
            executableSize: executable.LongLength,
            executableSha256: Convert.ToHexString(
                    SHA256.HashData(executable))
                .ToLowerInvariant());

    private static VerifiedHandleScopeCompatibilityCatalog VerifyCatalog(
        IReadOnlyList<HandleScopeCompatibleRelease> releases,
        string recommendedVersion)
    {
        var catalog = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            releases.OrderBy(release => new Version(release.Version)).ToArray(),
            recommendedVersion);
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
