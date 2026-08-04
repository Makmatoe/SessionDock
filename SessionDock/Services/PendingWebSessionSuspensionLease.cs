namespace SessionDock.Services;

/// <summary>
/// Owns a WebView suspension attempt that outlived the playback start budget.
/// A late successful suspension remains active until playback releases the
/// lease; every other outcome releases the suspension gate immediately.
/// </summary>
internal sealed class PendingWebSessionSuspensionLease : IDisposable
{
    private readonly TaskCompletionSource _releaseRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<Task> _resumeAsync;
    private readonly Func<bool> _canRemainSuspended;
    private readonly SemaphoreSlim _suspensionGate;
    private readonly Action<Exception>? _failureObserver;
    private int _disposed;

    internal PendingWebSessionSuspensionLease(
        Task<bool> suspensionTask,
        Func<Task> resumeAsync,
        SemaphoreSlim suspensionGate,
        Action<Exception>? failureObserver = null,
        Func<bool>? canRemainSuspended = null)
    {
        ArgumentNullException.ThrowIfNull(suspensionTask);
        ArgumentNullException.ThrowIfNull(resumeAsync);
        ArgumentNullException.ThrowIfNull(suspensionGate);

        _resumeAsync = resumeAsync;
        _canRemainSuspended = canRemainSuspended ?? (static () => true);
        _suspensionGate = suspensionGate;
        _failureObserver = failureObserver;
        Completion = CompleteAsync(suspensionTask);
    }

    internal Task Completion { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _releaseRequested.TrySetResult();
    }

    private async Task CompleteAsync(Task<bool> suspensionTask)
    {
        try
        {
            var suspended = await suspensionTask.ConfigureAwait(false);
            if (!suspended)
                return;

            if (!CanRemainSuspended())
            {
                await _resumeAsync().ConfigureAwait(false);
                return;
            }

            // Holding this task incomplete is intentional: the WebView stays
            // suspended for the remainder of macro playback. If Stop won the
            // race before suspension settled, the signal is already complete
            // and the late suspension is resumed immediately.
            await _releaseRequested.Task.ConfigureAwait(false);
            await _resumeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                _failureObserver?.Invoke(exception);
            }
            catch
            {
                // Diagnostics must never interfere with gate recovery.
            }
        }
        finally
        {
            // This async completion path is the sole owner of the gate, which
            // makes timeout, cancellation, failure, and Dispose races converge
            // on exactly one release.
            _suspensionGate.Release();
        }
    }

    private bool CanRemainSuspended()
    {
        try
        {
            return _canRemainSuspended();
        }
        catch (Exception exception)
        {
            try
            {
                _failureObserver?.Invoke(exception);
            }
            catch
            {
                // Diagnostics must never interfere with safe resumption.
            }
            return false;
        }
    }
}
