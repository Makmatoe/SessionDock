using SessionDock.Models;

namespace SessionDock.Services;

public sealed record ResolvedLaunchInput(
    string AccountDestination,
    string Destination,
    LaunchTarget Target,
    string? ServerJobId,
    long? TrackedPlaceId);

public static class LaunchInputResolver
{
    internal const string UntrackedServerJobIdErrorKey =
        "Validation.Destination.UntrackedServerJobId";

    public static bool TryResolve(
        string input,
        IEnumerable<RecentExperience> recentExperiences,
        out ResolvedLaunchInput? resolved,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recentExperiences);
        resolved = null;
        var accountDestination = input.Trim();
        var destination = accountDestination;
        string? serverJobId = null;
        long? trackedPlaceId = null;

        if (RecentServerJoinResolver.TryResolve(
                destination,
                recentExperiences,
                out var trackedServer))
        {
            destination = trackedServer!.Destination;
            serverJobId = Guid.Parse(trackedServer.ServerJobId!).ToString("D");
            trackedPlaceId = trackedServer.PlaceId;
        }
        else if (Guid.TryParse(destination, out _))
        {
            error = UntrackedServerJobIdErrorKey;
            return false;
        }

        if (!DestinationParser.TryParse(destination, out var target, out error))
            return false;

        resolved = new ResolvedLaunchInput(
            accountDestination,
            destination,
            target!,
            serverJobId,
            trackedPlaceId);
        error = string.Empty;
        return true;
    }
}
