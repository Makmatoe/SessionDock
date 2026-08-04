using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccountDestinationServiceTests
{
    private const string ServerJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";

    [Fact]
    public void TryApplyToAll_ValidDestinationUpdatesEveryAccount()
    {
        var settings = CreateSettings();

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [],
            " 24680 ",
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(2, assignedCount);
        Assert.All(
            settings.Accounts,
            account => Assert.Equal("24680", account.Destination));
    }

    [Fact]
    public void TryApplyToAll_TrackedJobIdPreservesTheTrackedSelector()
    {
        var settings = CreateSettings();
        var recent = new RecentExperience
        {
            Destination = "24680",
            PlaceId = 24680,
            ServerJobId = ServerJobId,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [recent],
            ServerJobId,
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(2, assignedCount);
        Assert.All(
            settings.Accounts,
            account => Assert.Equal(ServerJobId, account.Destination));
    }

    [Fact]
    public void TryApplyToAll_InvalidDestinationDoesNotPartiallyUpdateAccounts()
    {
        var settings = CreateSettings();
        var original = settings.Accounts
            .Select(account => account.Destination)
            .ToArray();

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [],
            "not a destination",
            out var assignedCount,
            out _);

        Assert.False(success);
        Assert.Equal(0, assignedCount);
        Assert.Equal(
            original,
            settings.Accounts.Select(account => account.Destination));
    }

    [Fact]
    public void TryApplyToAll_NoAccountsExplainsWhyNothingChanged()
    {
        var success = AccountDestinationService.TryApplyToAll(
            new AppSettings(),
            [],
            "24680",
            out var assignedCount,
            out var error);

        Assert.False(success);
        Assert.Equal(0, assignedCount);
        Assert.Equal("Validation.Destination.AccountRequired", error);
    }

    [Fact]
    public void TryApplyToAll_MatchingNamedDestinationUpdatesAssignments()
    {
        var settings = CreateSettings();
        settings.NamedDestinations =
        [
            new NamedDestination
            {
                Id = "farm",
                Name = "Farm",
                Value = "https://www.roblox.com/games/24680/Farm"
            }
        ];

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [],
            "24680",
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(2, assignedCount);
        var destination = Assert.Single(settings.NamedDestinations);
        Assert.Equal(
            settings.Accounts.Select(account => account.Key),
            destination.AccountKeys);
        Assert.All(
            settings.Accounts,
            account => Assert.Equal(destination.Value, account.Destination));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void TryApplyToAll_ScaledBatchUsesOneExactNamedAssignment(
        int accountCount)
    {
        var accounts = Enumerable.Range(0, accountCount)
            .Select(index => new AccountProfile
            {
                Key = $"account-{index:D3}",
                Destination = "11111"
            })
            .ToList();
        var settings = new AppSettings
        {
            Accounts = accounts,
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "scaled",
                    Name = "Scaled",
                    Value = "https://www.roblox.com/games/24680/Scaled"
                }
            ]
        };

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [],
            "24680",
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(accountCount, assignedCount);
        var destination = Assert.Single(settings.NamedDestinations);
        Assert.Equal(
            accounts.Select(account => account.Key),
            destination.AccountKeys);
        Assert.All(
            accounts,
            account => Assert.Equal(
                destination.Value,
                account.Destination));
    }

    [Fact]
    public void TryApplyToAll_AmbiguousEquivalentTargetsPreservePriorChoices()
    {
        var first = new AccountProfile
        {
            Key = "first",
            Destination = "24680"
        };
        var second = new AccountProfile
        {
            Key = "second",
            Destination = "https://www.roblox.com/games/24680/Second"
        };
        var unassigned = new AccountProfile
        {
            Key = "unassigned",
            Destination = "11111"
        };
        var settings = new AppSettings
        {
            Accounts = [first, second, unassigned],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "first-id",
                    Name = "First",
                    Value = "24680",
                    AccountKeys = [first.Key]
                },
                new NamedDestination
                {
                    Id = "second-id",
                    Name = "Second",
                    Value = "https://www.roblox.com/games/24680/Second",
                    AccountKeys = [second.Key]
                }
            ]
        };

        var success = AccountDestinationService.TryApplyToAll(
            settings,
            [],
            "https://www.roblox.com/games/24680/Requested",
            out _,
            out var error);

        Assert.True(success, error);
        Assert.Equal("24680", first.Destination);
        Assert.Equal(
            "https://www.roblox.com/games/24680/Second",
            second.Destination);
        Assert.Equal(
            "https://www.roblox.com/games/24680/Requested",
            unassigned.Destination);
        Assert.Equal(
            [first.Key],
            settings.NamedDestinations.Single(destination =>
                destination.Id == "first-id").AccountKeys);
        Assert.Equal(
            [second.Key],
            settings.NamedDestinations.Single(destination =>
                destination.Id == "second-id").AccountKeys);
    }

    private static AppSettings CreateSettings() => new()
    {
        Accounts =
        [
            new AccountProfile { Key = "one", Destination = "111" },
            new AccountProfile { Key = "two", Destination = "222" }
        ]
    };
}
