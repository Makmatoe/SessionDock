using SessionDock.Models;

namespace SessionDock.Services;

public static class AccountDestinationService
{
    public static bool TryApplyToAll(
        AppSettings settings,
        IEnumerable<RecentExperience> recentExperiences,
        string input,
        out int assignedCount,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Accounts ??= [];
        if (!TryResolve(
                settings.Accounts,
                recentExperiences,
                input,
                out var resolved,
                out assignedCount,
                out error))
        {
            return false;
        }

        foreach (var account in settings.Accounts.ToArray())
        {
            NamedDestinationPolicy.SetAccountDestination(
                settings,
                account.Key,
                resolved!.AccountDestination);
        }
        return true;
    }

    [Obsolete(
        "Use the AppSettings overload so named destination assignments remain synchronized.")]
    public static bool TryApplyToAll(
        IList<AccountProfile> accounts,
        IEnumerable<RecentExperience> recentExperiences,
        string input,
        out int assignedCount,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (!TryResolve(
                accounts,
                recentExperiences,
                input,
                out var resolved,
                out assignedCount,
                out error))
        {
            return false;
        }

        foreach (var account in accounts)
            account.Destination = resolved!.AccountDestination;
        return true;
    }

    private static bool TryResolve(
        ICollection<AccountProfile> accounts,
        IEnumerable<RecentExperience> recentExperiences,
        string input,
        out ResolvedLaunchInput? resolved,
        out int assignedCount,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(recentExperiences);
        ArgumentNullException.ThrowIfNull(input);
        assignedCount = 0;
        resolved = null;
        if (accounts.Count == 0)
        {
            error = "Validation.Destination.AccountRequired";
            return false;
        }
        if (!LaunchInputResolver.TryResolve(
                input,
                recentExperiences,
                out resolved,
                out error))
        {
            return false;
        }

        assignedCount = accounts.Count;
        return true;
    }
}
