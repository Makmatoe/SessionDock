using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroPlaybackRetryTrackerTests
{
    [Fact]
    public void Failures_BackOffExponentiallyAndSuccessResetsTarget()
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);

        Assert.True(tracker.CanAttempt("account-a"));
        tracker.ReportFailure("account-a");
        Assert.False(tracker.CanAttempt("account-a"));
        Assert.True(tracker.CanAttempt("account-b"));

        timestamp = 250;
        Assert.True(tracker.CanAttempt("account-a"));
        tracker.ReportFailure("account-a");
        timestamp = 749;
        Assert.False(tracker.CanAttempt("account-a"));
        timestamp = 750;
        Assert.True(tracker.CanAttempt("account-a"));

        tracker.ReportSuccess("account-a");
        Assert.True(tracker.CanAttempt("account-a"));
    }

    [Fact]
    public void FailureDelay_IsCappedAtFiveSeconds()
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);

        for (var index = 0; index < 10; index++)
        {
            tracker.ReportFailure("account-a");
            timestamp += 5_000;
        }
        tracker.ReportFailure("account-a");

        timestamp += 4_999;
        Assert.False(tracker.CanAttempt("account-a"));
        timestamp++;
        Assert.True(tracker.CanAttempt("account-a"));
    }
}
