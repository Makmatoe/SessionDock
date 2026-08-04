using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class NamedDestinationPersistenceTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-named-destination-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SaveLoad_PreservesStableIdsAndMirrorsAssignments()
    {
        var accountKey = Guid.NewGuid().ToString("N");
        var account = new AccountProfile
        {
            Key = accountKey,
            UserId = 42,
            Username = "builder",
            SessionFolder = $@"Profiles\{accountKey}",
            Destination = "67890"
        };
        var settings = new AppSettings
        {
            Accounts = [account],
            ActiveAccountKey = account.Key,
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "stable-destination-id",
                    Name = "Main game",
                    Value = "12345",
                    AccountKeys = [account.Key]
                }
            ]
        };

        new SettingsService(_storageDirectory).Save(settings);
        var loaded = new SettingsService(_storageDirectory).Load();

        var destination = Assert.Single(loaded.NamedDestinations);
        Assert.Equal("stable-destination-id", destination.Id);
        Assert.Equal("Main game", destination.Name);
        Assert.Equal("12345", destination.Value);
        Assert.Equal(new[] { account.Key }, destination.AccountKeys);
        Assert.Equal("12345", Assert.Single(loaded.Accounts).Destination);
    }

    [Fact]
    public void Save_NormalizesDuplicateIdsNamesAndAccountAssignments()
    {
        var accountKey = Guid.NewGuid().ToString("N");
        var account = new AccountProfile
        {
            Key = accountKey,
            UserId = 42,
            Username = "builder",
            SessionFolder = $@"Profiles\{accountKey}"
        };
        var settings = new AppSettings
        {
            Accounts = [account],
            ActiveAccountKey = account.Key,
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "duplicate-id",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys = [account.Key, account.Key]
                },
                new NamedDestination
                {
                    Id = "duplicate-id",
                    Name = "farm",
                    Value = "67890",
                    AccountKeys = [account.Key, "missing"]
                }
            ]
        };

        new SettingsService(_storageDirectory).Save(settings);
        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(2, loaded.NamedDestinations.Count);
        Assert.Equal(
            2,
            loaded.NamedDestinations.Select(destination => destination.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            2,
            loaded.NamedDestinations.Select(destination => destination.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            1,
            loaded.NamedDestinations.Sum(destination =>
                destination.AccountKeys.Count));
        Assert.Equal("12345", Assert.Single(loaded.Accounts).Destination);
    }

    [Fact]
    public void EditUnassign_SaveLoad_DoesNotResurrectThePriorDestination()
    {
        var retainedKey = Guid.NewGuid().ToString("N");
        var removedKey = Guid.NewGuid().ToString("N");
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = retainedKey,
                    UserId = 42,
                    Username = "retained",
                    SessionFolder = $@"Profiles\{retainedKey}",
                    Destination = "12345"
                },
                new AccountProfile
                {
                    Key = removedKey,
                    UserId = 43,
                    Username = "removed",
                    SessionFolder = $@"Profiles\{removedKey}",
                    Destination = "12345"
                }
            ],
            ActiveAccountKey = retainedKey,
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "stable-destination-id",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys = [retainedKey, removedKey]
                }
            ]
        };
        var service = new SettingsService(_storageDirectory);
        service.Save(settings);
        var loaded = service.Load();

        Assert.True(NamedDestinationPolicy.TryUpsert(
            loaded,
            destinationId: "stable-destination-id",
            name: "Farm",
            value: "12345",
            accountKeys: [retainedKey],
            out _,
            out _));
        service.Save(loaded);
        var reopened = service.Load();

        Assert.Equal(
            new[] { retainedKey },
            Assert.Single(reopened.NamedDestinations).AccountKeys);
        Assert.Equal(
            "12345",
            reopened.Accounts.Single(account =>
                account.Key == retainedKey).Destination);
        Assert.Null(reopened.Accounts.Single(account =>
            account.Key == removedKey).Destination);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }
}
