using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SettingsMutationTests
{
    [Fact]
    public void TryCommit_IOException_RestoresOriginalSettings()
    {
        var settings = CreateSettings();
        var originalAccount = settings.Accounts[0];
        var originalRecent = settings.RecentExperiences[0];

        var committed = SettingsMutation.TryCommit(
            settings,
            () => MutateEverySettingsArea(settings),
            _ => throw new IOException("disk unavailable"),
            out var failure);

        Assert.False(committed);
        Assert.IsType<IOException>(failure);
        AssertOriginalSettings(
            settings,
            originalAccount,
            originalRecent);
    }

    [Fact]
    public void TryCommit_UnauthorizedAccess_RestoresOriginalSettings()
    {
        var settings = CreateSettings();
        var originalAccount = settings.Accounts[0];
        var originalRecent = settings.RecentExperiences[0];

        var committed = SettingsMutation.TryCommit(
            settings,
            () => MutateEverySettingsArea(settings),
            _ => throw new UnauthorizedAccessException("write denied"),
            out var failure);

        Assert.False(committed);
        Assert.IsType<UnauthorizedAccessException>(failure);
        AssertOriginalSettings(
            settings,
            originalAccount,
            originalRecent);
    }

    [Fact]
    public void TryCommit_UnexpectedFailure_RestoresThenRethrows()
    {
        var settings = CreateSettings();

        Assert.Throws<InvalidOperationException>(() =>
            SettingsMutation.TryCommit(
                settings,
                () => settings.Accounts[0].Label = "Changed",
                _ => throw new InvalidOperationException("programmer failure"),
                out _));

        Assert.Equal("Original", settings.Accounts[0].Label);
    }

    [Fact]
    public void TryCommit_MutationIOException_RestoresThenRethrows()
    {
        var settings = CreateSettings();

        Assert.Throws<IOException>(() =>
            SettingsMutation.TryCommit(
                settings,
                () =>
                {
                    settings.Accounts[0].Label = "Changed";
                    throw new IOException("mutation defect");
                },
                _ => Assert.Fail("Save must not run after a failed mutation."),
                out _));

        Assert.Equal("Original", settings.Accounts[0].Label);
    }

    [Fact]
    public void TryCommit_Success_KeepsMutation()
    {
        var settings = CreateSettings();
        AppSettings? saved = null;

        var committed = SettingsMutation.TryCommit(
            settings,
            () => settings.Accounts[0].Label = "Changed",
            candidate => saved = candidate,
            out var failure);

        Assert.True(committed);
        Assert.Null(failure);
        Assert.Same(settings, saved);
        Assert.Equal("Changed", settings.Accounts[0].Label);
    }

    private static AppSettings CreateSettings()
    {
        var key = Guid.NewGuid().ToString("N");
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{key}",
                    Label = "Original",
                    ColorHex = "#7C5CFC",
                    Destination = "123"
                }
            ],
            ActiveAccountKey = key,
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination = "123",
                    PlaceId = 123,
                    Name = "Original place",
                    AccountUserId = 42,
                    LastLaunchedAt = new DateTimeOffset(
                        2026,
                        7,
                        22,
                        10,
                        0,
                        0,
                        TimeSpan.Zero)
                }
            ],
            UiSoundsEnabled = true,
            UseLightTheme = true,
            Language = LocalizationPreference.Dutch,
            StartupSound = "soft"
        };
    }

    private static void MutateEverySettingsArea(AppSettings settings)
    {
        settings.Accounts[0].Label = "Changed";
        settings.Accounts.Clear();
        settings.Accounts.Add(new AccountProfile
        {
            Key = Guid.NewGuid().ToString("N"),
            UserId = 99,
            Username = "other"
        });
        settings.ActiveAccountKey = settings.Accounts[0].Key;
        settings.RecentExperiences[0].Name = "Changed place";
        settings.RecentExperiences.Clear();
        settings.UiSoundsEnabled = false;
        settings.UseLightTheme = false;
        settings.Language = LocalizationPreference.English;
        settings.StartupSound = "bright";
        settings.CustomStartupSoundFileName = "startup-custom.wav";
        settings.PendingProfileDeletionKeys.Add(Guid.NewGuid().ToString("N"));
        settings.LockedUserId = 99;
        settings.LockedUsername = "legacy";
        settings.PlaceId = 999;
        settings.Destination = "999";
    }

    private static void AssertOriginalSettings(
        AppSettings settings,
        AccountProfile originalAccount,
        RecentExperience originalRecent)
    {
        Assert.Same(originalAccount, Assert.Single(settings.Accounts));
        Assert.Equal("Original", originalAccount.Label);
        Assert.Equal("#7C5CFC", originalAccount.ColorHex);
        Assert.Equal("123", originalAccount.Destination);
        Assert.Equal(originalAccount.Key, settings.ActiveAccountKey);
        Assert.Same(originalRecent, Assert.Single(settings.RecentExperiences));
        Assert.Equal("Original place", originalRecent.Name);
        Assert.True(settings.UiSoundsEnabled);
        Assert.True(settings.UseLightTheme);
        Assert.Equal(LocalizationPreference.Dutch, settings.Language);
        Assert.Equal("soft", settings.StartupSound);
        Assert.Null(settings.CustomStartupSoundFileName);
        Assert.Empty(settings.PendingProfileDeletionKeys);
        Assert.Null(settings.LockedUserId);
        Assert.Null(settings.LockedUsername);
        Assert.Null(settings.PlaceId);
        Assert.Null(settings.Destination);
    }
}
