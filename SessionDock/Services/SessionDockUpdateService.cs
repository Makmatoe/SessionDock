using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SessionDock.ReleaseTrust;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SessionDock.Services;

internal sealed class SessionDockUpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/Makmatoe/SessionDock";
    private const string PublicKeyResourceName =
        "SessionDock.Embedded.ReleasePublicKey.pem";
    private const int MaximumManifestRedirects = 3;
    private const int MaximumNuspecMetadataElements = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ProcessLifetimePackageLease ApplyPackageLease = new();
    private readonly UpdateManager _manager;
    private readonly HttpClient _httpClient;

    public SessionDockUpdateService()
    {
        var source = new GithubSource(
            RepositoryUrl,
            accessToken: null,
            prerelease: false,
            downloader: new BoundedUpdateDownloader());
        _manager = new UpdateManager(
            source,
            new UpdateOptions
            {
                AllowVersionDowngrade = false,
                ExplicitChannel = ReleaseDescriptorPolicy.Channel,
                MaximumDeltasBeforeFallback = 10
            });

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SessionDock/2.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool CanSelfUpdate => _manager.IsInstalled && !_manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ??
        (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown");

    public VelopackAsset? PendingUpdate =>
        CanSelfUpdate ? _manager.UpdatePendingRestart : null;

    public async Task<AvailableSessionDockUpdate?> CheckAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSelfUpdate)
            return null;

        var update = await UpdateFeedReader.ReadAsync(
            () => _manager.CheckForUpdatesAsync());
        if (update is null)
            return null;

        var verified = await FetchAndVerifyDescriptorAsync(
            update.TargetFullRelease,
            cancellationToken);
        return new AvailableSessionDockUpdate(update, verified);
    }

    public async Task<VerifiedReleaseDescriptor> VerifyPendingAsync(
        VelopackAsset pending,
        CancellationToken cancellationToken)
    {
        var localIdentity = await CreateLocalPackageIdentityAsync(
            pending,
            VelopackLocator.Current.PackagesDir,
            cancellationToken);
        var verified = await FetchAndVerifyDescriptorAsync(
            pending,
            localIdentity,
            cancellationToken);
        await VerifyPreparedPackageAsync(pending, verified, cancellationToken);
        return verified;
    }

    public async Task DownloadAsync(
        AvailableSessionDockUpdate update,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        await _manager.DownloadUpdatesAsync(
            update.UpdateInfo,
            progress,
            cancellationToken);
        await VerifyPreparedPackageAsync(
            update.UpdateInfo.TargetFullRelease,
            update.Release,
            cancellationToken);
    }

    public async Task ApplyAfterExitAsync(
        VelopackAsset asset,
        VerifiedReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(release);
        var lease = await OpenVerifiedPackageLeaseAsync(
            asset,
            release,
            VelopackLocator.Current.PackagesDir,
            cancellationToken);
        ApplyPackageLease.Schedule(
            lease,
            () => _manager.WaitExitThenApplyUpdates(
                asset,
                silent: false,
                restart: true));
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<VerifiedReleaseDescriptor> FetchAndVerifyDescriptorAsync(
        VelopackAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.SHA256))
        {
            throw new ReleaseTrustException(
                "The published update is missing its SHA-256 hash.");
        }

        var identity = new ReleaseAssetIdentity(
            asset.Version.ToString(),
            asset.FileName,
            asset.Size,
            asset.SHA256);
        return await FetchAndVerifyDescriptorAsync(
            asset,
            identity,
            cancellationToken);
    }

    private async Task<VerifiedReleaseDescriptor> FetchAndVerifyDescriptorAsync(
        VelopackAsset asset,
        ReleaseAssetIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(identity);
        var version = asset.Version.ToString();
        var tag = $"v{version}";
        var descriptorUrl = new Uri(
            $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/" +
            ReleaseDescriptorPolicy.DescriptorFileName);
        using var response = await SendManifestRequestAsync(
            descriptorUrl,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ReleaseTrustException(
                "The signed release descriptor could not be retrieved from GitHub.");
        }

        if (response.Content.Headers.ContentLength is <= 0 or
            > ReleaseDescriptorPolicy.MaximumDescriptorBytes)
        {
            throw new ReleaseTrustException(
                "GitHub returned release details with an invalid size.");
        }

        var json = await ReadBoundedUtf8Async(response, cancellationToken);
        return ReleaseDescriptorPolicy.Verify(
            json,
            identity,
            ReadEmbeddedPublicKey());
    }

    private async Task<HttpResponseMessage> SendManifestRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirect = 0; redirect <= MaximumManifestRedirects; redirect++)
        {
            if (!IsAllowedManifestUri(currentUri, initialUri))
            {
                throw new ReleaseTrustException(
                    "GitHub redirected the release manifest to an untrusted address.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
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
            if (location is null || redirect == MaximumManifestRedirects)
            {
                throw new ReleaseTrustException(
                    "GitHub returned an invalid release-manifest redirect.");
            }

            currentUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);
        }

        throw new ReleaseTrustException(
            "GitHub returned too many release-manifest redirects.");
    }

    internal static bool IsAllowedManifestUri(Uri value, Uri initialUri)
    {
        if (!value.IsAbsoluteUri ||
            value.Scheme != Uri.UriSchemeHttps ||
            !value.IsDefaultPort ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            return false;
        }

        if (value.Equals(initialUri))
            return true;

        return value.Host.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase) ||
            value.Host.Equals(
                "objects.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadBoundedUtf8Async(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > ReleaseDescriptorPolicy.MaximumDescriptorBytes)
                throw new ReleaseTrustException("The release details exceeded the size limit.");
            output.Write(buffer, 0, read);
        }

        try
        {
            return StrictUtf8.GetString(output.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseTrustException(
                "The release details were not valid UTF-8.",
                exception);
        }
    }

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = typeof(SessionDockUpdateService).Assembly
            .GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new ReleaseTrustException(
                "The built-in SessionDock release key is unavailable.");
        using var reader = new StreamReader(stream, StrictUtf8);
        return reader.ReadToEnd();
    }

    private async Task VerifyPreparedPackageAsync(
        VelopackAsset asset,
        VerifiedReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
        var packagePath = LocatePackage(
            VelopackLocator.Current.PackagesDir,
            asset.FileName);

        var descriptor = release.Descriptor;
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (packageStream.Length != descriptor.PackageSize)
            throw new ReleaseTrustException("The downloaded package size changed.");

        var actualHash = await SHA256.HashDataAsync(
            packageStream,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(descriptor.PackageSha256)))
        {
            throw new ReleaseTrustException(
                "The downloaded package failed its signed SHA-256 check.");
        }

        packageStream.Position = 0;
        using var archive = new ZipArchive(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        var useCurrentLayout = ReleaseDescriptorPolicy.IsCurrentIdentity(
            release.Descriptor);
        ReleasePackagePolicy.ValidateEntries(
            archive.Entries.Select(entry =>
                new ReleasePackageEntryIdentity(
                    entry.FullName,
                    entry.Length,
                    entry.CompressedLength,
                    entry.ExternalAttributes)),
            useCurrentLayout);
        ValidatePackageMetadata(archive, release, useCurrentLayout);

        var verificationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-UpdateVerify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verificationDirectory);
        try
        {
            foreach (var entryName in ReleasePackagePolicy.GetExecutableEntryNames(
                         useCurrentLayout))
            {
                var entry = archive.GetEntry(entryName) ??
                    throw new ReleaseTrustException(
                        "The downloaded package is missing an executable payload.");
                var verificationPath = Path.Combine(
                    verificationDirectory,
                    Path.GetFileName(entryName));
                await using (var input = entry.Open())
                await using (var output = new FileStream(
                    verificationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                var mainExecutable = useCurrentLayout
                    ? "lib/app/SessionDock.exe"
                    : "lib/app/RobloxOne.exe";
                if (entryName.Equals(mainExecutable, StringComparison.Ordinal))
                {
                    var fileVersion = FileVersionInfo.GetVersionInfo(
                        verificationPath).FileVersion;
                    if (!Version.TryParse(fileVersion, out var executableVersion) ||
                        executableVersion.Major != release.Version.Major ||
                        executableVersion.Minor != release.Version.Minor ||
                        executableVersion.Build != release.Version.Build)
                    {
                        throw new ReleaseTrustException(
                            "The downloaded executable version does not match the signed release.");
                    }
                }

                ValidatePortableExecutable(verificationPath);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(verificationDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(
                    $"Update verification cleanup failed: {exception.GetType().Name}.");
            }
        }
    }

    internal static async Task<ReleaseAssetIdentity> CreateLocalPackageIdentityAsync(
        VelopackAsset asset,
        string? packagesDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var packagePath = LocatePackage(packagesDirectory, asset.FileName);
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(
            packageStream,
            cancellationToken);
        return new ReleaseAssetIdentity(
            asset.Version.ToString(),
            Path.GetFileName(packagePath),
            packageStream.Length,
            Convert.ToHexString(actualHash));
    }

    internal static async Task<FileStream> OpenVerifiedPackageLeaseAsync(
        VelopackAsset asset,
        VerifiedReleaseDescriptor release,
        string? packagesDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(release);
        var descriptor = release.Descriptor;
        if (!asset.Version.ToString().Equals(
                descriptor.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                asset.FileName,
                descriptor.PackageFile,
                StringComparison.Ordinal))
        {
            throw new ReleaseTrustException(
                "The downloaded package identity changed before installation.");
        }

        var packagePath = LocatePackage(packagesDirectory, asset.FileName);
        var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (packageStream.Length != descriptor.PackageSize)
                throw new ReleaseTrustException("The downloaded package size changed.");

            var actualHash = await SHA256.HashDataAsync(
                packageStream,
                cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    Convert.FromHexString(descriptor.PackageSha256)))
            {
                throw new ReleaseTrustException(
                    "The downloaded package changed before installation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return packageStream;
        }
        catch
        {
            await packageStream.DisposeAsync();
            throw;
        }
    }

    private static string LocatePackage(
        string? packagesDirectory,
        string fileName)
    {
        if (string.IsNullOrWhiteSpace(packagesDirectory))
        {
            throw new ReleaseTrustException(
                "The installed update package directory is unavailable.");
        }

        try
        {
            var packagesRoot = Path.GetFullPath(packagesDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var packagePath = Path.GetFullPath(
                Path.Combine(packagesDirectory, fileName));
            if (!packagePath.StartsWith(
                    packagesRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(packagePath).Equals(
                    fileName,
                    StringComparison.Ordinal) ||
                !File.Exists(packagePath) ||
                (File.GetAttributes(packagePath) &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new ReleaseTrustException(
                    "The downloaded update package could not be located safely.");
            }

            return packagePath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            throw new ReleaseTrustException(
                "The downloaded update package could not be located safely.",
                exception);
        }
    }

    private static void ValidatePortableExecutable(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length ||
            header[0] != (byte)'M' ||
            header[1] != (byte)'Z')
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }

        var peOffset = BitConverter.ToInt32(header[60..64]);
        if (peOffset < header.Length || peOffset > stream.Length - 4)
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length ||
            signature[0] != (byte)'P' ||
            signature[1] != (byte)'E' ||
            signature[2] != 0 ||
            signature[3] != 0)
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }
    }

    internal static void ValidatePackageMetadata(
        ZipArchive archive,
        VerifiedReleaseDescriptor release,
        bool useCurrentLayout)
    {
        var nuspecBytes = ReadArchiveEntry(
            archive,
            useCurrentLayout ? "SessionDockApp.nuspec" : "RobloxOne.nuspec");
        var versionBytes = ReadArchiveEntry(archive, "lib/app/sq.version");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(nuspecBytes),
                SHA256.HashData(versionBytes)))
        {
            throw new ReleaseTrustException(
                "The downloaded package contains inconsistent application metadata.");
        }

        XDocument document;
        try
        {
            using var input = new MemoryStream(nuspecBytes, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 256 * 1024,
                XmlResolver = null
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata is malformed.",
                exception);
        }

        var root = document.Root;
        var metadataElements = root?.Elements().Where(element =>
            element.Name.LocalName.Equals(
                "metadata",
                StringComparison.Ordinal)).ToArray() ?? [];
        var metadata = metadataElements.Length == 1 ? metadataElements[0] : null;
        if (root is null ||
            !root.Name.LocalName.Equals("package", StringComparison.Ordinal) ||
            !IsAllowedNuspecNamespace(root.Name.NamespaceName) ||
            metadata is null ||
            metadata.Name.Namespace != root.Name.Namespace)
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata is incomplete.");
        }

        var elements = metadata.Elements().ToArray();
        if (elements.Length > MaximumNuspecMetadataElements ||
            document.Descendants().Any(element =>
                element.Name.Namespace != root.Name.Namespace))
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata has an invalid namespace or too many fields.");
        }

        var requiredValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = useCurrentLayout
                ? ReleaseDescriptorPolicy.VelopackPackageId
                : ReleaseDescriptorPolicy.LegacyVelopackPackageId,
            ["version"] = release.Descriptor.Version,
            ["channel"] = useCurrentLayout
                ? ReleaseDescriptorPolicy.Channel
                : ReleaseDescriptorPolicy.LegacyChannel,
            ["mainExe"] = useCurrentLayout ? "SessionDock.exe" : "RobloxOne.exe",
            ["os"] = "win",
            ["rid"] = "win-x64",
            ["machineArchitecture"] = "x64"
        };
        foreach (var required in requiredValues)
        {
            var matches = elements.Where(element =>
                element.Name.LocalName.Equals(
                    required.Key,
                    StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 ||
                matches[0].HasElements ||
                !matches[0].Value.Equals(required.Value, StringComparison.Ordinal))
            {
                throw new ReleaseTrustException(
                    "The downloaded package metadata does not match the signed release.");
            }
        }

        var releaseNotesElements = elements.Where(element =>
            element.Name.LocalName.Equals(
                "releaseNotes",
                StringComparison.Ordinal)).ToArray();
        if (releaseNotesElements.Length > 1 ||
            (releaseNotesElements.Length == 1 && releaseNotesElements[0].HasElements))
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata contains duplicate or malformed release notes.");
        }

        if (releaseNotesElements.Length == 1)
        {
            var releaseNotes = releaseNotesElements[0]
                .Value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
            if (!releaseNotes.Equals(
                    release.Descriptor.ReleaseNotes,
                    StringComparison.Ordinal))
            {
                throw new ReleaseTrustException(
                    "The downloaded package release notes do not match the signed release.");
            }
        }

        var releaseNotesHtmlElements = elements.Where(element =>
            element.Name.LocalName.Equals(
                "releaseNotesHtml",
                StringComparison.Ordinal)).ToArray();
        if (releaseNotesHtmlElements.Length > 1 ||
            (releaseNotesHtmlElements.Length == 1 &&
             releaseNotesHtmlElements[0].HasElements))
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata contains duplicate or malformed rendered release notes.");
        }

        if (releaseNotesHtmlElements.Length == 1)
        {
            var releaseNotesHtml = releaseNotesHtmlElements[0].Value;
            if (releaseNotesHtml.Length >
                    ReleaseDescriptorPolicy.MaximumReleaseNotesLength * 2 ||
                releaseNotesHtml.Contains(
                    "<script",
                    StringComparison.OrdinalIgnoreCase) ||
                releaseNotesHtml.Contains(
                    "javascript:",
                    StringComparison.OrdinalIgnoreCase) ||
                releaseNotesHtml.Any(character =>
                    char.IsControl(character) &&
                    character is not ('\r' or '\n' or '\t')))
            {
                throw new ReleaseTrustException(
                    "The downloaded package contains unsafe rendered release notes.");
            }
        }
    }

    private static bool IsAllowedNuspecNamespace(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.Host.Equals(
                "schemas.microsoft.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith(
                   "/packaging/",
                   StringComparison.Ordinal) &&
               uri.AbsolutePath.EndsWith(
                   "/nuspec.xsd",
                   StringComparison.Ordinal);
    }

    private static byte[] ReadArchiveEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ??
            throw new ReleaseTrustException(
                "The downloaded package is missing required metadata.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        input.CopyTo(output);
        return output.ToArray();
    }
}

internal sealed record AvailableSessionDockUpdate(
    UpdateInfo UpdateInfo,
    VerifiedReleaseDescriptor Release);

internal sealed class ProcessLifetimePackageLease : IDisposable
{
    private FileStream? _lease;

    public void Schedule(FileStream lease, Action scheduleApply)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(scheduleApply);
        if (Interlocked.CompareExchange(
                ref _lease,
                lease,
                comparand: null) is not null)
        {
            lease.Dispose();
            throw new InvalidOperationException(
                "An update is already scheduled for installation.");
        }

        try
        {
            scheduleApply();
        }
        catch
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _lease,
                        value: null,
                        comparand: lease),
                    lease))
            {
                lease.Dispose();
            }

            throw;
        }
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _lease, value: null)?.Dispose();
}
