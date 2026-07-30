using System.Globalization;
using System.Windows;

namespace SessionDock.Services;

internal sealed class LocalizedTextSnapshot
{
    private const string ResourcePrefix =
        "/SessionDock;component/Localization/Strings.";
    private const string ResourceSuffix = ".xaml";
    private static readonly object ResourceLoadLock = new();
    private readonly IReadOnlyDictionary<string, string> _strings;

    private LocalizedTextSnapshot(
        CultureInfo culture,
        IReadOnlyDictionary<string, string> strings)
    {
        Culture = CultureInfo.ReadOnly(
            culture ?? throw new ArgumentNullException(nameof(culture)));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    internal CultureInfo Culture { get; }

    internal static LocalizedTextSnapshot Capture(
        AppLocalizationService localization,
        IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return Capture(
            localization.EffectiveCulture,
            localization.GetString,
            keys);
    }

    internal static LocalizedTextSnapshot Capture(
        CultureInfo culture,
        Func<string, string> getString,
        IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(keys);

        var strings = keys
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                key => key,
                getString,
                StringComparer.Ordinal);
        return new LocalizedTextSnapshot(culture, strings);
    }

    internal static LocalizedTextSnapshot FromResources(
        CultureInfo requestedCulture,
        IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(requestedCulture);
        ArgumentNullException.ThrowIfNull(keys);

        var cultureName = LocalizationPreference.Resolve(
            requestedCulture.Name,
            requestedCulture);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var english = LoadResourceDictionary(LocalizationPreference.English);
        var localized = cultureName.Equals(
            LocalizationPreference.English,
            StringComparison.Ordinal)
                ? null
                : LoadResourceDictionary(cultureName);
        return Capture(
            culture,
            key => FindString(localized, key) ??
                FindString(english, key) ??
                key,
            keys);
    }

    internal string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _strings.TryGetValue(key, out var value) ? value : key;
    }

    internal string Format(string key, params object?[] arguments) =>
        LocalizationCulture.Format(
            Culture,
            GetString(key),
            arguments);

    private static ResourceDictionary LoadResourceDictionary(
        string cultureName)
    {
        lock (ResourceLoadLock)
        {
            return (ResourceDictionary)Application.LoadComponent(
                new Uri(
                    $"{ResourcePrefix}{cultureName}{ResourceSuffix}",
                    UriKind.Relative));
        }
    }

    private static string? FindString(
        ResourceDictionary? dictionary,
        string key) =>
        dictionary?.Contains(key) == true
            ? dictionary[key] as string
            : null;
}
