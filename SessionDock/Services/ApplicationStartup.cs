namespace SessionDock.Services;

internal static class ApplicationStartup
{
    internal const string LocalDataFailureKey =
        "Startup.LocalDataFailureDetail";

    internal static bool TryStart(
        Action start,
        Action<string> reportLocalDataFailure)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(reportLocalDataFailure);
        try
        {
            start();
            return true;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            reportLocalDataFailure(LocalDataFailureKey);
            return false;
        }
    }
}
