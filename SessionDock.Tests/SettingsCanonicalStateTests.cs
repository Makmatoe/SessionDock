using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SettingsCanonicalStateTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-canonical-settings-{Guid.NewGuid():N}");

    [Fact]
    public void Load_NullActiveAccountKey_SelectsAndPersistsFirstAccount()
    {
        var first = CreateAccount(new string('a', 32), 1, "first");
        var second = CreateAccount(new string('b', 32), 2, "second");
        WriteRawSettings(new AppSettings
        {
            Accounts = [first, second],
            ActiveAccountKey = null
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(first.Key, loaded.ActiveAccountKey);
        Assert.Equal(first.Key, ReadPersistedSettings().ActiveAccountKey);
    }

    [Fact]
    public void Load_MiscasedActiveAccountKey_CanonicalizesWithoutSelectingFirst()
    {
        var first = CreateAccount(new string('a', 32), 1, "first");
        var second = CreateAccount(new string('b', 32), 2, "second");
        WriteRawSettings(new AppSettings
        {
            Accounts = [first, second],
            ActiveAccountKey = second.Key.ToUpperInvariant()
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(second.Key, loaded.ActiveAccountKey);
        Assert.Equal(second.Key, ReadPersistedSettings().ActiveAccountKey);
    }

    [Fact]
    public void Load_BuiltInStartupSoundClearsStaleCustomFileReference()
    {
        WriteRawSettings(new AppSettings
        {
            StartupSound = UiSoundService.StartupSoft,
            CustomStartupSoundFileName = "startup-custom.wav"
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Null(loaded.CustomStartupSoundFileName);
        Assert.Null(ReadPersistedSettings().CustomStartupSoundFileName);
    }

    [Theory]
    [InlineData("123", 999, true, 123, false)]
    [InlineData("123", 0, true, 123, false)]
    [InlineData(
        "https://www.roblox.com/games/456?privateServerLinkCode=abcdef",
        999,
        false,
        456,
        true)]
    [InlineData(
        "https://www.roblox.com/share?code=abcdef",
        789,
        false,
        789,
        true)]
    public void Load_ReconcilesRecentDerivedDestinationMetadata(
        string destination,
        long storedPlaceId,
        bool storedPrivate,
        long expectedPlaceId,
        bool expectedPrivate)
    {
        WriteRawSettings(new AppSettings
        {
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination = destination,
                    PlaceId = storedPlaceId,
                    IsPrivateServer = storedPrivate,
                    LastLaunchedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        var recent = Assert.Single(loaded.RecentExperiences);
        Assert.Equal(expectedPlaceId, recent.PlaceId);
        Assert.Equal(expectedPrivate, recent.IsPrivateServer);
        var persisted = Assert.Single(ReadPersistedSettings().RecentExperiences);
        Assert.Equal(expectedPlaceId, persisted.PlaceId);
        Assert.Equal(expectedPrivate, persisted.IsPrivateServer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private void WriteRawSettings(AppSettings settings)
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            JsonSerializer.Serialize(settings));
    }

    private AppSettings ReadPersistedSettings() =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(
            Path.Combine(_storageDirectory, "settings.json")))!;

    private static AccountProfile CreateAccount(
        string key,
        long userId,
        string username) =>
        new()
        {
            Key = key,
            UserId = userId,
            Username = username,
            SessionFolder = $@"Profiles\{key}"
        };
}
