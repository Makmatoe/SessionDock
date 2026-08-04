using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BatchRetryDestinationPolicyTests
{
    [Fact]
    public void CreateRetryAccount_EffectiveDestinationOverridesLiveDefault()
    {
        var current = CreateAccount("account-a", "999");
        IReadOnlyDictionary<string, string> effective =
            new Dictionary<string, string>
            {
                ["ACCOUNT-A"] = "123"
            };

        var retry = BatchRetryDestinationPolicy.CreateRetryAccount(
            current,
            effective);

        Assert.Equal("123", retry.Destination);
        Assert.Equal("999", current.Destination);
        Assert.NotSame(current, retry);
    }

    [Fact]
    public void CreateRetryAccount_MissingSnapshotKeepsLiveDefault()
    {
        var current = CreateAccount("account-a", "999");
        IReadOnlyDictionary<string, string> effective =
            new Dictionary<string, string>
            {
                ["account-b"] = "123"
            };

        var retry = BatchRetryDestinationPolicy.CreateRetryAccount(
            current,
            effective);

        Assert.Equal("999", retry.Destination);
    }

    [Fact]
    public void CreateRetryAccount_ExplicitEmptySnapshotClearsLiveDefault()
    {
        var current = CreateAccount("account-a", "999");
        IReadOnlyDictionary<string, string> effective =
            new Dictionary<string, string>
            {
                ["account-a"] = string.Empty
            };

        var retry = BatchRetryDestinationPolicy.CreateRetryAccount(
            current,
            effective);

        Assert.Equal(string.Empty, retry.Destination);
    }

    private static AccountProfile CreateAccount(
        string key,
        string? destination) =>
        new()
        {
            Key = key,
            UserId = 1,
            Username = "Player",
            SessionFolder = key,
            Destination = destination
        };
}
