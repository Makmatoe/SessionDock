using System.Text.Json;
using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelProvenanceTests
{
    [Fact]
    public void EmbeddedUpstreamManifest_PinsSnapshotAndBlocksUnlicensedRelease()
    {
        using var stream = typeof(ExactWheelSession).Assembly
            .GetManifestResourceStream("SessionDock.ExactWheel.Upstream.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ExactWheel", root.GetProperty("component").GetString());
        Assert.Equal("1.1.0", root.GetProperty("componentVersion").GetString());
        Assert.Equal(1, root.GetProperty("macroFormatVersion").GetInt32());
        Assert.Equal("uncommitted-snapshot", root.GetProperty("sourceState").GetString());
        Assert.Equal(45, root.GetProperty("sourceFileCount").GetInt32());
        Assert.Equal(396_664, root.GetProperty("sourceBytes").GetInt32());
        Assert.Equal(
            "fc3016982b2a7c710ecac8c534d2a85d4cbf74041f3fc00d3cfb3523438f87e5",
            root.GetProperty("canonicalManifestSha256").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceCommit").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("license").ValueKind);
        Assert.True(root.GetProperty("releaseBlockedPendingLicense").GetBoolean());
        Assert.Equal(
            "managed-compatible-port",
            root.GetProperty("integrationKind").GetString());
    }

    [Fact]
    public void TagRelease_GatesProvenanceBeforeBuildAndStagesImmutableInput()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "release.yml"));
        var gateIndex = workflow.IndexOf(
            "- name: Enforce ExactWheel release provenance",
            StringComparison.Ordinal);
        var buildIndex = workflow.IndexOf(
            "- name: Build, test, and publish the application",
            StringComparison.Ordinal);

        Assert.True(gateIndex >= 0);
        Assert.True(buildIndex > gateIndex);
        Assert.Contains(
            "./scripts/Verify-ExactWheelReleaseProvenance.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item SessionDock.ExactWheel/exactwheel-upstream.json " +
            "artifacts/release-input/exactwheel-upstream.json",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item scripts/Verify-ExactWheelReleaseProvenance.ps1 " +
            "artifacts/release-input/scripts/Verify-ExactWheelReleaseProvenance.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-BundledExactWheelManifest " +
            "./release-input/exactwheel-upstream.json",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseGateAndSbom_FailClosedWithoutEnteringNormalBuilds()
    {
        var root = FindRepositoryRoot();
        var gate = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-ExactWheelReleaseProvenance.ps1"));
        var sbom = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "New-ReleaseSbom.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "scripts", "Build.ps1"));
        var ci = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "ci.yml"));

        Assert.Contains("releaseBlockedPendingLicense is true", gate);
        Assert.Contains("license is missing", gate);
        Assert.Contains("sourceState is not immutable-git", gate);
        Assert.Contains("sourceCommit is missing", gate);
        Assert.Contains("sourceTag is missing", gate);
        Assert.Contains("[0-9a-f]{40}|[0-9a-f]{64}", gate);
        Assert.Contains("SPDXRef-Package-ExactWheel", sbom);
        Assert.Contains("Verify-ExactWheelReleaseProvenance.ps1", sbom);
        Assert.Contains("relationshipType = 'CONTAINS'", sbom);
        Assert.DoesNotContain("Verify-ExactWheelReleaseProvenance.ps1", build);
        Assert.DoesNotContain("Verify-ExactWheelReleaseProvenance.ps1", ci);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
