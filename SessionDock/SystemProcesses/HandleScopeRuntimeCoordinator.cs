using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using SessionDock.HandleScope;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

public sealed class HandleScopeRuntimeCoordinator : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifetimeLock = new();
    private readonly HandleScopeIntegrationConfigurationStore _configurationStore;
    private readonly HandleScopeRuntimeSourceStore _sourceStore;
    private readonly HandleScopeSelectionStore _selectionStore;
    private readonly HandleScopeCompatibilityCatalogService _catalogService;
    private readonly BundledHandleScopeRuntime _bundledRuntime;
    private readonly HttpClient _client;
    private readonly HandleScopeApiBootstrapper _bundledBootstrapper;
    private readonly HandleScopeApiBootstrapper _standaloneBootstrapper;
    private Task? _shutdownTask;
    private volatile bool _disposed;

    public HandleScopeRuntimeCoordinator()
    {
        var localAppDataRoot = Path.GetFullPath(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));
        var sessionDockRoot = Path.GetFullPath(AppDataPaths.RootDirectory);
        _configurationStore = new HandleScopeIntegrationConfigurationStore(
            localAppDataRoot,
            Path.Combine(sessionDockRoot, "handlescope.json"));
        _sourceStore = new HandleScopeRuntimeSourceStore(
            Path.Combine(sessionDockRoot, "handlescope-runtime.json"));
        _selectionStore = new HandleScopeSelectionStore(
            Path.Combine(sessionDockRoot, "handlescope-preferences.json"));
        _catalogService = new HandleScopeCompatibilityCatalogService();
        _bundledRuntime = new BundledHandleScopeRuntime();
        _client = new HttpClient(
            CreateSecureHandler(),
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _bundledBootstrapper = new HandleScopeApiBootstrapper(
            _bundledRuntime,
            _client,
            _bundledRuntime,
            new BundledSelectionSource(_selectionStore));

        var standaloneLoader = new HandleScopeConnectionLoader(
            Path.Combine(localAppDataRoot, "HandleScope", "connection.json"),
            localAppDataRoot,
            isReparsePoint: null);
        _standaloneBootstrapper = new HandleScopeApiBootstrapper(
            standaloneLoader,
            _client,
            HandleScopeProcessVerifier.CreateDefault(),
            _selectionStore);
    }

    public async Task<HandleScopeRuntimeSnapshot> InspectAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            token => InspectCoreAsync(ensureReady: true, token),
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> EnableAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                var write = _configurationStore.TrySetEnabled(
                    enabled: true,
                    repairExisting: false);
                if (write != HandleScopeConfigurationWriteResult.Succeeded)
                {
                    return ConfigurationError(
                        canRepair: write ==
                            HandleScopeConfigurationWriteResult.RepairRequired);
                }
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> DisableAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                var write = _configurationStore.TrySetEnabled(
                    enabled: false,
                    repairExisting: false);
                if (write != HandleScopeConfigurationWriteResult.Succeeded)
                {
                    return ConfigurationError(
                        canRepair: write ==
                            HandleScopeConfigurationWriteResult.RepairRequired);
                }
                await _bundledRuntime.StopAsync(token).ConfigureAwait(false);
                return CreateSnapshot(
                    HandleScopeRuntimeState.Off,
                    ReadSourceOrDefault(),
                    standaloneVersion: null);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> RestartAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                var source = await ResolveSourceAsync(token).ConfigureAwait(false);
                if (source == HandleScopeRuntimeSource.Bundled)
                    await _bundledRuntime.StopAsync(token).ConfigureAwait(false);
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> RepairAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                _sourceStore.Write(HandleScopeRuntimeSource.Bundled);
                _selectionStore.Write(HandleScopeSelection.Default);
                var write = _configurationStore.TrySetEnabled(
                    enabled: true,
                    repairExisting: true);
                if (write != HandleScopeConfigurationWriteResult.Succeeded)
                    return ConfigurationError(canRepair: false);
                await _bundledRuntime.StopAsync(token).ConfigureAwait(false);
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> SetRuntimeSourceAsync(
        HandleScopeRuntimeSource source,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                if (!Enum.IsDefined(source))
                    throw new ArgumentOutOfRangeException(nameof(source));
                _sourceStore.Write(source);
                if (source == HandleScopeRuntimeSource.Standalone)
                    await _bundledRuntime.StopAsync(token).ConfigureAwait(false);
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> SetApiContractAsync(
        HandleScopeApiContract apiContract,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                if (!Enum.IsDefined(apiContract))
                    throw new ArgumentOutOfRangeException(nameof(apiContract));
                var current = _selectionStore.Read();
                _ = await ResolveSourceAsync(token).ConfigureAwait(false);
                var versionMode = current.IsValid
                    ? current.Selection.VersionMode
                    : HandleScopeVersionSelectionMode.Automatic;
                var exactVersion = versionMode == HandleScopeVersionSelectionMode.Exact &&
                                   current.IsValid
                    ? current.Selection.ExactVersion
                    : null;
                _selectionStore.Write(new HandleScopeSelection(
                    versionMode,
                    exactVersion,
                    ToStoredApiContract(apiContract)));
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> SetRuntimeVersionAsync(
        HandleScopeVersionSelectionMode versionMode,
        Version? exactVersion,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                _ = await ResolveSourceAsync(token).ConfigureAwait(false);
                _selectionStore.WriteRuntimeVersionPreference(
                    versionMode,
                    exactVersion,
                    LoadCompatibleStandaloneVersions());
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<HandleScopeRuntimeSnapshot> RefreshReviewedVersionsAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                if (await ResolveSourceAsync(token).ConfigureAwait(false) !=
                    HandleScopeRuntimeSource.Standalone)
                {
                    throw new InvalidOperationException(
                        "Reviewed standalone versions can be refreshed only for the standalone source.");
                }

                _ = await _catalogService.RefreshAsync(token)
                    .ConfigureAwait(false);
                return await InspectCoreAsync(ensureReady: true, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    internal bool IsEnabled =>
        _configurationStore.Read() is { IsValid: true, IsEnabled: true };

    internal async Task<HandleScopeConnection?> GetReadyConnectionAsync(
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
            async token =>
            {
                var configuration = _configurationStore.Read();
                var sourceResult = _sourceStore.Read();
                var selection = _selectionStore.Read();
                if (!CanUseReadyConnection(
                        configuration,
                        sourceResult,
                        selection))
                {
                    return null;
                }

                var source = await ResolveSourceAsync(
                    sourceResult,
                    selection,
                    token).ConfigureAwait(false);
                return await GetReadyConnectionCoreAsync(source, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

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

    private async Task<HandleScopeRuntimeSnapshot> InspectCoreAsync(
        bool ensureReady,
        CancellationToken cancellationToken)
    {
        var configuration = _configurationStore.Read();
        var sourceResult = _sourceStore.Read();
        var selection = _selectionStore.Read();
        if (!configuration.IsValid || !sourceResult.IsValid || !selection.IsValid)
        {
            return ConfigurationError(
                canRepair: configuration.CanRepair ||
                    !sourceResult.IsValid || !selection.IsValid);
        }

        var source = await ResolveSourceAsync(
                sourceResult,
                selection,
                cancellationToken)
            .ConfigureAwait(false);
        if (!configuration.IsEnabled)
        {
            if (_bundledRuntime.Load() is not null)
                await _bundledRuntime.StopAsync(cancellationToken)
                    .ConfigureAwait(false);
            return CreateSnapshot(
                HandleScopeRuntimeState.Off,
                source,
                standaloneVersion: null);
        }

        if (!ensureReady)
        {
            return CreateSnapshot(
                HandleScopeRuntimeState.Starting,
                source,
                standaloneVersion: null);
        }

        var connection = await GetReadyConnectionCoreAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return CreateSnapshot(
                source == HandleScopeRuntimeSource.Standalone
                    ? HandleScopeRuntimeState.StandaloneUnavailable
                    : HandleScopeRuntimeState.NeedsAttention,
                source,
                standaloneVersion: null);
        }

        return CreateSnapshot(
            HandleScopeRuntimeState.Ready,
            source,
            source == HandleScopeRuntimeSource.Standalone
                ? connection.RuntimeIdentity?.Version.ToString(3)
                : null);
    }

    private async Task<HandleScopeConnection?> GetReadyConnectionCoreAsync(
        HandleScopeRuntimeSource source,
        CancellationToken cancellationToken)
    {
        if (source == HandleScopeRuntimeSource.Bundled)
        {
            if (await _bundledRuntime.EnsureStartedAsync(cancellationToken)
                    .ConfigureAwait(false) is null)
                return null;
            return await _bundledBootstrapper.GetExistingAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return await _standaloneBootstrapper.GetExistingAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HandleScopeRuntimeSource> ResolveSourceAsync(
        CancellationToken cancellationToken) =>
        await ResolveSourceAsync(
            _sourceStore.Read(),
            _selectionStore.Read(),
            cancellationToken).ConfigureAwait(false);

    private async Task<HandleScopeRuntimeSource> ResolveSourceAsync(
        HandleScopeRuntimeSourceReadResult stored,
        HandleScopeSelectionReadResult selection,
        CancellationToken cancellationToken)
    {
        if (!stored.IsValid)
            return HandleScopeRuntimeSource.Bundled;
        if (stored.Exists)
            return stored.Source;

        var source = selection.IsValid &&
                     selection.Selection.VersionMode is
                         HandleScopeVersionSelectionMode.KeepInstalled or
                         HandleScopeVersionSelectionMode.Exact
            ? HandleScopeRuntimeSource.Standalone
            : HandleScopeRuntimeSource.Bundled;

        if (source == HandleScopeRuntimeSource.Bundled &&
            _configurationStore.Read() is { IsValid: true, IsEnabled: true })
        {
            try
            {
                if (await _standaloneBootstrapper.GetExistingAsync(
                        cancellationToken).ConfigureAwait(false) is not null)
                {
                    source = HandleScopeRuntimeSource.Standalone;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or
                    InvalidOperationException or UnauthorizedAccessException or
                    NotSupportedException)
            {
                Trace.WriteLine(
                    $"Standalone HandleScope migration probe failed safely: {exception.GetType().Name}.");
            }
        }

        _sourceStore.Write(source);
        return source;
    }

    internal static bool CanUseReadyConnection(
        HandleScopeConfigurationSnapshot configuration,
        HandleScopeRuntimeSourceReadResult source,
        HandleScopeSelectionReadResult selection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        return configuration.IsValid &&
               configuration.IsEnabled &&
               source.IsValid &&
               selection.IsValid;
    }

    private HandleScopeRuntimeSource ReadSourceOrDefault()
    {
        var result = _sourceStore.Read();
        return result.IsValid ? result.Source : HandleScopeRuntimeSource.Bundled;
    }

    private HandleScopeRuntimeSnapshot CreateSnapshot(
        HandleScopeRuntimeState state,
        HandleScopeRuntimeSource source,
        string? standaloneVersion)
    {
        var selection = _selectionStore.Read();
        var compatibleVersions = LoadCompatibleStandaloneVersions();
        return new(
            state,
            source,
            HandleScopeBroker.ComponentVersion,
            standaloneVersion,
            selection.IsValid
                ? FromStoredApiContract(selection.Selection.ExactApiContract)
                : HandleScopeApiContract.Automatic,
            selection.IsValid
                ? selection.Selection.VersionMode
                : HandleScopeVersionSelectionMode.Automatic,
            selection.IsValid
                ? selection.Selection.ExactVersion
                : null,
            compatibleVersions,
            CanRepairConfiguration: false);
    }

    private HandleScopeRuntimeSnapshot ConfigurationError(bool canRepair)
    {
        var selection = _selectionStore.Read();
        return new(
            HandleScopeRuntimeState.ConfigurationError,
            HandleScopeRuntimeSource.Bundled,
            HandleScopeBroker.ComponentVersion,
            StandaloneVersion: null,
            selection.IsValid
                ? FromStoredApiContract(selection.Selection.ExactApiContract)
                : HandleScopeApiContract.Automatic,
            selection.IsValid
                ? selection.Selection.VersionMode
                : HandleScopeVersionSelectionMode.Automatic,
            selection.IsValid
                ? selection.Selection.ExactVersion
                : null,
            LoadCompatibleStandaloneVersions(),
            CanRepairConfiguration: canRepair);
    }

    private IReadOnlyList<Version> LoadCompatibleStandaloneVersions()
    {
        try
        {
            var catalog = _catalogService.Load();
            return HandleScopeCompatibilityCatalogService.GetCompatibleReleases(
                    catalog,
                    HandleScopeCompatibilityRequirements.SessionDockVersion,
                    HandleScopeCompatibilityRequirements.CompiledApiContracts,
                    HandleScopeCompatibilityRequirements.RequiredCapabilities)
                .Select(release => new Version(release.Version))
                .Distinct()
                .OrderByDescending(version => version)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is HandleScopeCatalogException or
                SessionDock.ReleaseTrust.ReleaseTrustException or IOException or
                UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException)
        {
            Trace.WriteLine(
                $"Standalone HandleScope version choices are unavailable: {exception.GetType().Name}.");
            return [];
        }
    }

    private async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linkedCancellation;
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        }

        using (linkedCancellation)
        {
            var entered = false;
            try
            {
                await _operationGate.WaitAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
                entered = true;
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return await operation(linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                    _operationGate.Release();
            }
        }
    }

    private Task GetOrStartShutdown()
    {
        lock (_lifetimeLock)
        {
            if (_shutdownTask is not null)
                return _shutdownTask;

            _disposed = true;
            _lifetimeCancellation.Cancel();
            _shutdownTask = ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        await _operationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            await _bundledRuntime.ShutdownAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _catalogService.Dispose();
            _client.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string? ToStoredApiContract(
        HandleScopeApiContract apiContract) => apiContract switch
        {
            HandleScopeApiContract.Automatic => null,
            HandleScopeApiContract.V2 => "v2",
            HandleScopeApiContract.V1 => "v1",
            _ => throw new ArgumentOutOfRangeException(nameof(apiContract))
        };

    private static HandleScopeApiContract FromStoredApiContract(
        string? apiContract) => apiContract switch
        {
            null => HandleScopeApiContract.Automatic,
            "v2" => HandleScopeApiContract.V2,
            "v1" => HandleScopeApiContract.V1,
            _ => HandleScopeApiContract.Automatic
        };

    private static SocketsHttpHandler CreateSecureHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Credentials = null,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 8,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null
    };

    private sealed class BundledSelectionSource : IHandleScopeSelectionSource
    {
        private readonly HandleScopeSelectionStore _store;

        internal BundledSelectionSource(HandleScopeSelectionStore store)
        {
            _store = store;
        }

        public HandleScopeSelectionReadResult Read()
        {
            var result = _store.Read();
            return !result.IsValid
                ? result
                : result with
                {
                    Selection = new HandleScopeSelection(
                        HandleScopeVersionSelectionMode.Automatic,
                        ExactVersion: null,
                        result.Selection.ExactApiContract)
                };
        }
    }
}
