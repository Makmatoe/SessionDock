using System.Text.RegularExpressions;

namespace SessionDock.Tests;

public sealed class InstallationDocumentationTests
{
    private const string DistributionHold = "Distribution hold — 2026-08-04";
    private const string PortableFileName = "SessionDock-win-x64-Portable.zip";
    private const string RetiredSetupFileName = "SessionDock-win-x64-Setup.exe";
    private const string ExactWheelCommit =
        "f32799820fb4a31089523beb184314542f4fe521";

    [Fact]
    public void EveryProductReadmeHonorsTheDistributionHold()
    {
        var root = FindRepositoryRoot();
        var readmes = Directory.EnumerateFiles(
                root,
                "README*",
                SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrMetadataPath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedReadmes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "README.md",
            Path.Combine(
                "docs",
                "images",
                "sessiondock-v2.7.0",
                "README.md"),
            Path.Combine("marketing", "README.md"),
            Path.Combine(
                "marketing",
                "trusted",
                "v2.7.0",
                "README.md"),
            Path.Combine("SessionDock", "README.md"),
            Path.Combine(
                "SessionDock",
                "SystemProcesses",
                "README.md")
        };

        Assert.Equal(
            expectedReadmes.OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase),
            readmes.Select(path => Path.GetRelativePath(root, path)));

        foreach (var readme in readmes)
        {
            var contents = File.ReadAllText(readme);
            Assert.Contains(
                "distribution hold",
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                RetiredSetupFileName,
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(
                @"https://github\.com/Makmatoe/SessionDock/releases/(?:latest/)?download/[^\s)]*SessionDock-win-x64-Setup\.exe",
                contents);
        }
    }

    [Fact]
    public void CurrentUserDocumentationRetainsHoldAndFuturePortableFlow()
    {
        var root = FindRepositoryRoot();
        var rootReadme = Read(root, "README.md");
        var gettingStarted = Read(root, "docs", "GETTING_STARTED.md");
        var updates = Read(root, "docs", "UPDATES.md");
        var desktopReadme = Read(root, "SessionDock", "README.md");
        var integrationsReadme = Read(
            root,
            "SessionDock",
            "SystemProcesses",
            "README.md");
        var security = Read(root, "SECURITY.md");

        var currentDocuments = new[]
        {
            rootReadme,
            gettingStarted,
            updates,
            desktopReadme,
            integrationsReadme,
            security
        };

        Assert.All(currentDocuments, contents =>
        {
            Assert.Contains(DistributionHold, contents, StringComparison.Ordinal);
            Assert.DoesNotContain(
                RetiredSetupFileName,
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Azure Artifact Signing",
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "SignPath",
                contents,
                StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains("zero-asset security-hold", updates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zero-asset security-hold", gettingStarted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Download nothing", gettingStarted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lifts the hold", updates,
            StringComparison.OrdinalIgnoreCase);

        foreach (var contents in new[] { rootReadme, gettingStarted, updates })
        {
            Assert.Contains(PortableFileName, contents, StringComparison.Ordinal);
            Assert.Contains("GitHub Releases", contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Extract All", contents, StringComparison.Ordinal);
            Assert.Contains("new folder", contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SessionDock.exe", contents, StringComparison.Ordinal);
            Assert.Contains("first-launch", contents,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CurrentDocumentationExplainsUnsignedSafetyAndUpdateSplit()
    {
        var root = FindRepositoryRoot();
        var rootReadme = Read(root, "README.md");
        var updates = Read(root, "docs", "UPDATES.md");
        var detectionResponse = Read(
            root,
            "docs",
            "DEFENDER_DETECTION_RESPONSE.md");

        foreach (var contents in new[] { rootReadme, updates, detectionResponse })
        {
            Assert.Contains("Unknown publisher", contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("named", contents, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("detection", contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hash", contents, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("never override", rootReadme,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never disable Defender", rootReadme,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Portable copies", updates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update manually", updates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing installed", updates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NUPKG", updates, StringComparison.Ordinal);
        Assert.Contains("Discord", updates, StringComparison.Ordinal);
        Assert.Contains("never download a SessionDock", updates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("withdraw the affected release assets", detectionResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two recognized unsigned Velopack", detectionResponse,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintainerGuideDefinesTransparentUnsignedReleaseInventory()
    {
        var root = FindRepositoryRoot();
        var releasing = Read(root, "docs", "RELEASING.md");

        foreach (var fileName in new[]
                 {
                     "SessionDock.exe",
                     "SessionDock.dll",
                     "SessionDock.HandleScope.dll",
                     "SessionDock.ExactWheel.dll",
                     "SessionDock.ReleaseTrust.dll",
                     "Velopack.dll"
                 })
        {
            Assert.Contains(fileName, releasing, StringComparison.Ordinal);
        }

        foreach (var requiredControl in new[]
                 {
                     "portable ZIP",
                     "full NUPKG",
                     "feed",
                     "SBOM",
                     "SHA256SUMS.txt",
                     "descriptor",
                     "attestation",
                     "-DisableRemediation",
                     "draft",
                     "public",
                     "laptop",
                     "before publication",
                     ExactWheelCommit,
                     "ExactWheel provenance pins 14",
                     "implementation/lock files",
                     "separately pinned current",
                     "build definition",
                     "root MIT license",
                     "SessionDock_ExecutionStub.exe",
                     "Squirrel.exe",
                     "Velopack 1.2.0",
                     "NUPKG-only helpers",
                     "vendor code sections"
                 })
        {
            Assert.Contains(requiredControl, releasing,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(DistributionHold, releasing, StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredSetupFileName, releasing,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Azure Artifact Signing", releasing,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @".\artifacts\SessionDock-win-x64-Portable.zip",
            releasing,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workflow does not replace", releasing,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release-publication", releasing,
            StringComparison.Ordinal);
        Assert.Contains("zero-asset security-hold", releasing,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separate immutable announcement", releasing,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetiredSetupAndManualAnnouncementArtifactsAreAbsent()
    {
        var root = FindRepositoryRoot();

        Assert.False(File.Exists(Path.Combine(
            root,
            "docs",
            "assets",
            "install-latest-sessiondock.svg")));
        Assert.False(File.Exists(Path.Combine(
            root,
            "SessionDock-Discord-Announcement.md")));

        foreach (var contents in ReadMaintainedDocumentation(root))
        {
            Assert.DoesNotContain(
                "TinyClicks",
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "Azure Artifact Signing",
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "SignPath",
                contents,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CurrentDocumentationDescribesDestinationAndMacroDataLifecycle()
    {
        var root = FindRepositoryRoot();
        var rootReadme = Read(root, "README.md");
        var security = Read(root, "SECURITY.md");
        var privacy = Read(root, "docs", "PRIVACY.md");
        var gettingStarted = Read(root, "docs", "GETTING_STARTED.md");
        var templates = Read(root, "docs", "TEMPLATES_AND_MACROS.md");
        var updates = Read(root, "docs", "UPDATES.md");
        var desktopReadme = Read(root, "SessionDock", "README.md");
        var contributing = Read(root, "CONTRIBUTING.md");
        var announcementReadme = Read(
            root,
            "discord-release-bot",
            "README.md");

        Assert.Contains("saved destination", rootReadme,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-slot destinations", gettingStarted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolved per-slot destinations", privacy,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matching eligible named-destination", templates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Private-server and tracked-server", templates,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("automatically includes", privacy,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("library-wide", privacy,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automatically selects", privacy,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("also selects its required macros", gettingStarted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("richer versioned `.sessiondock` ZIP", security,
            StringComparison.Ordinal);
        Assert.Contains("SessionDock_ExecutionStub.exe", security,
            StringComparison.Ordinal);
        Assert.Contains("NUPKG-only helpers", security,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SessionDock.HandleScope.dll", contributing,
            StringComparison.Ordinal);
        Assert.DoesNotContain("embeds it only in `SessionDock.exe`", contributing,
            StringComparison.OrdinalIgnoreCase);

        foreach (var contents in new[]
                 {
                     rootReadme,
                     gettingStarted,
                     templates,
                     desktopReadme
                 })
        {
            Assert.DoesNotContain(
                "confirmed unreferenced removal retains the payload",
                contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "removal never deletes the content-addressed payload",
                contents,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("exact unreferenced", gettingStarted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "## Moving from Roblox One or SessionDock 2.3.0 and earlier",
            updates,
            StringComparison.Ordinal);
        Assert.Contains(DistributionHold, announcementReadme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaintainedMarkdownRelativeLinksAndAnchorsResolve()
    {
        var root = FindRepositoryRoot();
        var errors = new List<string>();
        var linkPattern = new Regex(
            @"(?<!!)\[[^\]]+\]\((?<target>[^)\s]+)",
            RegexOptions.CultureInvariant);

        foreach (var file in EnumerateMaintainedMarkdownFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (Match match in linkPattern.Matches(lines[lineIndex]))
                {
                    var target = match.Groups["target"].Value.Trim('<', '>');
                    if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fragmentIndex = target.IndexOf('#');
                    var relativePath = fragmentIndex < 0
                        ? target
                        : target[..fragmentIndex];
                    var fragment = fragmentIndex < 0
                        ? null
                        : Uri.UnescapeDataString(target[(fragmentIndex + 1)..])
                            .ToLowerInvariant();
                    var targetFile = string.IsNullOrEmpty(relativePath)
                        ? file
                        : Path.GetFullPath(Path.Combine(
                            Path.GetDirectoryName(file)!,
                            Uri.UnescapeDataString(relativePath)
                                .Replace('/', Path.DirectorySeparatorChar)));

                    if (!File.Exists(targetFile) && !Directory.Exists(targetFile))
                    {
                        errors.Add(
                            $"{Path.GetRelativePath(root, file)}:{lineIndex + 1} " +
                            $"targets missing '{target}'.");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(fragment) &&
                        File.Exists(targetFile) &&
                        Path.GetExtension(targetFile).Equals(
                            ".md",
                            StringComparison.OrdinalIgnoreCase) &&
                        !GetMarkdownHeadingSlugs(targetFile).Contains(fragment))
                    {
                        errors.Add(
                            $"{Path.GetRelativePath(root, file)}:{lineIndex + 1} " +
                            $"targets missing anchor '#{fragment}'.");
                    }
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static IReadOnlyList<string> ReadMaintainedDocumentation(string root) =>
        EnumerateMaintainedMarkdownFiles(root)
            .Select(File.ReadAllText)
            .ToArray();

    private static IEnumerable<string> EnumerateMaintainedMarkdownFiles(
        string root) =>
        Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrHistoricalDocumentation(root, path));

    private static bool IsGeneratedOrHistoricalDocumentation(
        string root,
        string path)
    {
        var relative = Path.GetRelativePath(root, path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment is
                "bin" or "obj" or "node_modules" or ".git" or ".codex-temp"))
        {
            return true;
        }

        return relative.StartsWith(
                   $"SessionDock{Path.DirectorySeparatorChar}ReleaseNotes{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(
                   $"docs{Path.DirectorySeparatorChar}images{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(
                   $"marketing{Path.DirectorySeparatorChar}trusted{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedOrMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals(
            ".codex-temp",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            ".git",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "artifacts",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "bin",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "discord-release-bot",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "node_modules",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "obj",
            StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> GetMarkdownHeadingSlugs(string path)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var duplicateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var match = Regex.Match(
                line,
                @"^#{1,6}\s+(?<heading>.+?)\s*#*\s*$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var slug = Regex.Replace(
                match.Groups["heading"].Value.ToLowerInvariant(),
                @"[^\p{L}\p{Nd}\s_-]",
                string.Empty,
                RegexOptions.CultureInvariant);
            slug = Regex.Replace(
                slug,
                @"\s+",
                "-",
                RegexOptions.CultureInvariant);
            duplicateCounts.TryGetValue(slug, out var duplicateCount);
            duplicateCounts[slug] = duplicateCount + 1;
            if (duplicateCount > 0)
                slug = $"{slug}-{duplicateCount}";
            slugs.Add(slug);
        }

        return slugs;
    }

    private static string Read(string root, params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine([root, .. relativeSegments]));

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
