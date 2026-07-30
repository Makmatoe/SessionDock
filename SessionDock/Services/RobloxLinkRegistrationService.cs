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
    RobloxLinkRegistrationState State);

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

    internal RobloxLinkRegistrationStatus Inspect(
        string progIdDescription,
        string protocolDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progIdDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolDescription);

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
            return new RobloxLinkRegistrationStatus(
                ClassifyRegistrationState(
                    ownership,
                    progId is not null,
                    protocol is not null,
                    openWithValueExists,
                    ownership == RobloxLinkRegistrationOwnership.Owned &&
                    progId is not null && HasExpectedValues(
                        progId,
                        progIdDescription),
                    ownership == RobloxLinkRegistrationOwnership.Owned &&
                    protocol is not null && HasExpectedValues(
                        protocol,
                        protocolDescription)));
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable);
        }
    }

    internal RobloxLinkRegistrationStatus Enable(
        string progIdDescription,
        string protocolDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progIdDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolDescription);
        var status = Inspect(progIdDescription, protocolDescription);
        if (status.State is RobloxLinkRegistrationState.Conflict or
            RobloxLinkRegistrationState.Unavailable)
        {
            return status;
        }

        try
        {
            WriteOwnedHandler(ProgIdPath, progIdDescription);
            WriteOwnedHandler(
                ProtocolPath,
                protocolDescription);
            using var openWith = Registry.CurrentUser.CreateSubKey(
                RobloxOpenWithPath,
                writable: true);
            openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
            return Inspect(progIdDescription, protocolDescription);
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable);
        }
    }

    internal RobloxLinkRegistrationStatus Disable(
        string progIdDescription,
        string protocolDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progIdDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolDescription);

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
                return Inspect(progIdDescription, protocolDescription);
            if (ownership == RobloxLinkRegistrationOwnership.Conflict)
            {
                return new RobloxLinkRegistrationStatus(
                    RobloxLinkRegistrationState.Conflict);
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

            return Inspect(progIdDescription, protocolDescription);
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return new RobloxLinkRegistrationStatus(
                RobloxLinkRegistrationState.Unavailable);
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

    internal static bool HasExpectedHandlerValues(
        string? description,
        string? owner,
        string? urlProtocol,
        string? command,
        string expectedDescription,
        string expectedCommand) =>
        description?.Equals(
            expectedDescription,
            StringComparison.Ordinal) == true &&
        HasOwnerMarker(owner) &&
        urlProtocol is not null &&
        command?.Equals(expectedCommand, StringComparison.Ordinal) == true;

    internal static RobloxLinkRegistrationState ClassifyRegistrationState(
        RobloxLinkRegistrationOwnership ownership,
        bool progIdExists,
        bool protocolExists,
        bool openWithValueExists,
        bool progIdHasExpectedValues,
        bool protocolHasExpectedValues)
    {
        if (ownership == RobloxLinkRegistrationOwnership.Empty)
            return RobloxLinkRegistrationState.Disabled;
        if (ownership == RobloxLinkRegistrationOwnership.Conflict)
            return RobloxLinkRegistrationState.Conflict;

        return progIdExists && protocolExists && openWithValueExists &&
               progIdHasExpectedValues && protocolHasExpectedValues
            ? RobloxLinkRegistrationState.Enabled
            : RobloxLinkRegistrationState.UpdateRequired;
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

    private bool HasExpectedValues(
        RegistryKey key,
        string expectedDescription)
    {
        using var command = key.OpenSubKey(@"shell\open\command");
        return HasExpectedHandlerValues(
            key.GetValue(null) as string,
            key.GetValue(OwnerValueName) as string,
            key.GetValue("URL Protocol") as string,
            command?.GetValue(null) as string,
            expectedDescription,
            _expectedCommand);
    }

    private static bool IsOwned(RegistryKey? key) =>
        key is null || HasOwnerMarker(key.GetValue(OwnerValueName) as string);

    private static bool IsExpectedRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or
            IOException;
}
