using System.Globalization;
using SessionDock.ReleaseTrust;

namespace SessionDock.Tests;

public sealed class ReleasePackagePolicyTests
{
    [Fact]
    public void ValidateEntries_ExactPackageAllowlist_IsAcceptedInAnyOrder()
    {
        var entries = CreateValidEntries();
        entries.Reverse();

        ReleasePackagePolicy.ValidateEntries(entries);
    }

    [Fact]
    public void ExecutableEntryNames_ContainsEveryExecutableThatMustBeVerified()
    {
        Assert.Equal(
            new[]
            {
                "lib/app/RobloxOne.exe",
                "lib/app/RobloxOne_ExecutionStub.exe",
                "lib/app/Squirrel.exe"
            },
            ReleasePackagePolicy.ExecutableEntryNames);
    }

    [Fact]
    public void ValidateEntries_SessionDockLayout_IsAcceptedOnlyAsCurrentLayout()
    {
        var entries = CreateValidCurrentEntries();

        ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true);
        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
        Assert.Equal(
            new[]
            {
                "lib/app/SessionDock.exe"
            },
            ReleasePackagePolicy.GetExecutableEntryNames(useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_PublicV312CompactManifest_IsAccepted()
    {
        var manifest = ReadPublicV312Manifest();
        var entries = manifest.Entries.Select(entry =>
            new ReleasePackageEntryIdentity(
                entry.FullName,
                entry.Length,
                entry.CompressedLength,
                entry.ExternalAttributes)).ToList();

        var mainExecutable = Assert.Single(
            entries,
            entry => entry.FullName == "lib/app/SessionDock.exe");
        Assert.Equal(187_904, mainExecutable.Length);
        Assert.Equal(
            "SessionDockApp-3.1.2-win-x64-sessiondock-full.nupkg",
            manifest.PackageFile);
        Assert.Equal("public GitHub release v3.1.2", manifest.Source);
        Assert.Equal(81_267_441, manifest.PackageSize);
        Assert.Equal(
            "19918D00E234E32EF9265050FD54165DE6DB1C7F5D933CB504F3BA99A5AFBB11",
            manifest.PackageSha256);
        Assert.Equal(manifest.EntryCount, entries.Count);
        Assert.Equal(
            manifest.TotalUncompressedBytes,
            entries.Sum(entry => entry.Length));
        Assert.Equal(
            manifest.TotalCompressedBytes,
            entries.Sum(entry => entry.CompressedLength));
        Assert.Equal(
            manifest.MaximumEntryNameLength,
            entries.Max(entry => entry.FullName!.Length));
        Assert.Equal(
            manifest.ExternalAttributes,
            entries.Select(entry => entry.ExternalAttributes).Distinct().ToArray());

        ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true);
    }

    [Theory]
    [InlineData("../lib/app/Untrusted.dll")]
    [InlineData("lib/app/../Untrusted.dll")]
    [InlineData("lib/app/runtime/../../Untrusted.dll")]
    [InlineData(@"lib\app\Untrusted.dll")]
    [InlineData("/lib/app/Untrusted.dll")]
    [InlineData("C:/lib/app/Untrusted.dll")]
    [InlineData("lib/app//Untrusted.dll")]
    [InlineData("lib/app/./Untrusted.dll")]
    [InlineData("lib/app/NUL.dll")]
    [InlineData("lib/app/runtime/COM1.txt")]
    [InlineData("lib/app/Untrusted.dll:payload")]
    [InlineData("lib/app/Untrusted.dll.")]
    [InlineData("lib/app/Untrusted.dll ")]
    public void ValidateEntries_CurrentLayoutUnsafePath_IsRejected(string name)
    {
        var entries = CreateValidCurrentEntries();
        entries[^1] = entries[^1] with { FullName = name };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutCaseInsensitiveCollision_IsRejected()
    {
        var entries = CreateValidCurrentEntries();
        entries[^2] = entries[^2] with
        {
            FullName = "lib/app/runtime/Collision.dll"
        };
        entries[^1] = entries[^1] with
        {
            FullName = "lib/app/runtime/collision.DLL"
        };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x40)]
    [InlineData(0x400)]
    [InlineData(unchecked((int)0xA0000000))]
    [InlineData(unchecked((int)0x40000000))]
    public void ValidateEntries_CurrentLayoutNonFileEntry_IsRejected(
        int externalAttributes)
    {
        var entries = CreateValidCurrentEntries();
        entries[^1] = entries[^1] with
        {
            ExternalAttributes = externalAttributes
        };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Theory]
    [InlineData("[Content_Types].xml")]
    [InlineData("_rels/.rels")]
    [InlineData("SessionDockApp.nuspec")]
    [InlineData("lib/app/SessionDock.exe")]
    [InlineData("lib/app/SessionDock_ExecutionStub.exe")]
    [InlineData("lib/app/Squirrel.exe")]
    [InlineData("lib/app/sq.version")]
    public void ValidateEntries_CurrentLayoutMissingCriticalEntry_IsRejected(
        string name)
    {
        var entries = CreateValidCurrentEntries();
        entries.RemoveAll(entry => entry.FullName == name);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutAdditionalSafeEntries_AreAccepted()
    {
        var entries = CreateValidCurrentEntries();
        entries.RemoveAll(entry =>
            entry.FullName is
                "lib/app/SessionDock.dll" or
                "lib/app/SessionDock.HandleScope.dll" or
                "lib/app/SessionDock.ExactWheel.dll" or
                "lib/app/SessionDock.ReleaseTrust.dll" or
                "lib/app/Velopack.dll" or
                "lib/app/SessionDock.deps.json" or
                "lib/app/SessionDock.runtimeconfig.json" or
                "lib/app/THIRD_PARTY_NOTICES.md");
        entries.Add(Entry("package/services/metadata/core-properties.psmdcp"));

        ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(unchecked((int)0x40000000))]
    public void ValidateEntries_CurrentLayoutSafeDirectoryEntry_IsAccepted(
        int externalAttributes)
    {
        var entries = CreateValidCurrentEntries();
        entries.Add(new ReleasePackageEntryIdentity(
            "lib/app/runtime/",
            0,
            0,
            externalAttributes));

        ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true);
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutDirectoryAndFileCollision_IsRejected()
    {
        var entries = CreateValidCurrentEntries();
        entries.Add(new ReleasePackageEntryIdentity(
            "lib/app/runtime/collision/",
            0,
            0,
            0x10));
        entries.Add(Entry("lib/app/runtime/Collision"));

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutFileAndChildPathConflict_IsRejected()
    {
        var entries = CreateValidCurrentEntries();
        entries.Add(Entry("lib/app/Runtime"));
        entries.Add(Entry("lib/app/runtime/child.dll"));

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutEntryCountOverBound_IsRejected()
    {
        var entries = CreateValidCurrentEntries(entryCount: 4097);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutOptionalEmptyFile_IsAccepted()
    {
        var entries = CreateValidCurrentEntries();
        entries[^1] = Entry("lib/app/runtime/empty.dat", 0, 0);

        ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true);
    }

    [Fact]
    public void ValidateEntries_CurrentLayoutRequiredEmptyFile_IsRejected()
    {
        var entries = CreateValidCurrentEntries();
        var index = entries.FindIndex(
            entry => entry.FullName == "lib/app/SessionDock.exe");
        entries[index] = Entry("lib/app/SessionDock.exe", 0, 0);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries, useCurrentLayout: true));
    }

    [Theory]
    [InlineData("lib/app/Untrusted.exe")]
    [InlineData("../[Content_Types].xml")]
    [InlineData("/[Content_Types].xml")]
    [InlineData("C:/[Content_Types].xml")]
    [InlineData("[content_types].xml")]
    [InlineData(@"lib\app\RobloxOne.exe")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ValidateEntries_UnsafeOrUnexpectedName_IsRejected(string? name)
    {
        var entries = CreateValidEntries();
        entries[0] = entries[0] with { FullName = name };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_ExtraEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries.Add(new ReleasePackageEntryIdentity(
            "lib/app/Untrusted.exe",
            64 * 1024,
            1024));

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_MissingEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries.RemoveAt(0);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_DuplicateEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries[^1] = entries[^1] with { FullName = entries[0].FullName };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_NullEntry_IsRejectedAsTrustFailure()
    {
        var entries = CreateValidEntries();
        entries[0] = null!;

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Theory]
    [InlineData("[Content_Types].xml", 0)]
    [InlineData("lib/app/LICENSE.md", (2L * 1024 * 1024) + 1)]
    [InlineData("lib/app/RobloxOne.exe", (1024L * 1024) - 1)]
    [InlineData("lib/app/RobloxOne.exe", (1024L * 1024 * 1024) + 1)]
    [InlineData("lib/app/RobloxOne_ExecutionStub.exe", (128L * 1024 * 1024) + 1)]
    [InlineData("lib/app/Squirrel.exe", (256L * 1024 * 1024) + 1)]
    public void ValidateEntries_EntryOutsideItsUncompressedLimit_IsRejected(
        string name,
        long length)
    {
        var entries = CreateValidEntries();
        var index = entries.FindIndex(entry => entry.FullName == name);
        entries[index] = entries[index] with { Length = length };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((1024L * 1024 * 1024) + 1)]
    public void ValidateEntries_InvalidCompressedSize_IsRejected(long compressedLength)
    {
        var entries = CreateValidEntries();
        entries[0] = entries[0] with { CompressedLength = compressedLength };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_AggregateExpansionOverLimit_IsRejected()
    {
        var entries = CreateValidEntries();
        SetLength(entries, "lib/app/RobloxOne.exe", 768L * 1024 * 1024);
        SetLength(
            entries,
            "lib/app/RobloxOne_ExecutionStub.exe",
            128L * 1024 * 1024);
        SetLength(entries, "lib/app/Squirrel.exe", 256L * 1024 * 1024);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    private static List<ReleasePackageEntryIdentity> CreateValidEntries() =>
    [
        Entry("[Content_Types].xml"),
        Entry("_rels/.rels"),
        Entry("RobloxOne.nuspec"),
        Entry("lib/app/LICENSE.md"),
        Entry(
            "lib/app/RobloxOne.exe",
            ReleaseDescriptorPolicy.MinimumPackageSize),
        Entry("lib/app/RobloxOne_ExecutionStub.exe", 64 * 1024),
        Entry("lib/app/Squirrel.exe", 64 * 1024),
        Entry("lib/app/sq.version"),
        Entry("lib/app/THIRD_PARTY_NOTICES.md"),
        Entry("lib/app/licenses/DotNet-LICENSE.txt"),
        Entry("lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt"),
        Entry("lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt"),
        Entry("lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt"),
        Entry("lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt"),
        Entry("lib/app/licenses/Velopack-LICENSE.txt")
    ];

    private static List<ReleasePackageEntryIdentity> CreateValidCurrentEntries(
        int entryCount = 24)
    {
        var entries = new List<ReleasePackageEntryIdentity>
        {
            Entry("[Content_Types].xml"),
            Entry("_rels/.rels"),
            Entry("SessionDockApp.nuspec"),
            Entry("lib/app/LICENSE.md"),
            Entry("lib/app/SessionDock.exe", 187_904),
            Entry("lib/app/SessionDock.dll", 3_099_136),
            Entry("lib/app/SessionDock.HandleScope.dll", 131_072),
            Entry("lib/app/SessionDock.ExactWheel.dll", 134_144),
            Entry("lib/app/SessionDock.ReleaseTrust.dll", 62_464),
            Entry("lib/app/Velopack.dll", 278_016),
            Entry("lib/app/SessionDock_ExecutionStub.exe", 417_280),
            Entry("lib/app/Squirrel.exe", 3_866_112),
            Entry("lib/app/SessionDock.deps.json", 62_590),
            Entry("lib/app/SessionDock.runtimeconfig.json", 623),
            Entry("lib/app/sq.version"),
            Entry("lib/app/THIRD_PARTY_NOTICES.md"),
            Entry("lib/app/licenses/DotNet-LICENSE.txt"),
            Entry("lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt"),
            Entry("lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt"),
            Entry("lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt"),
            Entry("lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt"),
            Entry("lib/app/licenses/Velopack-LICENSE.txt")
        };

        for (var index = entries.Count; index < entryCount; index++)
        {
            entries.Add(Entry(
                $"lib/app/runtime/runtime-{index:D4}.dll",
                16 * 1024));
        }

        return entries;
    }

    private static ReleasePackageEntryIdentity Entry(
        string fullName,
        long length = 1,
        long compressedLength = 1) =>
        new(fullName, length, compressedLength);

    private static void SetLength(
        List<ReleasePackageEntryIdentity> entries,
        string name,
        long length)
    {
        var index = entries.FindIndex(entry => entry.FullName == name);
        entries[index] = entries[index] with { Length = length };
    }

    private static PublicPackageManifest ReadPublicV312Manifest()
    {
        using var stream = typeof(ReleasePackagePolicyTests).Assembly
            .GetManifestResourceStream(
                "SessionDock.Tests.TestData.SessionDockApp.3.1.2.PackageManifest.tsv")
            ?? throw new InvalidOperationException(
                "The compact SessionDock v3.1.2 package manifest is unavailable.");
        using var reader = new StreamReader(stream);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 9; index++)
        {
            var parts = (reader.ReadLine() ?? string.Empty).Split('\t', 2);
            if (parts.Length != 2 || !metadata.TryAdd(parts[0], parts[1]))
            {
                throw new InvalidOperationException(
                    "The compact SessionDock v3.1.2 package manifest has invalid metadata.");
            }
        }

        if (!string.Equals(
                reader.ReadLine(),
                "fullName\tlength\tcompressedLength\texternalAttributes",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The compact SessionDock v3.1.2 package manifest has an invalid header.");
        }

        var entries = new List<PublicPackageEntry>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 4)
            {
                throw new InvalidOperationException(
                    "The compact SessionDock v3.1.2 package manifest has an invalid entry.");
            }

            entries.Add(new PublicPackageEntry(
                parts[0],
                long.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture)));
        }

        static int ParseInt(IReadOnlyDictionary<string, string> values, string key) =>
            int.Parse(values[key], CultureInfo.InvariantCulture);
        static long ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
            long.Parse(values[key], CultureInfo.InvariantCulture);

        return new PublicPackageManifest(
            metadata["source"],
            metadata["packageFile"],
            ParseLong(metadata, "packageSize"),
            metadata["packageSha256"],
            ParseInt(metadata, "entryCount"),
            ParseLong(metadata, "totalUncompressedBytes"),
            ParseLong(metadata, "totalCompressedBytes"),
            ParseInt(metadata, "maximumEntryNameLength"),
            metadata["externalAttributes"].Split(',').Select(value =>
                int.Parse(value, CultureInfo.InvariantCulture)).ToArray(),
            entries.ToArray());
    }

    private sealed record PublicPackageManifest(
        string Source,
        string PackageFile,
        long PackageSize,
        string PackageSha256,
        int EntryCount,
        long TotalUncompressedBytes,
        long TotalCompressedBytes,
        int MaximumEntryNameLength,
        int[] ExternalAttributes,
        PublicPackageEntry[] Entries);

    private sealed record PublicPackageEntry(
        string FullName,
        long Length,
        long CompressedLength,
        int ExternalAttributes);

}
