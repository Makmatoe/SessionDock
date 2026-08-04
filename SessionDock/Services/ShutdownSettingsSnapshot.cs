using SessionDock.Models;

namespace SessionDock.Services;

internal static class ShutdownSettingsSnapshot
{
    internal static AppSettings Create(
        AppSettings settings,
        DestinationPersistenceRequest? capturedDestinationRequest,
        DestinationPersistenceRequest? currentDestinationRequest,
        WindowPlacementSettings? mainWindowPlacement = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = AppSettingsSnapshot.Create(settings);
        if (mainWindowPlacement is not null)
        {
            snapshot.MainWindowPlacement =
                AppSettingsSnapshot.Clone(mainWindowPlacement);
        }
        if (capturedDestinationRequest is null ||
            capturedDestinationRequest != currentDestinationRequest)
        {
            return snapshot;
        }

        var profile = snapshot.Accounts.FirstOrDefault(account =>
            account.Key.Equals(
                capturedDestinationRequest.AccountKey,
                StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            NamedDestinationPolicy.SetAccountDestination(
                snapshot,
                profile.Key,
                capturedDestinationRequest.Destination);
        }
        return snapshot;
    }
}
