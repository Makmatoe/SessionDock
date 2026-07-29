using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class JoinUserWatchPolicyTests
{
    private static readonly JoinUserIdentity Identity = new(
        42,
        "TargetUser",
        "Target User");
    private static readonly JoinUserResolution Resolution = new(
        42,
        "TargetUser",
        "Target User",
        123456,
        "a18c877e-4070-4a84-a5f7-36668b46a77d");

    [Fact]
    public void IdentityReadyAndAvailablePresenceTriggerExactlyOneNextStep()
    {
        var policy = new JoinUserWatchPolicy();

        var identity = policy.ObserveIdentity(
            new JoinUserIdentityLookupResult(
                JoinUserIdentityAvailability.Available,
                Identity),
            TimeSpan.Zero);
        var presence = policy.ObservePresence(
            new JoinUserLookupResult(
                JoinUserAvailability.Available,
                Resolution),
            TimeSpan.FromSeconds(1));

        Assert.Equal(JoinUserWatchAction.IdentityReady, identity.Action);
        Assert.Equal(TimeSpan.Zero, identity.Delay);
        Assert.Equal(JoinUserWatchAction.Join, presence.Action);
        Assert.Equal(TimeSpan.Zero, presence.Delay);
    }

    [Theory]
    [InlineData("Offline", 30)]
    [InlineData("NotInExperience", 30)]
    [InlineData("NotJoinable", 60)]
    public void ValidUnavailablePresenceContinuesAtBoundedRate(
        string availability,
        double expectedSeconds)
    {
        var policy = new JoinUserWatchPolicy();

        var decision = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                Enum.Parse<JoinUserAvailability>(availability)),
            TimeSpan.FromMinutes(1),
            randomUnitInterval: 0.5);

        Assert.Equal(JoinUserWatchAction.Continue, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), decision.Delay);
    }

    [Fact]
    public void ServiceFailuresBackOffAndCapAtFiveMinutes()
    {
        var policy = new JoinUserWatchPolicy();
        var delays = Enumerable.Range(0, 6)
            .Select(_ => policy.ObservePresence(
                JoinUserLookupResult.Unavailable(
                    JoinUserAvailability.ServiceUnavailable),
                TimeSpan.FromMinutes(1),
                randomUnitInterval: 0.5).Delay)
            .ToArray();

        Assert.Equal(
            [30, 60, 120, 240, 300, 300],
            delays.Select(delay => (int)delay.TotalSeconds));
    }

    [Fact]
    public void FailureBackoffRemainsCappedAfterPositiveJitter()
    {
        var policy = new JoinUserWatchPolicy();
        JoinUserWatchDecision decision = null!;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            decision = policy.ObservePresence(
                JoinUserLookupResult.Unavailable(
                    JoinUserAvailability.ServiceUnavailable),
                TimeSpan.FromMinutes(1),
                randomUnitInterval: 1);
        }

        Assert.Equal(
            JoinUserWatchPolicy.MaximumFailureBackoff,
            decision.Delay);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(75, 75)]
    [InlineData(600, 300)]
    public void RateLimitRetryAfterHonorsHardBounds(
        double requestedSeconds,
        double expectedSeconds)
    {
        var policy = new JoinUserWatchPolicy();

        var decision = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.RateLimited,
                TimeSpan.FromSeconds(requestedSeconds)),
            TimeSpan.FromMinutes(1));

        Assert.Equal(JoinUserWatchAction.Continue, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), decision.Delay);
    }

    [Fact]
    public void SuccessfulPresenceResetsFailureBackoff()
    {
        var policy = new JoinUserWatchPolicy();
        _ = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.ServiceUnavailable),
            TimeSpan.Zero,
            randomUnitInterval: 0.5);
        _ = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.ServiceUnavailable),
            TimeSpan.Zero,
            randomUnitInterval: 0.5);

        var normal = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.Offline),
            TimeSpan.FromMinutes(1),
            randomUnitInterval: 0.5);
        var failureAfterReset = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.ServiceUnavailable),
            TimeSpan.FromMinutes(2),
            randomUnitInterval: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(30), normal.Delay);
        Assert.Equal(TimeSpan.FromSeconds(30), failureAfterReset.Delay);
    }

    [Fact]
    public void UserNotFoundAndSessionLossStopWithoutDelay()
    {
        var policy = new JoinUserWatchPolicy();

        var missing = policy.ObserveIdentity(
            JoinUserIdentityLookupResult.Unavailable(
                JoinUserIdentityAvailability.UserNotFound),
            TimeSpan.Zero);
        var session = policy.ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.SessionUnavailable),
            TimeSpan.Zero);

        Assert.Equal(JoinUserWatchAction.StopUserNotFound, missing.Action);
        Assert.Equal(TimeSpan.Zero, missing.Delay);
        Assert.Equal(
            JoinUserWatchAction.StopSessionUnavailable,
            session.Action);
        Assert.Equal(TimeSpan.Zero, session.Delay);
    }

    [Fact]
    public void FourHourLimitExpiresBeforeAnotherPollOrLaunch()
    {
        var policy = new JoinUserWatchPolicy();

        var decision = policy.ObservePresence(
            new JoinUserLookupResult(
                JoinUserAvailability.Available,
                Resolution),
            JoinUserWatchPolicy.MaximumWatchDuration);

        Assert.True(policy.HasExpired(
            JoinUserWatchPolicy.MaximumWatchDuration));
        Assert.Equal(JoinUserWatchAction.Expired, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Fact]
    public void FinalDelayIsClampedToTheFourHourExpiry()
    {
        var policy = new JoinUserWatchPolicy();

        var bounded = policy.BoundDelayToExpiry(
            TimeSpan.FromMinutes(5),
            JoinUserWatchPolicy.MaximumWatchDuration -
            TimeSpan.FromSeconds(7));
        var expired = policy.BoundDelayToExpiry(
            TimeSpan.FromSeconds(30),
            JoinUserWatchPolicy.MaximumWatchDuration);

        Assert.Equal(TimeSpan.FromSeconds(7), bounded);
        Assert.Equal(TimeSpan.Zero, expired);
    }

    [Theory]
    [InlineData(0.0, 24)]
    [InlineData(0.5, 30)]
    [InlineData(1.0, 36)]
    public void NormalPollingUsesBoundedTwentyPercentJitter(
        double randomUnitInterval,
        double expectedSeconds)
    {
        var decision = new JoinUserWatchPolicy().ObservePresence(
            JoinUserLookupResult.Unavailable(
                JoinUserAvailability.Offline),
            TimeSpan.Zero,
            randomUnitInterval);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), decision.Delay);
        Assert.True(decision.Delay >= TimeSpan.FromSeconds(15));
    }
}
