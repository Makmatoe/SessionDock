using System.Globalization;

namespace SessionDock.Services;

internal static class LocalizationCulture
{
    internal static string FormatLocalDateTime(
        DateTimeOffset value,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return value.ToLocalTime().ToString("g", culture);
    }

    internal static string Format(
        CultureInfo culture,
        string format,
        params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Format(culture, format, arguments);
    }
}
