using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SessionDock.ReleaseTrust;

namespace SessionDock.Services;

internal sealed record BundledReleaseNote(
    Version Version,
    string DisplayText,
    string CultureName = LocalizationPreference.English,
    bool IsEnglishFallback = false);

internal sealed record BundledReleaseNotes(
    BundledReleaseNote Current,
    BundledReleaseNote? Previous);

internal static partial class BundledReleaseNotesCatalog
{
    private const string ResourcePrefix =
        "SessionDock.Embedded.ReleaseNotes.";
    private const string ResourceSuffix = ".md";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    public static BundledReleaseNotes CurrentAndPrevious =>
        LoadForCurrentAssembly();

    internal static BundledReleaseNotes LoadForCurrentAssembly(
        CultureInfo? culture = null)
    {
        var assembly = typeof(BundledReleaseNotesCatalog).Assembly;
        var currentVersion = assembly.GetName().Version ??
            throw new InvalidDataException(
                "The application assembly has no release version.");
        var requestedCulture = LocalizationPreference.Resolve(
            LocalizationPreference.System,
            culture ?? CultureInfo.CurrentUICulture);
        return Load(assembly, currentVersion, requestedCulture);
    }

    internal static BundledReleaseNotes Load(
        Assembly assembly,
        Version currentVersion,
        string cultureName = LocalizationPreference.English)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(currentVersion);

        var notes = new List<BundledReleaseNote>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(
                         ResourcePrefix,
                         StringComparison.Ordinal)))
        {
            if (!resourceName.EndsWith(
                    ResourceSuffix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an unsupported name.");
            }

            var resourceIdentity = resourceName[
                ResourcePrefix.Length..^ResourceSuffix.Length];
            var identityMatch = ReleaseResourcePattern().Match(
                resourceIdentity);
            if (!identityMatch.Success)
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an invalid version or culture.");
            }

            var versionText = identityMatch.Groups["version"].Value;
            var noteCulture = identityMatch.Groups["culture"].Success
                ? identityMatch.Groups["culture"].Value
                : LocalizationPreference.English;
            if (!Version.TryParse(versionText, out var version) ||
                !version.ToString(3).Equals(
                    versionText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an invalid version.");
            }
            if (!LocalizationPreference.SupportedValues
                    .Skip(1)
                    .Contains(noteCulture, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an unsupported culture.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName) ??
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' could not be opened.");
            if (stream.Length >
                ReleaseDescriptorPolicy.MaximumReleaseNotesLength * 4L + 3)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} exceed the supported size.");
            }

            string markdown;
            try
            {
                using var reader = new StreamReader(
                    stream,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: false);
                markdown = reader.ReadToEnd();
                if (markdown.StartsWith('\uFEFF'))
                    markdown = markdown[1..];
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} are not valid UTF-8.",
                    exception);
            }

            try
            {
                var displayText = ReleaseNotesTextFormatter.Format(markdown);
                if (string.IsNullOrWhiteSpace(displayText))
                {
                    throw new InvalidDataException(
                        $"Bundled release notes for {versionText} are empty.");
                }
                notes.Add(new BundledReleaseNote(
                    version,
                    displayText,
                    noteCulture));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} are invalid.",
                    exception);
            }
        }

        return Select(currentVersion, notes, cultureName);
    }

    internal static BundledReleaseNotes Select(
        Version currentVersion,
        IEnumerable<BundledReleaseNote> notes,
        string cultureName = LocalizationPreference.English)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(notes);
        var requestedCulture = LocalizationPreference.Normalize(cultureName);
        if (requestedCulture.Equals(
                LocalizationPreference.System,
                StringComparison.Ordinal))
        {
            requestedCulture = LocalizationPreference.English;
        }
        var installedVersion = Normalize(currentVersion);
        var ordered = notes
            .OrderBy(note => note.Version)
            .ThenBy(note => note.CultureName, StringComparer.Ordinal)
            .ToArray();
        if (ordered
            .GroupBy(note => (note.Version, note.CultureName))
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Bundled release notes contain a duplicate version and culture.");
        }

        var current = SelectLocalizedNote(
            installedVersion,
            ordered,
            requestedCulture);
        var previousVersion = ordered
            .Select(note => note.Version)
            .Distinct()
            .Where(version => version < installedVersion)
            .LastOrDefault();
        var previous = previousVersion is null
            ? null
            : SelectLocalizedNote(
                previousVersion,
                ordered,
                requestedCulture);
        return new BundledReleaseNotes(current, previous);
    }

    private static BundledReleaseNote SelectLocalizedNote(
        Version version,
        IReadOnlyCollection<BundledReleaseNote> notes,
        string requestedCulture)
    {
        var localized = notes.SingleOrDefault(note =>
            note.Version == version &&
            note.CultureName.Equals(
                requestedCulture,
                StringComparison.Ordinal));
        if (localized is not null)
            return localized with { IsEnglishFallback = false };

        var english = notes.SingleOrDefault(note =>
            note.Version == version &&
            note.CultureName.Equals(
                LocalizationPreference.English,
                StringComparison.Ordinal));
        if (english is null)
        {
            throw new InvalidDataException(
                $"Bundled release notes for SessionDock {version.ToString(3)} are unavailable.");
        }

        return english with
        {
            IsEnglishFallback = !requestedCulture.Equals(
                LocalizationPreference.English,
                StringComparison.Ordinal)
        };
    }

    private static Version Normalize(Version version)
    {
        if (version.Build < 0)
        {
            throw new InvalidDataException(
                "The application release version must contain major, minor, and patch components.");
        }
        return new Version(version.Major, version.Minor, version.Build);
    }

    [GeneratedRegex(
        "^(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)(?:\\.(?<culture>[a-z]{2}-[A-Z]{2}))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseResourcePattern();
}
