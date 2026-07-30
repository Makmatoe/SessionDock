using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeReleaseInstaller : IDisposable
{
    internal const string PinnedVersion =
        HandleScopeInstalledRuntimeVerifier.SupportedVersion;
    internal const string PinnedTag = "v0.1.3";
    internal const long PinnedPackageSize = 100_839_933;
    internal const long PinnedChecksumsSize = 198;
    internal const string PinnedPackageSha256 =
        "64934117a3dc7b9aa52a0d3ef34df9627e898e045895a3bd3817f46da0531aee";
    internal const string PinnedChecksumsSha256 =
        "f6dec9303d3520d69f4b19123818aee8e502b2379c5e225035eb4d65d5549631";

    internal static HandleScopeReleaseIdentity CreatePinnedRelease() => new(
        PinnedVersion,
        PinnedTag,
        new HandleScopeReleaseAsset(
            $"HandleScope-{PinnedVersion}-win-x64.zip",
            PinnedPackageSize,
            Convert.FromHexString(PinnedPackageSha256),
            new Uri(
                $"https://github.com/Makmatoe/HandleScope/releases/download/{PinnedTag}/HandleScope-{PinnedVersion}-win-x64.zip")),
        new HandleScopeReleaseAsset(
            "SHA256SUMS.txt",
            PinnedChecksumsSize,
            Convert.FromHexString(PinnedChecksumsSha256),
            new Uri(
                $"https://github.com/Makmatoe/HandleScope/releases/download/{PinnedTag}/SHA256SUMS.txt")));

    private const int MaximumAssetRedirects = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);
    private readonly string _temporaryRoot;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>>
        _runProcess;
    private readonly HandleScopeReleaseIdentity _release;
    private readonly HttpClient _client;
    private bool _disposed;

    internal HandleScopeReleaseInstaller()
        : this(
            CreateDownloadHandler(),
            Path.Combine(Path.GetTempPath(), "SessionDock.HandleScope"),
            RunProcessAsync,
            release: null)
    {
    }

    internal HandleScopeReleaseInstaller(
        HttpMessageHandler handler,
        string temporaryRoot,
        Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess,
        HandleScopeReleaseIdentity? release = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentNullException.ThrowIfNull(runProcess);
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _runProcess = runProcess;
        _release = release ?? CreatePinnedRelease();
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout
        };
        var applicationVersion = typeof(HandleScopeReleaseInstaller)
            .Assembly
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SessionDock/{applicationVersion}");
    }

    internal async Task<HandleScopeReleaseInstallResult> InstallPinnedAsync(
        IProgress<HandleScopeReleaseInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        progress?.Report(new(
            HandleScopeReleaseInstallStage.CheckingRelease,
            Version: null,
            Percentage: null));

        string? operationRoot = null;
        try
        {
            var release = _release;
            var checksumBytes = await DownloadSmallAssetAsync(
                release.Checksums,
                HandleScopeReleasePolicy.MaximumChecksumBytes,
                cancellationToken);
            VerifyHash(
                checksumBytes,
                release.Checksums.Sha256,
                "The HandleScope checksum download failed its GitHub SHA-256 check.");
            HandleScopeReleasePolicy.VerifyChecksumManifest(
                checksumBytes,
                release);

            cancellationToken.ThrowIfCancellationRequested();
            operationRoot = CreateOperationDirectory();
            var archivePath = Path.Combine(
                operationRoot,
                release.Package.Name);
            progress?.Report(new(
                HandleScopeReleaseInstallStage.DownloadingPackage,
                release.Version,
                0));
            await DownloadPackageAsync(
                release,
                archivePath,
                progress,
                cancellationToken);

            progress?.Report(new(
                HandleScopeReleaseInstallStage.VerifyingPackage,
                release.Version,
                Percentage: null));
            var extractionRoot = Path.Combine(operationRoot, "extracted");
            var installerPath = await HandleScopeReleasePolicy.ExtractAndVerifyAsync(
                archivePath,
                extractionRoot,
                release.Version,
                cancellationToken);

            var verificationStartInfo = CreateInstallerStartInfo(
                installerPath,
                verifyOnly: true);
            var verificationExitCode = await _runProcess(
                verificationStartInfo,
                cancellationToken);
            if (verificationExitCode != 0)
            {
                throw new HandleScopeInstallException(
                    "HandleScope rejected its downloaded release inventory. Nothing was installed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(
                HandleScopeReleaseInstallStage.InstallingPackage,
                release.Version,
                Percentage: null));
            var installStartInfo = CreateInstallerStartInfo(
                installerPath,
                verifyOnly: false);

            // Once the reviewed installer starts, let its atomic replacement
            // finish instead of interrupting it during a file swap.
            var installExitCode = await _runProcess(
                installStartInfo,
                CancellationToken.None);
            if (installExitCode != 0)
            {
                throw new HandleScopeInstallException(
                    "HandleScope's per-user installer did not complete. Its atomic file step preserves the prior install on replacement failure, but a later start or autostart step may have failed after the new files were installed. Refresh the status before retrying.");
            }

            return new HandleScopeReleaseInstallResult(release.Version);
        }
        catch (HandleScopeInstallException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
                UnauthorizedAccessException or InvalidDataException or
                CryptographicException or ArgumentException or
                InvalidOperationException or NotSupportedException or
                Win32Exception)
        {
            throw new HandleScopeInstallException(
                $"HandleScope {PinnedVersion} could not be installed safely. No unverified package was run.",
                exception);
        }
        finally
        {
            if (operationRoot is not null)
                TryDeleteOperationDirectory(operationRoot);
        }
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
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 16,
        PreAuthenticate = false,
        UseCookies = false,
        ActivityHeadersPropagator = null
    };

    internal static ProcessStartInfo CreateInstallerStartInfo(
        string installerPath,
        bool verifyOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var workingDirectory = Path.GetDirectoryName(fullInstallerPath)
            ?? throw new ArgumentException(
                "The HandleScope installer path has no parent directory.",
                nameof(installerPath));
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(fullInstallerPath);
        if (verifyOnly)
        {
            startInfo.ArgumentList.Add("-VerifyOnly");
        }
        else
        {
            startInfo.ArgumentList.Add("-StartNow");
            startInfo.ArgumentList.Add("-EnableAutostart");
        }
        LocalApiLaunchHook.RemoveConfigurationFromChildEnvironment(startInfo);
        return startInfo;
    }

    private async Task<byte[]> DownloadSmallAssetAsync(
        HandleScopeReleaseAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetRequestAsync(
            asset.DownloadUri,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is <= 0 ||
            response.Content.Headers.ContentLength > maximumBytes ||
            response.Content.Headers.ContentLength != asset.Size)
        {
            throw new HandleScopeInstallException(
                "The HandleScope checksum file could not be downloaded from GitHub.");
        }

        var bytes = await ReadBoundedAsync(
            response.Content,
            maximumBytes,
            cancellationToken);
        if (bytes.LongLength != asset.Size)
        {
            throw new HandleScopeInstallException(
                "The HandleScope checksum download changed size.");
        }
        return bytes;
    }

    private async Task DownloadPackageAsync(
        HandleScopeReleaseIdentity release,
        string targetPath,
        IProgress<HandleScopeReleaseInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetRequestAsync(
            release.Package.DownloadUri,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is <= 0 ||
            response.Content.Headers.ContentLength != release.Package.Size ||
            response.Content.Headers.ContentLength >
                HandleScopeReleasePolicy.MaximumPackageBytes)
        {
            throw new HandleScopeInstallException(
                "The HandleScope package could not be downloaded from GitHub.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var output = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var lastPercentage = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            downloaded = checked(downloaded + read);
            if (downloaded > release.Package.Size ||
                downloaded > HandleScopeReleasePolicy.MaximumPackageBytes)
            {
                throw new HandleScopeInstallException(
                    "The HandleScope package exceeded its published size.");
            }
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var percentage = (int)(downloaded * 100 / release.Package.Size);
            if (percentage > lastPercentage)
            {
                lastPercentage = percentage;
                progress?.Report(new(
                    HandleScopeReleaseInstallStage.DownloadingPackage,
                    release.Version,
                    percentage));
            }
        }
        await output.FlushAsync(cancellationToken);

        if (downloaded != release.Package.Size ||
            !CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                release.Package.Sha256))
        {
            throw new HandleScopeInstallException(
                "The HandleScope package failed its published SHA-256 check.");
        }
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirect = 0; redirect <= MaximumAssetRedirects; redirect++)
        {
            if (!HandleScopeReleasePolicy.IsAllowedAssetUri(
                    currentUri,
                    initialUri))
            {
                throw new HandleScopeInstallException(
                    "GitHub redirected the HandleScope download to an untrusted address.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
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
            if (location is null || redirect == MaximumAssetRedirects)
            {
                throw new HandleScopeInstallException(
                    "GitHub returned an invalid HandleScope download redirect.");
            }
            currentUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);
        }

        throw new HandleScopeInstallException(
            "GitHub returned too many HandleScope download redirects.");
    }

    private string CreateOperationDirectory()
    {
        Directory.CreateDirectory(_temporaryRoot);
        EnsurePathHasNoReparsePoints(_temporaryRoot);
        var operationRoot = Path.Combine(
            _temporaryRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        EnsurePathHasNoReparsePoints(operationRoot);
        return operationRoot;
    }

    private void TryDeleteOperationDirectory(string operationRoot)
    {
        try
        {
            var normalizedTemporaryRoot = _temporaryRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedOperationRoot = Path.GetFullPath(operationRoot);
            if (!normalizedOperationRoot.StartsWith(
                    normalizedTemporaryRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedOperationRoot.Equals(
                    _temporaryRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(normalizedOperationRoot))
            {
                EnsureTreeHasNoReparsePoints(normalizedOperationRoot);
                Directory.Delete(normalizedOperationRoot, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            Trace.WriteLine(
                $"HandleScope installer cleanup failed: {exception.GetType().Name}.");
        }
    }

    private static void EnsurePathHasNoReparsePoints(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path));
             directory is not null;
             directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new HandleScopeInstallException(
                    "The temporary HandleScope download path is linked and cannot be used safely.");
            }
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The temporary HandleScope tree contains a linked item.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
            {
                throw new HandleScopeInstallException(
                    "A HandleScope release response exceeded its size limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static void VerifyHash(
        ReadOnlySpan<byte> contents,
        ReadOnlySpan<byte> expectedHash,
        string errorMessage)
    {
        var actualHash = SHA256.HashData(contents);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new HandleScopeInstallException(errorMessage);
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new HandleScopeInstallException(
                "Windows PowerShell could not start the HandleScope installer.");
        }
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}

internal enum HandleScopeReleaseInstallStage
{
    CheckingRelease,
    DownloadingPackage,
    VerifyingPackage,
    InstallingPackage
}

internal sealed record HandleScopeReleaseInstallProgress(
    HandleScopeReleaseInstallStage Stage,
    string? Version,
    int? Percentage);

internal sealed record HandleScopeReleaseInstallResult(string Version);
