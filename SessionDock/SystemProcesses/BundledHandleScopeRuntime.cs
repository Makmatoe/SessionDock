using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SessionDock.HandleScope;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal sealed partial class BundledHandleScopeRuntime :
    IHandleScopeConnectionSource,
    IHandleScopeResolvedProcessVerifier,
    IDisposable,
    IAsyncDisposable
{
    private const int MaximumHandshakeBytes = 2048;
    private const int MaximumStartupAttempts = 2;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StartupRetryDelay =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);
    private static readonly HandleScopeRuntimeIdentity RuntimeIdentity = new(
        new Version(HandleScopeBroker.ComponentVersion),
        $"bundled-v{HandleScopeBroker.ComponentVersion}",
        ["v1", "v2"],
        [
            "handlescope.http.v1",
            "handlescope.http.v2",
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1",
            "handlescope.setup.native.v1"
        ]);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HttpClient _client;
    private readonly object _lifetimeLock = new();
    private readonly object _stateLock = new();
    private Process? _process;
    private HandleScopeWorkerJob? _job;
    private HandleScopeConnection? _connection;
    private Task? _shutdownTask;
    private volatile bool _disposed;

    internal BundledHandleScopeRuntime()
    {
        _client = new HttpClient(CreateSecureHandler(), disposeHandler: true)
        {
            Timeout = ShutdownTimeout
        };
    }

    internal async Task<HandleScopeConnection?> EnsureStartedAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = Load();
            if (existing is not null)
                return existing;

            await StopOwnedCoreAsync(CancellationToken.None)
                .ConfigureAwait(false);
            for (var attempt = 1; attempt <= MaximumStartupAttempts; attempt++)
            {
                var connection = await StartCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (connection is not null)
                    return connection;
                if (attempt == MaximumStartupAttempts)
                    break;

                Trace.WriteLine(
                    "Bundled HandleScope worker start will retry once after a bounded delay.");
                await Task.Delay(StartupRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopOwnedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public HandleScopeConnection? Load()
    {
        lock (_stateLock)
        {
            if (_connection is null ||
                !TryVerifyExpected(_connection, out _))
            {
                return null;
            }
            return _connection;
        }
    }

    public bool IsExpected(HandleScopeConnection connection) =>
        TryResolveExpected(connection, out _);

    public bool TryResolveExpected(
        HandleScopeConnection connection,
        out HandleScopeRuntimeIdentity? runtimeIdentity)
    {
        lock (_stateLock)
            return TryVerifyExpected(connection, out runtimeIdentity);
    }

    public void Dispose()
    {
        GetOrStartShutdown().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() =>
        await GetOrStartShutdown().ConfigureAwait(false);

    internal async Task ShutdownAsync(
        CancellationToken cancellationToken = default) =>
        await GetOrStartShutdown().WaitAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<HandleScopeConnection?> StartCoreAsync(
        CancellationToken cancellationToken)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        using var current = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ??
                AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(HandleScopeWorkerCommand.CommandName);
        startInfo.ArgumentList.Add(
            current.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(
                CultureInfo.InvariantCulture));
        SanitizeEnvironment(startInfo);

        Process? process = null;
        HandleScopeWorkerJob? job = null;
        try
        {
            job = HandleScopeWorkerJob.Create();
            process = Process.Start(startInfo);
            if (process is null || !VerifyChildIdentity(process, executablePath))
                return null;

            job.Assign(process);
            await process.StandardInput.BaseStream.WriteAsync(
                "START\n"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();

            using var startupTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(StartupTimeout);
            var handshake = await ReadHandshakeAsync(
                process.StandardOutput.BaseStream,
                startupTimeout.Token).ConfigureAwait(false);
            if (handshake is null ||
                !TryCreateConnection(handshake, process, out var connection) ||
                connection is null ||
                !VerifyChildIdentity(process, executablePath))
            {
                return null;
            }

            lock (_stateLock)
            {
                _process = process;
                _job = job;
                _connection = connection;
            }
            process = null;
            job = null;
            return connection;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException or JsonException or
                OperationCanceledException)
        {
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            Trace.WriteLine(
                $"Bundled HandleScope worker did not start: {exception.GetType().Name}.");
            return null;
        }
        finally
        {
            job?.Dispose();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        NotSupportedException)
                {
                    // The job close above is the primary exact-child fallback.
                }
                process.Dispose();
            }
        }
    }

    private async Task StopOwnedCoreAsync(CancellationToken cancellationToken)
    {
        using var stopDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopDeadline.CancelAfter(ShutdownTimeout);
        var stopToken = stopDeadline.Token;

        Process? process;
        HandleScopeWorkerJob? job;
        HandleScopeConnection? connection;
        lock (_stateLock)
        {
            process = _process;
            job = _job;
            connection = _connection;
            _process = null;
            _job = null;
            _connection = null;
        }

        if (process is null)
        {
            job?.Dispose();
            return;
        }

        try
        {
            if (connection is not null && !process.HasExited)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(connection.BaseUrl, "/v1/shutdown"));
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    connection.Token);
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    stopToken).ConfigureAwait(false);
                if (response.StatusCode is not (
                        HttpStatusCode.Accepted or HttpStatusCode.OK))
                {
                    Trace.WriteLine(
                        "Bundled HandleScope rejected its authenticated shutdown request.");
                }
            }

            if (!process.HasExited)
                await process.WaitForExitAsync(stopToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
                InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException or OperationCanceledException or
                ObjectDisposedException)
        {
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                // Continue into the exact job-owned termination fallback.
            }
            Trace.WriteLine(
                $"Bundled HandleScope shutdown used its job fallback: {exception.GetType().Name}.");
        }
        finally
        {
            job?.Dispose();
            process.Dispose();
        }
    }

    private bool TryVerifyExpected(
        HandleScopeConnection connection,
        out HandleScopeRuntimeIdentity? runtimeIdentity)
    {
        runtimeIdentity = null;
        var process = _process;
        var expectedConnection = _connection;
        if (process is null || expectedConnection is null ||
            !ReferenceEquals(connection, expectedConnection) &&
            connection != expectedConnection)
        {
            return false;
        }

        try
        {
            var path = Environment.ProcessPath;
            if (path is null ||
                process.HasExited ||
                process.Id != connection.ApiProcessId ||
                !VerifyChildIdentity(process, path))
            {
                return false;
            }

            var processStarted = new DateTimeOffset(
                process.StartTime.ToUniversalTime());
            var discoveryStarted = connection.StartedAtUtc.ToUniversalTime();
            if (discoveryStarted < processStarted - TimeSpan.FromSeconds(5) ||
                discoveryStarted > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5))
            {
                return false;
            }

            runtimeIdentity = RuntimeIdentity;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool VerifyChildIdentity(
        Process process,
        string expectedExecutablePath)
    {
        try
        {
            var actualPath = process.MainModule?.FileName;
            return !process.HasExited &&
                actualPath is not null &&
                Path.GetFullPath(actualPath).Equals(
                    Path.GetFullPath(expectedExecutablePath),
                    StringComparison.OrdinalIgnoreCase) &&
                WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                    process);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadHandshakeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[256];
        while (buffer.Length <= MaximumHandshakeBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return null;
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (buffer.Length + count > MaximumHandshakeBytes)
                return null;
            buffer.Write(chunk, 0, count);
            if (newline >= 0)
            {
                if (newline != read - 1 || buffer.Length == 0)
                    return null;
                return buffer.ToArray();
            }
        }
        return null;
    }

    private static bool TryCreateConnection(
        byte[] handshake,
        Process process,
        out HandleScopeConnection? connection)
    {
        connection = null;
        using var document = JsonDocument.Parse(
            handshake,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
        var root = document.RootElement;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetPropertyCount() != 7 ||
            !root.EnumerateObject().All(property => names.Add(property.Name)) ||
            !names.SetEquals(
            [
                "schemaVersion",
                "componentVersion",
                "apiVersion",
                "baseUrl",
                "token",
                "processId",
                "startedAtUtc"
            ]) ||
            !root.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !root.TryGetProperty("componentVersion", out var componentVersion) ||
            componentVersion.ValueKind != JsonValueKind.String ||
            componentVersion.GetString() != HandleScopeBroker.ComponentVersion ||
            !root.TryGetProperty("apiVersion", out var apiVersion) ||
            apiVersion.ValueKind != JsonValueKind.String ||
            apiVersion.GetString() != "v1" ||
            !root.TryGetProperty("baseUrl", out var baseUrlValue) ||
            baseUrlValue.ValueKind != JsonValueKind.String ||
            !HandleScopeConnectionLoader.TryValidateBaseUrl(
                baseUrlValue.GetString(),
                out var baseUrl) ||
            !root.TryGetProperty("token", out var tokenValue) ||
            tokenValue.ValueKind != JsonValueKind.String ||
            tokenValue.GetString() is not { } token ||
            !TokenPattern().IsMatch(token) ||
            !root.TryGetProperty("processId", out var processIdValue) ||
            !processIdValue.TryGetInt32(out var processId) ||
            processId != process.Id ||
            !root.TryGetProperty("startedAtUtc", out var startedAtValue) ||
            startedAtValue.ValueKind != JsonValueKind.String ||
            !startedAtValue.TryGetDateTimeOffset(out var startedAtUtc))
        {
            return false;
        }

        connection = new(
            baseUrl!,
            token,
            "v1",
            processId,
            startedAtUtc);
        return true;
    }

    private static void SanitizeEnvironment(ProcessStartInfo startInfo)
    {
        var names = startInfo.Environment.Keys.ToArray();
        foreach (var name in names)
        {
            if (name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("KESTREL__", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("SESSIONDOCK_HANDLESCOPE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(name);
            }
        }
    }

    private static SocketsHttpHandler CreateSecureHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = ShutdownTimeout,
        Credentials = null,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 8,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null
    };

    private Task GetOrStartShutdown()
    {
        lock (_lifetimeLock)
        {
            if (_shutdownTask is not null)
                return _shutdownTask;

            _disposed = true;
            _shutdownTask = ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            await StopOwnedCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException or
                HttpRequestException or InvalidOperationException or IOException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Trace.WriteLine(
                $"Bundled HandleScope shutdown used its job fallback: {exception.GetType().Name}.");
        }
        finally
        {
            lock (_stateLock)
            {
                _connection = null;
                _job?.Dispose();
                _job = null;
                _process?.Dispose();
                _process = null;
            }
            _lifecycleGate.Release();
            _client.Dispose();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
