namespace SessionDock.Tests;

public sealed class InstallationDocumentationTests
{
    private const string InstallLabel = "Install Latest SessionDock release";
    private const string LatestSetupUrl =
        "https://github.com/Makmatoe/SessionDock/releases/latest/download/" +
        "SessionDock-win-x64-Setup.exe";

    [Fact]
    public void EveryReadmeOffersTheCanonicalOneClickSetupDownload()
    {
        var root = FindRepositoryRoot();
        var readmes = Directory.EnumerateFiles(
                root,
                "README*",
                SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrMetadataPath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedReadmes = new[]
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
        }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            expectedReadmes,
            readmes.Select(path => Path.GetRelativePath(root, path)));
        foreach (var readme in readmes)
        {
            var contents = File.ReadAllText(readme);
            Assert.Contains(InstallLabel, contents, StringComparison.Ordinal);
            Assert.Contains(LatestSetupUrl, contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallButtonIsRepositoryOwnedAndUpdatesGuideUsesSameSetupUrl()
    {
        var root = FindRepositoryRoot();
        var buttonPath = Path.Combine(
            root,
            "docs",
            "assets",
            "install-latest-sessiondock.svg");
        var button = File.ReadAllText(buttonPath);
        var updates = File.ReadAllText(Path.Combine(root, "docs", "UPDATES.md"));

        Assert.Contains(InstallLabel, button, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", button, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LatestSetupUrl, updates, StringComparison.Ordinal);
    }

    private static bool IsGeneratedOrMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals(
            ".git",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "artifacts",
            StringComparison.OrdinalIgnoreCase) || segment.Equals(
            "bin",
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
