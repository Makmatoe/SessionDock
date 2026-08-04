using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccountRemovalSettingsPolicyTests
{
    [Fact]
    public void RemoveAccounts_InactiveAccountPreservesCanonicalActiveAccount()
    {
        var first = CreateAccount("first");
        var active = CreateAccount("active");
        var removed = CreateAccount("removed");
        var settings = new AppSettings
        {
            Accounts = [first, active, removed],
            ActiveAccountKey = "ACTIVE"
        };

        var removedCount = AccountRemovalSettingsPolicy.RemoveAccounts(
            settings,
            [removed.Key]);

        Assert.Equal(1, removedCount);
        Assert.Equal([first, active], settings.Accounts);
        Assert.Equal(active.Key, settings.ActiveAccountKey);
    }

    [Fact]
    public void RemoveAccounts_PrunesDependentSettingsAndSelectsFallback()
    {
        var removed = CreateAccount("removed");
        var retained = CreateAccount("retained");
        var settings = new AppSettings
        {
            Accounts = [removed, retained],
            ActiveAccountKey = removed.Key,
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "destination",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys = [removed.Key, retained.Key]
                }
            ],
            BatchLaunchPresets =
            [
                new BatchLaunchPreset
                {
                    Name = "Both",
                    AccountKeys = [removed.Key, retained.Key],
                    DelaySeconds = 4
                }
            ]
        };

        var removedCount = AccountRemovalSettingsPolicy.RemoveAccounts(
            settings,
            ["REMOVED"]);

        Assert.Equal(1, removedCount);
        Assert.Equal(retained.Key, settings.ActiveAccountKey);
        Assert.Equal(
            [retained.Key],
            Assert.Single(settings.NamedDestinations).AccountKeys);
        Assert.Empty(settings.BatchLaunchPresets);
        Assert.Equal("12345", retained.Destination);
    }

    private static AccountProfile CreateAccount(string key) => new()
    {
        Key = key,
        UserId = key.GetHashCode(StringComparison.Ordinal),
        Username = key,
        SessionFolder = key
    };
}
