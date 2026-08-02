using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionDock.SystemProcesses;

internal static partial class HandleScopeReleasePolicy
{
    internal const int MaximumMetadataBytes = 1024 * 1024;
    internal const int MaximumChecksumBytes = 64 * 1024;
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal const long MaximumExtractedBytes = 1024L * 1024 * 1024;
    internal const int MaximumArchiveEntries = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly char[] InvalidFileNameCharacters =
        Path.GetInvalidFileNameChars();

    internal static void VerifyChecksumManifest(
        ReadOnlySpan<byte> contents,
        HandleScopeReleaseIdentity release)
    {
        ArgumentNullException.ThrowIfNull(release);
        string text;
        try
        {
            text = StrictUtf8.GetString(contents);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope checksum file is not valid UTF-8.",
                exception);
        }

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            var match = ChecksumLinePattern().Match(line);
            if (!match.Success || entries.Count >= 32)
            {
                throw new HandleScopeInstallException(
                    "The HandleScope checksum file is malformed.");
            }

            var name = match.Groups["name"].Value;
            if (!entries.TryAdd(
                    name,
                    Convert.FromHexString(match.Groups["hash"].Value)))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope checksum file contains duplicate entries.");
            }
        }

        if (!entries.TryGetValue(release.Package.Name, out var packageHash) ||
            !CryptographicOperations.FixedTimeEquals(
                packageHash,
                release.Package.Sha256))
        {
            throw new HandleScopeInstallException(
                "The HandleScope package does not match its published checksum.");
        }
    }

    internal static bool IsAllowedAssetUri(Uri value, Uri initialUri)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(initialUri);
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

    internal static async Task<string> ExtractAndVerifyAsync(
        string archivePath,
        string extractionRoot,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Directory.CreateDirectory(extractionRoot);
        var normalizedExtractionRoot = Path.GetFullPath(extractionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var bundleName = $"HandleScope-{version}-win-x64";
        var bundlePrefix = bundleName + "/";

        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        if (archive.Entries.Count is <= 0 or > MaximumArchiveEntries)
            throw InvalidArchive();

        var plans = new List<ArchiveEntryPlan>(archive.Entries.Count);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = CreateEntryPlan(
                entry,
                normalizedExtractionRoot,
                bundlePrefix);
            if (!destinations.Add(plan.DestinationPath))
                throw InvalidArchive();
            if (!plan.IsDirectory)
            {
                totalLength = checked(totalLength + entry.Length);
                if (totalLength > MaximumExtractedBytes)
                    throw InvalidArchive();
            }
            plans.Add(plan);
        }

        var bundleRoot = Path.Combine(extractionRoot, bundleName);
        var manifestPath = Path.Combine(bundleRoot, "CONTENTS.sha256");
        var installerPath = Path.Combine(
            bundleRoot,
            "api",
            "Install-HandleScopeApi.ps1");
        var executablePath = Path.Combine(
            bundleRoot,
            "api",
            "HandleScope.Api.exe");
        if (!destinations.Contains(Path.GetFullPath(manifestPath)) ||
            !destinations.Contains(Path.GetFullPath(installerPath)) ||
            !destinations.Contains(Path.GetFullPath(executablePath)))
        {
            throw InvalidArchive();
        }

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.IsDirectory)
            {
                Directory.CreateDirectory(plan.DestinationPath);
                continue;
            }

            var parent = Path.GetDirectoryName(plan.DestinationPath)
                ?? throw InvalidArchive();
            Directory.CreateDirectory(parent);
            await using var input = plan.Entry.Open();
            await using var output = new FileStream(
                plan.DestinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous);
            await CopyExactAsync(
                input,
                output,
                plan.Entry.Length,
                cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        await VerifyExtractedManifestAsync(
            bundleRoot,
            manifestPath,
            cancellationToken);
        return installerPath;
    }

    private static ArchiveEntryPlan CreateEntryPlan(
        ZipArchiveEntry entry,
        string extractionRoot,
        string bundlePrefix)
    {
        var name = entry.FullName;
        var isDirectory = name.EndsWith('/');
        if (string.IsNullOrEmpty(name) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.StartsWith('/') ||
            name.Contains("//", StringComparison.Ordinal) ||
            !name.StartsWith(bundlePrefix, StringComparison.Ordinal) ||
            entry.Length < 0 ||
            entry.CompressedLength < 0 ||
            entry.Length > MaximumPackageBytes ||
            (isDirectory && entry.Length != 0) ||
            IsLinkedArchiveEntry(entry))
        {
            throw InvalidArchive();
        }

        var trimmedName = isDirectory ? name[..^1] : name;
        var segments = trimmedName.Split('/');
        if (segments.Length < (isDirectory ? 1 : 2) ||
            segments.Any(segment => !IsSafeWindowsPathSegment(segment)))
        {
            throw InvalidArchive();
        }

        var relativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var destination = Path.GetFullPath(Path.Combine(
            extractionRoot,
            relativePath));
        if (!destination.StartsWith(
                extractionRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidArchive();
        }

        return new ArchiveEntryPlan(entry, destination, isDirectory);
    }

    private static bool IsLinkedArchiveEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixType == 0xA000 ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsSafeWindowsPathSegment(string value)
    {
        if (value.Length is <= 0 or > 255 ||
            value is "." or ".." ||
            value.EndsWith(' ') ||
            value.EndsWith('.') ||
            value.IndexOfAny(InvalidFileNameCharacters) >= 0 ||
            value.Any(char.IsControl))
        {
            return false;
        }

        var stem = value.Split('.')[0];
        return !ReservedWindowsNamePattern().IsMatch(stem);
    }

    private static async Task CopyExactAsync(
        Stream input,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            copied = checked(copied + read);
            if (copied > expectedLength || copied > MaximumPackageBytes)
                throw InvalidArchive();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (copied != expectedLength)
            throw InvalidArchive();
    }

    private static async Task VerifyExtractedManifestAsync(
        string bundleRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists ||
            manifestInfo.Length is <= 0 or > MaximumMetadataBytes)
        {
            throw InvalidArchive();
        }

        var manifestBytes = await File.ReadAllBytesAsync(
            manifestPath,
            cancellationToken);
        string manifestText;
        try
        {
            manifestText = StrictUtf8.GetString(manifestBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope bundle manifest is not valid UTF-8.",
                exception);
        }

        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var rawLine in manifestText.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            var match = BundleManifestLinePattern().Match(line);
            if (!match.Success || expected.Count >= MaximumArchiveEntries)
                throw InvalidArchive();
            var path = match.Groups["path"].Value;
            if (!IsSafeManifestPath(path) ||
                !expected.TryAdd(
                    path,
                    Convert.FromHexString(match.Groups["hash"].Value)))
            {
                throw InvalidArchive();
            }
        }

        var actualFiles = Directory.EnumerateFiles(
                bundleRoot,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !path.Equals(
                manifestPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (actualFiles.Length != expected.Count)
            throw InvalidArchive();

        foreach (var path in actualFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(bundleRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!expected.TryGetValue(relativePath, out var expectedHash))
                throw InvalidArchive();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(
                stream,
                cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    expectedHash))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope bundle failed its internal integrity check.");
            }
        }
    }

    private static bool IsSafeManifestPath(string path)
    {
        if (path.Length is <= 0 or > 2048 ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Equals("CONTENTS.sha256", StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(IsSafeWindowsPathSegment);
    }

    private static HandleScopeInstallException InvalidArchive() => new(
        "The HandleScope package contains an unsafe or invalid file layout.");

    [GeneratedRegex(
        @"^(?<hash>[0-9a-f]{64})  (?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,255})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();

    [GeneratedRegex(
        @"^(?<hash>[0-9a-f]{64})  (?<path>[^\\\r\n]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BundleManifestLinePattern();

    [GeneratedRegex(
        @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsNamePattern();

    private sealed record ArchiveEntryPlan(
        ZipArchiveEntry Entry,
        string DestinationPath,
        bool IsDirectory);
}

internal sealed record HandleScopeReleaseIdentity(
    string Version,
    string TagName,
    HandleScopeReleaseAsset Package,
    HandleScopeReleaseAsset Checksums);

internal sealed record HandleScopeReleaseAsset(
    string Name,
    long Size,
    byte[] Sha256,
    Uri DownloadUri);

internal enum HandleScopeInstallFailureKind
{
    ReleaseDownload,
    ReleaseIntegrity,
    LocalEnvironment,
    Installer
}

internal sealed class HandleScopeInstallException : Exception
{
    internal HandleScopeInstallException(string message)
        : this(HandleScopeInstallFailureKind.ReleaseIntegrity, message)
    {
    }

    internal HandleScopeInstallException(string message, Exception innerException)
        : this(
            HandleScopeInstallFailureKind.ReleaseIntegrity,
            message,
            innerException)
    {
    }

    internal HandleScopeInstallException(
        HandleScopeInstallFailureKind failureKind,
        string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    internal HandleScopeInstallException(
        HandleScopeInstallFailureKind failureKind,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    internal HandleScopeInstallFailureKind FailureKind { get; }
}
