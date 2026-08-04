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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void UnavailableClientBatches_AreReleasedInBoundedRetryWaves(
        int clientCount)
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);
        var keys = Enumerable.Range(0, clientCount)
            .Select(index => $"account-{index:D3}")
            .ToArray();

        foreach (var key in keys)
            tracker.ReportFailure(key);

        Assert.Equal(
            SessionMacroPlaybackRetryTracker.InitialDelay,
            tracker.GetDelayUntilNextAttempt());
        timestamp = 249;
        Assert.DoesNotContain(keys, tracker.CanAttempt);

        var admittedCount = 0;
        var waveCount = (int)Math.Ceiling(
            (double)clientCount /
            SessionMacroPlaybackRetryTracker.RetryWaveSize);
        for (var wave = 0; wave < waveCount; wave++)
        {
            timestamp = 250 +
                (long)wave *
                (long)SessionMacroPlaybackRetryTracker
                    .RetryWaveSpacing.TotalMilliseconds;
            var eligibleCount = keys.Count(tracker.CanAttempt);
            Assert.Equal(
                Math.Min(
                    SessionMacroPlaybackRetryTracker.RetryWaveSize,
                    clientCount - admittedCount),
                eligibleCount);
            admittedCount += eligibleCount;
        }

        Assert.Equal(clientCount, admittedCount);
    }

    [Fact]
    public void RetryWaves_RemainBoundedWhenClockAdvancesBetweenReports()
    {
        long timestamp = 1;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);
        var keys = Enumerable.Range(0, 100)
            .Select(index => $"account-{index:D3}")
            .ToArray();

        foreach (var key in keys)
        {
            tracker.ReportFailure(key);
            timestamp++;
        }

        for (var wave = 0; wave < 13; wave++)
        {
            timestamp = 300 + (wave * 50);
            var eligibleCount = keys.Count(tracker.CanAttempt);
            Assert.Equal(Math.Min(8, keys.Length - (wave * 8)), eligibleCount);
        }
    }

    [Fact]
    public void LatePolling_StillAdmitsOnlyOneBoundedRetryCohort()
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);
        var keys = Enumerable.Range(0, 128)
            .Select(index => $"account-{index:D3}")
            .ToArray();

        foreach (var key in keys)
            tracker.ReportFailure(key);

        // Simulate a healthy macro segment taking longer than every
        // originally scheduled retry wave.
        timestamp = 2_000;
        Assert.Equal(8, keys.Count(tracker.CanAttempt));
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            tracker.GetDelayUntilNextAttempt());

        timestamp = 2_049;
        Assert.Equal(0, keys.Count(tracker.CanAttempt));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1),
            tracker.GetDelayUntilNextAttempt());

        timestamp = 2_050;
        Assert.Equal(8, keys.Count(tracker.CanAttempt));
    }

    [Fact]
    public void NewFailure_IsNotDelayedByAnOlderTargetsLongBackoffWave()
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);

        tracker.ReportFailure("slow");
        timestamp = 250;
        tracker.ReportFailure("slow");
        timestamp = 750;
        tracker.ReportFailure("slow");
        timestamp = 1_750;
        tracker.ReportFailure("slow");
        timestamp = 3_750;
        tracker.ReportFailure("slow");
        timestamp = 7_750;
        tracker.ReportFailure("slow");

        tracker.ReportFailure("new");

        timestamp = 7_999;
        Assert.False(tracker.CanAttempt("new"));
        timestamp = 8_000;
        Assert.True(tracker.CanAttempt("new"));
        Assert.False(tracker.CanAttempt("slow"));
        timestamp = 12_750;
        Assert.True(tracker.CanAttempt("slow"));
    }

    [Fact]
    public void EarliestRetryDelay_TracksTimeAndIgnoresTerminalFailures()
    {
        long timestamp = 0;
        var tracker = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);

        tracker.ReportFailure(
            "terminal",
            SessionMacroPlaybackRetryDisposition.Terminal);
        Assert.Null(tracker.GetDelayUntilNextAttempt());
        Assert.False(tracker.CanAttempt("terminal"));

        tracker.ReportFailure("transient");
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            tracker.GetDelayUntilNextAttempt());
        timestamp = 125;
        Assert.Equal(
            TimeSpan.FromMilliseconds(125),
            tracker.GetDelayUntilNextAttempt());
        timestamp = 250;
        Assert.Equal(TimeSpan.Zero, tracker.GetDelayUntilNextAttempt());

        tracker.ReportSuccess("transient");
        Assert.Null(tracker.GetDelayUntilNextAttempt());
        timestamp = 60_000;
        Assert.False(tracker.CanAttempt("terminal"));
        tracker.ReportSuccess("terminal");
        Assert.True(tracker.CanAttempt("terminal"));
    }
}
