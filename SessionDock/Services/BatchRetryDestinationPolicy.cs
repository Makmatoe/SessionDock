using SessionDock.Models;

namespace SessionDock.Services;

internal static class BatchRetryDestinationPolicy
{
    internal static AccountProfile CreateRetryAccount(
        AccountProfile currentAccount,
        IReadOnlyDictionary<string, string> effectiveDestinations)
    {
        ArgumentNullException.ThrowIfNull(currentAccount);
        ArgumentNullException.ThrowIfNull(effectiveDestinations);

        var retryAccount = AppSettingsSnapshot.Clone(currentAccount);
        foreach (var pair in effectiveDestinations)
        {
            if (!pair.Key.Equals(
                    retryAccount.Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The retry snapshot is the launch that actually failed. It must
            // win over a live account default, including an explicit empty
            // value, or template-specific destinations silently change.
            retryAccount.Destination = pair.Value;
            break;
        }

        return retryAccount;
    }
}
