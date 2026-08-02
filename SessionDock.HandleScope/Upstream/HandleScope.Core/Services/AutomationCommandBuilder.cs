using HandleScope.Models;

namespace HandleScope.Services;

public static class AutomationCommandBuilder
{
    public static string BuildRecurringCloseCommand(
        string processName,
        HandleEntry handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(handle);

        if (!RobloxAutomationRecipe.IsSupported(processName, handle))
        {
            throw new ArgumentException(
                "Release automation is limited to the exact Roblox singleton event recipe.",
                nameof(handle));
        }

        return
            "& \"$env:LOCALAPPDATA\\Programs\\HandleScope\\Api\\Invoke-HandleScopeClose.ps1\"" +
            $" -ProcessName {QuotePowerShell(RobloxAutomationRecipe.ProcessName)}" +
            $" -HandleName {QuotePowerShell(handle.Name)}" +
            $" -Type {QuotePowerShell(handle.ObjectType)}" +
            $" -Access {QuotePowerShell(handle.AccessDisplay)}" +
            " -Exact -AllProcesses";
    }

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
