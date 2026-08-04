namespace SessionDock.Tests;

public sealed class InstallationDocumentationTests
{
    private const string LatestInstallLabel = "Install Latest SessionDock release";
    private const string LatestSetupUrl =
        "https://github.com/Makmatoe/SessionDock/releases/latest/download/" +
        "SessionDock-win-x64-Setup.exe";
    private const string DistributionHold = "Distribution hold — 2026-08-04";

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
            Assert.Contains("distribution hold", contents,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LatestSetupUrl, contents,
                StringComparison.Ordinal);
            Assert.DoesNotMatch(
                @"https://github\.com/Makmatoe/SessionDock/releases/(?:latest/)?download/[^\s)]*SessionDock-win-x64-Setup\.exe",
                contents);
        }
    }

    [Fact]
    public void InstallButtonIsDormantUntilAReviewedReleaseLiftsTheHold()
    {
        var root = FindRepositoryRoot();
        var buttonPath = Path.Combine(
            root,
            "docs",
            "assets",
            "install-latest-sessiondock.svg");
        var button = File.ReadAllText(buttonPath);
        var updates = File.ReadAllText(Path.Combine(root, "docs", "UPDATES.md"));
        var gettingStarted = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "GETTING_STARTED.md"));
        var readmes = Directory.EnumerateFiles(root, "README*", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrMetadataPath(root, path))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains(LatestInstallLabel, button, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", button, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DistributionHold, updates, StringComparison.Ordinal);
        Assert.Contains(DistributionHold, gettingStarted, StringComparison.Ordinal);
        Assert.DoesNotContain(LatestSetupUrl, updates, StringComparison.Ordinal);
        Assert.DoesNotContain(LatestSetupUrl, gettingStarted, StringComparison.Ordinal);
        Assert.All(readmes, contents =>
            Assert.DoesNotContain("install-latest-sessiondock.svg", contents,
                StringComparison.OrdinalIgnoreCase));
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
