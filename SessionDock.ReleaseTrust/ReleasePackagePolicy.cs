namespace SessionDock.ReleaseTrust;

public sealed record ReleasePackageEntryIdentity(
    string? FullName,
    long Length,
    long CompressedLength,
    int ExternalAttributes = 0);

public static class ReleasePackagePolicy
{
    public const long MaximumUncompressedBytes = 1024L * 1024 * 1024;

    private const int MaximumCurrentEntryCount = 4096;
    private const int MaximumEntryNameLength = 512;
    private const long MaximumMetadataBytes = 256 * 1024;
    private const long MaximumNoticeBytes = 2 * 1024 * 1024;
    private const long MinimumExecutableBytes = 64 * 1024;

    private static readonly Dictionary<string, EntryLimits> LegacyEntries =
        CreateExpectedEntries(
            "RobloxOne.nuspec",
            "RobloxOne.exe",
            "RobloxOne_ExecutionStub.exe");
    private static readonly Dictionary<string, EntryLimits> CurrentRequiredEntries =
        CreateCurrentRequiredEntries();
    private static readonly HashSet<string> WindowsDeviceNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    public static IReadOnlyList<string> ExecutableEntryNames =>
        GetExecutableEntryNames(useCurrentLayout: false);

    public static void ValidateEntries(
        IEnumerable<ReleasePackageEntryIdentity> entries,
        bool useCurrentLayout = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.ToArray();
        if (useCurrentLayout)
        {
            ValidateCurrentEntries(materialized);
            return;
        }

        ValidateLegacyEntries(materialized);
    }

    private static void ValidateLegacyEntries(
        ReleasePackageEntryIdentity[] entries)
    {
        if (entries.Length != LegacyEntries.Count)
        {
            throw new ReleaseTrustException(
                "The update package contains missing or unexpected entries.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.FullName) ||
                !seen.Add(entry.FullName) ||
                !LegacyEntries.TryGetValue(entry.FullName, out var limits))
            {
                throw new ReleaseTrustException(
                    "The update package contains a duplicate, unsafe, or unexpected entry.");
            }

            if (entry.Length < limits.Minimum ||
                entry.Length > limits.Maximum ||
                entry.CompressedLength <= 0 ||
                entry.CompressedLength > ReleaseDescriptorPolicy.MaximumPackageSize)
            {
                throw new ReleaseTrustException(
                    $"The update package entry '{entry.FullName}' has an invalid size.");
            }

            try
            {
                totalLength = checked(totalLength + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new ReleaseTrustException(
                    "The update package expands beyond the permitted size.",
                    exception);
            }
        }

        if (totalLength > MaximumUncompressedBytes)
        {
            throw new ReleaseTrustException(
                "The update package expands beyond the permitted size.");
        }
    }

    private static void ValidateCurrentEntries(
        ReleasePackageEntryIdentity[] entries)
    {
        if (entries.Length < CurrentRequiredEntries.Count ||
            entries.Length > MaximumCurrentEntryCount)
        {
            throw new ReleaseTrustException(
                "The update package contains missing or too many entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(
            CurrentRequiredEntries.Keys,
            StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in entries)
        {
            if (entry is null ||
                !TryGetSafeCurrentEntryName(
                    entry.FullName,
                    out var canonicalName,
                    out var isDirectory) ||
                !HasSafeEntryType(entry.ExternalAttributes, isDirectory) ||
                !seen.Add(canonicalName))
            {
                throw new ReleaseTrustException(
                    "The update package contains a duplicate or unsafe entry path.");
            }

            if (isDirectory)
            {
                if (entry.Length != 0 || entry.CompressedLength != 0)
                {
                    throw new ReleaseTrustException(
                        $"The update package entry '{entry.FullName}' has an invalid size.");
                }

                continue;
            }

            var isRequired = CurrentRequiredEntries.TryGetValue(
                entry.FullName!,
                out var limits);
            limits ??= new EntryLimits(
                0,
                ReleaseDescriptorPolicy.MaximumPackageSize);
            ValidateEntrySize(entry, limits);
            if (isRequired)
                required.Remove(entry.FullName!);
            AddToTotalLength(entry.Length, ref totalLength);
        }

        if (required.Count != 0)
        {
            throw new ReleaseTrustException(
                "The update package is missing a required application entry.");
        }

        if (totalLength > MaximumUncompressedBytes)
        {
            throw new ReleaseTrustException(
                "The update package expands beyond the permitted size.");
        }
    }

    private static bool TryGetSafeCurrentEntryName(
        string? fullName,
        out string canonicalName,
        out bool isDirectory)
    {
        canonicalName = string.Empty;
        isDirectory = false;
        if (string.IsNullOrWhiteSpace(fullName) ||
            fullName.Length > MaximumEntryNameLength ||
            fullName[0] == '/' ||
            fullName.Contains('\\') ||
            fullName.Any(char.IsControl))
        {
            return false;
        }

        isDirectory = fullName.EndsWith('/');
        canonicalName = isDirectory ? fullName[..^1] : fullName;
        if (canonicalName.Length == 0)
            return false;

        var segments = canonicalName.Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment[^1] is ' ' or '.' ||
                segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
            {
                return false;
            }

            var extensionSeparator = segment.IndexOf('.');
            var deviceCandidate = extensionSeparator >= 0
                ? segment[..extensionSeparator]
                : segment;
            if (WindowsDeviceNames.Contains(deviceCandidate))
                return false;
        }

        return true;
    }

    private static bool HasSafeEntryType(
        int externalAttributes,
        bool isDirectory)
    {
        const uint windowsDirectory = 0x10;
        const uint windowsDevice = 0x40;
        const uint windowsReparsePoint = 0x400;
        const uint unixFileTypeMask = 0xF000;
        const uint unixDirectory = 0x4000;
        const uint unixRegularFile = 0x8000;

        var attributes = unchecked((uint)externalAttributes);
        if ((attributes &
             (windowsDevice | windowsReparsePoint)) != 0)
        {
            return false;
        }

        var unixFileType = (attributes >> 16) & unixFileTypeMask;
        if (isDirectory)
            return unixFileType is 0 or unixDirectory;

        return (attributes & windowsDirectory) == 0 &&
               unixFileType is 0 or unixRegularFile;
    }

    private static void ValidateEntrySize(
        ReleasePackageEntryIdentity entry,
        EntryLimits limits)
    {
        if (entry.Length < limits.Minimum ||
            entry.Length > limits.Maximum ||
            entry.CompressedLength < 0 ||
            (entry.Length > 0 && entry.CompressedLength == 0) ||
            entry.CompressedLength > ReleaseDescriptorPolicy.MaximumPackageSize)
        {
            throw new ReleaseTrustException(
                $"The update package entry '{entry.FullName}' has an invalid size.");
        }
    }

    private static void AddToTotalLength(long length, ref long totalLength)
    {
        try
        {
            totalLength = checked(totalLength + length);
        }
        catch (OverflowException exception)
        {
            throw new ReleaseTrustException(
                "The update package expands beyond the permitted size.",
                exception);
        }
    }

    public static IReadOnlyList<string> GetExecutableEntryNames(
        bool useCurrentLayout) =>
        useCurrentLayout
            ? [
                "lib/app/SessionDock.exe"
            ]
            : [
                "lib/app/RobloxOne.exe",
                "lib/app/RobloxOne_ExecutionStub.exe",
                "lib/app/Squirrel.exe"
            ];

    private static Dictionary<string, EntryLimits> CreateExpectedEntries(
        string nuspecName,
        string mainExecutable,
        string executionStub)
    {
        return new Dictionary<string, EntryLimits>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = new(1, MaximumMetadataBytes),
            ["_rels/.rels"] = new(1, MaximumMetadataBytes),
            [nuspecName] = new(1, MaximumMetadataBytes),
            ["lib/app/LICENSE.md"] = new(1, MaximumNoticeBytes),
            [$"lib/app/{mainExecutable}"] = new(
                ReleaseDescriptorPolicy.MinimumPackageSize,
                ReleaseDescriptorPolicy.MaximumPackageSize),
            [$"lib/app/{executionStub}"] = new(
                MinimumExecutableBytes,
                128 * 1024 * 1024),
            ["lib/app/Squirrel.exe"] = new(
                MinimumExecutableBytes,
                256 * 1024 * 1024),
            ["lib/app/sq.version"] = new(1, MaximumMetadataBytes),
            ["lib/app/THIRD_PARTY_NOTICES.md"] = new(1, MaximumNoticeBytes),
            ["lib/app/licenses/DotNet-LICENSE.txt"] = new(1, MaximumNoticeBytes),
            ["lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Velopack-LICENSE.txt"] = new(1, MaximumNoticeBytes)
        };
    }

    private static Dictionary<string, EntryLimits> CreateCurrentRequiredEntries()
    {
        return new Dictionary<string, EntryLimits>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = new(1, MaximumMetadataBytes),
            ["_rels/.rels"] = new(1, MaximumMetadataBytes),
            ["SessionDockApp.nuspec"] = new(1, MaximumMetadataBytes),
            ["lib/app/SessionDock.exe"] = new(
                MinimumExecutableBytes,
                128 * 1024 * 1024),
            ["lib/app/SessionDock_ExecutionStub.exe"] = new(
                MinimumExecutableBytes,
                128 * 1024 * 1024),
            ["lib/app/Squirrel.exe"] = new(
                MinimumExecutableBytes,
                256 * 1024 * 1024),
            ["lib/app/sq.version"] = new(1, MaximumMetadataBytes)
        };
    }

    private sealed record EntryLimits(long Minimum, long Maximum);
}
