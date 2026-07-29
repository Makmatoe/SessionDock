using SessionDock.Models;

namespace SessionDock.Services;

internal static class AppSettingsSnapshot
{
    // Keep persisted-field copying centralized here so save snapshots and
    // mutation rollback cannot drift as the settings schema evolves.
    internal static AppSettings Create(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = new AppSettings
        {
            Accounts = source.Accounts.Select(Clone).ToList(),
            RecentExperiences = source.RecentExperiences
                .Select(Clone)
                .ToList(),
            BatchLaunchPresets = source.BatchLaunchPresets
                .Select(Clone)
                .ToList()
        };
        CopyRootState(source, snapshot);
        return snapshot;
    }

    internal static void Restore(
        AppSettings source,
        AppSettings target,
        IReadOnlyList<AccountProfile> originalAccounts,
        IReadOnlyList<RecentExperience> originalRecentExperiences)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(originalAccounts);
        ArgumentNullException.ThrowIfNull(originalRecentExperiences);
        if (source.Accounts.Count != originalAccounts.Count ||
            source.RecentExperiences.Count != originalRecentExperiences.Count)
        {
            throw new InvalidOperationException(
                "The settings snapshot no longer matches its original items.");
        }

        for (var index = 0; index < originalAccounts.Count; index++)
            Copy(source.Accounts[index], originalAccounts[index]);
        target.Accounts = [.. originalAccounts];

        for (var index = 0; index < originalRecentExperiences.Count; index++)
        {
            Copy(
                source.RecentExperiences[index],
                originalRecentExperiences[index]);
        }
        target.RecentExperiences = [.. originalRecentExperiences];
        target.BatchLaunchPresets = source.BatchLaunchPresets
            .Select(Clone)
            .ToList();
        CopyRootState(source, target);
    }

    internal static AccountProfile Clone(AccountProfile source)
    {
        var clone = new AccountProfile();
        Copy(source, clone);
        return clone;
    }

    internal static void Copy(AccountProfile source, AccountProfile target)
    {
        target.Key = source.Key;
        target.UserId = source.UserId;
        target.Username = source.Username;
        target.SessionFolder = source.SessionFolder;
        target.Label = source.Label;
        target.Group = source.Group;
        target.ColorHex = source.ColorHex;
        target.Destination = source.Destination;
    }

    internal static RecentExperience Clone(RecentExperience source)
    {
        var clone = new RecentExperience();
        Copy(source, clone);
        return clone;
    }

    internal static BatchLaunchPreset Clone(BatchLaunchPreset source) =>
        new()
        {
            Name = source.Name,
            AccountKeys = [.. source.AccountKeys],
            DelaySeconds = source.DelaySeconds
        };

    internal static WindowPlacementSettings Clone(
        WindowPlacementSettings source) =>
        new()
        {
            MonitorDeviceName = source.MonitorDeviceName,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            Width = source.Width,
            Height = source.Height,
            IsMaximized = source.IsMaximized
        };

    internal static void Copy(RecentExperience source, RecentExperience target)
    {
        target.Destination = source.Destination;
        target.PlaceId = source.PlaceId;
        target.Name = source.Name;
        target.CustomName = source.CustomName;
        target.IsPrivateServer = source.IsPrivateServer;
        target.IsPinned = source.IsPinned;
        target.ServerJobId = source.ServerJobId;
        target.AccountUserId = source.AccountUserId;
        target.AccountUsername = source.AccountUsername;
        target.LastLaunchedAt = source.LastLaunchedAt;
    }

    private static void CopyRootState(
        AppSettings source,
        AppSettings target)
    {
        target.ActiveAccountKey = source.ActiveAccountKey;
        target.BatchLaunchDelaySeconds = source.BatchLaunchDelaySeconds;
        target.MainWindowPlacement = source.MainWindowPlacement is null
            ? null
            : Clone(source.MainWindowPlacement);
        target.UiSoundsEnabled = source.UiSoundsEnabled;
        target.UseLightTheme = source.UseLightTheme;
        target.Language = source.Language;
        target.StartupSound = source.StartupSound;
        target.CustomStartupSoundFileName =
            source.CustomStartupSoundFileName;
        target.PendingProfileDeletionKeys =
            [.. source.PendingProfileDeletionKeys];
        target.LockedUserId = source.LockedUserId;
        target.LockedUsername = source.LockedUsername;
        target.PlaceId = source.PlaceId;
        target.Destination = source.Destination;
    }
}
