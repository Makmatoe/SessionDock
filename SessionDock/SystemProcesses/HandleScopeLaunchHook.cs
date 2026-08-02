using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionDock.SystemProcesses;

public sealed class HandleScopeLaunchHook : ILaunchHook
{
    private const string SessionIdToken = "{SESSION_ID}";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions RequestJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HandleScopeConfigurationLoader _configurationLoader;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HttpClient _client;
    private readonly HandleScopeApiBootstrapper _apiBootstrapper;
    private readonly HandleScopeRuntimeCoordinator? _runtimeCoordinator;
    private readonly object _lifetimeLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public bool IsConfigured => _runtimeCoordinator?.IsEnabled ??
        _configurationLoader.LoadEnabled() is not null;

    public HandleScopeLaunchHook()
        : this(new HandleScopeConfigurationLoader(), new HandleScopeConnectionLoader())
    {
    }

    internal HandleScopeLaunchHook(
        HandleScopeRuntimeCoordinator runtimeCoordinator)
        : this(
            new HandleScopeConfigurationLoader(),
            new HandleScopeConnectionLoader())
    {
        ArgumentNullException.ThrowIfNull(runtimeCoordinator);
        _runtimeCoordinator = runtimeCoordinator;
    }

    public HandleScopeLaunchHook(
        HandleScopeConfigurationLoader configurationLoader,
        string connectionPath)
        : this(
            configurationLoader,
            new HandleScopeConnectionLoader(connectionPath))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionPath);
    }

    internal HandleScopeLaunchHook(
        HandleScopeConfigurationLoader configurationLoader,
        HandleScopeConnectionLoader connectionLoader)
        : this(
            configurationLoader,
            connectionLoader,
            CreateDefaultHandler(),
            HandleScopeProcessVerifier.CreateDefault())
    {
    }

    internal HandleScopeLaunchHook(
        HandleScopeConfigurationLoader configurationLoader,
        HandleScopeConnectionLoader connectionLoader,
        HttpMessageHandler handler,
        IHandleScopeProcessVerifier processVerifier)
    {
        ArgumentNullException.ThrowIfNull(configurationLoader);
        ArgumentNullException.ThrowIfNull(connectionLoader);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(processVerifier);
        _configurationLoader = configurationLoader;
        _connectionLoader = connectionLoader;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SessionDock/3.0");
        _apiBootstrapper = new HandleScopeApiBootstrapper(
            _connectionLoader,
            _client,
            processVerifier);
    }

    private static HttpMessageHandler CreateDefaultHandler() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = RequestTimeout,
            Credentials = null,
            MaxConnectionsPerServer = 1,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false
        };

    public async Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        lock (_lifetimeLock)
        {
            if (_disposed)
                return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            Trace.WriteLine(
                "HandleScope operation skipped: another operation is already active.");
            return;
        }

        try
        {
            await NotifyCoreAsync(launchEvent, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
            _disposed = true;
        _client.Dispose();
    }

    private async Task NotifyCoreAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken)
    {
        var configuration = _configurationLoader.LoadEnabled();
        if (configuration is null)
            return;
        if (launchEvent.ProcessId <= 0)
        {
            Trace.WriteLine("HandleScope operation skipped: launched PID is unavailable.");
            return;
        }

        try
        {
            configuration = ResolveSessionSelector(
                configuration,
                launchEvent.ProcessId);
            if (configuration is null)
                return;

            var connection = _runtimeCoordinator is null
                ? await _apiBootstrapper.GetExistingAsync(cancellationToken)
                : await _runtimeCoordinator.GetReadyConnectionAsync(
                    cancellationToken);
            if (connection is null)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                configuration.RetryTimeoutSeconds));
            var closed = await RetryPidCloseAsync(
                launchEvent.ProcessId,
                configuration,
                connection,
                timeout.Token);
            if (closed && configuration.AllProcesses)
            {
                // The discovery token is intentionally short-lived. Treat the
                // all-process sweep as a separate operation and reload its
                // connection instead of carrying credentials across the two.
                var sweepConnection = _runtimeCoordinator is null
                    ? await _apiBootstrapper.GetExistingAsync(timeout.Token)
                    : await _runtimeCoordinator.GetReadyConnectionAsync(
                        timeout.Token);
                if (sweepConnection is null)
                    return;

                await SweepAllProcessesAsync(
                    configuration,
                    sweepConnection,
                    timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine("HandleScope PID retry window expired.");
        }
        catch (OperationCanceledException)
        {
            // Application shutdown cancellation is expected.
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"HandleScope integration failed: {ex.GetType().Name}.");
        }
    }

    private static HandleScopeConfiguration? ResolveSessionSelector(
        HandleScopeConfiguration configuration,
        int processId)
    {
        if (!configuration.HandleName.Contains(
                SessionIdToken,
                StringComparison.OrdinalIgnoreCase))
        {
            return configuration;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            using var current = Process.GetCurrentProcess();
            if (process.HasExited ||
                process.SessionId != current.SessionId ||
                !process.ProcessName.Equals(
                    HandleScopeConfigurationLoader.RequiredProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine(
                    "HandleScope session selector was rejected for an unexpected process.");
                return null;
            }

            return new HandleScopeConfiguration
            {
                Enabled = configuration.Enabled,
                ProcessName = configuration.ProcessName,
                HandleName = configuration.HandleName.Replace(
                    SessionIdToken,
                    process.SessionId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase),
                HandleType = configuration.HandleType,
                Access = configuration.Access,
                Match = configuration.Match,
                CloseAll = configuration.CloseAll,
                AllProcesses = configuration.AllProcesses,
                RetryTimeoutSeconds = configuration.RetryTimeoutSeconds,
                RetryIntervalMilliseconds =
                    configuration.RetryIntervalMilliseconds
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            Trace.WriteLine(
                $"HandleScope session selector could not be resolved: {ex.GetType().Name}.");
            return null;
        }
    }

    private async Task<bool> RetryPidCloseAsync(
        int processId,
        HandleScopeConfiguration configuration,
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HasProcessExited(processId))
                return false;

            var dryRun = await SendCloseAsync(
                CreatePidRequest(processId, configuration, dryRun: true),
                connection,
                processId,
                cancellationToken);
            if (dryRun.Kind == CloseOutcomeKind.Failure)
                return false;

            if (dryRun.Kind == CloseOutcomeKind.Completed)
            {
                var close = await SendCloseAsync(
                    CreatePidRequest(
                        processId,
                        configuration,
                        dryRun: false,
                        dryRun.PlanId),
                    connection,
                    processId,
                    cancellationToken);
                if (close.ClosedExpectedProcess)
                    return true;
                if (close.Kind == CloseOutcomeKind.Failure)
                    return false;
            }

            await Task.Delay(
                configuration.RetryIntervalMilliseconds,
                cancellationToken);
        }

        return false;
    }

    private async Task SweepAllProcessesAsync(
        HandleScopeConfiguration configuration,
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        var dryRun = await SendCloseAsync(
            CreateAllProcessesRequest(configuration, dryRun: true),
            connection,
            expectedProcessId: null,
            cancellationToken);
        if (dryRun.Kind != CloseOutcomeKind.Completed)
            return;

        await SendCloseAsync(
            CreateAllProcessesRequest(
                configuration,
                dryRun: false,
                dryRun.PlanId),
            connection,
            expectedProcessId: null,
            cancellationToken);
    }

    private async Task<CloseOutcome> SendCloseAsync(
        CloseRequest payload,
        HandleScopeConnection connection,
        int? expectedProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = CreateNegotiatedCloseEndpoint(connection);
            if (endpoint is null)
            {
                Trace.WriteLine(
                    "HandleScope request rejected an uncompiled protocol adapter.");
                return CloseOutcome.Failure;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(
                    payload,
                    options: RequestJsonOptions)
            };
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", connection.Token);

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return CloseOutcome.NoMatch;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Trace.WriteLine(
                    $"HandleScope returned HTTP {(int)response.StatusCode}.");
                return CloseOutcome.Failure;
            }

            return await ParseResponseAsync(
                response,
                expectedProcessId,
                payload.DryRun,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or InvalidDataException)
        {
            Trace.WriteLine(
                $"HandleScope request failed: {ex.GetType().Name}.");
            return CloseOutcome.Failure;
        }
    }

    internal static Uri? CreateNegotiatedCloseEndpoint(
        HandleScopeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var protocol = connection.NegotiatedProtocol;
        return protocol is not null &&
               HandleScopeProtocolNegotiator.IsCompiledAdapter(protocol)
            ? new Uri(connection.BaseUrl, protocol.CloseEndpoint)
            : null;
    }

    private static async Task<CloseOutcome> ParseResponseAsync(
        HttpResponseMessage response,
        int? expectedProcessId,
        bool expectedDryRun,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("HandleScope response was too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("HandleScope response was too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            buffer,
            new JsonDocumentOptions { MaxDepth = 16 },
            cancellationToken: cancellationToken);
        if (!TryValidateOperationDocument(
                document.RootElement,
                expectedProcessId,
                expectedDryRun,
                out var closedExpectedProcess,
                out var planId))
        {
            Trace.WriteLine("HandleScope returned an invalid operation response.");
            return CloseOutcome.Failure;
        }

        return new CloseOutcome(
            CloseOutcomeKind.Completed,
            closedExpectedProcess,
            planId);
    }

    internal static bool TryValidateOperationDocument(
        JsonElement root,
        int? expectedProcessId,
        bool expectedDryRun,
        out bool closedExpectedProcess) =>
        TryValidateOperationDocument(
            root,
            expectedProcessId,
            expectedDryRun,
            out closedExpectedProcess,
            out _);

    internal static bool TryValidateOperationDocument(
        JsonElement root,
        int? expectedProcessId,
        bool expectedDryRun,
        out bool closedExpectedProcess,
        out string? planId)
    {
        closedExpectedProcess = false;
        planId = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !HasUniqueRootProperties(root) ||
            !TryGetRequiredString(root, "policy", out var policy) ||
            !policy.Equals(
                HandleScopeApiBootstrapper.RequiredPolicy,
                StringComparison.Ordinal) ||
            !TryGetRequiredBoolean(root, "dryRun", out var dryRun) ||
            dryRun != expectedDryRun ||
            !TryGetRequiredNonNegativeInteger(root, "processCount", out _) ||
            !TryGetRequiredNonNegativeInteger(
                root,
                "matchedProcessCount",
                out _) ||
            !TryGetRequiredNonNegativeInteger(root, "matchCount", out var matchCount) ||
            !TryGetRequiredNonNegativeInteger(root, "closedCount", out var closedCount) ||
            !TryGetRequiredNonNegativeInteger(root, "failedCount", out var failedCount) ||
            !TryGetRequiredArray(root, "matches", out var matches) ||
            !TryGetRequiredArray(root, "closed", out var closed) ||
            !TryGetRequiredArray(root, "failures", out var failures) ||
            matchCount != matches.GetArrayLength() ||
            closedCount != closed.GetArrayLength() ||
            failedCount != failures.GetArrayLength() ||
            failedCount != 0)
        {
            return false;
        }

        if (expectedDryRun)
        {
            if (!TryGetRequiredString(root, "planId", out var dryRunPlanId) ||
                !IsCanonicalPlanId(dryRunPlanId) ||
                matchCount <= 0 ||
                closedCount != 0 ||
                expectedProcessId is int expectedPid &&
                !CollectionContainsPid(matches, expectedPid))
            {
                return false;
            }

            planId = dryRunPlanId;
            return true;
        }

        if (!root.TryGetProperty("planId", out var executionPlanId) ||
            executionPlanId.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        closedExpectedProcess = closedCount > 0 &&
            (expectedProcessId is not int pid || CollectionContainsPid(closed, pid));
        return closedExpectedProcess;
    }

    private static CloseRequest CreatePidRequest(
        int processId,
        HandleScopeConfiguration configuration,
        bool dryRun,
        string? planId = null) =>
        new(
            new ProcessSelector(processId, null),
            CreateHandleSelector(configuration),
            configuration.CloseAll,
            dryRun,
            false,
            planId);

    private static CloseRequest CreateAllProcessesRequest(
        HandleScopeConfiguration configuration,
        bool dryRun,
        string? planId = null) =>
        new(
            new ProcessSelector(null, configuration.ProcessName),
            CreateHandleSelector(configuration),
            configuration.CloseAll,
            dryRun,
            true,
            planId);

    private static HandleSelector CreateHandleSelector(
        HandleScopeConfiguration configuration) =>
        new(
            configuration.HandleName,
            configuration.Match,
            configuration.HandleType,
            configuration.Access);

    private static bool HasProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUniqueRootProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string name,
        out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.GetString() is { } stringValue &&
               (value = stringValue) is not null;
    }

    private static bool TryGetRequiredBoolean(
        JsonElement root,
        string name,
        out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetRequiredNonNegativeInteger(
        JsonElement root,
        string name,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value >= 0;
    }

    private static bool TryGetRequiredArray(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        value = default;
        return root.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }

    private static bool CollectionContainsPid(
        JsonElement collection,
        int processId) =>
        collection.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("pid", out var pid) &&
            pid.ValueKind == JsonValueKind.Number &&
            pid.TryGetInt32(out var value) &&
            value == processId);

    private static bool IsCanonicalPlanId(string value) =>
        value.Length == 43 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-');

    private sealed record CloseRequest(
        ProcessSelector Process,
        HandleSelector Handle,
        bool CloseAll,
        bool DryRun,
        bool AllProcesses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PlanId);

    private sealed record ProcessSelector(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? Pid,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name);

    private sealed record HandleSelector(
        string Name,
        string Match,
        string? Type,
        string? Access);

    private enum CloseOutcomeKind
    {
        Completed,
        NoMatch,
        Failure
    }

    private sealed record CloseOutcome(
        CloseOutcomeKind Kind,
        bool ClosedExpectedProcess,
        string? PlanId)
    {
        public static CloseOutcome NoMatch { get; } =
            new(CloseOutcomeKind.NoMatch, false, null);
        public static CloseOutcome Failure { get; } =
            new(CloseOutcomeKind.Failure, false, null);
    }
}
