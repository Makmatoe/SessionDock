using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SessionDock.SystemProcesses;

namespace SessionDock.Services;

public sealed class RobloxClientService
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ForcedCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TotalCloseTimeout = TimeSpan.FromSeconds(12);
    private static readonly Regex ExecutablePattern = new(
        "^\\s*\"(?<path>[^\"]+\\.exe)\"|^\\s*(?<path>[^\\s]+\\.exe)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? FindPlayerPath()
    {
        try
        {
            var registeredPath = FindRegisteredPlayerPath();
            if (registeredPath is not null)
                return registeredPath;

            var versionsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "Versions");
            if (!Directory.Exists(versionsDirectory) ||
                IsReparsePoint(versionsDirectory))
            {
                return null;
            }

            return Directory.EnumerateDirectories(
                    versionsDirectory,
                    "version-*",
                    SearchOption.TopDirectoryOnly)
                .Where(directory => !IsReparsePoint(directory))
                .Take(256)
                .Select(directory => Path.Combine(directory, "RobloxPlayerBeta.exe"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault(path =>
                    RobloxExecutableTrust.IsTrustedPlayerPath(path));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    public Task<LaunchResult> LaunchAsync(
        string launchUri,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var playerPath = FindPlayerPath();
                if (playerPath is null)
                {
                    return LaunchResult.Failed(
                        "Main.ClientNotFoundDetail");
                }

                // A cached discovery result is never sufficient for a launch.
                // Re-run online revocation and signer validation immediately
                // before Windows is asked to execute the file.
                if (!RobloxExecutableTrust.IsTrustedPlayerPath(
                        playerPath,
                        forceRefresh: true))
                {
                    return LaunchResult.Failed(
                        "Main.ClientVerificationFailureDetail");
                }

                var startInfo = CreatePlayerStartInfo(playerPath, launchUri);
                cancellationToken.ThrowIfCancellationRequested();
                using var process = Process.Start(startInfo);
                return process is null
                    ? LaunchResult.Failed("Main.ClientNoProcessDetail")
                    : LaunchResult.Succeeded(
                        process.Id,
                        TryCreatePlayerIdentity(
                            process,
                            playerPath,
                            out var identity)
                            ? identity
                            : null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return LaunchResult.Failed("Main.ClientStartFailureDetail");
            }
        }, cancellationToken);
    }

    internal static ProcessStartInfo CreatePlayerStartInfo(
        string playerPath,
        string launchUri,
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchUri);
        var startInfo = new ProcessStartInfo
        {
            FileName = playerPath,
            UseShellExecute = false
        };
        if (inheritedEnvironment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var variable in inheritedEnvironment)
            {
                if (variable.Value is not null)
                    startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        LocalApiLaunchHook.RemoveConfigurationFromChildEnvironment(startInfo);
        startInfo.ArgumentList.Add(launchUri);
        return startInfo;
    }

    public Task<ClosePlayersResult> CloseAllPlayersAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CloseAllPlayers(cancellationToken), cancellationToken);

    public Task<RunningRobloxClientsResult> GetRunningPlayersAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => GetRunningPlayers(cancellationToken),
            cancellationToken);

    public Task<CloseRobloxClientResult> ClosePlayerAsync(
        RobloxClientProcessIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Task.Run(
            () => ClosePlayer(identity, cancellationToken),
            cancellationToken);
    }

    private static RunningRobloxClientsResult GetRunningPlayers(
        CancellationToken cancellationToken)
    {
        var scan = FindRunningPlayers(cancellationToken);
        try
        {
            var clients = scan.VerifiedProcesses
                .Select(CreateRunningClientSnapshot)
                .OrderByDescending(client => client.Identity.StartTimeUtc)
                .ToArray();
            return new RunningRobloxClientsResult(
                clients,
                scan.UnverifiedCount);
        }
        finally
        {
            foreach (var verifiedProcess in scan.VerifiedProcesses)
                verifiedProcess.Process.Dispose();
        }
    }

    private static RunningRobloxClient CreateRunningClientSnapshot(
        VerifiedPlayerProcess verifiedProcess)
    {
        var process = verifiedProcess.Process;
        try
        {
            var hasVisibleWindow = process.MainWindowHandle != IntPtr.Zero;
            var windowTitle = hasVisibleWindow
                ? process.MainWindowTitle
                : null;
            return new RunningRobloxClient(
                verifiedProcess.Identity,
                hasVisibleWindow,
                string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new RunningRobloxClient(
                verifiedProcess.Identity,
                HasVisibleWindow: false,
                WindowTitle: null);
        }
    }

    private static CloseRobloxClientResult ClosePlayer(
        RobloxClientProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.AlreadyExited);
        }

        using (process)
            return ClosePlayerCore(
                identity,
                new SystemCloseableRobloxProcess(process),
                cancellationToken);
    }

    internal static CloseRobloxClientResult ClosePlayerCore(
        RobloxClientProcessIdentity identity,
        ICloseableRobloxProcess process,
        CancellationToken cancellationToken,
        Func<
            ICloseableRobloxProcess,
            TimeSpan,
            CancellationToken,
            bool>? waitForExit = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        waitForExit ??= WaitForProcessToExit;

        if (process.HasExited)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.AlreadyExited);
        }
        if (!process.IsStillVerified(identity))
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.IdentityMismatch);
        }

        var gracefulCloseRequested = false;
        try
        {
            if (process.HasVisibleWindow)
                gracefulCloseRequested = process.RequestGracefulClose();
        }
        catch (InvalidOperationException)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Closed);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Failed);
        }

        if (gracefulCloseRequested &&
            waitForExit(
                process,
                GracefulCloseTimeout,
                cancellationToken))
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Closed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Closed);
        }
        if (!process.IsStillVerified(identity))
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.IdentityMismatch);
        }

        try
        {
            process.ForceClose();
        }
        catch (InvalidOperationException)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Closed);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Failed);
        }

        return new CloseRobloxClientResult(
            waitForExit(
                process,
                ForcedCloseTimeout,
                cancellationToken)
                ? CloseRobloxClientStatus.Closed
                : CloseRobloxClientStatus.Failed);
    }

    private static ClosePlayersResult CloseAllPlayers(
        CancellationToken cancellationToken)
    {
        var seenProcessIds = new HashSet<int>();
        var backgroundProcessIds = new HashSet<int>();
        var closedProcessIds = new HashSet<int>();
        var deadline = DateTimeOffset.UtcNow + TotalCloseTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = FindRunningPlayers(cancellationToken);
            foreach (var processId in scan.AllProcessIds)
                seenProcessIds.Add(processId);
            foreach (var processId in scan.BackgroundProcessIds)
                backgroundProcessIds.Add(processId);

            if (scan.VerifiedProcesses.Count == 0)
            {
                return new ClosePlayersResult(
                    seenProcessIds.Count,
                    closedProcessIds.Count,
                    0,
                    scan.UnverifiedCount,
                    backgroundProcessIds.Count);
            }

            try
            {
                foreach (var verifiedProcess in scan.VerifiedProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var process = verifiedProcess.Process;
                    if (!IsStillVerified(verifiedProcess))
                        continue;
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch (InvalidOperationException)
                    {
                        closedProcessIds.Add(process.Id);
                    }
                }

                WaitForPlayersToExit(
                    scan.VerifiedProcesses,
                    GracefulCloseTimeout,
                    closedProcessIds,
                    cancellationToken);

                foreach (var verifiedProcess in scan.VerifiedProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var process = verifiedProcess.Process;
                    if (HasExited(process))
                        continue;
                    if (!IsStillVerified(verifiedProcess))
                        continue;
                    try
                    {
                        process.Kill(entireProcessTree: false);
                    }
                    catch (InvalidOperationException)
                    {
                        closedProcessIds.Add(process.Id);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // The final rescan reports a verified process that could not be closed.
                    }
                }

                WaitForPlayersToExit(
                    scan.VerifiedProcesses,
                    ForcedCloseTimeout,
                    closedProcessIds,
                    cancellationToken);
            }
            finally
            {
                foreach (var process in scan.VerifiedProcesses)
                    process.Process.Dispose();
            }
        }

        var remaining = FindRunningPlayers(cancellationToken);
        try
        {
            foreach (var processId in remaining.AllProcessIds)
                seenProcessIds.Add(processId);
            foreach (var processId in remaining.BackgroundProcessIds)
                backgroundProcessIds.Add(processId);
            return new ClosePlayersResult(
                seenProcessIds.Count,
                closedProcessIds.Count,
                remaining.VerifiedProcesses.Count,
                remaining.UnverifiedCount,
                backgroundProcessIds.Count);
        }
        finally
        {
            foreach (var process in remaining.VerifiedProcesses)
                process.Process.Dispose();
        }
    }

    private static PlayerProcessScan FindRunningPlayers(
        CancellationToken cancellationToken = default)
    {
        var verified = new List<VerifiedPlayerProcess>();
        var allProcessIds = new List<int>();
        var backgroundProcessIds = new List<int>();
        var unverified = 0;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    continue;
                }
                allProcessIds.Add(process.Id);
                var executablePath = process.MainModule?.FileName;
                if (executablePath is not null &&
                    RobloxExecutableTrust.IsTrustedPlayerPath(executablePath) &&
                    WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                        process))
                {
                    var startTimeUtc = process.StartTime.ToUniversalTime();
                    var isBackgroundProcess = process.MainWindowHandle == IntPtr.Zero;
                    verified.Add(new VerifiedPlayerProcess(
                        process,
                        new RobloxClientProcessIdentity(
                            process.Id,
                            startTimeUtc,
                            Path.GetFullPath(executablePath))));
                    if (isBackgroundProcess)
                        backgroundProcessIds.Add(process.Id);
                    continue;
                }

                unverified++;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or ArgumentException)
            {
                if (!HasExited(process))
                    unverified++;
            }

            process.Dispose();
        }

        return new PlayerProcessScan(
            verified,
            allProcessIds,
            backgroundProcessIds,
            unverified);
    }

    private static void WaitForPlayersToExit(
        IEnumerable<VerifiedPlayerProcess> processes,
        TimeSpan timeout,
        ISet<int> closedProcessIds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allExited = true;
            foreach (var verifiedProcess in processes)
            {
                var process = verifiedProcess.Process;
                if (HasExited(process))
                    closedProcessIds.Add(process.Id);
                else
                    allExited = false;
            }

            if (allExited)
                return;
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
        }

        foreach (var verifiedProcess in processes)
        {
            var process = verifiedProcess.Process;
            if (HasExited(process))
                closedProcessIds.Add(process.Id);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool WaitForProcessToExit(
        ICloseableRobloxProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return true;
            cancellationToken.WaitHandle.WaitOne(
                TimeSpan.FromMilliseconds(100));
        }
        return process.HasExited;
    }

    private static bool IsStillVerified(VerifiedPlayerProcess verifiedProcess)
        => IsStillVerified(
            verifiedProcess.Process,
            verifiedProcess.Identity);

    private static bool IsStillVerified(
        Process process,
        RobloxClientProcessIdentity expectedIdentity)
    {
        try
        {
            return !process.HasExited &&
                   process.MainModule?.FileName is { } path &&
                   MatchesPlayerIdentity(
                       expectedIdentity,
                       process.Id,
                       process.StartTime.ToUniversalTime(),
                       path) &&
                   RobloxExecutableTrust.IsTrustedPlayerPath(
                       path,
                       forceRefresh: true) &&
                   WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                       process);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreatePlayerIdentity(
        Process process,
        string executablePath,
        out RobloxClientProcessIdentity? identity)
    {
        try
        {
            identity = new RobloxClientProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime(),
                Path.GetFullPath(executablePath));
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or ArgumentException)
        {
            identity = null;
            return false;
        }
    }

    internal static bool MatchesPlayerIdentity(
        RobloxClientProcessIdentity expectedIdentity,
        int processId,
        DateTime startTimeUtc,
        string executablePath)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        return expectedIdentity.ProcessId == processId &&
               expectedIdentity.StartTimeUtc == startTimeUtc &&
               expectedIdentity.ExecutablePath.Equals(
                   executablePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRegisteredPlayerPath()
    {
        foreach (var scheme in new[] { "roblox-player", "roblox" })
        {
            using var commandKey = Registry.ClassesRoot.OpenSubKey(
                $@"{scheme}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var match = ExecutablePattern.Match(command);
            var path = match.Success ? match.Groups["path"].Value : string.Empty;
            if (File.Exists(path) && RobloxExecutableTrust.IsTrustedPlayerPath(path))
                return path;
        }

        return null;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public sealed record LaunchResult(
        bool Success,
        string? Error,
        int? ProcessId,
        RobloxClientProcessIdentity? PlayerIdentity)
    {
        public static LaunchResult Succeeded(
            int processId,
            RobloxClientProcessIdentity? playerIdentity) =>
            new(true, null, processId, playerIdentity);

        public static LaunchResult Failed(string error) =>
            new(false, error, null, null);
    }

    public sealed record ClosePlayersResult(
        int Found,
        int Closed,
        int Remaining,
        int Unverified,
        int BackgroundFound)
    {
        public bool Success => Remaining == 0 && Unverified == 0;
    }

    private sealed record PlayerProcessScan(
        IReadOnlyList<VerifiedPlayerProcess> VerifiedProcesses,
        IReadOnlyList<int> AllProcessIds,
        IReadOnlyList<int> BackgroundProcessIds,
        int UnverifiedCount);

    private sealed record VerifiedPlayerProcess(
        Process Process,
        RobloxClientProcessIdentity Identity);

    private sealed class SystemCloseableRobloxProcess(Process process) :
        ICloseableRobloxProcess
    {
        public bool HasExited => RobloxClientService.HasExited(process);

        public bool HasVisibleWindow => process.MainWindowHandle != IntPtr.Zero;

        public bool IsStillVerified(RobloxClientProcessIdentity identity) =>
            RobloxClientService.IsStillVerified(process, identity);

        public bool RequestGracefulClose() => process.CloseMainWindow();

        public void ForceClose() => process.Kill(entireProcessTree: false);
    }

}

internal interface ICloseableRobloxProcess
{
    bool HasExited { get; }

    bool HasVisibleWindow { get; }

    bool IsStillVerified(RobloxClientProcessIdentity identity);

    bool RequestGracefulClose();

    void ForceClose();
}
