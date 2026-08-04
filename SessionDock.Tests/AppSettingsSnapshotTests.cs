using System.Reflection;
using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AppSettingsSnapshotTests
{
    [Fact]
    public void Create_CopiesEverySettingWithoutSharingMutableState()
    {
        var source = CreateSettings();
        var expectedJson = JsonSerializer.Serialize(source);

        var snapshot = AppSettingsSnapshot.Create(source);

        Assert.Equal(expectedJson, JsonSerializer.Serialize(snapshot));
        Assert.NotSame(source, snapshot);
        AssertReferencePropertiesAreIndependent(source, snapshot);
        AssertReferencePropertiesAreIndependent(
            source.Accounts[0],
            snapshot.Accounts[0]);
        AssertReferencePropertiesAreIndependent(
            source.NamedDestinations[0],
            snapshot.NamedDestinations[0]);
        AssertReferencePropertiesAreIndependent(
            source.RecentExperiences[0],
            snapshot.RecentExperiences[0]);
        AssertReferencePropertiesAreIndependent(
            source.BatchLaunchPresets[0],
            snapshot.BatchLaunchPresets[0]);
        Assert.NotSame(source.Accounts[0], snapshot.Accounts[0]);
        Assert.NotSame(
            source.NamedDestinations[0],
            snapshot.NamedDestinations[0]);
        Assert.NotSame(
            source.RecentExperiences[0],
            snapshot.RecentExperiences[0]);
        Assert.NotSame(
            source.BatchLaunchPresets[0],
            snapshot.BatchLaunchPresets[0]);

        MutateEverySettingsArea(source);

        Assert.Equal(expectedJson, JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Restore_RestoresEverySettingAndOriginalItemIdentity()
    {
        var settings = CreateSettings();
        var state = AppSettingsSnapshot.Create(settings);
        var originalAccounts = settings.Accounts.ToArray();
        var originalRecentExperiences = settings.RecentExperiences.ToArray();
        var expectedJson = JsonSerializer.Serialize(state);

        MutateEverySettingsArea(settings);

        AppSettingsSnapshot.Restore(
            state,
            settings,
            originalAccounts,
            originalRecentExperiences);

        Assert.Equal(expectedJson, JsonSerializer.Serialize(settings));
        Assert.Same(originalAccounts[0], Assert.Single(settings.Accounts));
        Assert.Same(
            originalRecentExperiences[0],
            Assert.Single(settings.RecentExperiences));
        Assert.NotSame(state.Accounts[0], settings.Accounts[0]);
        Assert.NotSame(
            state.RecentExperiences[0],
            settings.RecentExperiences[0]);
        Assert.NotSame(
            state.PendingProfileDeletionKeys,
            settings.PendingProfileDeletionKeys);
        AssertReferencePropertiesAreIndependent(state, settings);
        AssertReferencePropertiesAreIndependent(
            state.Accounts[0],
            settings.Accounts[0]);
        AssertReferencePropertiesAreIndependent(
            state.NamedDestinations[0],
            settings.NamedDestinations[0]);
        AssertReferencePropertiesAreIndependent(
            state.RecentExperiences[0],
            settings.RecentExperiences[0]);
        AssertReferencePropertiesAreIndependent(
            state.BatchLaunchPresets[0],
            settings.BatchLaunchPresets[0]);
    }

    [Fact]
    public void SnapshotSchema_RequiresExplicitReviewOfEveryModelProperty()
    {
        AssertPublicProperties<AppSettings>(
            "Accounts",
            "ActiveAccountKey",
            "BatchLaunchDelaySeconds",
            "BatchLaunchPresets",
            "CustomStartupSoundFileName",
            "Destination",
            "Language",
            "LockedUserId",
            "LockedUsername",
            "MainWindowPlacement",
            "NamedDestinations",
            "PendingProfileDeletionKeys",
            "PlaceId",
            "RecentExperiences",
            "StartupSound",
            "UseLightTheme",
            "UiSoundsEnabled");
        AssertPublicProperties<WindowPlacementSettings>(
            "Height",
            "IsMaximized",
            "MonitorDeviceName",
            "OffsetX",
            "OffsetY",
            "Width");
        AssertPublicProperties<AccountProfile>(
            "ColorHex",
            "Destination",
            "Group",
            "Key",
            "Label",
            "SessionFolder",
            "UserId",
            "Username");
        AssertPublicProperties<NamedDestination>(
            "AccountKeys",
            "Id",
            "Name",
            "Value");
        AssertPublicProperties<BatchLaunchPreset>(
            "AccountKeys",
            "DelaySeconds",
            "Name");
        AssertPublicProperties<RecentExperience>(
            "AccountUserId",
            "AccountUsername",
            "CustomName",
            "Destination",
            "IsPinned",
            "IsPrivateServer",
            "LastLaunchedAt",
            "Name",
            "PlaceId",
            "ServerJobId");
    }

    [Fact]
    public void SnapshotFixture_AssignsNonDefaultValueToEveryModelProperty()
    {
        var settings = CreateSettings();

        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings,
            new AppSettings());
        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings.Accounts[0],
            new AccountProfile());
        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings.NamedDestinations[0],
            new NamedDestination());
        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings.RecentExperiences[0],
            new RecentExperience());
        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings.BatchLaunchPresets[0],
            new BatchLaunchPreset());
        AssertEveryPropertyHasNonDefaultFixtureValue(
            settings.MainWindowPlacement!,
            new WindowPlacementSettings());
    }

    private static AppSettings CreateSettings()
    {
        const string accountKey = "0123456789abcdef0123456789abcdef";
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = accountKey,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{accountKey}",
                    Label = "Primary",
                    Group = "Friends",
                    ColorHex = "#7C5CFC",
                    Destination = "12345"
                }
            ],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "fedcba9876543210fedcba9876543210",
                    Name = "Main game",
                    Value = "12345",
                    AccountKeys = [accountKey]
                }
            ],
            ActiveAccountKey = accountKey,
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination = "12345",
                    PlaceId = 12345,
                    Name = "Example place",
                    CustomName = "Favorite place",
                    IsPrivateServer = true,
                    IsPinned = true,
                    ServerJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d",
                    AccountUserId = 42,
                    AccountUsername = "builder",
                    LastLaunchedAt = new DateTimeOffset(
                        2026,
                        7,
                        22,
                        10,
                        30,
                        0,
                        TimeSpan.Zero)
                }
            ],
            BatchLaunchPresets =
            [
                new BatchLaunchPreset
                {
                    Name = "Friends",
                    AccountKeys = [accountKey],
                    DelaySeconds = 15
                }
            ],
            BatchLaunchDelaySeconds = 15,
            MainWindowPlacement = new WindowPlacementSettings
            {
                MonitorDeviceName = @"\\.\DISPLAY2",
                OffsetX = 120,
                OffsetY = 80,
                Width = 1080,
                Height = 720,
                IsMaximized = true
            },
            UiSoundsEnabled = false,
            UseLightTheme = true,
            Language = LocalizationPreference.Dutch,
            StartupSound = "bright",
            CustomStartupSoundFileName = "startup-custom.wav",
            PendingProfileDeletionKeys =
                ["fedcba9876543210fedcba9876543210"],
            LockedUserId = 84,
            LockedUsername = "legacy-user",
            PlaceId = 54321,
            Destination = "54321"
        };
    }

    private static void MutateEverySettingsArea(AppSettings settings)
    {
        var originalAccount = settings.Accounts[0];
        originalAccount.Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        originalAccount.UserId = 99;
        originalAccount.Username = "mutated";
        originalAccount.SessionFolder = "Profiles\\mutated";
        originalAccount.Label = "Mutated account";
        originalAccount.Group = "Mutated group";
        originalAccount.ColorHex = "#000000";
        originalAccount.Destination = "99999";
        settings.Accounts = [new AccountProfile()];
        settings.ActiveAccountKey = null;

        settings.NamedDestinations[0].Id =
            "11111111111111111111111111111111";
        settings.NamedDestinations[0].Name = "Mutated destination";
        settings.NamedDestinations[0].Value = "99999";
        settings.NamedDestinations[0].AccountKeys[0] =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        settings.NamedDestinations = [new NamedDestination()];

        var originalRecent = settings.RecentExperiences[0];
        originalRecent.Destination = "99999";
        originalRecent.PlaceId = 99999;
        originalRecent.Name = "Mutated place";
        originalRecent.CustomName = null;
        originalRecent.IsPrivateServer = false;
        originalRecent.IsPinned = false;
        originalRecent.ServerJobId = null;
        originalRecent.AccountUserId = 99;
        originalRecent.AccountUsername = "mutated";
        originalRecent.LastLaunchedAt = DateTimeOffset.MinValue;
        settings.RecentExperiences = [new RecentExperience()];

        settings.BatchLaunchPresets[0].Name = "Mutated preset";
        settings.BatchLaunchPresets[0].AccountKeys[0] =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        settings.BatchLaunchPresets[0].DelaySeconds = 3;
        settings.BatchLaunchPresets = [new BatchLaunchPreset()];
        settings.BatchLaunchDelaySeconds = 3;
        settings.MainWindowPlacement = null;

        settings.UiSoundsEnabled = true;
        settings.UseLightTheme = false;
        settings.Language = LocalizationPreference.English;
        settings.StartupSound = "soft";
        settings.CustomStartupSoundFileName = null;
        settings.PendingProfileDeletionKeys = [];
        settings.LockedUserId = null;
        settings.LockedUsername = null;
        settings.PlaceId = null;
        settings.Destination = null;
    }

    private static void AssertPublicProperties<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            actual);
    }

    private static void AssertEveryPropertyHasNonDefaultFixtureValue<T>(
        T populated,
        T defaults)
    {
        foreach (var property in GetWritableProperties<T>())
        {
            var populatedJson = JsonSerializer.Serialize(
                property.GetValue(populated),
                property.PropertyType);
            var defaultJson = JsonSerializer.Serialize(
                property.GetValue(defaults),
                property.PropertyType);
            Assert.NotEqual(defaultJson, populatedJson);
        }
    }

    private static void AssertReferencePropertiesAreIndependent<T>(
        T source,
        T copy)
    {
        foreach (var property in GetWritableProperties<T>().Where(property =>
                     !property.PropertyType.IsValueType &&
                     property.PropertyType != typeof(string)))
        {
            Assert.NotSame(
                property.GetValue(source),
                property.GetValue(copy));
        }
    }

    private static IEnumerable<PropertyInfo> GetWritableProperties<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite);
}
