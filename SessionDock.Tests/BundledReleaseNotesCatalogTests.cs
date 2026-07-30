using System.Globalization;
using System.IO;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BundledReleaseNotesCatalogTests
{
    [Fact]
    public void Select_UsesInstalledAndNearestLowerSemanticVersion()
    {
        var notes = new[]
        {
            CreateNote("2.11.0"),
            CreateNote("2.8.0"),
            CreateNote("2.10.0"),
            CreateNote("2.9.9")
        };

        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 10, 0, 42),
            notes);

        Assert.Equal(new Version(2, 10, 0), selected.Current.Version);
        Assert.Equal(new Version(2, 9, 9), selected.Previous!.Version);
    }

    [Fact]
    public void Select_MissingCurrentVersionIsExplicitlyRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BundledReleaseNotesCatalog.Select(
                new Version(2, 10, 0),
                [CreateNote("2.9.9"), CreateNote("2.11.0")]));

        Assert.Contains("2.10.0", exception.Message);
        Assert.Contains("unavailable", exception.Message);
    }

    [Fact]
    public void Select_FirstReleaseCanHaveNoPreviousNotes()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(1, 0, 0),
            [CreateNote("1.0.0"), CreateNote("1.1.0")]);

        Assert.Equal(new Version(1, 0, 0), selected.Current.Version);
        Assert.Null(selected.Previous);
    }

    [Fact]
    public void Select_DuplicateVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            BundledReleaseNotesCatalog.Select(
                new Version(2, 3, 1),
                [CreateNote("2.3.1"), CreateNote("2.3.1")]));
    }

    [Fact]
    public void Select_AllowsSameVersionInDifferentCultures()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 3, 1),
            [
                CreateNote("2.3.1", LocalizationPreference.English),
                CreateNote("2.3.1", LocalizationPreference.Dutch)
            ],
            LocalizationPreference.Dutch);

        Assert.Equal(LocalizationPreference.Dutch, selected.Current.CultureName);
        Assert.False(selected.Current.IsEnglishFallback);
    }

    [Fact]
    public void Select_UsesEnglishFallbackAndMarksItExplicitly()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 3, 1),
            [CreateNote("2.3.1", LocalizationPreference.English)],
            LocalizationPreference.French);

        Assert.Equal(LocalizationPreference.English, selected.Current.CultureName);
        Assert.True(selected.Current.IsEnglishFallback);
    }

    [Fact]
    public void Select_ExplicitEnglishIsNotMarkedAsFallback()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 3, 1),
            [CreateNote("2.3.1", LocalizationPreference.English)],
            LocalizationPreference.English);

        Assert.False(selected.Current.IsEnglishFallback);
    }

    [Fact]
    public void Select_LocalizesCurrentAndFallsBackPreviousIndependently()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 3, 1),
            [
                CreateNote("2.3.0", LocalizationPreference.English),
                CreateNote("2.3.1", LocalizationPreference.English),
                CreateNote("2.3.1", LocalizationPreference.German)
            ],
            LocalizationPreference.German);

        Assert.Equal(LocalizationPreference.German, selected.Current.CultureName);
        Assert.False(selected.Current.IsEnglishFallback);
        Assert.Equal(LocalizationPreference.English, selected.Previous!.CultureName);
        Assert.True(selected.Previous.IsEnglishFallback);
    }

    [Fact]
    public void LoadForCurrentAssembly_ContainsReadableCurrentAndPreviousNotes()
    {
        var assembly = typeof(MainWindow).Assembly;
        var assemblyVersion = assembly.GetName().Version!;
        var expectedCurrent = new Version(
            assemblyVersion.Major,
            assemblyVersion.Minor,
            assemblyVersion.Build);

        foreach (var cultureName in LocalizationPreference.SupportedValues.Skip(1))
        {
            var notes = BundledReleaseNotesCatalog.LoadForCurrentAssembly(
                CultureInfo.GetCultureInfo(cultureName));

            Assert.Equal(expectedCurrent, notes.Current.Version);
            Assert.Equal(cultureName, notes.Current.CultureName);
            Assert.False(notes.Current.IsEnglishFallback);
            Assert.NotNull(notes.Previous);
            Assert.True(notes.Previous.Version < notes.Current.Version);
            Assert.Contains(
                $"SessionDock {notes.Current.Version.ToString(3)}",
                notes.Current.DisplayText);
            Assert.Contains(
                $"SessionDock {notes.Previous.Version.ToString(3)}",
                notes.Previous.DisplayText);
            Assert.DoesNotContain("# SessionDock", notes.Current.DisplayText);
            Assert.DoesNotContain("**", notes.Current.DisplayText);
            Assert.DoesNotContain("# SessionDock", notes.Previous.DisplayText);
            Assert.DoesNotContain("**", notes.Previous.DisplayText);
            var localizedPreviousResource =
                $"SessionDock.Embedded.ReleaseNotes.{notes.Previous.Version.ToString(3)}.{cultureName}.md";
            var previousHasRequestedCulture = assembly
                .GetManifestResourceNames()
                .Contains(
                    localizedPreviousResource,
                    StringComparer.Ordinal);
            Assert.Equal(
                previousHasRequestedCulture
                    ? cultureName
                    : LocalizationPreference.English,
                notes.Previous.CultureName);
            Assert.Equal(
                !previousHasRequestedCulture &&
                    cultureName != LocalizationPreference.English,
                notes.Previous.IsEnglishFallback);
        }
    }

    [Fact]
    public void ExpectedCatalogFailuresAreContainedWithoutHidingProgrammerFaults()
    {
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new InvalidDataException()));
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new IOException()));
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new UnauthorizedAccessException()));
        Assert.False(MainWindow.IsExpectedReleaseNotesFailure(
            new InvalidOperationException()));
    }

    private static BundledReleaseNote CreateNote(
        string version,
        string cultureName = LocalizationPreference.English) =>
        new(
            new Version(version),
            $"Notes for {version} ({cultureName})",
            cultureName);
}
