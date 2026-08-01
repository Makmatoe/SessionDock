using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeReleaseInstaller : IDisposable
{
    internal const string PinnedVersion =
        HandleScopeInstalledRuntimeVerifier.SupportedVersion;
    internal const string PinnedTag = "v0.1.4";
    internal const long PinnedPackageSize = 100_841_616;
    internal const long PinnedChecksumsSize = 198;
    internal const string PinnedPackageSha256 =
        "b06bfe850b8334b6be86d9037ea43e7210845420e7473cf7c17d030277c06622";
    internal const string PinnedChecksumsSha256 =
        "860bcd77e7cd83693a87b15a1f464908e6dbe43195b0ed0572684e009b1e6ccf";

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
    private readonly Func<
        ProcessStartInfo,
        CancellationToken,
        Task<HandleScopeInstallerProcessResult>>
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
        Func<
            ProcessStartInfo,
            CancellationToken,
            Task<HandleScopeInstallerProcessResult>> runProcess,
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
            var verificationResult = await _runProcess(
                verificationStartInfo,
                cancellationToken);
            if (verificationResult.ExitCode != 0)
            {
                throw new HandleScopeInstallException(
                    HandleScopeInstallFailureKind.Installer,
                    AppendInstallerReason(
                        "HandleScope rejected its downloaded release inventory. Nothing was installed.",
                        verificationResult.FailureReason));
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
            var installResult = await _runProcess(
                installStartInfo,
                CancellationToken.None);
            if (installResult.ExitCode != 0)
            {
                throw new HandleScopeInstallException(
                    HandleScopeInstallFailureKind.Installer,
                    AppendInstallerReason(
                        "HandleScope's per-user installer did not complete. Its atomic file step preserves the prior install on replacement failure, but a later start or autostart step may have failed after the new files were installed. Refresh the status before retrying.",
                        installResult.FailureReason));
            }

            return new HandleScopeReleaseInstallResult(release.Version);
        }
        catch (HandleScopeInstallException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.ReleaseDownload,
                "The HandleScope download timed out before it could be verified.",
                exception);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
                UnauthorizedAccessException or InvalidDataException or
                CryptographicException or ArgumentException or
                InvalidOperationException or NotSupportedException or
                Win32Exception)
        {
            throw new HandleScopeInstallException(
                ClassifyUnexpectedFailure(exception),
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
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
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
            HasInvalidDeclaredLength(
                response.Content.Headers.ContentLength,
                asset.Size,
                maximumBytes))
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.ReleaseDownload,
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
            HasInvalidDeclaredLength(
                response.Content.Headers.ContentLength,
                release.Package.Size,
                HandleScopeReleasePolicy.MaximumPackageBytes))
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.ReleaseDownload,
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

    private static bool HasInvalidDeclaredLength(
        long? declaredLength,
        long expectedLength,
        long maximumLength) =>
        expectedLength <= 0 ||
        maximumLength <= 0 ||
        expectedLength > maximumLength ||
        declaredLength is { } length &&
        (length <= 0 || length != expectedLength || length > maximumLength);

    private static string AppendInstallerReason(
        string message,
        string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? message
            : $"{message} Installer detail: {reason}";

    private static HandleScopeInstallFailureKind ClassifyUnexpectedFailure(
        Exception exception) => exception switch
        {
            HttpRequestException => HandleScopeInstallFailureKind.ReleaseDownload,
            InvalidDataException or CryptographicException =>
                HandleScopeInstallFailureKind.ReleaseIntegrity,
            Win32Exception or IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                NotSupportedException =>
                HandleScopeInstallFailureKind.LocalEnvironment,
            _ => HandleScopeInstallFailureKind.LocalEnvironment
        };

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
                    HandleScopeInstallFailureKind.LocalEnvironment,
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

    internal static async Task<HandleScopeInstallerProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new HandleScopeInstallException(
                HandleScopeInstallFailureKind.LocalEnvironment,
                "Windows PowerShell could not start the HandleScope installer.");
        }
        var standardErrorTask = ReadBoundedProcessOutputAsync(
            process.StandardError,
            cancellationToken);
        var standardOutputTask = ReadBoundedProcessOutputAsync(
            process.StandardOutput,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            await ObserveCanceledOutputAsync(
                standardErrorTask,
                standardOutputTask);
            throw;
        }
        var standardError = await standardErrorTask;
        var standardOutput = await standardOutputTask;
        var reason = ExtractProcessFailureReason(standardError) ??
            ExtractProcessFailureReason(standardOutput);
        return new(process.ExitCode, reason);
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
                NotSupportedException)
        {
            Trace.WriteLine(
                $"Canceled HandleScope installer cleanup failed: {exception.GetType().Name}.");
        }
    }

    private static async Task ObserveCanceledOutputAsync(
        Task<string> standardErrorTask,
        Task<string> standardOutputTask)
    {
        try
        {
            await Task.WhenAll(standardErrorTask, standardOutputTask);
        }
        catch (OperationCanceledException)
        {
            // Both redirected readers use the same canceled operation token.
        }
    }

    private static async Task<string> ReadBoundedProcessOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int maximumRetainedCharacters = 16 * 1024;
        var retained = new StringBuilder(maximumRetainedCharacters);
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return retained.ToString();
            var remaining = maximumRetainedCharacters - retained.Length;
            if (remaining > 0)
                retained.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    internal static string? ExtractProcessFailureReason(string output)
    {
        var trimmed = output.TrimStart();
        if (!trimmed.StartsWith("#< CLIXML", StringComparison.Ordinal))
            return FirstUsefulOutputLine(output);

        var xmlStart = trimmed.IndexOf('<', "#< CLIXML".Length);
        if (xmlStart < 0)
            return null;
        try
        {
            using var textReader = new StringReader(trimmed[xmlStart..]);
            using var xmlReader = XmlReader.Create(
                textReader,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 16 * 1024,
                    XmlResolver = null
                });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            var error = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "S" &&
                    string.Equals(
                        (string?)element.Attribute("S"),
                        "Error",
                        StringComparison.Ordinal));
            return error is null
                ? null
                : FirstUsefulOutputLine(
                    DecodePowerShellCliXmlEscapes(error.Value));
        }
        catch (Exception exception) when (
            exception is XmlException or InvalidOperationException or
                ArgumentException)
        {
            Trace.WriteLine(
                $"HandleScope installer CLIXML could not be parsed: {exception.GetType().Name}.");
            return null;
        }
    }

    private static string DecodePowerShellCliXmlEscapes(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (index + 6 < value.Length &&
                value[index] == '_' &&
                value[index + 1] == 'x' &&
                value[index + 6] == '_' &&
                int.TryParse(
                    value.AsSpan(index + 2, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var codePoint))
            {
                decoded.Append((char)codePoint);
                index += 6;
                continue;
            }
            decoded.Append(value[index]);
        }
        return decoded.ToString();
    }

    private static string? FirstUsefulOutputLine(string output)
    {
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0 ||
                value.StartsWith("At ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith('+'))
            {
                continue;
            }
            return value.Length <= 512 ? value : value[..512];
        }
        return null;
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

internal sealed record HandleScopeInstallerProcessResult(
    int ExitCode,
    string? FailureReason);

internal sealed record HandleScopeReleaseInstallResult(string Version);
