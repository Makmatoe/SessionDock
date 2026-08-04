using SessionDock.Models;

namespace SessionDock.Services;

internal static class AccountRemovalSettingsPolicy
{
    internal static int RemoveAccounts(
        AppSettings settings,
        IEnumerable<string> accountKeys)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accountKeys);
        settings.Accounts ??= [];

        var removedKeys = accountKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = removedKeys.Count == 0
            ? 0
            : settings.Accounts.RemoveAll(account =>
                account is not null && removedKeys.Contains(account.Key));

        _ = NamedDestinationPolicy.NormalizeInPlace(settings);
        BatchLaunchPreferences.PrunePresetsForCurrentAccounts(settings);

        var active = string.IsNullOrWhiteSpace(settings.ActiveAccountKey)
            ? null
            : settings.Accounts.FirstOrDefault(account =>
                account is not null &&
                string.Equals(
                    account.Key,
                    settings.ActiveAccountKey,
                    StringComparison.OrdinalIgnoreCase));
        settings.ActiveAccountKey = active?.Key ??
            settings.Accounts.FirstOrDefault(account => account is not null)?.Key;
        return removed;
    }
}
