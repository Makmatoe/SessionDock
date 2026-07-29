using System.Globalization;
using System.Text;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class BatchLaunchPreferences
{
    internal const int DefaultDelaySeconds = 8;
    internal const int MaximumAccountGroupLength = 40;
    internal const int MaximumPresetNameLength = 40;
    internal const int MaximumPresets = 24;
    internal const int MaximumAccountsPerPreset = 64;

    internal static readonly IReadOnlyList<int> SupportedDelaySeconds =
        [3, 5, 8, 10, 15, 20, 30];

    internal static int NormalizeDelaySeconds(int delaySeconds) =>
        SupportedDelaySeconds.Contains(delaySeconds)
            ? delaySeconds
            : DefaultDelaySeconds;

    internal static string? NormalizeAccountGroup(string? group) =>
        NormalizeDisplayText(group, MaximumAccountGroupLength);

    internal static string? NormalizePresetName(string? name) =>
        NormalizeDisplayText(name, MaximumPresetNameLength);

    internal static IReadOnlyList<BatchLaunchPreset> NormalizePresets(
        IEnumerable<BatchLaunchPreset>? presets,
        IEnumerable<AccountProfile>? accounts)
    {
        var accountsByKey = (accounts ?? [])
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Key,
                StringComparer.OrdinalIgnoreCase);
        var normalized = new List<BatchLaunchPreset>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in presets ?? [])
        {
            if (preset is null || normalized.Count >= MaximumPresets)
                continue;

            var name = NormalizePresetName(preset.Name);
            if (name is null || names.Contains(name))
                continue;

            var keys = (preset.AccountKeys ?? [])
                .Where(key =>
                    !string.IsNullOrWhiteSpace(key) &&
                    accountsByKey.ContainsKey(key))
                .Select(key => accountsByKey[key])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAccountsPerPreset)
                .ToList();
            if (keys.Count < 2)
                continue;

            names.Add(name);
            normalized.Add(new BatchLaunchPreset
            {
                Name = name,
                AccountKeys = keys,
                DelaySeconds = NormalizeDelaySeconds(preset.DelaySeconds)
            });
        }

        return normalized;
    }

    internal static bool AreEquivalent(
        IEnumerable<BatchLaunchPreset>? first,
        IEnumerable<BatchLaunchPreset>? second)
    {
        var left = (first ?? []).ToArray();
        var right = (second ?? []).ToArray();
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!string.Equals(
                    left[index]?.Name,
                    right[index]?.Name,
                    StringComparison.Ordinal) ||
                left[index]?.DelaySeconds != right[index]?.DelaySeconds ||
                !(left[index]?.AccountKeys ?? []).SequenceEqual(
                    right[index]?.AccountKeys ?? [],
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryCreatePreset(
        string? name,
        IEnumerable<AccountProfile> selectedAccounts,
        int delaySeconds,
        out BatchLaunchPreset? preset,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        preset = null;
        var normalizedName = NormalizePresetName(name);
        if (normalizedName is null)
        {
            error = "Enter a name for this preset.";
            return false;
        }

        var accountKeys = selectedAccounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .Select(account => account.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accountKeys.Count < 2)
        {
            error = "Select at least two accounts before saving a preset.";
            return false;
        }
        if (accountKeys.Count > MaximumAccountsPerPreset)
        {
            error =
                $"A preset can include up to {MaximumAccountsPerPreset} accounts.";
            return false;
        }

        preset = new BatchLaunchPreset
        {
            Name = normalizedName,
            AccountKeys = accountKeys,
            DelaySeconds = NormalizeDelaySeconds(delaySeconds)
        };
        error = string.Empty;
        return true;
    }

    internal static IReadOnlyList<AccountProfile> ResolveAccounts(
        IEnumerable<string>? accountKeys,
        IEnumerable<AccountProfile> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var accountsByKey = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        return (accountKeys ?? [])
            .Where(key =>
                !string.IsNullOrWhiteSpace(key) &&
                accountsByKey.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => accountsByKey[key])
            .ToArray();
    }

    internal static void PrunePresetsForCurrentAccounts(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.BatchLaunchPresets = NormalizePresets(
                settings.BatchLaunchPresets,
                settings.Accounts)
            .ToList();
    }

    internal static IReadOnlyList<string> GetRetryAccountKeys(
        IEnumerable<string?> failedAccountKeys,
        IEnumerable<AccountProfile> accounts)
    {
        ArgumentNullException.ThrowIfNull(failedAccountKeys);
        return ResolveAccounts(
                failedAccountKeys.OfType<string>(),
                accounts)
            .Select(account => account.Key)
            .ToArray();
    }

    private static string? NormalizeDisplayText(
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        var pendingSpace = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var requiredLength = rune.Utf16SequenceLength +
                (pendingSpace ? 1 : 0);
            if (builder.Length + requiredLength > maximumLength)
                break;
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
