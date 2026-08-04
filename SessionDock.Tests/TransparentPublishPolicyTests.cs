using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class TransparentPublishPolicyTests
{
    [Fact]
    public void ApplicationProject_PublishesInspectableSelfContainedFiles()
    {
        var project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "SessionDock.csproj"));

        AssertProperty(project, "PublishSingleFile", "false");
        AssertProperty(project, "SelfContained", "true");
        AssertProperty(project, "RuntimeFrameworkVersion", "10.0.10");
        AssertProperty(project, "IncludeNativeLibrariesForSelfExtract", "false");
        AssertProperty(project, "EnableCompressionInSingleFile", "false");
        AssertProperty(project, "PublishTrimmed", "false");
        AssertProperty(project, "PublishReadyToRun", "false");
    }

    [Fact]
    public void BuildAndVerifier_EnforceTransparentPinnedInventory()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "scripts", "Build.ps1"));
        var verifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Publish.ps1"));
        var securityPatchVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Test-DotNetSecurityPatch.ps1"));

        Assert.Contains("-p:PublishSingleFile=false", build, StringComparison.Ordinal);
        Assert.Contains(
            "-p:IncludeNativeLibrariesForSelfExtract=false",
            build,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:EnableCompressionInSingleFile=false",
            build,
            StringComparison.Ordinal);
        Assert.Contains("SessionDock.ExactWheel.dll", build, StringComparison.Ordinal);
        Assert.Contains("SessionDock.HandleScope.dll", build, StringComparison.Ordinal);

        Assert.Contains("SessionDock.deps.json", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.runtimeconfig.json", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.ExactWheel.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.HandleScope.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.ReleaseTrust.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("unexpected executable or script payload", verifier,
            StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", verifier, StringComparison.Ordinal);
        Assert.Contains(
            "$hash.AppendData([IO.File]::ReadAllBytes($AspNetCoreNotice))",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains("Publish output must not contain symbolic links", verifier,
            StringComparison.Ordinal);
        Assert.Contains("Production SessionDock.dll contains the test-only", verifier,
            StringComparison.Ordinal);
        Assert.Contains("PublishTrimmed' 'PublishTrimmed value'", securityPatchVerifier,
            StringComparison.Ordinal);
        Assert.Contains("PublishReadyToRun' 'PublishReadyToRun value'", securityPatchVerifier,
            StringComparison.Ordinal);
        Assert.Contains("must not contain Microsoft.NET.ILLink.Tasks", securityPatchVerifier,
            StringComparison.Ordinal);
    }

    private static void AssertProperty(
        XDocument project,
        string propertyName,
        string expectedValue)
    {
        var values = project.Descendants(propertyName)
            .Select(element => element.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([expectedValue], values);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
