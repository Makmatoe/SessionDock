namespace SessionDock.Services;

internal sealed record AutoJoinWatchContext(
    string AccountKey,
    long AccountUserId,
    string RequestedText)
{
    internal bool Matches(AutoJoinWatchContextState current) =>
        current.IsJoinUserMode &&
        current.IsAutoJoinEnabled &&
        current.IsSessionCurrent &&
        string.Equals(
            current.AccountKey,
            AccountKey,
            StringComparison.OrdinalIgnoreCase) &&
        current.ProfileUserId == AccountUserId &&
        current.AuthenticatedUserId == AccountUserId &&
        string.Equals(
            current.RequestedText,
            RequestedText,
            StringComparison.Ordinal);
}

internal sealed record AutoJoinWatchContextState(
    string? AccountKey,
    long? ProfileUserId,
    long? AuthenticatedUserId,
    string RequestedText,
    bool IsJoinUserMode,
    bool IsAutoJoinEnabled,
    bool IsSessionCurrent);
