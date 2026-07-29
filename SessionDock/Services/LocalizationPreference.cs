using System.Globalization;

namespace SessionDock.Services;

internal static class LocalizationPreference
{
    internal const string System = "system";
    internal const string English = "en-US";
    internal const string Dutch = "nl-NL";

    internal static IReadOnlyList<string> SupportedValues { get; } =
        [System, English, Dutch];

    internal static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase))
            return System;
        if (string.Equals(trimmed, English, StringComparison.OrdinalIgnoreCase))
            return English;
        if (string.Equals(trimmed, Dutch, StringComparison.OrdinalIgnoreCase))
            return Dutch;
        return System;
    }

    internal static string Resolve(
        string? preference,
        CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);
        var normalized = Normalize(preference);
        if (!normalized.Equals(System, StringComparison.Ordinal))
            return normalized;

        return systemCulture.TwoLetterISOLanguageName.Equals(
            "nl",
            StringComparison.OrdinalIgnoreCase)
                ? Dutch
                : English;
    }
}
