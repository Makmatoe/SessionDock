using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

public sealed class HandleScopeIntegrationService : IDisposable
{
    private const int MaximumHealthResponseBytes = 16 * 1024;
    private const string RequiredPolicy = "roblox-singleton-event-v1";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly string _localAppDataRoot;
    private readonly string _installRoot;
    private readonly string _executablePath;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HandleScopeIntegrationConfigurationStore _configurationStore;
    private readonly IHandleScopeProcessVerifier _processVerifier;
    private readonly IHandleScopeInstalledRuntimeVerifier _installedRuntimeVerifier;
    private readonly HandleScopeApiBootstrapper _apiBootstrapper;
    private readonly Func<string, bool>? _isReparsePoint;
    private readonly HttpClient _client;
    private bool _disposed;

    public HandleScopeIntegrationService()
        : this(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            AppDataPaths.RootDirectory,
            CreateSecureHandler(),
            processVerifier: null,
            isReparsePoint: null)
    {
    }

    internal HandleScopeIntegrationService(
        string localAppDataRoot,
        string sessionDockDataRoot,
        HttpMessageHandler handler,
        IHandleScopeProcessVerifier? processVerifier,
        Func<string, bool>? isReparsePoint,
        IHandleScopeInstalledRuntimeVerifier? installedRuntimeVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDockDataRoot);
        ArgumentNullException.ThrowIfNull(handler);

        _localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        _installRoot = Path.GetFullPath(Path.Combine(
            _localAppDataRoot,
            "Programs",
            "HandleScope",
            "Api"));
        _executablePath = Path.Combine(_installRoot, "HandleScope.Api.exe");
        var configurationPath = Path.Combine(
            Path.GetFullPath(sessionDockDataRoot),
            "handlescope.json");
        var connectionPath = Path.Combine(
            _localAppDataRoot,
            "HandleScope",
            "connection.json");

        _connectionLoader = new HandleScopeConnectionLoader(
            connectionPath,
            _localAppDataRoot,
            isReparsePoint);
        _configurationStore = new HandleScopeIntegrationConfigurationStore(
            _localAppDataRoot,
            configurationPath,
            isReparsePoint);
        _installedRuntimeVerifier = installedRuntimeVerifier ??
            new HandleScopeInstalledRuntimeVerifier();
        _processVerifier = processVerifier ?? new HandleScopeProcessVerifier(
            _localAppDataRoot,
            _executablePath,
            isReparsePoint,
            _installedRuntimeVerifier);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout
        };
        _apiBootstrapper = new HandleScopeApiBootstrapper(
            _connectionLoader,
            _client,
            _processVerifier,
            new HandleScopeSelectionStore(Path.Combine(
                Path.GetFullPath(sessionDockDataRoot),
                "handlescope-preferences.json")));
        _isReparsePoint = isReparsePoint;
    }

    public Task<HandleScopeIntegrationResult> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(InspectLocal());
    }

    public async Task<HandleScopeIntegrationResult> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var local = InspectLocal();
        if (local.State is not HandleScopeIntegrationState.InstalledStopped)
            return local;

        try
        {
            var connection = _connectionLoader.Load();
            if (connection is null ||
                !_processVerifier.IsExpected(connection))
            {
                return Result(HandleScopeIntegrationState.InstalledStopped);
            }

            var health = await ProbeHealthAsync(
                connection.BaseUrl,
                cancellationToken);
            if (health == HealthProbeResult.Ready &&
                _processVerifier is IHandleScopeResolvedProcessVerifier)
            {
                var negotiated = await _apiBootstrapper.GetExistingAsync(
                    cancellationToken);
                return negotiated is null
                    ? Result(HandleScopeIntegrationState.UpdateRequired)
                    : Result(HandleScopeIntegrationState.Ready);
            }
            return health switch
            {
                HealthProbeResult.Ready =>
                    Result(HandleScopeIntegrationState.Ready),
                HealthProbeResult.UpdateRequired =>
                    Result(HandleScopeIntegrationState.UpdateRequired),
                HealthProbeResult.Invalid =>
                    Result(HandleScopeIntegrationState.ConfigurationError),
                _ => Result(HandleScopeIntegrationState.InstalledStopped)
            };
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or
                InvalidOperationException or ArgumentException or
                UnauthorizedAccessException or Win32Exception or
                NotSupportedException or TaskCanceledException or
                ObjectDisposedException)
        {
            return Result(HandleScopeIntegrationState.InstalledStopped);
        }
    }

    public Task<HandleScopeIntegrationResult> EnableAsync(
        bool repairExisting = false,
        CancellationToken cancellationToken = default)
    {
        return SetEnabledAsync(
            enabled: true,
            repairExisting,
            cancellationToken);
    }

    public Task<HandleScopeIntegrationResult> DisableAsync(
        bool repairExisting = false,
        CancellationToken cancellationToken = default)
    {
        return SetEnabledAsync(
            enabled: false,
            repairExisting,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }

    internal static SocketsHttpHandler CreateSecureHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = RequestTimeout,
        Credentials = null,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 8,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null
    };

    private Task<HandleScopeIntegrationResult> SetEnabledAsync(
        bool enabled,
        bool repairExisting,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var writeResult = _configurationStore.TrySetEnabled(
            enabled,
            repairExisting);
        if (writeResult is not HandleScopeConfigurationWriteResult.Succeeded)
        {
            return Task.FromResult(Result(
                HandleScopeIntegrationState.ConfigurationError,
                canRepairConfiguration:
                    writeResult is HandleScopeConfigurationWriteResult.RepairRequired));
        }

        return Task.FromResult(InspectLocal());
    }

    private HandleScopeIntegrationResult InspectLocal()
    {
        var install = InspectInstall();
        if (install is InstallInspection.Missing)
            return Result(HandleScopeIntegrationState.NotInstalled);
        if (install is InstallInspection.Invalid)
            return Result(HandleScopeIntegrationState.ConfigurationError);

        var configuration = _configurationStore.Read();
        if (!configuration.IsValid)
        {
            return Result(
                HandleScopeIntegrationState.ConfigurationError,
                configuration.CanRepair);
        }

        return Result(configuration.IsEnabled
            ? HandleScopeIntegrationState.InstalledStopped
            : HandleScopeIntegrationState.RunningDisabled);
    }

    private InstallInspection InspectInstall()
    {
        try
        {
            if (!TryGetAttributes(_installRoot, out var installAttributes))
                return InstallInspection.Missing;
            if ((installAttributes & FileAttributes.Directory) == 0)
                return InstallInspection.Invalid;
            if (!HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _installRoot,
                    targetMustExist: true,
                    _isReparsePoint))
            {
                return InstallInspection.Invalid;
            }
            if (!TryGetAttributes(_executablePath, out var executableAttributes))
                return InstallInspection.Missing;
            if (!HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _executablePath,
                    targetMustExist: true,
                    _isReparsePoint) ||
                (executableAttributes & FileAttributes.Directory) != 0)
            {
                return InstallInspection.Invalid;
            }

            return HasPortableExecutableHeader(_executablePath) &&
                _installedRuntimeVerifier.IsAuthorized(_executablePath)
                ? InstallInspection.Valid
                : InstallInspection.Invalid;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return InstallInspection.Invalid;
        }
    }

    private async Task<HealthProbeResult> ProbeHealthAsync(
        Uri baseUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUrl, "/v1/health"));
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.UpgradeRequired)
        {
            return HealthProbeResult.UpdateRequired;
        }
        if (!response.IsSuccessStatusCode)
            return HealthProbeResult.Unavailable;
        if (response.Content.Headers.ContentLength > MaximumHealthResponseBytes)
            return HealthProbeResult.Invalid;

        var json = await ReadBoundedHealthResponseAsync(
            response.Content,
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!TryReadStrictHealthDocument(
                    root,
                    out var status,
                    out var apiVersion,
                    out var policy))
            {
                return HealthProbeResult.Invalid;
            }
            if (!apiVersion.Equals("v1", StringComparison.Ordinal) ||
                !policy.Equals(RequiredPolicy, StringComparison.Ordinal))
            {
                return HealthProbeResult.UpdateRequired;
            }

            return status.Equals("ready", StringComparison.Ordinal)
                ? HealthProbeResult.Ready
                : HealthProbeResult.Unavailable;
        }
        catch (JsonException)
        {
            return HealthProbeResult.Invalid;
        }
    }

    private static async Task<byte[]> ReadBoundedHealthResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(
            cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumHealthResponseBytes)
                throw new InvalidDataException(
                    "The HandleScope health response is too large.");
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool TryReadStrictHealthDocument(
        JsonElement root,
        out string status,
        out string apiVersion,
        out string policy)
    {
        status = string.Empty;
        apiVersion = string.Empty;
        policy = string.Empty;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetPropertyCount() != 3)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (property.Name)
            {
                case "status":
                    status = property.Value.GetString() ?? string.Empty;
                    break;
                case "apiVersion":
                    apiVersion = property.Value.GetString() ?? string.Empty;
                    break;
                case "policy":
                    policy = property.Value.GetString() ?? string.Empty;
                    break;
                default:
                    return false;
            }
        }

        return names.SetEquals(["status", "apiVersion", "policy"]);
    }

    private static bool HasPortableExecutableHeader(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length < 68)
            return false;

        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length ||
            header[0] != (byte)'M' ||
            header[1] != (byte)'Z')
        {
            return false;
        }

        var peOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            header[60..64]);
        if (peOffset < 64 || peOffset > stream.Length - 4)
            return false;

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == signature.Length &&
            signature.SequenceEqual("PE\0\0"u8);
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static HandleScopeIntegrationResult Result(
        HandleScopeIntegrationState state,
        bool canRepairConfiguration = false) =>
        new(state, canRepairConfiguration);

    private enum InstallInspection
    {
        Missing,
        Valid,
        Invalid
    }

    private enum HealthProbeResult
    {
        Unavailable,
        Ready,
        UpdateRequired,
        Invalid
    }
}
