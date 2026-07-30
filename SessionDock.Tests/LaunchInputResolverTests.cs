using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class LaunchInputResolverTests
{
    private const string ServerJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";

    [Fact]
    public void TryResolve_KnownServerJobId_UsesTrackedDestination()
    {
        var recent = new RecentExperience
        {
            Destination = "24680",
            PlaceId = 24680,
            ServerJobId = ServerJobId,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };

        var success = LaunchInputResolver.TryResolve(
            ServerJobId,
            [recent],
            out var resolved,
            out _);

        Assert.True(success);
        Assert.Equal(24680, resolved!.Target.PlaceId);
        Assert.Equal(ServerJobId, resolved.ServerJobId);
        Assert.Equal(ServerJobId, resolved.AccountDestination);
    }

    [Fact]
    public void TryResolve_UnknownBareGuid_ExplainsHowToRejoin()
    {
        var success = LaunchInputResolver.TryResolve(
            ServerJobId,
            [],
            out var resolved,
            out var error);

        Assert.False(success);
        Assert.Null(resolved);
        Assert.Equal(
            LaunchInputResolver.UntrackedServerJobIdErrorKey,
            error);
        Assert.Contains(
            "not in Recent",
            EnglishResourceText.Get(error),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_ExplicitCodeGuid_RemainsAValidShareCode()
    {
        var success = LaunchInputResolver.TryResolve(
            $"code={ServerJobId}",
            [],
            out var resolved,
            out _);

        Assert.True(success);
        Assert.Equal(ServerJobId, resolved!.Target.ShareCode);
    }
}
