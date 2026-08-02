using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

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

        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bundle = await ExtractAndVerifyLockedAsync(
            archiveStream,
            extractionRoot,
            Path.GetDirectoryName(Path.GetFullPath(extractionRoot))
                ?? extractionRoot,
            version,
            cancellationToken);
        return bundle.InstallerPath;
    }

    internal static async Task<HandleScopeVerifiedBundle>
        ExtractAndVerifyLockedAsync(
            FileStream archiveStream,
            string extractionRoot,
            string protectionRoot,
            string version,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!archiveStream.CanRead || !archiveStream.CanSeek)
            throw new ArgumentException(
                "The verified HandleScope archive stream must be readable and seekable.",
                nameof(archiveStream));

        archiveStream.Position = 0;
        Directory.CreateDirectory(extractionRoot);
        var normalizedExtractionRoot = Path.GetFullPath(extractionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedProtectionRoot = Path.GetFullPath(protectionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFullPath(extractionRoot).StartsWith(
                normalizedProtectionRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The HandleScope extraction root is outside its protected operation directory.",
                nameof(extractionRoot));
        }
        var bundleName = $"HandleScope-{version}-win-x64";
        var bundlePrefix = bundleName + "/";

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

        var manifestPlan = plans.SingleOrDefault(plan =>
            plan.DestinationPath.Equals(
                Path.GetFullPath(manifestPath),
                StringComparison.OrdinalIgnoreCase));
        if (manifestPlan is null || manifestPlan.IsDirectory ||
            manifestPlan.Entry.Length is <= 0 or > MaximumMetadataBytes)
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

        var manifestBytes = await ReadArchiveEntryAsync(
            manifestPlan.Entry,
            MaximumMetadataBytes,
            cancellationToken);
        var expected = ParseBundleManifest(manifestBytes);
        var expectedDirectories = CreateExpectedDirectorySet(
            bundleRoot,
            plans);
        return await HandleScopeVerifiedBundle.AcquireAsync(
            normalizedProtectionRoot,
            bundleRoot,
            installerPath,
            manifestPath,
            manifestBytes,
            expected,
            expectedDirectories,
            cancellationToken);
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

    private static async Task<byte[]> ReadArchiveEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await CopyExactAsync(input, output, entry.Length, cancellationToken);
        if (output.Length is <= 0 || output.Length > maximumBytes)
            throw InvalidArchive();
        return output.ToArray();
    }

    private static IReadOnlyDictionary<string, byte[]> ParseBundleManifest(
        ReadOnlySpan<byte> manifestBytes)
    {
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
        if (expected.Count == 0)
            throw InvalidArchive();
        return expected;
    }

    private static IReadOnlySet<string> CreateExpectedDirectorySet(
        string bundleRoot,
        IReadOnlyList<ArchiveEntryPlan> plans)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var directory = plan.IsDirectory
                ? plan.DestinationPath
                : Path.GetDirectoryName(plan.DestinationPath)
                    ?? throw InvalidArchive();
            while (!directory.Equals(bundleRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(bundleRoot, directory)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) ||
                    relative is ".." or ".")
                {
                    throw InvalidArchive();
                }
                expected.Add(relative);
                directory = Path.GetDirectoryName(directory)
                    ?? throw InvalidArchive();
            }
        }
        return expected;
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

internal sealed class HandleScopeVerifiedBundle : IDisposable
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly string _bundleRoot;
    private readonly IReadOnlyDictionary<string, byte[]> _expectedFiles;
    private readonly IReadOnlySet<string> _expectedDirectories;
    private readonly Dictionary<string, FileStream> _fileLocks;
    private readonly Dictionary<string, DirectoryLock> _directoryLocks;
    private bool _disposed;

    private HandleScopeVerifiedBundle(
        string bundleRoot,
        string installerPath,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlySet<string> expectedDirectories,
        Dictionary<string, FileStream> fileLocks,
        Dictionary<string, DirectoryLock> directoryLocks)
    {
        _bundleRoot = bundleRoot;
        InstallerPath = installerPath;
        _expectedFiles = expectedFiles;
        _expectedDirectories = expectedDirectories;
        _fileLocks = fileLocks;
        _directoryLocks = directoryLocks;
    }

    internal string InstallerPath { get; }

    internal static async Task<HandleScopeVerifiedBundle> AcquireAsync(
        string protectionRoot,
        string bundleRoot,
        string installerPath,
        string manifestPath,
        ReadOnlyMemory<byte> trustedManifestBytes,
        IReadOnlyDictionary<string, byte[]> expectedInventory,
        IReadOnlySet<string> expectedDirectories,
        CancellationToken cancellationToken)
    {
        var normalizedProtectionRoot = Path.GetFullPath(protectionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedBundleRoot = Path.GetFullPath(bundleRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedBundleRoot.StartsWith(
                normalizedProtectionRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidBundle();
        }

        var directoryLocks = new Dictionary<string, DirectoryLock>(
            StringComparer.OrdinalIgnoreCase);
        var fileLocks = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            foreach (var directory in CreateDirectoryLockPaths(
                         normalizedProtectionRoot,
                         normalizedBundleRoot,
                         expectedDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directoryLocks.Add(directory, OpenDirectoryLock(directory));
            }

            var expectedFiles = new Dictionary<string, byte[]>(
                StringComparer.Ordinal)
            {
                ["CONTENTS.sha256"] = SHA256.HashData(
                    trustedManifestBytes.Span)
            };
            foreach (var pair in expectedInventory)
                expectedFiles.Add(pair.Key, pair.Value.ToArray());

            foreach (var pair in expectedFiles.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pair.Key == "CONTENTS.sha256"
                    ? Path.GetFullPath(manifestPath)
                    : Path.GetFullPath(Path.Combine(
                        normalizedBundleRoot,
                        pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(
                        normalizedBundleRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals(
                        normalizedBundleRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidBundle();
                }
                fileLocks.Add(
                    pair.Key,
                    OpenReadLock(path));
            }

            var bundle = new HandleScopeVerifiedBundle(
                normalizedBundleRoot,
                Path.GetFullPath(installerPath),
                expectedFiles,
                new HashSet<string>(expectedDirectories, StringComparer.Ordinal),
                fileLocks,
                directoryLocks);
            await bundle.RevalidateForExecutionAsync(cancellationToken);
            return bundle;
        }
        catch
        {
            foreach (var stream in fileLocks.Values)
                stream.Dispose();
            foreach (var directory in directoryLocks.Values)
                directory.Dispose();
            throw;
        }
    }

    internal async Task RevalidateForExecutionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var pair in _directoryLocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularDirectory(pair.Key);
            using var current = OpenDirectoryLock(pair.Key);
            if (current.Identity != pair.Value.Identity)
                throw InvalidBundle();
        }

        var (actualFiles, actualDirectories) = EnumerateExactTree(_bundleRoot);
        if (!actualFiles.SetEquals(_expectedFiles.Keys) ||
            !actualDirectories.SetEquals(_expectedDirectories))
        {
            throw InvalidBundle();
        }

        foreach (var pair in _fileLocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(
                _bundleRoot,
                pair.Key.Replace('/', Path.DirectorySeparatorChar));
            EnsureRegularFile(path);
            await using var current = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (GetIdentity(current.SafeFileHandle) !=
                GetIdentity(pair.Value.SafeFileHandle))
            {
                throw InvalidBundle();
            }

            pair.Value.Position = 0;
            var actualHash = await SHA256.HashDataAsync(
                pair.Value,
                cancellationToken);
            pair.Value.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    _expectedFiles[pair.Key]))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope bundle changed after verification and was not executed.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var stream in _fileLocks.Values)
            stream.Dispose();
        foreach (var directory in _directoryLocks.Values)
            directory.Dispose();
    }

    private static IReadOnlyList<string> CreateDirectoryLockPaths(
        string protectionRoot,
        string bundleRoot,
        IReadOnlySet<string> expectedDirectories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            protectionRoot
        };
        var relativeBundle = Path.GetRelativePath(protectionRoot, bundleRoot);
        if (relativeBundle is ".." ||
            relativeBundle.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw InvalidBundle();
        }

        var current = protectionRoot;
        foreach (var segment in relativeBundle.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            paths.Add(Path.GetFullPath(current));
        }
        foreach (var relative in expectedDirectories)
        {
            paths.Add(Path.GetFullPath(Path.Combine(
                bundleRoot,
                relative.Replace('/', Path.DirectorySeparatorChar))));
        }
        return paths
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static FileStream OpenReadLock(string path)
    {
        EnsureRegularFile(path);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            EnsureRegularFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static DirectoryLock OpenDirectoryLock(string path)
    {
        EnsureRegularDirectory(path);
        var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            EnsureRegularDirectory(path);
            return new DirectoryLock(handle, GetIdentity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static (HashSet<string> Files, HashSet<string> Directories)
        EnumerateExactTree(string bundleRoot)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(bundleRoot);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (++entries > HandleScopeReleasePolicy.MaximumArchiveEntries)
                    throw InvalidBundle();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw InvalidBundle();
                var relative = Path.GetRelativePath(bundleRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Add(relative))
                        throw InvalidBundle();
                    pending.Push(path);
                }
                else if (!files.Add(relative))
                {
                    throw InvalidBundle();
                }
            }
        }
        return (files, directories);
    }

    private static void EnsureRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidBundle();
        }
    }

    private static void EnsureRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory |
                           FileAttributes.ReparsePoint)) != 0)
        {
            throw InvalidBundle();
        }
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);
    }

    private static HandleScopeInstallException InvalidBundle() => new(
        "The HandleScope package changed or its locked extraction tree is unsafe.");

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    private sealed record DirectoryLock(
        SafeFileHandle Handle,
        FileIdentity Identity) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
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
