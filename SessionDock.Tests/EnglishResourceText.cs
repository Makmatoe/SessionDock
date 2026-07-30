using System.Collections.Concurrent;
using System.Globalization;
using SessionDock.Services;

namespace SessionDock.Tests;

internal static class EnglishResourceText
{
    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.Ordinal);
    private static readonly object ResourceLoadLock = new();

    internal static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Cache.GetOrAdd(
            key,
            static resourceKey => Load(resourceKey));
    }

    private static string Load(string key)
    {
        lock (ResourceLoadLock)
        {
            return LocalizedTextSnapshot.FromResources(
                    CultureInfo.GetCultureInfo(LocalizationPreference.English),
                    [key])
                .GetString(key);
        }
    }
}
