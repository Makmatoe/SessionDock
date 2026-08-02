using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeCompatibilityCatalogService : IDisposable
{
    internal const string BootstrapResourceName =
        "SessionDock.Embedded.HandleScopeCompatibilityBootstrap.json";
    internal const string PublicKeyResourceName =
        "SessionDock.Embedded.ReleasePublicKey.pem";
    internal static readonly Uri LatestCatalogUri = new(
        "https://github.com/Makmatoe/SessionDock/releases/latest/download/" +
        HandleScopeCompatibilityCatalogPolicy.FileName);

    private const int MaximumRedirects = 4;
    private static readonly TimeSpan CatalogLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly string _floorPath;
    private readonly string _lockPath;
    private readonly HttpClient _client;
    private readonly string _bootstrapJson;
    private readonly string _publicKeyPem;
    private bool _disposed;

    internal HandleScopeCompatibilityCatalogService()
        : this(
            CreateDownloadHandler(),
            Path.Combine(
                AppDataPaths.RootDirectory,
                "HandleScopeCompatibility",
                HandleScopeCompatibilityCatalogPolicy.FileName),
            ReadEmbeddedText(BootstrapResourceName),
            ReadEmbeddedText(PublicKeyResourceName))
    {
    }

    internal HandleScopeCompatibilityCatalogService(
        HttpMessageHandler handler,
        string cachePath,
        string bootstrapJson,
        string publicKeyPem,
        string? floorPath = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        _cachePath = Path.GetFullPath(cachePath);
        _floorPath = Path.GetFullPath(floorPath ?? _cachePath + ".floor");
        _lockPath = _floorPath + ".lock";
        if (string.Equals(
                _cachePath,
                _floorPath,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                _cachePath,
                _lockPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(_cachePath),
                Path.GetDirectoryName(_floorPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The HandleScope catalog cache and rollback floor must share one directory.",
                nameof(floorPath));
        }
        _bootstrapJson = bootstrapJson;
        _publicKeyPem = publicKeyPem;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SessionDock/2.8");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    internal VerifiedHandleScopeCompatibilityCatalog Load()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var catalogLock = AcquireCatalogLock();
        return LoadUnderLock();
    }

    internal HandleScopeCatalogReadLease AcquireCurrentCatalog(
        VerifiedHandleScopeCompatibilityCatalog expectedSnapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        if (expectedSnapshot.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new HandleScopeCatalogException(
                "The expected HandleScope catalog snapshot has expired.");
        }

        var catalogLock = AcquireCatalogLock(cancellationToken);
        try
        {
            var current = LoadUnderLock();
            var now = DateTimeOffset.UtcNow;
            if (expectedSnapshot.ExpiresAt <= now ||
                current.ExpiresAt <= now ||
                !SatisfiesFloor(current, expectedSnapshot))
            {
                throw new HandleScopeCatalogException(
                    "The current HandleScope catalog cannot satisfy the expected authenticated snapshot.");
            }
            return new(current, catalogLock);
        }
        catch
        {
            catalogLock.Dispose();
            throw;
        }
    }

    internal async Task<VerifiedHandleScopeCompatibilityCatalog> RefreshAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await SendCatalogRequestAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HandleScopeCatalogException(
                "The signed HandleScope version catalog could not be retrieved from GitHub.");
        }
        if (response.Content.Headers.ContentLength is < 0 or
            > HandleScopeCompatibilityCatalogPolicy.MaximumCatalogBytes)
        {
            throw new HandleScopeCatalogException(
                "GitHub returned a HandleScope version catalog outside the safe size boundary.");
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeCatalogException(
                "The HandleScope version catalog is not valid UTF-8.",
                exception);
        }

        VerifiedHandleScopeCompatibilityCatalog remote;
        try
        {
            remote = HandleScopeCompatibilityCatalogPolicy.Verify(
                json,
                _publicKeyPem);
        }
        catch (ReleaseTrustException exception)
        {
            throw new HandleScopeCatalogException(
                "The HandleScope version catalog failed signature or policy verification.",
                exception);
        }

        using var catalogLock = await AcquireCatalogLockAsync(
            cancellationToken);
        var bootstrap = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            _bootstrapJson);
        var floor = ReadAuthenticatedCatalog(
            _floorPath,
            "HandleScope catalog rollback floor");
        var cached = ReadCacheForFloor(floor is not null);
        floor = AdvanceFloorFromCache(floor, cached);
        if (remote.ExpiresAt <= DateTimeOffset.UtcNow ||
            !SatisfiesFloor(remote, bootstrap) ||
            floor is not null && !SatisfiesFloor(remote, floor.Catalog))
        {
            throw new HandleScopeCatalogException(
                "The HandleScope version catalog is older than the last trusted catalog.");
        }

        WriteVerifiedCatalog(
            _floorPath,
            json,
            "authenticated rollback floor");
        WriteVerifiedCatalog(_cachePath, json, "version catalog cache");
        return remote;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }

    internal static SocketsHttpHandler CreateDownloadHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        Credentials = null,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 16,
        PreAuthenticate = false,
        UseCookies = false,
        ActivityHeadersPropagator = null
    };

    internal static IReadOnlyList<HandleScopeCompatibleRelease>
        GetCompatibleReleases(
            VerifiedHandleScopeCompatibilityCatalog catalog,
            Version sessionDockVersion,
            IReadOnlySet<string> compiledApiContracts,
            IReadOnlySet<string> requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessionDockVersion);
        ArgumentNullException.ThrowIfNull(compiledApiContracts);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        return catalog.Releases
            .Where(pair => IsCompatible(
                pair.Key,
                pair.Value,
                sessionDockVersion,
                compiledApiContracts,
                requiredCapabilities) &&
                !IsRuntimeIdentityRevoked(catalog, pair.Value))
            .OrderByDescending(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();
    }

    internal static bool IsRuntimeIdentityRevoked(
        VerifiedHandleScopeCompatibilityCatalog catalog,
        HandleScopeCompatibleRelease release)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(release);
        return catalog.Releases.Values.Any(candidate =>
            candidate.Status == "revoked" &&
            candidate.ApiExecutable.Size == release.ApiExecutable.Size &&
            candidate.ApiExecutable.Sha256 == release.ApiExecutable.Sha256);
    }

    internal static bool IsCompatible(
        Version releaseVersion,
        HandleScopeCompatibleRelease release,
        Version sessionDockVersion,
        IReadOnlySet<string> compiledApiContracts,
        IReadOnlySet<string> requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(releaseVersion);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(sessionDockVersion);
        ArgumentNullException.ThrowIfNull(compiledApiContracts);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        if (!Version.TryParse(release.Version, out var declaredVersion) ||
            declaredVersion != releaseVersion ||
            release.Status != "supported" ||
            release.ApiContracts is null ||
            release.Capabilities is null ||
            !Version.TryParse(release.MinimumSessionDockVersion, out var minimum) ||
            sessionDockVersion < minimum ||
            release.MaximumSessionDockVersionExclusive is { } maximumText &&
            (!Version.TryParse(maximumText, out var maximum) ||
             sessionDockVersion >= maximum))
        {
            return false;
        }

        var capabilities = release.Capabilities.ToHashSet(StringComparer.Ordinal);
        var expectedHttpCapabilities = release.ApiContracts
            .Select(contract => $"handlescope.http.{contract}")
            .ToHashSet(StringComparer.Ordinal);
        var declaredHttpCapabilities = capabilities
            .Where(capability => capability.StartsWith(
                "handlescope.http.",
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        return expectedHttpCapabilities.SetEquals(declaredHttpCapabilities) &&
            release.ApiContracts.Any(contract =>
                compiledApiContracts.Contains(contract) &&
                capabilities.Contains($"handlescope.http.{contract}")) &&
            requiredCapabilities.All(capabilities.Contains);
    }

    private VerifiedHandleScopeCompatibilityCatalog LoadUnderLock()
    {
        var bootstrap = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            _bootstrapJson);
        var floor = ReadAuthenticatedCatalog(
            _floorPath,
            "HandleScope catalog rollback floor");
        var cached = ReadCacheForFloor(floor is not null);
        floor = AdvanceFloorFromCache(floor, cached);
        if (floor is null)
        {
            return bootstrap.ExpiresAt > DateTimeOffset.UtcNow
                ? bootstrap
                : throw new HandleScopeCatalogException(
                    "The packaged HandleScope catalog has expired and no current authenticated catalog is available.");
        }

        VerifiedHandleScopeCompatibilityCatalog? selected = null;
        var currentFloor = TryVerifyCurrent(floor);
        if (currentFloor is not null &&
            SatisfiesFloor(currentFloor, bootstrap))
        {
            selected = currentFloor;
        }
        if (cached is not null)
        {
            var currentCached = TryVerifyCurrent(cached);
            if (currentCached is not null &&
                SatisfiesFloor(currentCached, floor.Catalog) &&
                SatisfiesFloor(currentCached, bootstrap) &&
                (selected is null || SatisfiesFloor(currentCached, selected)))
            {
                selected = currentCached;
            }
        }

        if (bootstrap.ExpiresAt > DateTimeOffset.UtcNow &&
            StrictlyAdvances(bootstrap, floor.Catalog))
        {
            if (selected is null || StrictlyAdvances(bootstrap, selected))
            {
                selected = bootstrap;
            }
            else if (!SatisfiesFloor(selected, bootstrap))
            {
                throw new HandleScopeCatalogException(
                    "The packaged and cached HandleScope catalogs have conflicting authenticated histories.");
            }
        }

        return selected ?? throw new HandleScopeCatalogException(
            "No current HandleScope catalog satisfies the authenticated rollback floor.");
    }

    private AuthenticatedCatalog? ReadCacheForFloor(bool floorExists)
    {
        try
        {
            return ReadAuthenticatedCatalog(
                _cachePath,
                "HandleScope catalog cache");
        }
        catch (HandleScopeCatalogException exception) when (floorExists)
        {
            Trace.WriteLine(
                $"Cached HandleScope catalog cannot satisfy its authenticated floor: {exception.InnerException?.GetType().Name ?? exception.GetType().Name}.");
            return null;
        }
    }

    private AuthenticatedCatalog? AdvanceFloorFromCache(
        AuthenticatedCatalog? floor,
        AuthenticatedCatalog? cached)
    {
        if (cached is null ||
            floor is not null && !StrictlyAdvances(cached.Catalog, floor.Catalog))
        {
            return floor;
        }

        WriteVerifiedCatalog(
            _floorPath,
            cached.Json,
            "authenticated rollback floor");
        return cached;
    }

    private AuthenticatedCatalog? ReadAuthenticatedCatalog(
        string path,
        string description)
    {
        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            var info = new FileInfo(path);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.ReparsePoint)) != 0 ||
                info.Length is <= 0 or
                    > HandleScopeCompatibilityCatalogPolicy.MaximumCatalogBytes)
            {
                throw new InvalidDataException(
                    $"The {description} is outside its safe file boundary.");
            }
            var json = StrictUtf8.GetString(File.ReadAllBytes(path));
            var catalog = HandleScopeCompatibilityCatalogPolicy.Deserialize(json);
            if (!DateTimeOffset.TryParseExact(
                    catalog.GeneratedAt,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var generatedAt) ||
                generatedAt.Offset != TimeSpan.Zero)
            {
                throw new ReleaseTrustException(
                    "The authenticated HandleScope catalog generation time is invalid.");
            }
            var verified = HandleScopeCompatibilityCatalogPolicy.Verify(
                json,
                _publicKeyPem,
                generatedAt);
            return new(json, verified);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                DecoderFallbackException or ReleaseTrustException or
                NotSupportedException or ArgumentException or
                CryptographicException)
        {
            throw new HandleScopeCatalogException(
                $"The {description} is invalid and cannot be trusted.",
                exception);
        }
    }

    private VerifiedHandleScopeCompatibilityCatalog? TryVerifyCurrent(
        AuthenticatedCatalog catalog)
    {
        try
        {
            return HandleScopeCompatibilityCatalogPolicy.Verify(
                catalog.Json,
                _publicKeyPem);
        }
        catch (ReleaseTrustException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendCatalogRequestAsync(
        CancellationToken cancellationToken)
    {
        var current = LatestCatalogUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsAllowedCatalogUri(current, redirect == 0))
            {
                throw new HandleScopeCatalogException(
                    "GitHub redirected the HandleScope version catalog to an untrusted address.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is not (
                    HttpStatusCode.MovedPermanently or
                    HttpStatusCode.Redirect or
                    HttpStatusCode.RedirectMethod or
                    HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect == MaximumRedirects)
            {
                throw new HandleScopeCatalogException(
                    "GitHub returned an invalid HandleScope catalog redirect.");
            }
            current = location.IsAbsoluteUri
                ? location
                : new Uri(current, location);
        }

        throw new HandleScopeCatalogException(
            "GitHub returned too many HandleScope catalog redirects.");
    }

    private static bool IsAllowedCatalogUri(Uri uri, bool initial)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }
        if (initial)
            return uri == LatestCatalogUri;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith(
                       "/Makmatoe/SessionDock/releases/download/",
                       StringComparison.Ordinal) &&
                   uri.AbsolutePath.EndsWith(
                       "/" + HandleScopeCompatibilityCatalogPolicy.FileName,
                       StringComparison.Ordinal) &&
                   string.IsNullOrEmpty(uri.Query);
        }
        return uri.Host.Equals(
                   "release-assets.githubusercontent.com",
                   StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals(
                   "objects.githubusercontent.com",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read >
                HandleScopeCompatibilityCatalogPolicy.MaximumCatalogBytes)
            {
                throw new HandleScopeCatalogException(
                    "The HandleScope version catalog exceeded its safe size boundary.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private void WriteVerifiedCatalog(
        string path,
        string json,
        string description)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new HandleScopeCatalogException(
                $"The HandleScope {description} has no parent directory.");
        Directory.CreateDirectory(directory);
        if ((new DirectoryInfo(directory).Attributes &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new HandleScopeCatalogException(
                "The HandleScope catalog storage directory cannot be a reparse point.");
        }
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new HandleScopeCatalogException(
                $"The HandleScope {description} must be a regular file.");
        }

        var temporaryPath = path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json.TrimEnd('\r', '\n'));
                writer.Write('\n');
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            throw new HandleScopeCatalogException(
                $"The verified HandleScope {description} could not be stored safely.",
                exception);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { /* The temporary file contains public signed metadata only. */ }
        }
    }

    private CatalogLockLease AcquireCatalogLock(
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var semaphore = ProcessLocks.GetOrAdd(
            _lockPath,
            static _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(CatalogLockTimeout, cancellationToken))
        {
            throw new HandleScopeCatalogException(
                "The HandleScope catalog trust-state lock could not be acquired safely.");
        }

        try
        {
            return OpenCatalogLock(
                semaphore,
                started,
                cancellationToken);
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private async Task<CatalogLockLease> AcquireCatalogLockAsync(
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var semaphore = ProcessLocks.GetOrAdd(
            _lockPath,
            static _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(CatalogLockTimeout, cancellationToken))
        {
            throw new HandleScopeCatalogException(
                "The HandleScope catalog trust-state lock could not be acquired safely.");
        }

        try
        {
            return await OpenCatalogLockAsync(
                semaphore,
                started,
                cancellationToken);
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private CatalogLockLease OpenCatalogLock(
        SemaphoreSlim semaphore,
        long started,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return OpenCatalogLockOnce(semaphore);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                var delay = GetLockRetryDelay(started);
                if (delay <= TimeSpan.Zero)
                    throw CreateCatalogLockException(exception);
                if (cancellationToken.WaitHandle.WaitOne(delay))
                    cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (IsCatalogLockFailure(exception))
            {
                throw CreateCatalogLockException(exception);
            }
        }
    }

    private async Task<CatalogLockLease> OpenCatalogLockAsync(
        SemaphoreSlim semaphore,
        long started,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return OpenCatalogLockOnce(semaphore);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                var delay = GetLockRetryDelay(started);
                if (delay <= TimeSpan.Zero)
                    throw CreateCatalogLockException(exception);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception exception) when (IsCatalogLockFailure(exception))
            {
                throw CreateCatalogLockException(exception);
            }
        }
    }

    private CatalogLockLease OpenCatalogLockOnce(SemaphoreSlim semaphore)
    {
        var directory = Path.GetDirectoryName(_lockPath)
            ?? throw new InvalidOperationException(
                "The HandleScope catalog lock has no parent directory.");
        Directory.CreateDirectory(directory);
        if ((new DirectoryInfo(directory).Attributes &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The HandleScope catalog lock directory is not a regular local directory.");
        }

        FileAttributes? lockAttributes = null;
        try
        {
            lockAttributes = File.GetAttributes(_lockPath);
        }
        catch (FileNotFoundException)
        {
            // A missing lock file is the normal first-use state.
        }
        catch (DirectoryNotFoundException)
        {
            // Directory.CreateDirectory above makes this a concurrent removal;
            // opening the lock below will fail closed if it remains absent.
        }
        if (lockAttributes is { } attributes &&
            (attributes & (FileAttributes.Directory |
                           FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                "The HandleScope catalog lock path is not a regular local file.");
        }

        var stream = new FileStream(
            _lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        return new(stream, semaphore);
    }

    private static bool IsLockContention(IOException exception)
    {
        var errorCode = exception.HResult & 0xffff;
        return errorCode is 11 or 32 or 33;
    }

    private static bool IsCatalogLockFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or
            InvalidOperationException;

    private static TimeSpan GetLockRetryDelay(long started)
    {
        var remaining = CatalogLockTimeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        var interval = TimeSpan.FromMilliseconds(50);
        return remaining < interval ? remaining : interval;
    }

    private static HandleScopeCatalogException CreateCatalogLockException(
        Exception exception) => new(
        "The HandleScope catalog trust-state lock is unavailable or ambiguous.",
        exception);

    private static bool SatisfiesFloor(
        VerifiedHandleScopeCompatibilityCatalog candidate,
        VerifiedHandleScopeCompatibilityCatalog floor) =>
        IsSameCatalog(candidate, floor) || StrictlyAdvances(candidate, floor);

    private static bool StrictlyAdvances(
        VerifiedHandleScopeCompatibilityCatalog candidate,
        VerifiedHandleScopeCompatibilityCatalog floor) =>
        candidate.Catalog.Sequence > floor.Catalog.Sequence &&
        candidate.GeneratedAt > floor.GeneratedAt;

    private static bool IsSameCatalog(
        VerifiedHandleScopeCompatibilityCatalog candidate,
        VerifiedHandleScopeCompatibilityCatalog floor)
    {
        if (candidate.Catalog.Sequence != floor.Catalog.Sequence ||
            candidate.GeneratedAt != floor.GeneratedAt)
        {
            return false;
        }

        var candidateDigest = SHA256.HashData(
            HandleScopeCompatibilityCatalogPolicy.CreateCanonicalPayload(
                candidate.Catalog));
        var floorDigest = SHA256.HashData(
            HandleScopeCompatibilityCatalogPolicy.CreateCanonicalPayload(
                floor.Catalog));
        return CryptographicOperations.FixedTimeEquals(
            candidateDigest,
            floorDigest);
    }

    private sealed record AuthenticatedCatalog(
        string Json,
        VerifiedHandleScopeCompatibilityCatalog Catalog);

    private sealed class CatalogLockLease(
        FileStream stream,
        SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                stream.Dispose();
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource is unavailable: {resourceName}");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}

internal sealed class HandleScopeCatalogReadLease : IDisposable
{
    private IDisposable? _lock;

    internal HandleScopeCatalogReadLease(
        VerifiedHandleScopeCompatibilityCatalog catalog,
        IDisposable catalogLock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(catalogLock);
        Catalog = catalog;
        _lock = catalogLock;
    }

    internal VerifiedHandleScopeCompatibilityCatalog Catalog { get; }

    public void Dispose() => Interlocked.Exchange(ref _lock, null)?.Dispose();
}

internal sealed class HandleScopeCatalogException : Exception
{
    internal HandleScopeCatalogException(string message)
        : base(message)
    {
    }

    internal HandleScopeCatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
