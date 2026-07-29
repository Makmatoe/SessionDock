using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SessionDock.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activationSignal;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _ownsMutex;
    private readonly string _externalLinkPipeName;
    private Task? _activationListener;
    private Task? _externalLinkListener;
    private bool _disposed;

    public bool IsPrimaryInstance => _ownsMutex;

    public SingleInstanceService(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Any(character =>
                character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw new ArgumentException(
                "The application ID contains an invalid synchronization name character.",
                nameof(applicationId));
        }

        var namePrefix = $@"Local\{applicationId}";
        _externalLinkPipeName = BuildExternalLinkPipeName(
            applicationId,
            Process.GetCurrentProcess().SessionId);
        _activationSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $@"{namePrefix}.Activate");
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            $@"{namePrefix}.Mutex",
            out _ownsMutex);
    }

    public void NotifyPrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
            _activationSignal.Set();
    }

    public void ListenForActivationRequests(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (!_ownsMutex)
            throw new InvalidOperationException(
                "Only the primary instance can listen for activation requests.");
        if (_activationListener is not null)
            throw new InvalidOperationException(
                "The activation listener has already been started.");

        _activationListener = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                _activationSignal,
                _shutdown.Token.WaitHandle
            };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                if (_shutdown.IsCancellationRequested)
                    return;
                activationRequested();
            }
        });
    }

    public async Task<bool> ForwardExternalLinkAsync(
        string externalLink,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(externalLink);
        if (_ownsMutex)
        {
            throw new InvalidOperationException(
                "The primary instance cannot forward a link to itself.");
        }
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                _externalLinkPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            await ExternalLinkPipeProtocol.WriteAsync(
                    client,
                    externalLink,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            await client.FlushAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void ListenForExternalLinkRequests(Action<string> linkReceived)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(linkReceived);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException(
                "Only the primary instance can listen for external links.");
        }
        if (_externalLinkListener is not null)
        {
            throw new InvalidOperationException(
                "The external-link listener has already been started.");
        }

        _externalLinkListener = ListenForExternalLinksAsync(
            linkReceived,
            _shutdown.Token);
    }

    internal static string BuildExternalLinkPipeName(
        string applicationId,
        int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        return $"{applicationId}.{sessionId}.ExternalLinks";
    }

    private async Task ListenForExternalLinksAsync(
        Action<string> linkReceived,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _externalLinkPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var messageTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                messageTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                var message = await ExternalLinkPipeProtocol.ReadAsync(
                        server,
                        messageTimeout.Token)
                    .ConfigureAwait(false);
                linkReceived(message);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A same-user client that stalls is disconnected after the
                // bounded read and cannot block later requests.
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or
                    UnauthorizedAccessException)
            {
                // Malformed or disconnected clients are ignored without
                // logging any external input.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _activationSignal.Set();
        try
        {
            Task.WaitAll(
                new[] { _activationListener, _externalLinkListener }
                    .OfType<Task>()
                    .ToArray(),
                TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Process exit must continue if the optional listener is stopping.
        }

        if (_ownsMutex)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // A defensive cross-thread disposal still closes the process
                // handle below. Normal WPF startup and exit use the UI thread.
            }
        }
        _instanceMutex.Dispose();
        _activationSignal.Dispose();
        _shutdown.Dispose();
    }
}

internal static class ExternalLinkPipeProtocol
{
    internal const int MaximumPayloadBytes =
        ExternalRobloxLinkPolicy.MaximumInputLength * 4;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static async Task WriteAsync(
        Stream stream,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var payload = StrictUtf8.GetBytes(message);
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("The external-link payload is invalid.");

        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken)
            .ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaximumPayloadBytes)
            throw new InvalidDataException("The external-link frame length is invalid.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The external-link frame is not valid UTF-8.",
                exception);
        }
    }
}
