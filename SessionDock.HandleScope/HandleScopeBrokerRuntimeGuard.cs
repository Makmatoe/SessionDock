using System.Security.Cryptography;
using System.Text;
using HandleScope.Models;

namespace SessionDock.HandleScope;

internal static class HandleScopeBrokerRuntimeGuard
{
    private static readonly HashSet<string> ServiceAccounts =
    [
        "S-1-5-18",
        "S-1-5-19",
        "S-1-5-20"
    ];

    internal static bool IsAllowed(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return !identity.IsElevated &&
               identity.WindowsSessionId != 0 &&
               !ServiceAccounts.Contains(identity.OwnerSid);
    }

    internal static string GetInstanceName(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var sidHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.OwnerSid));
        return $@"Local\SessionDock.HandleScope.Broker.{Convert.ToHexString(sidHash.AsSpan(0, 8))}.{identity.WindowsSessionId}";
    }
}
