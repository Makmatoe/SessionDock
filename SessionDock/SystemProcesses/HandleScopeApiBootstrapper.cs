using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeApiBootstrapper
{
    internal const string RequiredPolicy = "roblox-singleton-event-v1";
    private const int MaximumHealthResponseBytes = 64 * 1024;
    private const int MaximumMetadataResponseBytes = 64 * 1024;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HttpClient _client;
    private readonly IHandleScopeProcessVerifier _processVerifier;
    private readonly HandleScopeSelectionStore? _selectionStore;

    public HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client)
        : this(
            connectionLoader,
            client,
            HandleScopeProcessVerifier.CreateDefault(),
            new HandleScopeSelectionStore())
    {
    }

    internal HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client,
        IHandleScopeProcessVerifier processVerifier)
        : this(
            connectionLoader,
            client,
            processVerifier,
            processVerifier is IHandleScopeResolvedProcessVerifier
                ? new HandleScopeSelectionStore()
                : null)
    {
    }

    internal HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client,
        IHandleScopeProcessVerifier processVerifier,
        HandleScopeSelectionStore? selectionStore)
    {
        ArgumentNullException.ThrowIfNull(connectionLoader);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(processVerifier);
        _connectionLoader = connectionLoader;
        _client = client;
        _processVerifier = processVerifier;
        _selectionStore = selectionStore;
    }

    public async Task<HandleScopeConnection?> GetExistingAsync(
        CancellationToken cancellationToken)
    {
        var existing = _connectionLoader.Load();
        if (existing is null)
        {
            Trace.WriteLine(
                "HandleScope is enabled, but its installed local API is unavailable.");
            return null;
        }

        HandleScopeRuntimeIdentity? runtimeIdentity = null;
        var hasResolvedIdentity = false;
        if (_processVerifier is IHandleScopeResolvedProcessVerifier resolvedVerifier)
        {
            hasResolvedIdentity = true;
            if (!resolvedVerifier.TryResolveExpected(
                    existing,
                    out runtimeIdentity) ||
                runtimeIdentity is null)
            {
                Trace.WriteLine(
                    "HandleScope is enabled, but its installed local API is unavailable.");
                return null;
            }
        }
        else if (!_processVerifier.IsExpected(existing))
        {
            Trace.WriteLine(
                "HandleScope is enabled, but its installed local API is unavailable.");
            return null;
        }

        if (!await IsReadyAsync(existing, cancellationToken))
        {
            Trace.WriteLine(
                "HandleScope is enabled, but its installed local API is unavailable.");
            return null;
        }

        // Older internal test/process-verifier implementations predate runtime
        // identity resolution. Keep that seam on the already reviewed v1 adapter;
        // production always implements IHandleScopeResolvedProcessVerifier and
        // therefore cannot enter this branch.
        if (!hasResolvedIdentity)
        {
            return existing with
            {
                NegotiatedProtocol =
                    HandleScopeProtocolNegotiator.LegacyV1Adapter
            };
        }

        var selectionResult = _selectionStore?.Read() ??
            new HandleScopeSelectionReadResult(
                HandleScopeSelection.Default,
                Exists: false,
                IsValid: true);
        if (!selectionResult.IsValid)
        {
            Trace.WriteLine(
                "HandleScope protocol negotiation rejected an invalid local preference.");
            return null;
        }

        var negotiation = await NegotiateAsync(
            existing,
            runtimeIdentity!,
            selectionResult.Selection,
            cancellationToken);
        if (negotiation is null)
        {
            Trace.WriteLine(
                "HandleScope runtime metadata is unavailable or incompatible.");
            return null;
        }

        return existing with
        {
            RuntimeIdentity = runtimeIdentity,
            RuntimeMetadata = negotiation.Metadata,
            NegotiatedProtocol = negotiation.Adapter
        };
    }

    private async Task<HandleScopeNegotiation?> NegotiateAsync(
        HandleScopeConnection connection,
        HandleScopeRuntimeIdentity runtimeIdentity,
        HandleScopeSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(
                    connection.BaseUrl,
                    HandleScopeProtocolNegotiator.MetadataEndpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                connection.Token);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return HandleScopeProtocolNegotiator.TryUseLegacyV014(
                    runtimeIdentity,
                    selection,
                    out var legacyAdapter)
                    ? new HandleScopeNegotiation(legacyAdapter!, Metadata: null)
                    : null;
            }
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is < 0 or
                    > MaximumMetadataResponseBytes)
            {
                return null;
            }

            using var document = await ReadBoundedJsonAsync(
                response.Content,
                MaximumMetadataResponseBytes,
                maximumDepth: 8,
                cancellationToken);
            if (!HandleScopeProtocolNegotiator.TryParseMetadataDocument(
                    document.RootElement,
                    out var metadata) ||
                metadata is null ||
                !HandleScopeProtocolNegotiator.TryNegotiate(
                    metadata,
                    runtimeIdentity,
                    selection,
                    out var adapter) ||
                adapter is null)
            {
                return null;
            }

            return new HandleScopeNegotiation(adapter, metadata);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or IOException or
                InvalidDataException or TaskCanceledException or
                OperationCanceledException)
        {
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null;
        }
    }

    private async Task<bool> IsReadyAsync(
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(connection.BaseUrl, "/v1/health"));
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaximumHealthResponseBytes)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumHealthResponseBytes)
                    return false;
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: cancellationToken);
            return IsValidHealthDocument(document.RootElement);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or IOException or
            TaskCanceledException or OperationCanceledException)
        {
            if (ex is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpContent content,
        int maximumBytes,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException(
                    "The HandleScope metadata response is too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(
            buffer,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth
            },
            cancellationToken);
    }

    internal static bool IsValidHealthDocument(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.GetPropertyCount() == 3 &&
        HasUniqueProperties(root) &&
        root.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        status.GetString()?.Equals("ready", StringComparison.Ordinal) == true &&
        root.TryGetProperty("apiVersion", out var apiVersion) &&
        apiVersion.ValueKind == JsonValueKind.String &&
        apiVersion.GetString()?.Equals("v1", StringComparison.Ordinal) == true &&
        root.TryGetProperty("policy", out var policy) &&
        policy.ValueKind == JsonValueKind.String &&
        policy.GetString()?.Equals(RequiredPolicy, StringComparison.Ordinal) == true;

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private sealed record HandleScopeNegotiation(
        HandleScopeProtocolAdapter Adapter,
        HandleScopeApiMetadata? Metadata);

}
