using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AutoJoinLaunchGateTests
{
    [Fact]
    public void StopBeforeClaimPreventsLaunch()
    {
        var gate = new AutoJoinLaunchGate();

        Assert.True(gate.TryStop());
        Assert.False(gate.IsArmed);
        Assert.False(gate.TryClaimLaunch());
    }

    [Fact]
    public async Task ConcurrentResultsCanClaimAtMostOneLaunch()
    {
        var gate = new AutoJoinLaunchGate();

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(_ => Task.Run(gate.TryClaimLaunch)));

        Assert.Single(claims, claimed => claimed);
        Assert.False(gate.IsArmed);
        Assert.False(gate.TryStop());
    }

    [Fact]
    public async Task StopDuringInFlightCheckPreventsLaunchHandoff()
    {
        var gate = new AutoJoinLaunchGate();
        var presenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presenceResult = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var launchCount = 0;
        var watcher = Task.Run(
            async () =>
            {
                presenceStarted.SetResult();
                await presenceResult.Task;
                if (gate.TryClaimLaunch())
                    Interlocked.Increment(ref launchCount);
            },
            TestContext.Current.CancellationToken);

        await presenceStarted.Task;
        Assert.True(gate.TryStop());
        presenceResult.SetResult();
        await watcher;

        Assert.Equal(0, launchCount);
        Assert.False(gate.TryClaimLaunch());
    }
}
