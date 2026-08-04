using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RunningClientRegistryTests
{
    [Fact]
    public void Track_PreservesMultipleClientsForOneAccount()
    {
        var registry = new RunningClientRegistry();
        var first = CreateIdentity(101, 0);
        var second = CreateIdentity(202, 1);
        var attribution = CreateAttribution("main");

        registry.Track(first, attribution);
        registry.Track(second, attribution);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet(first, out var firstResult));
        Assert.True(registry.TryGet(second, out var secondResult));
        Assert.Equal("main", firstResult!.AccountLabel);
        Assert.Equal("main", secondResult!.AccountLabel);
    }

    [Fact]
    public void Track_ReplacesOnlyTheSameExactIdentity()
    {
        var registry = new RunningClientRegistry();
        var identity = CreateIdentity(101, 0);
        registry.Track(identity, CreateAttribution("old"));

        registry.Track(
            new RobloxClientProcessIdentity(
                identity.ProcessId,
                identity.StartTimeUtc,
                identity.ExecutablePath.ToUpperInvariant()),
            CreateAttribution("new"));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(identity, out var result));
        Assert.Equal("new", result!.AccountLabel);
    }

    [Fact]
    public void Prune_RemovesExitedIdentityWithoutGuessingReplacementPid()
    {
        var registry = new RunningClientRegistry();
        var exited = CreateIdentity(101, 0);
        var running = CreateIdentity(202, 1);
        registry.Track(exited, CreateAttribution("removed account"));
        registry.Track(running, CreateAttribution("current account"));

        registry.Prune([running]);

        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet(exited, out _));
        Assert.True(registry.TryGet(running, out var result));
        Assert.Equal("current account", result!.AccountLabel);
    }

    [Fact]
    public void Reconcile_PreservesAttributionAcrossIncompleteScan()
    {
        var registry = new RunningClientRegistry();
        var identity = CreateIdentity(101, 0);
        registry.Track(identity, CreateAttribution("main"));

        registry.Reconcile([], scanIsComplete: false);

        Assert.True(registry.TryGet(identity, out var preserved));
        Assert.Equal("main", preserved!.AccountLabel);

        registry.Reconcile([identity], scanIsComplete: true);
        Assert.True(registry.TryGet(identity, out _));

        registry.Reconcile([], scanIsComplete: true);
        Assert.False(registry.TryGet(identity, out _));
    }

    [Fact]
    public void Snapshot_ReturnsAttributedClientsInLaunchOrder()
    {
        var registry = new RunningClientRegistry();
        var laterIdentity = CreateIdentity(202, 1);
        var earlierIdentity = CreateIdentity(101, 0);
        var sameTimeHigherPid = CreateIdentity(303, 2);
        var launchTime = new DateTimeOffset(
            2026,
            7,
            22,
            12,
            0,
            0,
            TimeSpan.Zero);

        registry.Track(
            laterIdentity,
            CreateAttribution("later") with
            {
                LaunchedAt = launchTime.AddMinutes(1)
            });
        registry.Track(
            sameTimeHigherPid,
            CreateAttribution("same time, higher pid") with
            {
                LaunchedAt = launchTime
            });
        registry.Track(
            earlierIdentity,
            CreateAttribution("earlier") with
            {
                LaunchedAt = launchTime
            });

        var snapshot = registry.Snapshot();

        Assert.Collection(
            snapshot,
            item => Assert.Equal(101, item.Identity.ProcessId),
            item => Assert.Equal(303, item.Identity.ProcessId),
            item => Assert.Equal(202, item.Identity.ProcessId));
        Assert.Equal("earlier", snapshot[0].Attribution.AccountLabel);
    }

    [Fact]
    public void CreateRunningClientAttribution_PreservesActualLaunchDestination()
    {
        var account = new Models.AccountProfile
        {
            Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            UserId = 123,
            Username = "test_account",
            Destination = "https://www.roblox.com/games/111/account-default"
        };
        var recent = new Models.RecentExperience
        {
            Destination = "https://www.roblox.com/games/222/template-slot",
            PlaceId = 222,
            Name = "Template destination",
            AccountUserId = account.UserId,
            AccountUsername = account.Username,
            LastLaunchedAt = new DateTimeOffset(
                2026,
                7,
                22,
                12,
                0,
                0,
                TimeSpan.Zero)
        };

        var attribution = MainWindow.CreateRunningClientAttribution(
            account,
            recent);

        Assert.Equal(recent.Destination, attribution.LaunchDestination);
        Assert.NotEqual(account.Destination, attribution.LaunchDestination);
    }

    private static RobloxClientProcessIdentity CreateIdentity(
        int processId,
        int addedMinutes) =>
        new(
            processId,
            new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc)
                .AddMinutes(addedMinutes),
            @"C:\TestData\Roblox\Versions\version-a\RobloxPlayerBeta.exe");

    private static RunningClientAttribution CreateAttribution(string label) =>
        new(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            123,
            "test_account",
            label,
            "#4D8DFF",
            920587237,
            "Test Experience",
            "https://www.roblox.com/games/920587237",
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
}
