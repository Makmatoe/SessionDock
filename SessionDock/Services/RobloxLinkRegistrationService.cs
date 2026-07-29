using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SessionDock.Services;

internal enum RobloxLinkRegistrationState
{
    Disabled,
    Enabled,
    UpdateRequired,
    Conflict,
    Unavailable
}

internal sealed record RobloxLinkRegistrationStatus(
    RobloxLinkRegistrationState State,
    string Description);

internal enum RobloxLinkRegistrationOwnership
{
    Empty,
    Owned,
    Conflict
}

internal sealed class RobloxLinkRegistrationService
{
    internal const string ProgId = "SessionDock.RobloxLink";
    internal const string ProtocolName = ExternalRobloxLinkPolicy.HandlerScheme;
    internal const string OwnerValueName = "SessionDock.Owner";
    internal const string OwnerValue = "Makmatoe.SessionDock.OpenWith.v1";
    internal const string ProgIdPath = @"Software\Classes\SessionDock.RobloxLink";
    internal const string ProtocolPath = @"Software\Classes\sessiondock-roblox";
    internal const string RobloxOpenWithPath =
        @"Software\Classes\roblox\OpenWithProgids";

    private readonly string _expectedCommand;

    internal RobloxLinkRegistrationService(string? executablePath = null)
    {
        var resolvedExecutablePath = executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException(
                "SessionDock could not determine its executable path.");
        _expectedCommand = BuildOpenCommand(resolvedExecutablePath);
    }

    internal RobloxLinkRegistrationStatus Inspect()
    {
        try
        {
            using var progId = Registry.CurrentUser.OpenSubKey(ProgIdPath);
            using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolPath);
            using var openWith = Registry.CurrentUser.OpenSubKey(RobloxOpenWithPath);
            var openWithValueExists = openWith?.GetValueNames().Contains(
                ProgId,
                StringComparer.OrdinalIgnoreCase) == true;
            var ownership = ClassifyOwnership(
                progId is not null,
                progId?.GetValue(OwnerValueName) as string,
                protocol is not null,
                protocol?.GetValue(OwnerValueName) as string,
                openWithValueExists);
            if (ownership == RobloxLinkRegistrationOwnership.Empty)
            {
                return new RobloxLinkRegistrationStatus(
                    RobloxLinkRegistrationState.Disabled,
                    "Windows link handling is not enabled.");
            }
            if (ownership == RobloxLinkRegistrationOwnership.Conflict)
            {
                return new RobloxLinkRegistrationStatus(
                    RobloxLinkRegistrationState.Conflict,
                    "A registration at SessionDock's reserved names is not owned by this feature. It was preserved and cannot be changed here.");
            }

            if (progId is null || protocol is null || !openWithValueExists ||
                !HasExpectedValues(progId) || !HasExpectedValues(protocol))
            {
                return new RobloxLinkRegistrationStatus(
                    RobloxLinkRegistrationState.UpdateRequired,
                    "SessionDock owns the registration, but it is incomplete or points to an older executable location.");
            }

            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Enabled,
                "The per-user Open with SessionDock handler is enabled.");
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable,
                "Windows did not allow SessionDock to inspect this user's link-handler registration.");
        }
    }

    internal RobloxLinkRegistrationStatus Enable()
    {
        var status = Inspect();
        if (status.State is RobloxLinkRegistrationState.Conflict or
            RobloxLinkRegistrationState.Unavailable)
        {
            return status;
        }

        try
        {
            WriteOwnedHandler(ProgIdPath, "URL:SessionDock Roblox link");
            WriteOwnedHandler(
                ProtocolPath,
                "URL:Open Roblox link with SessionDock");
            using var openWith = Registry.CurrentUser.CreateSubKey(
                RobloxOpenWithPath,
                writable: true);
            openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
            return Inspect();
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable,
                "Windows did not allow SessionDock to enable the per-user link handler. No elevation was attempted.");
        }
    }

    internal RobloxLinkRegistrationStatus Disable()
    {
        try
        {
            bool progIdExists;
            bool protocolExists;
            bool openWithValueExists;
            RobloxLinkRegistrationOwnership ownership;
            using (var progId = Registry.CurrentUser.OpenSubKey(ProgIdPath))
            using (var protocol = Registry.CurrentUser.OpenSubKey(ProtocolPath))
            using (var openWith = Registry.CurrentUser.OpenSubKey(
                       RobloxOpenWithPath))
            {
                progIdExists = progId is not null;
                protocolExists = protocol is not null;
                openWithValueExists = openWith?.GetValueNames().Contains(
                    ProgId,
                    StringComparer.OrdinalIgnoreCase) == true;
                ownership = ClassifyOwnership(
                    progIdExists,
                    progId?.GetValue(OwnerValueName) as string,
                    protocolExists,
                    protocol?.GetValue(OwnerValueName) as string,
                    openWithValueExists);
            }

            if (ownership == RobloxLinkRegistrationOwnership.Empty)
                return Inspect();
            if (ownership == RobloxLinkRegistrationOwnership.Conflict)
            {
                return new RobloxLinkRegistrationStatus(
                    RobloxLinkRegistrationState.Conflict,
                    "The registration is not fully owned by SessionDock, so nothing was removed.");
            }

            using (var writableOpenWith = Registry.CurrentUser.OpenSubKey(
                       RobloxOpenWithPath,
                       writable: true))
            {
                writableOpenWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            if (progIdExists)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    ProgIdPath,
                    throwOnMissingSubKey: false);
            }
            if (protocolExists)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    ProtocolPath,
                    throwOnMissingSubKey: false);
            }

            return Inspect();
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable,
                "Windows did not allow SessionDock to disable the per-user link handler. No elevation was attempted.");
        }
    }

    internal static string BuildOpenCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains('"') ||
            executablePath.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                "The executable path is not a safe absolute Windows executable path.",
                nameof(executablePath));
        }

        return $"\"{executablePath}\" {ExternalLaunchCommandLine.OpenLinkOption} \"%1\"";
    }

    internal static bool HasOwnerMarker(string? owner) =>
        owner?.Equals(OwnerValue, StringComparison.Ordinal) == true;

    internal static RobloxLinkRegistrationOwnership ClassifyOwnership(
        bool progIdExists,
        string? progIdOwner,
        bool protocolExists,
        string? protocolOwner,
        bool openWithValueExists)
    {
        if (!progIdExists && !protocolExists && !openWithValueExists)
            return RobloxLinkRegistrationOwnership.Empty;
        if (progIdExists && !HasOwnerMarker(progIdOwner) ||
            protocolExists && !HasOwnerMarker(protocolOwner) ||
            !progIdExists && openWithValueExists)
        {
            return RobloxLinkRegistrationOwnership.Conflict;
        }

        return RobloxLinkRegistrationOwnership.Owned;
    }

    private void WriteOwnedHandler(string path, string description)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(path))
        {
            if (existing is not null && !IsOwned(existing))
            {
                throw new SecurityException(
                    "The reserved link-handler key is owned by another application.");
            }
        }

        using var root = Registry.CurrentUser.CreateSubKey(path, writable: true);
        root.SetValue(null, description, RegistryValueKind.String);
        root.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
        root.SetValue(OwnerValueName, OwnerValue, RegistryValueKind.String);
        using var command = root.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(null, _expectedCommand, RegistryValueKind.String);
    }

    private bool HasExpectedValues(RegistryKey key)
    {
        if (!HasOwnerMarker(key.GetValue(OwnerValueName) as string) ||
            key.GetValue("URL Protocol") is not string)
        {
            return false;
        }

        using var command = key.OpenSubKey(@"shell\open\command");
        return command?.GetValue(null) is string value &&
               value.Equals(_expectedCommand, StringComparison.Ordinal);
    }

    private static bool IsOwned(RegistryKey? key) =>
        key is null || HasOwnerMarker(key.GetValue(OwnerValueName) as string);

    private static bool IsExpectedRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or
            IOException;
}
