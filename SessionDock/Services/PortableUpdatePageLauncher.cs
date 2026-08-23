using System.ComponentModel;
using System.Diagnostics;

namespace SessionDock.Services;

internal static class PortableUpdatePageLauncher
{
    public const string LatestReleaseUrl =
        "https://github.com/Makmatoe/SessionDock/releases/latest";

    public static bool TryOpen() => TryOpen(Process.Start);

    internal static bool TryOpen(
        Func<ProcessStartInfo, Process?> startProcess)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        try
        {
            using var process = startProcess(CreateStartInfo());
            return process is not null;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            Trace.WriteLine(
                $"The canonical release page could not be opened: {exception.GetType().Name}.");
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = LatestReleaseUrl,
        UseShellExecute = true
    };
}
