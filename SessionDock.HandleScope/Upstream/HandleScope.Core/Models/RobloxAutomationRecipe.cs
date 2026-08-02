using System.Globalization;

namespace HandleScope.Models;

public static class RobloxAutomationRecipe
{
    public const string ProcessName = "RobloxPlayerBeta";
    public const string HandleType = "Event";
    public const uint HandleAccess = 0x001F0003;
    private const string HandlePrefix = @"\Sessions\";
    private const string HandleSuffix = @"\BaseNamedObjects\ROBLOX_singletonEvent";

    public static bool IsSupported(string processName, HandleEntry handle) =>
        IsSupportedProcessName(processName) &&
        string.Equals(handle.ObjectType, HandleType, StringComparison.Ordinal) &&
        handle.GrantedAccess == HandleAccess &&
        TryGetSessionId(handle.Name, out _);

    public static bool IsSupportedProcessName(string? processName)
    {
        if (string.IsNullOrEmpty(processName) || processName != processName.Trim())
        {
            return false;
        }

        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return string.Equals(normalized, ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetSessionId(string? name, out uint sessionId)
    {
        sessionId = 0;
        if (name is null ||
            !name.StartsWith(HandlePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(HandleSuffix, StringComparison.Ordinal) ||
            name.Length <= HandlePrefix.Length + HandleSuffix.Length)
        {
            return false;
        }

        var sessionText = name[HandlePrefix.Length..^HandleSuffix.Length];
        return uint.TryParse(
                   sessionText,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out sessionId) &&
               sessionId > 0;
    }
}
