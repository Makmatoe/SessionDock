using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeProcessVerifier
{
    bool IsExpected(HandleScopeConnection connection);
}

internal interface IHandleScopeResolvedProcessVerifier :
    IHandleScopeProcessVerifier
{
    bool TryResolveExpected(
        HandleScopeConnection connection,
        out HandleScopeRuntimeIdentity? runtimeIdentity);
}

internal sealed class HandleScopeProcessVerifier :
    IHandleScopeResolvedProcessVerifier
{
    internal const string ExpectedProcessName = "HandleScope.Api";
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(5);

    private readonly string _localAppDataRoot;
    private readonly string _expectedExecutablePath;
    private readonly Func<string, bool>? _isReparsePoint;
    private readonly IHandleScopeInstalledRuntimeVerifier _installedRuntimeVerifier;

    internal HandleScopeProcessVerifier(
        string localAppDataRoot,
        string expectedExecutablePath,
        Func<string, bool>? isReparsePoint = null,
        IHandleScopeInstalledRuntimeVerifier? installedRuntimeVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        _localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        _expectedExecutablePath = Path.GetFullPath(expectedExecutablePath);
        _isReparsePoint = isReparsePoint;
        _installedRuntimeVerifier = installedRuntimeVerifier ??
            new HandleScopeInstalledRuntimeVerifier();
    }

    internal static HandleScopeProcessVerifier CreateDefault()
    {
        var localAppDataRoot = Path.GetFullPath(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));
        return new HandleScopeProcessVerifier(
            localAppDataRoot,
            GetExpectedExecutablePath(localAppDataRoot));
    }

    internal static string GetExpectedExecutablePath(string localAppDataRoot) =>
        Path.GetFullPath(Path.Combine(
            localAppDataRoot,
            "Programs",
            "HandleScope",
            "Api",
            "HandleScope.Api.exe"));

    public bool IsExpected(HandleScopeConnection connection) =>
        TryVerifyExpected(
            connection,
            requireRuntimeIdentity: false,
            out _);

    public bool TryResolveExpected(
        HandleScopeConnection connection,
        out HandleScopeRuntimeIdentity? runtimeIdentity) =>
        TryVerifyExpected(
            connection,
            requireRuntimeIdentity: true,
            out runtimeIdentity);

    private bool TryVerifyExpected(
        HandleScopeConnection connection,
        bool requireRuntimeIdentity,
        out HandleScopeRuntimeIdentity? runtimeIdentity)
    {
        ArgumentNullException.ThrowIfNull(connection);
        runtimeIdentity = null;

        try
        {
            if (!TryGetExpectedProcessSnapshot(
                    connection.ApiProcessId,
                    requireRuntimeIdentity,
                    out var snapshot,
                    out runtimeIdentity))
                return false;

            using var current = Process.GetCurrentProcess();
            return MatchesExpectedProcess(
                connection,
                _expectedExecutablePath,
                current.SessionId,
                snapshot,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                Win32Exception or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetExpectedProcessSnapshot(
        int processId,
        bool requireRuntimeIdentity,
        out HandleScopeProcessSnapshot snapshot,
        out HandleScopeRuntimeIdentity? runtimeIdentity)
    {
        snapshot = default;
        runtimeIdentity = null;
        if (!HandleScopePathSecurity.IsSafeExistingPath(
                _localAppDataRoot,
                _expectedExecutablePath,
                targetMustExist: true,
                _isReparsePoint))
        {
            return false;
        }
        if (_installedRuntimeVerifier is IHandleScopeRuntimeIdentityResolver resolver)
        {
            if (!resolver.TryIdentify(_expectedExecutablePath, out runtimeIdentity) ||
                runtimeIdentity is null)
            {
                return false;
            }
        }
        else if (requireRuntimeIdentity ||
                 !_installedRuntimeVerifier.IsAuthorized(_expectedExecutablePath))
        {
            return false;
        }

        using var process = Process.GetProcessById(processId);
        var actualPath = process.MainModule?.FileName;
        if (actualPath is null ||
            !WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                process))
        {
            return false;
        }

        snapshot = new HandleScopeProcessSnapshot(
            process.Id,
            process.HasExited,
            process.ProcessName,
            process.SessionId,
            Path.GetFullPath(actualPath),
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
        return true;
    }

    internal static bool MatchesExpectedProcess(
        HandleScopeConnection connection,
        string expectedExecutablePath,
        int currentSessionId,
        HandleScopeProcessSnapshot process,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        if (!MatchesExpectedIdentity(
                connection.ApiProcessId,
                expectedExecutablePath,
                currentSessionId,
                process))
        {
            return false;
        }

        var processStartedAtUtc = process.StartedAtUtc.ToUniversalTime();
        var discoveryStartedAtUtc = connection.StartedAtUtc.ToUniversalTime();
        return discoveryStartedAtUtc >= processStartedAtUtc - AllowedClockSkew &&
            discoveryStartedAtUtc <= utcNow.ToUniversalTime() + AllowedClockSkew;
    }

    private static bool MatchesExpectedIdentity(
        int expectedProcessId,
        string expectedExecutablePath,
        int currentSessionId,
        HandleScopeProcessSnapshot process) =>
        !process.HasExited &&
        process.ProcessId == expectedProcessId &&
        process.SessionId == currentSessionId &&
        process.ProcessName.Equals(
            ExpectedProcessName,
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(process.ExecutablePath).Equals(
            Path.GetFullPath(expectedExecutablePath),
            StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct HandleScopeProcessSnapshot(
    int ProcessId,
    bool HasExited,
    string ProcessName,
    int SessionId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc);
