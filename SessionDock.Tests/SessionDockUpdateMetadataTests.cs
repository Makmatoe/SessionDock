using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionDockUpdateMetadataTests
{
    private const string NuspecNamespace =
        "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";
    private const string SignedReleaseNotes = "Signed release notes.";

    [Fact]
    public void ValidatePackageMetadata_RequiredSubsetAndExtraFields_AreAccepted()
    {
        var nuspec = CreateNuspec(additionalMetadata: """
            <title>A future display title</title>
            <description>A future description</description>
            <repository type="git" url="https://github.com/Makmatoe/SessionDock" />
            <dependencies><group targetFramework="net10.0-windows" /></dependencies>
            """);
        using var archive = CreateArchive(nuspec);

        SessionDockUpdateService.ValidatePackageMetadata(
            archive,
            CreateVerifiedRelease(),
            useCurrentLayout: true);
    }

    [Fact]
    public void ValidatePackageMetadata_PublicV312Nuspec_IsAcceptedByteForByte()
    {
        var nuspec = ReadPublicV312Nuspec();
        Assert.Equal(8_947, nuspec.Length);
        Assert.Equal(
            "4E1795E9907A8AF8621EB6137DABF7F5E8FC561E84779529A3AC8580227A51C7",
            Convert.ToHexString(SHA256.HashData(nuspec)));

        var document = XDocument.Load(new MemoryStream(nuspec));
        var metadata = Assert.Single(document.Root!.Elements());
        var releaseNotes = Assert.Single(metadata.Elements(), element =>
                element.Name.LocalName.Equals(
                    "releaseNotes",
                    StringComparison.Ordinal))
            .Value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        using var archive = CreateArchive(nuspec);

        SessionDockUpdateService.ValidatePackageMetadata(
            archive,
            CreateVerifiedRelease(releaseNotes: releaseNotes),
            useCurrentLayout: true);
    }

    [Fact]
    public void ValidatePackageMetadata_LegacyRequiredSubset_IsAccepted()
    {
        var nuspec = CreateNuspec(
            packageId: ReleaseDescriptorPolicy.LegacyVelopackPackageId,
            version: "2.1.5",
            channel: ReleaseDescriptorPolicy.LegacyChannel,
            mainExecutable: "RobloxOne.exe");
        using var archive = CreateArchive(
            nuspec,
            nuspecName: "RobloxOne.nuspec");

        SessionDockUpdateService.ValidatePackageMetadata(
            archive,
            CreateVerifiedRelease(useLegacyIdentity: true),
            useCurrentLayout: false);
    }

    [Fact]
    public void ValidatePackageMetadata_DuplicateRequiredField_IsRejected()
    {
        var nuspec = CreateNuspec(
            additionalMetadata: "<id>SessionDockApp</id>");
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_CriticalMismatch_IsRejected()
    {
        var nuspec = CreateNuspec(version: "3.1.1");
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_ForeignNamespace_IsRejected()
    {
        var nuspec = CreateNuspec(
            additionalMetadata: "<foreign xmlns=\"urn:untrusted\">value</foreign>");
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_UnsupportedRootNamespace_IsRejected()
    {
        var nuspec = CreateNuspec().Replace(
            NuspecNamespace,
            "urn:untrusted",
            StringComparison.Ordinal);
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_Dtd_IsRejected()
    {
        var nuspec = CreateNuspec().Replace(
            "<package ",
            "<!DOCTYPE package [<!ENTITY injected 'unsafe'>]><package ",
            StringComparison.Ordinal);
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_TooManyFields_IsRejected()
    {
        var additional = string.Concat(Enumerable.Range(0, 128).Select(index =>
            $"<extra{index}>value</extra{index}>"));
        var nuspec = CreateNuspec(additionalMetadata: additional);
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_PresentReleaseNotesMustMatchDescriptor()
    {
        var nuspec = CreateNuspec(
            additionalMetadata: "<releaseNotes>Different notes.</releaseNotes>");
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_PresentUnsafeRenderedNotes_AreRejected()
    {
        var nuspec = CreateNuspec(
            additionalMetadata:
                "<releaseNotesHtml>&lt;script&gt;unsafe()&lt;/script&gt;</releaseNotesHtml>");
        using var archive = CreateArchive(nuspec);

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    [Fact]
    public void ValidatePackageMetadata_SquirrelMetadataMismatch_IsRejected()
    {
        var nuspec = CreateNuspec();
        using var archive = CreateArchive(
            nuspec,
            squirrelMetadata: nuspec + " ");

        Assert.Throws<ReleaseTrustException>(() =>
            SessionDockUpdateService.ValidatePackageMetadata(
                archive,
                CreateVerifiedRelease(),
                useCurrentLayout: true));
    }

    private static string CreateNuspec(
        string packageId = ReleaseDescriptorPolicy.VelopackPackageId,
        string version = "3.1.2",
        string channel = ReleaseDescriptorPolicy.Channel,
        string mainExecutable = "SessionDock.exe",
        string additionalMetadata = "") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="{{NuspecNamespace}}">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <channel>{{channel}}</channel>
            <mainExe>{{mainExecutable}}</mainExe>
            <os>win</os>
            <rid>win-x64</rid>
            <machineArchitecture>x64</machineArchitecture>
            {{additionalMetadata}}
          </metadata>
        </package>
        """;

    private static ZipArchive CreateArchive(
        string nuspec,
        string nuspecName = "SessionDockApp.nuspec",
        string? squirrelMetadata = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteEntry(writer, nuspecName, nuspec);
            WriteEntry(writer, "lib/app/sq.version", squirrelMetadata ?? nuspec);
        }

        stream.Position = 0;
        return new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false);
    }

    private static ZipArchive CreateArchive(byte[] nuspec)
    {
        var stream = new MemoryStream();
        using (var writer = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteEntry(writer, "SessionDockApp.nuspec", nuspec);
            WriteEntry(writer, "lib/app/sq.version", nuspec);
        }

        stream.Position = 0;
        return new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string value)
    {
        var entry = archive.CreateEntry(name);
        using var output = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        output.Write(value);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] value)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        output.Write(value);
    }

    private static byte[] ReadPublicV312Nuspec()
    {
        using var stream = typeof(SessionDockUpdateMetadataTests).Assembly
            .GetManifestResourceStream(
                "SessionDock.Tests.TestData.SessionDockApp.3.1.2.Nuspec.base64")
            ?? throw new InvalidOperationException(
                "The public SessionDock v3.1.2 Nuspec fixture is unavailable.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return Convert.FromBase64String(reader.ReadToEnd().Trim());
    }

    private static VerifiedReleaseDescriptor CreateVerifiedRelease(
        bool useLegacyIdentity = false,
        string releaseNotes = SignedReleaseNotes)
    {
        var version = useLegacyIdentity ? "2.1.5" : "3.1.2";
        var descriptor = new ReleaseDescriptor(
            ReleaseDescriptorPolicy.SchemaVersion,
            useLegacyIdentity
                ? ReleaseDescriptorPolicy.LegacyProduct
                : ReleaseDescriptorPolicy.Product,
            useLegacyIdentity
                ? ReleaseDescriptorPolicy.LegacyRepository
                : ReleaseDescriptorPolicy.Repository,
            useLegacyIdentity
                ? ReleaseDescriptorPolicy.LegacyChannel
                : ReleaseDescriptorPolicy.Channel,
            useLegacyIdentity
                ? ReleaseDescriptorPolicy.LegacyKeyId
                : ReleaseDescriptorPolicy.KeyId,
            version,
            $"v{version}",
            "2026-08-18T17:56:05.6825187+00:00",
            useLegacyIdentity
                ? "RobloxOne-2.1.5-win-x64-stable-full.nupkg"
                : "SessionDockApp-3.1.2-win-x64-sessiondock-full.nupkg",
            81_267_441,
            new string('A', 64),
            releaseNotes,
            "test-signature");
        return new VerifiedReleaseDescriptor(
            descriptor,
            Version.Parse(version),
            DateTimeOffset.Parse(descriptor.PublishedAt));
    }
}
