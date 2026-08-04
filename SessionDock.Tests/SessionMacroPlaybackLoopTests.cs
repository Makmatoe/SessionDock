using SessionDock.ExactWheel;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroPlaybackLoopTests
{
    [Fact]
    public async Task RunUntilStoppedAsync_RepeatsTheCompleteCycleUntilCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var turns = new List<string>();
        var cycleCount = 0;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SessionMacroPlaybackLoop.RunUntilStoppedAsync(
                _ =>
                {
                    turns.Add("client-a");
                    turns.Add("client-b");
                    turns.Add("whole-layout");
                    cycleCount++;
                    if (cycleCount == 3)
                        cancellation.Cancel();
                    return Task.FromResult(true);
                },
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(3, cycleCount);
        Assert.Equal(
            [
                "client-a", "client-b", "whole-layout",
                "client-a", "client-b", "whole-layout",
                "client-a", "client-b", "whole-layout"
            ],
            turns);
    }

    [Fact]
    public async Task RunUntilStoppedAsync_FatalCycleStopsWithoutRepeating()
    {
        var cycleCount = 0;

        await SessionMacroPlaybackLoop.RunUntilStoppedAsync(
            _ =>
            {
                cycleCount++;
                return Task.FromResult(false);
            },
            CancellationToken.None);

        Assert.Equal(1, cycleCount);
    }

    [Fact]
    public async Task RunUntilStoppedAsync_PreCancelledTokenRunsNoCycle()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cycleCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SessionMacroPlaybackLoop.RunUntilStoppedAsync(
                _ =>
                {
                    cycleCount++;
                    return Task.FromResult(true);
                },
                cancellation.Token));

        Assert.Equal(0, cycleCount);
    }

    [Fact]
    public void CycleBoundary_HasAMinimumCompleteCycleDuration()
    {
        Assert.True(
            SessionMacroPlaybackLoop.MinimumCycleDuration >=
                TimeSpan.FromMilliseconds(10));
    }

    [Theory]
    [InlineData(4, 6)]
    [InlineData(10, 0)]
    [InlineData(20, 0)]
    public async Task CycleBoundary_DelaysOnlyTheUnspentFloor(
        int elapsedMilliseconds,
        int expectedDelayMilliseconds)
    {
        var cycleCount = 0;
        var delays = new List<TimeSpan>();

        await SessionMacroPlaybackLoop.RunUntilStoppedAsync(
            _ => Task.FromResult(++cycleCount == 1),
            static () => 100,
            _ => TimeSpan.FromMilliseconds(elapsedMilliseconds),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        if (expectedDelayMilliseconds == 0)
            Assert.Empty(delays);
        else
            Assert.Equal(
                [TimeSpan.FromMilliseconds(expectedDelayMilliseconds)],
                delays);
    }

    [Fact]
    public void CleanCancellation_IsTheOnlyCancellationTreatedAsUserStop()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clean = Result(cleanupSucceeded: true);
        var cleanupFailure = Result(cleanupSucceeded: false);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SessionMacroPlaybackCancellation.ThrowIfCleanCancellation(
                clean,
                cancellation.Token));
        SessionMacroPlaybackCancellation.ThrowIfCleanCancellation(
            cleanupFailure,
            cancellation.Token);
    }

    [Fact]
    public void Runtime_UsesOnePassRecordingsInsideTheOuterLoop()
    {
        var host = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        var playback = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));
        var controller = File.ReadAllText(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml.cs"));
        var loop = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "SessionMacroPlaybackLoop.cs"));
        var leaseCache = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "SessionMacroPlaybackLeaseCache.cs"));

        Assert.Contains(
            "SessionMacroPlaybackLoop.RunUntilStoppedAsync(",
            host);
        Assert.Contains(
            "playbackSession.BeginPlaybackSequence()",
            host);
        Assert.Contains(
            "cancellationToken.IsCancellationRequested &&",
            host);
        Assert.Contains(
            "!_operationLifetime.IsShuttingDown",
            host);
        Assert.Contains("var hasWarnings = warnings.Count > 0;", host);
        Assert.Contains("string.Join(\" \", warnings)", host);
        Assert.Contains("SuppressDialog: externallyCancelled", host);
        Assert.Contains("!outcome.SuppressDialog", controller);
        Assert.Contains("var mayContinue = true;", host);
        Assert.Contains(
            "if (mayContinue && prepared.WholeTemplate is not null)",
            host);
        Assert.Contains("LoopCount = 1", playback);
        Assert.Contains("Infinite = false", playback);
        Assert.DoesNotContain(
            "var catalog = TryLoadSessionTemplateCatalog();",
            playback[playback.IndexOf(
                "private async Task<TemplateMacroPlaybackResult> PlayTemplateMacrosAsync",
                StringComparison.Ordinal)..]);
        Assert.Equal(
            2,
            CountOccurrences(
                playback,
                "playbackCache.GetOrLoadAndTransform("));
        Assert.Contains("playbackLeases.GetOrAcquire(", playback);
        Assert.Contains(
            "_singleTargetLeases.TryGetValue(key",
            leaseCache);
        Assert.Contains("_targetSetLeases", leaseCache);
        Assert.Contains(
            "windowService.AcquirePlaybackTargetLease(targets)",
            leaseCache);
        Assert.Contains("prepared.PlaybackLeases.Dispose()", host);
        Assert.DoesNotContain(
            "infinite: template.RepeatWholeLayoutMacro",
            playback);
        Assert.Equal(
            2,
            CountOccurrences(playback, "pauseOnFocusLoss: true"));
        Assert.Contains("var completed = 0;", playback);
        Assert.Contains("if (completed == 0)", playback);
        Assert.Contains(
            "SessionMacroPlaybackCancellation.ThrowIfCleanCancellation(",
            playback);
        Assert.DoesNotContain("ConfigureAwait(false)", loop);
    }

    private static ExactWheelPlaybackResult Result(bool cleanupSucceeded) =>
        new(
            ExactWheelPlaybackStopReason.Cancelled,
            0,
            0,
            0,
            0,
            0,
            0,
            cleanupSucceeded,
            "Playback was cancelled.");

    private static string RepoFile(params string[] components)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
            throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine([current.FullName, .. components]);
    }

    private static int CountOccurrences(string source, string value) =>
        (source.Length - source.Replace(
            value,
            string.Empty,
            StringComparison.Ordinal).Length) / value.Length;
}
