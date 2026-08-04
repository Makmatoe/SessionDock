using SessionDock.ExactWheel;

namespace SessionDock.Services;

internal static class SessionMacroPlaybackCancellation
{
    internal static void ThrowIfCleanCancellation(
        ExactWheelPlaybackResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Reason == ExactWheelPlaybackStopReason.Cancelled &&
            result.CleanupSucceeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
