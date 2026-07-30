using System.Globalization;

namespace SessionDock.Services;

internal static class LocalizationPreference
{
    internal const string System = "system";
    internal const string English = "en-US";
    internal const string Dutch = "nl-NL";
    internal const string German = "de-DE";
    internal const string French = "fr-FR";
    internal const string Spanish = "es-ES";

    internal static IReadOnlyList<string> SupportedValues { get; } =
        [System, English, Dutch, German, French, Spanish];

    internal static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase))
            return System;
        if (string.Equals(trimmed, English, StringComparison.OrdinalIgnoreCase))
            return English;
        if (string.Equals(trimmed, Dutch, StringComparison.OrdinalIgnoreCase))
            return Dutch;
        if (string.Equals(trimmed, German, StringComparison.OrdinalIgnoreCase))
            return German;
        if (string.Equals(trimmed, French, StringComparison.OrdinalIgnoreCase))
            return French;
        if (string.Equals(trimmed, Spanish, StringComparison.OrdinalIgnoreCase))
            return Spanish;
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

        return systemCulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "nl" => Dutch,
            "de" => German,
            "fr" => French,
            "es" => Spanish,
            _ => English
        };
    }
}
