namespace SessionDock.Services;

internal enum JoinUserAvailability
{
    Available,
    UserNotFound,
    Offline,
    NotInExperience,
    NotJoinable,
    RateLimited,
    SessionUnavailable,
    ServiceUnavailable
}

internal enum JoinUserIdentityAvailability
{
    Available,
    UserNotFound,
    RateLimited,
    SessionUnavailable,
    ServiceUnavailable
}

internal sealed record JoinUserIdentity(
    long UserId,
    string Username,
    string DisplayName);

internal sealed record JoinUserIdentityLookupResult(
    JoinUserIdentityAvailability Availability,
    JoinUserIdentity? Identity,
    TimeSpan? RetryAfter = null)
{
    internal static JoinUserIdentityLookupResult Unavailable(
        JoinUserIdentityAvailability availability,
        TimeSpan? retryAfter = null) => new(
            availability,
            null,
            retryAfter);
}

internal sealed record JoinUserResolution(
    long UserId,
    string Username,
    string DisplayName,
    long PlaceId,
    string ServerJobId);

internal sealed record JoinUserLookupResult(
    JoinUserAvailability Availability,
    JoinUserResolution? Resolution,
    TimeSpan? RetryAfter = null)
{
    internal static JoinUserLookupResult Unavailable(
        JoinUserAvailability availability,
        TimeSpan? retryAfter = null) => new(
            availability,
            null,
            retryAfter);
}
