using System.Text;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class ReleaseNotesPublicationPolicyTests
{
    [Fact]
    public void CurrentCanonicalEnglishNotes_FitEveryPrePublicationConsumer()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "SessionDock.csproj"));
        var versions = project
            .Descendants("Version")
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var version = Assert.Single(versions);
        Assert.Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", version);

        string? englishDescription = null;
        foreach (var culture in new[] { "de-DE", "en-US", "es-ES", "fr-FR", "nl-NL" })
        {
            var notesPath = Path.Combine(
                root,
                "SessionDock",
                "ReleaseNotes",
                $"{version}.{culture}.md");
            var bytes = File.ReadAllBytes(notesPath);
            Assert.NotEmpty(bytes);
            Assert.False(bytes.Length >= 3 &&
                         bytes[0] == 0xEF &&
                         bytes[1] == 0xBB &&
                         bytes[2] == 0xBF);

            var text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotMatch("[\\x00-\\x08\\x0B-\\x1F\\x7F]", text);
            Assert.True(text.EndsWith('\n'));
            Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal));

            var lines = text[..^1].Split('\n');
            Assert.True(lines.Length >= 3);
            Assert.Equal($"SessionDock {version}", lines[0]);
            Assert.Empty(lines[1]);
            var description = string.Join('\n', lines.Skip(2));
            Assert.False(string.IsNullOrWhiteSpace(description));
            if (culture == "en-US")
            {
                englishDescription = description;
            }
        }

        Assert.NotNull(englishDescription);
        Assert.InRange(englishDescription.Length, 1, 4096);

        var common = File.ReadAllText(Path.Combine(root, "scripts", "Common.ps1"));
        var repositoryVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Repository.ps1"));
        var releaseVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Release.ps1"));

        Assert.Contains("function Read-CanonicalReleaseNotes", common,
            StringComparison.Ordinal);
        Assert.Contains("function Assert-DiscordCompatibleReleaseNotes", common,
            StringComparison.Ordinal);
        Assert.Contains("(0|[1-9]\\d*)", common, StringComparison.Ordinal);
        Assert.Contains("[\\x00-\\x08\\x0B-\\x1F\\x7F]", common,
            StringComparison.Ordinal);
        Assert.Contains("$description.Length -gt 4096", common,
            StringComparison.Ordinal);
        Assert.Contains("Read-CanonicalReleaseNotes", repositoryVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Assert-DiscordCompatibleReleaseNotes", repositoryVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Assert-DiscordCompatibleReleaseNotes", releaseVerifier,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
