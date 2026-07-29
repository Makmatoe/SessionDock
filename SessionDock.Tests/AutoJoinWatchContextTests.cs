using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AutoJoinWatchContextTests
{
    private static readonly AutoJoinWatchContext Expected = new(
        "account-key",
        42,
        "@TargetUser");
    private static readonly AutoJoinWatchContextState Current = new(
        "ACCOUNT-KEY",
        42,
        42,
        "@TargetUser",
        IsJoinUserMode: true,
        IsAutoJoinEnabled: true,
        IsSessionCurrent: true);

    [Fact]
    public void MatchingPinnedContextRemainsCurrent()
    {
        Assert.True(Expected.Matches(Current));
    }

    [Fact]
    public void AnyRelevantContextChangeRejectsAStaleResult()
    {
        var changedContexts = new[]
        {
            Current with { AccountKey = "other-account" },
            Current with { ProfileUserId = 43 },
            Current with { AuthenticatedUserId = 43 },
            Current with { RequestedText = "@OtherUser" },
            Current with { IsJoinUserMode = false },
            Current with { IsAutoJoinEnabled = false },
            Current with { IsSessionCurrent = false }
        };

        Assert.All(
            changedContexts,
            current => Assert.False(Expected.Matches(current)));
    }
}
