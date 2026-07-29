namespace SessionDock.Models;

public sealed class AppSettings
{
    public List<AccountProfile> Accounts { get; set; } = [];
    public string? ActiveAccountKey { get; set; }
    public List<RecentExperience> RecentExperiences { get; set; } = [];
    public List<BatchLaunchPreset> BatchLaunchPresets { get; set; } = [];
    public int BatchLaunchDelaySeconds { get; set; } = 8;
    public bool UiSoundsEnabled { get; set; } = true;
    public bool UseLightTheme { get; set; }
    public string StartupSound { get; set; } = "soft";
    public string? CustomStartupSoundFileName { get; set; }
    public List<string> PendingProfileDeletionKeys { get; set; } = [];

    // Kept for automatic migration from the legacy Roblox One 1.x format.
    public long? LockedUserId { get; set; }
    public string? LockedUsername { get; set; }
    public long? PlaceId { get; set; }
    public string? Destination { get; set; }
}

public sealed class AccountProfile
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string SessionFolder { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Group { get; set; }
    public string? ColorHex { get; set; }
    public string? Destination { get; set; }
}

public sealed class BatchLaunchPreset
{
    public string Name { get; set; } = string.Empty;
    public List<string> AccountKeys { get; set; } = [];
    public int DelaySeconds { get; set; } = 8;
}
