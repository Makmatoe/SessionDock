using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccountDestinationServiceTests
{
    private const string ServerJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";

    [Fact]
    public void TryApplyToAll_ValidDestinationUpdatesEveryAccount()
    {
        var accounts = CreateAccounts();

        var success = AccountDestinationService.TryApplyToAll(
            accounts,
            [],
            " 24680 ",
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(2, assignedCount);
        Assert.All(accounts, account => Assert.Equal("24680", account.Destination));
    }

    [Fact]
    public void TryApplyToAll_TrackedJobIdPreservesTheTrackedSelector()
    {
        var accounts = CreateAccounts();
        var recent = new RecentExperience
        {
            Destination = "24680",
            PlaceId = 24680,
            ServerJobId = ServerJobId,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };

        var success = AccountDestinationService.TryApplyToAll(
            accounts,
            [recent],
            ServerJobId,
            out var assignedCount,
            out var error);

        Assert.True(success, error);
        Assert.Equal(2, assignedCount);
        Assert.All(accounts, account => Assert.Equal(ServerJobId, account.Destination));
    }

    [Fact]
    public void TryApplyToAll_InvalidDestinationDoesNotPartiallyUpdateAccounts()
    {
        var accounts = CreateAccounts();
        var original = accounts.Select(account => account.Destination).ToArray();

        var success = AccountDestinationService.TryApplyToAll(
            accounts,
            [],
            "not a destination",
            out var assignedCount,
            out _);

        Assert.False(success);
        Assert.Equal(0, assignedCount);
        Assert.Equal(original, accounts.Select(account => account.Destination));
    }

    [Fact]
    public void TryApplyToAll_NoAccountsExplainsWhyNothingChanged()
    {
        var success = AccountDestinationService.TryApplyToAll(
            [],
            [],
            "24680",
            out var assignedCount,
            out var error);

        Assert.False(success);
        Assert.Equal(0, assignedCount);
        Assert.Equal("Validation.Destination.AccountRequired", error);
    }

    private static List<AccountProfile> CreateAccounts() =>
    [
        new AccountProfile { Key = "one", Destination = "111" },
        new AccountProfile { Key = "two", Destination = "222" }
    ];
}
