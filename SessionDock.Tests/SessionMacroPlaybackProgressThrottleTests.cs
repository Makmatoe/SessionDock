using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroPlaybackProgressThrottleTests
{
    [Fact]
    public void EightClientCycle_ProducesOneTransientStatusWithinInterval()
    {
        const long timestamp = 10;
        var throttle = new SessionMacroPlaybackProgressThrottle(
            TimeSpan.FromMilliseconds(250),
            () => timestamp,
            timestampFrequency: 1_000);

        var reports = Enumerable.Range(0, 8)
            .Count(_ => throttle.TryAcquire());

        Assert.Equal(1, reports);
    }

    [Fact]
    public void TryAcquire_BoundsRepeatedProgressAndAllowsNextInterval()
    {
        long timestamp = 10;
        var throttle = new SessionMacroPlaybackProgressThrottle(
            TimeSpan.FromMilliseconds(250),
            () => timestamp,
            timestampFrequency: 1_000);

        Assert.True(throttle.TryAcquire());
        timestamp = 259;
        Assert.False(throttle.TryAcquire());
        timestamp = 260;
        Assert.True(throttle.TryAcquire());
    }

    [Fact]
    public void Reset_AndClockRegressionAllowImmediateProgress()
    {
        long timestamp = 1_000;
        var throttle = new SessionMacroPlaybackProgressThrottle(
            TimeSpan.FromMilliseconds(250),
            () => timestamp,
            timestampFrequency: 1_000);

        Assert.True(throttle.TryAcquire());
        timestamp = 900;
        Assert.True(throttle.TryAcquire());
        timestamp = 901;
        Assert.False(throttle.TryAcquire());

        throttle.Reset();

        Assert.True(throttle.TryAcquire());
    }
}
