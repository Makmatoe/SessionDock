namespace SessionDock.Services;

internal sealed class LatestOnlyRequestQueue<T>
    where T : notnull
{
    private readonly object _sync = new();
    private bool _processing;
    private bool _hasPending;
    private T? _pending;

    internal bool Enqueue(T request, out T? firstRequest)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (!_processing)
            {
                _processing = true;
                firstRequest = request;
                return true;
            }

            // Keep only the newest request while one request is active. This
            // bounds both memory and follow-up UI work under repeated IPC.
            _pending = request;
            _hasPending = true;
            firstRequest = default;
            return false;
        }
    }

    internal bool CompleteCurrent(out T? nextRequest)
    {
        lock (_sync)
        {
            if (_hasPending)
            {
                nextRequest = _pending!;
                _pending = default;
                _hasPending = false;
                return true;
            }

            _processing = false;
            nextRequest = default;
            return false;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _processing = false;
            _pending = default;
            _hasPending = false;
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return (_processing ? 1 : 0) + (_hasPending ? 1 : 0);
        }
    }
}
