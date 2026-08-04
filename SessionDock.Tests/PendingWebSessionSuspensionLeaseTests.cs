using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PendingWebSessionSuspensionLeaseTests
{
    [Fact]
    public async Task LateSuccess_RemainsSuspendedUntilPlaybackStops()
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var resumeCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () =>
            {
                Interlocked.Increment(ref resumeCount);
                return Task.CompletedTask;
            },
            gate);

        suspension.SetResult(true);
        await Task.Yield();

        Assert.False(lease.Completion.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Equal(0, Volatile.Read(ref resumeCount));

        lease.Dispose();
        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resumeCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task StopBeforeSettlement_ResumesLateSuccessImmediately()
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var resumeCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () =>
            {
                Interlocked.Increment(ref resumeCount);
                return Task.CompletedTask;
            },
            gate);

        lease.Dispose();
        suspension.SetResult(true);
        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resumeCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task BrowserWorkAfterRequest_ResumesLateSuccessImmediately()
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var browserGeneration = 1;
        var resumeCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () =>
            {
                Interlocked.Increment(ref resumeCount);
                return Task.CompletedTask;
            },
            gate,
            canRemainSuspended: () =>
                Volatile.Read(ref browserGeneration) == 1);

        Interlocked.Increment(ref browserGeneration);
        suspension.SetResult(true);
        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resumeCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task RejectedSuspension_ReleasesGateWithoutWaitingForStop()
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var resumeCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () =>
            {
                Interlocked.Increment(ref resumeCount);
                return Task.CompletedTask;
            },
            gate);

        suspension.SetResult(false);
        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, resumeCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ConcurrentStopCalls_ResumeAndReleaseExactlyOnce()
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var resumeCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () =>
            {
                Interlocked.Increment(ref resumeCount);
                return Task.CompletedTask;
            },
            gate);

        suspension.SetResult(true);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(lease.Dispose)));
        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resumeCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_ReleasesGateAndIsDiagnosticOnly(bool failWhileResuming)
    {
        var suspension = NewSignal<bool>();
        using var gate = new SemaphoreSlim(0, 1);
        var observedCount = 0;
        using var lease = new PendingWebSessionSuspensionLease(
            suspension.Task,
            () => failWhileResuming
                ? Task.FromException(new InvalidOperationException("resume"))
                : Task.CompletedTask,
            gate,
            _ => Interlocked.Increment(ref observedCount));

        if (failWhileResuming)
        {
            lease.Dispose();
            suspension.SetResult(true);
        }
        else
        {
            suspension.SetException(new InvalidOperationException("suspend"));
        }

        await lease.Completion.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, observedCount);
        Assert.Equal(1, gate.CurrentCount);
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
