using SessionDock.Models;

namespace SessionDock.Services;

internal static class NamedDestinationPolicy
{
    internal const int MaximumDestinations = 256;
    internal const int MaximumNameLength = 80;

    internal static bool NormalizeInPlace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hadNullAccounts = settings.Accounts is null;
        var destinationInput = settings.NamedDestinations;
        var hadNullDestinationList = destinationInput is null;
        var originalDestinationCount = destinationInput?.Count ?? 0;
        settings.Accounts ??= [];
        var originalDestinations = (destinationInput ?? [])
            .Where(destination => destination is not null)
            .Select(Clone)
            .ToArray();
        var originalAccountValues = settings.Accounts
            .Where(account => account is not null &&
                !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Destination,
                StringComparer.OrdinalIgnoreCase);

        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        MirrorAssignments(settings);

        return hadNullAccounts ||
            hadNullDestinationList ||
            originalDestinationCount != originalDestinations.Length ||
            !AreEquivalent(
                originalDestinations,
                settings.NamedDestinations) ||
            settings.Accounts.Where(account => account is not null).Any(account =>
                string.IsNullOrWhiteSpace(account.Key) ||
                !originalAccountValues.TryGetValue(account.Key, out var value) ||
                !string.Equals(
                    value,
                    account.Destination,
                    StringComparison.Ordinal));
    }

    internal static List<NamedDestination> Normalize(
        IEnumerable<NamedDestination>? destinations,
        IReadOnlyCollection<AccountProfile> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var validAccountKeys = accounts
            .Where(account => account is not null &&
                !string.IsNullOrWhiteSpace(account.Key))
            .Select(account => account.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedAccountKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<NamedDestination>();

        foreach (var source in (destinations ?? [])
                     .Where(destination => destination is not null)
                     .Take(MaximumDestinations))
        {
            if (!TryNormalizeValue(source.Value, out var value))
                continue;

            var id = NormalizeId(source.Id, usedIds);
            var baseName = NormalizeName(source.Name) ??
                $"Destination {normalized.Count + 1}";
            var name = CreateUniqueName(baseName, usedNames);
            var accountKeys = (source.AccountKeys ?? [])
                .Where(key =>
                    !string.IsNullOrWhiteSpace(key) &&
                    validAccountKeys.Contains(key) &&
                    assignedAccountKeys.Add(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            normalized.Add(new NamedDestination
            {
                Id = id,
                Name = name,
                Value = value,
                AccountKeys = accountKeys
            });
        }

        return normalized;
    }

    internal static bool TryUpsert(
        AppSettings settings,
        string? destinationId,
        string? name,
        string? value,
        IEnumerable<string> accountKeys,
        out string savedId,
        out string errorKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accountKeys);
        settings.Accounts ??= [];
        savedId = string.Empty;
        errorKey = string.Empty;
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            errorKey = "Validation.NamedDestination.NameRequired";
            return false;
        }
        if (normalizedName.Length > MaximumNameLength)
        {
            errorKey = "Validation.NamedDestination.NameTooLong";
            return false;
        }
        if (!TryNormalizeValue(value, out var normalizedValue))
        {
            errorKey = "Validation.NamedDestination.ValueInvalid";
            return false;
        }

        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        if (settings.NamedDestinations.Any(destination =>
                !string.Equals(
                    destination.Id,
                    destinationId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    destination.Name?.Trim(),
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            errorKey = "Validation.NamedDestination.NameUnique";
            return false;
        }
        if (destinationId is null &&
            settings.NamedDestinations.Count >= MaximumDestinations)
        {
            errorKey = "Validation.NamedDestination.TooMany";
            return false;
        }

        var validAccountKeys = settings.Accounts
            .Where(account => account is not null &&
                !string.IsNullOrWhiteSpace(account.Key))
            .Select(account => account.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedKeys = accountKeys
            .Where(key => !string.IsNullOrWhiteSpace(key) &&
                validAccountKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedKeySet = selectedKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var target = settings.NamedDestinations.FirstOrDefault(destination =>
            string.Equals(
                destination.Id,
                destinationId,
                StringComparison.OrdinalIgnoreCase));
        var previousValue = target?.Value;
        var removedAccountKeys = target is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : target.AccountKeys
                .Where(key => !selectedKeySet.Contains(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (target is null)
        {
            target = new NamedDestination
            {
                Id = Guid.NewGuid().ToString("N")
            };
            settings.NamedDestinations.Add(target);
        }

        foreach (var destination in settings.NamedDestinations)
        {
            if (ReferenceEquals(destination, target))
                continue;
            destination.AccountKeys ??= [];
            destination.AccountKeys.RemoveAll(selectedKeySet.Contains);
        }

        target.Name = normalizedName;
        target.Value = normalizedValue;
        target.AccountKeys = selectedKeys;
        if (previousValue is not null && removedAccountKeys.Count > 0)
        {
            foreach (var account in settings.Accounts.Where(account =>
                         account is not null &&
                         removedAccountKeys.Contains(account.Key) &&
                         string.Equals(
                             account.Destination,
                             previousValue,
                             StringComparison.Ordinal)))
            {
                account.Destination = null;
            }
        }
        foreach (var account in settings.Accounts.Where(account =>
                     account is not null &&
                     selectedKeySet.Contains(account.Key)))
        {
            account.Destination = normalizedValue;
        }

        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        MirrorAssignments(settings);
        savedId = settings.NamedDestinations.First(destination =>
            string.Equals(
                destination.Name,
                normalizedName,
                StringComparison.OrdinalIgnoreCase)).Id;
        return true;
    }

    internal static bool Delete(AppSettings settings, string destinationId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        settings.Accounts ??= [];
        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        var removed = settings.NamedDestinations.RemoveAll(destination =>
            string.Equals(
                destination.Id,
                destinationId,
                StringComparison.OrdinalIgnoreCase));
        // AccountProfile.Destination deliberately remains untouched. It becomes
        // a backward-compatible custom value after the named entry is removed.
        return removed > 0;
    }

    internal static void SetCustomDestination(
        AppSettings settings,
        string accountKey,
        string? destinationValue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        settings.Accounts ??= [];
        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        foreach (var destination in settings.NamedDestinations)
        {
            destination.AccountKeys ??= [];
            destination.AccountKeys.RemoveAll(key => string.Equals(
                key,
                accountKey,
                StringComparison.OrdinalIgnoreCase));
        }

        var account = settings.Accounts.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals(
                candidate.Key,
                accountKey,
                StringComparison.OrdinalIgnoreCase));
        if (account is not null)
            account.Destination = destinationValue;
    }

    internal static string? SetAccountDestination(
        AppSettings settings,
        string accountKey,
        string? destinationValue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        settings.Accounts ??= [];
        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);

        var account = settings.Accounts.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals(
                candidate.Key,
                accountKey,
                StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return null;

        var currentAssignment = settings.NamedDestinations.FirstOrDefault(
            destination => (destination.AccountKeys ?? []).Contains(
                accountKey,
                StringComparer.OrdinalIgnoreCase));
        var matchingDestinations = settings.NamedDestinations
            .Where(destination => DestinationValuesAreEquivalent(
                destination.Value,
                destinationValue))
            .ToArray();

        // Preserve an existing named assignment when duplicate saved
        // destinations point to the same target. Without that preference the
        // edit would silently discard a user's explicit checklist choice.
        var target = currentAssignment is not null &&
                     matchingDestinations.Contains(currentAssignment)
            ? currentAssignment
            : matchingDestinations.Length == 1
                ? matchingDestinations[0]
                : null;

        foreach (var destination in settings.NamedDestinations)
        {
            destination.AccountKeys ??= [];
            destination.AccountKeys.RemoveAll(key => string.Equals(
                key,
                accountKey,
                StringComparison.OrdinalIgnoreCase));
        }

        if (target is not null)
            target.AccountKeys.Add(account.Key);

        // A named assignment owns its stored representation. Equivalent forms
        // such as a place ID and its official Roblox URL therefore converge to
        // one value, while unmatched or ambiguous values remain lossless custom
        // destinations.
        account.Destination = target?.Value ?? destinationValue;
        return target?.Id;
    }

    internal static void SetAllAccountDestinations(
        AppSettings settings,
        string? destinationValue)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Accounts ??= [];
        foreach (var account in settings.Accounts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(account.Key);
        }

        settings.NamedDestinations = Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        var accountsByKey = settings.Accounts
            .GroupBy(
                account => account.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var currentAssignments = new Dictionary<string, NamedDestination>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var destination in settings.NamedDestinations)
        {
            foreach (var accountKey in destination.AccountKeys ?? [])
                currentAssignments[accountKey] = destination;
        }

        var matchingDestinations = settings.NamedDestinations
            .Where(destination => DestinationValuesAreEquivalent(
                destination.Value,
                destinationValue))
            .ToArray();
        foreach (var destination in settings.NamedDestinations)
            destination.AccountKeys = [];

        foreach (var (accountKey, matchingAccounts) in accountsByKey)
        {
            currentAssignments.TryGetValue(
                accountKey,
                out var currentAssignment);
            var target = currentAssignment is not null &&
                         matchingDestinations.Contains(currentAssignment)
                ? currentAssignment
                : matchingDestinations.Length == 1
                    ? matchingDestinations[0]
                    : null;
            if (target is not null)
                target.AccountKeys.Add(accountKey);
            foreach (var account in matchingAccounts)
                account.Destination = target?.Value ?? destinationValue;
        }
    }

    internal static string? GetAssignedDestinationId(
        AppSettings settings,
        string accountKey) =>
        (settings.NamedDestinations ?? []).FirstOrDefault(destination =>
            destination is not null &&
            (destination.AccountKeys ?? []).Contains(
                accountKey,
                StringComparer.OrdinalIgnoreCase))?.Id;

    internal static bool TryNormalizeValue(
        string? value,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 4096 &&
            (DestinationParser.TryParse(normalized, out _, out _) ||
             JoinUserDestination.TryParseStored(normalized, out _, out _));
    }

    internal static NamedDestination Clone(NamedDestination source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Value = source.Value,
        AccountKeys = [.. (source.AccountKeys ?? [])]
    };

    private static void MirrorAssignments(AppSettings settings)
    {
        settings.Accounts ??= [];
        var accounts = settings.Accounts
            .Where(account => account is not null &&
                !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var destination in settings.NamedDestinations ?? [])
        {
            if (destination is null)
                continue;
            foreach (var accountKey in destination.AccountKeys ?? [])
            {
                if (!accounts.TryGetValue(accountKey, out var matchingAccounts))
                    continue;
                foreach (var account in matchingAccounts)
                    account.Destination = destination.Value;
            }
        }
    }

    private static string NormalizeId(
        string? source,
        ISet<string> usedIds)
    {
        var candidate = source?.Trim();
        if (candidate is { Length: > 0 and <= 128 } &&
            usedIds.Add(candidate))
        {
            return candidate;
        }

        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!usedIds.Add(id));
        return id;
    }

    private static string? NormalizeName(string? source)
    {
        var name = source?.Trim();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : name[..Math.Min(name.Length, MaximumNameLength)];
    }

    private static string CreateUniqueName(
        string baseName,
        ISet<string> usedNames)
    {
        if (usedNames.Add(baseName))
            return baseName;
        for (var suffix = 2; suffix <= MaximumDestinations + 1; suffix++)
        {
            var suffixText = $" ({suffix})";
            var prefixLength = Math.Min(
                baseName.Length,
                MaximumNameLength - suffixText.Length);
            var candidate = baseName[..prefixLength] + suffixText;
            if (usedNames.Add(candidate))
                return candidate;
        }
        throw new InvalidOperationException(
            "A unique destination name could not be generated.");
    }

    private static bool AreEquivalent(
        IReadOnlyList<NamedDestination> left,
        IReadOnlyList<NamedDestination> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal) &&
            (pair.First.AccountKeys ?? []).SequenceEqual(
                pair.Second.AccountKeys ?? [],
                StringComparer.OrdinalIgnoreCase));

    private static bool DestinationValuesAreEquivalent(
        string? first,
        string? second)
    {
        if (first is null || second is null)
            return first is null && second is null;

        if (DestinationParser.TryParse(first, out var firstTarget, out _) &&
            DestinationParser.TryParse(second, out var secondTarget, out _))
        {
            return firstTarget == secondTarget;
        }

        if (JoinUserDestination.TryParseStored(
                first,
                out var firstUser,
                out _) &&
            JoinUserDestination.TryParseStored(
                second,
                out var secondUser,
                out _))
        {
            if (firstUser is null || secondUser is null)
                return false;
            if (firstUser.UserId is not null || secondUser.UserId is not null)
                return firstUser.UserId == secondUser.UserId;
            return string.Equals(
                firstUser.Username,
                secondUser.Username,
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            first.Trim(),
            second.Trim(),
            StringComparison.Ordinal);
    }
}
