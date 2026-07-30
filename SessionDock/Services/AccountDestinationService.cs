using SessionDock.Models;

namespace SessionDock.Services;

public static class AccountDestinationService
{
    public static bool TryApplyToAll(
        IList<AccountProfile> accounts,
        IEnumerable<RecentExperience> recentExperiences,
        string input,
        out int assignedCount,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(recentExperiences);
        ArgumentNullException.ThrowIfNull(input);

        assignedCount = 0;
        if (accounts.Count == 0)
        {
            error = "Validation.Destination.AccountRequired";
            return false;
        }

        if (!LaunchInputResolver.TryResolve(
                input,
                recentExperiences,
                out var resolved,
                out error))
        {
            return false;
        }

        foreach (var account in accounts)
            account.Destination = resolved!.AccountDestination;

        assignedCount = accounts.Count;
        return true;
    }
}
