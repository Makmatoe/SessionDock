using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HandleScope.Models;

namespace HandleScope.Services;

public sealed class HandleService
{
    private const int InitialSystemBufferSize = 1024 * 1024;
    private const int InitialObjectBufferSize = 1024;
    private const int MaximumSystemBufferSize = 256 * 1024 * 1024;
    private const int MaximumObjectBufferSize = 1024 * 1024;

    public IReadOnlyList<HandleEntry> FindHandles(
        int processId,
        string query,
        HandleMatchMode matchMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool includeUnnamed = false,
        Func<HandleEntry, bool>? resultFilter = null,
        int maximumMatches = int.MaxValue) =>
        FindHandlesCore(
            processId,
            query,
            matchMode,
            progress,
            cancellationToken,
            includeUnnamed,
            resultFilter,
            maximumMatches,
            expectedProcessCreationTimeUtcFileTime: null);

    public IReadOnlyList<HandleEntry> FindHandles(
        int processId,
        long expectedProcessCreationTimeUtcFileTime,
        string query,
        HandleMatchMode matchMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool includeUnnamed = false,
        Func<HandleEntry, bool>? resultFilter = null,
        int maximumMatches = int.MaxValue) =>
        FindHandlesCore(
            processId,
            query,
            matchMode,
            progress,
            cancellationToken,
            includeUnnamed,
            resultFilter,
            maximumMatches,
            expectedProcessCreationTimeUtcFileTime);

    private static IReadOnlyList<HandleEntry> FindHandlesCore(
        int processId,
        string query,
        HandleMatchMode matchMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool includeUnnamed,
        Func<HandleEntry, bool>? resultFilter,
        int maximumMatches,
        long? expectedProcessCreationTimeUtcFileTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMatches, 1);
        if (expectedProcessCreationTimeUtcFileTime is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedProcessCreationTimeUtcFileTime),
                "The expected process creation time must be a positive Windows file time.");
        }

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessDuplicateHandle | NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);

        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open PID {processId}. The process may be protected or may have exited.");
        }

        try
        {
            var processCreationTime =
                ProcessIdentityService.GetCreationTimeUtcFileTime(processHandle);
            EnsureExpectedProcessIdentity(
                processCreationTime,
                expectedProcessCreationTimeUtcFileTime);
            var systemHandles = QuerySystemHandles()
                .Where(entry => entry.UniqueProcessId == (nuint)(uint)processId)
                .ToArray();
            var matches = new List<ResolvedHandleMatch>();
            var devicePaths = BuildDevicePathMap();
            var normalizedQuery = query.Trim();

            for (var index = 0; index < systemHandles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var systemHandle = systemHandles[index];

                if (!NativeMethods.DuplicateHandleToCurrent(
                        processHandle,
                        ToIntPtr(systemHandle.HandleValue),
                        NativeMethods.GetCurrentProcess(),
                        out var duplicate,
                        0,
                        false,
                        NativeMethods.DuplicateSameAccess))
                {
                    ReportProgress(progress, index + 1, systemHandles.Length);
                    continue;
                }

                try
                {
                    var objectType = QueryObjectString(
                        duplicate,
                        NativeMethods.ObjectTypeInformation);
                    objectType = string.IsNullOrWhiteSpace(objectType)
                        ? "Unknown"
                        : objectType;
                    var nativeName = string.Equals(objectType, "File", StringComparison.OrdinalIgnoreCase)
                        ? QueryFileName(duplicate)
                        : QueryObjectString(duplicate, NativeMethods.ObjectNameInformation);

                    if (string.IsNullOrWhiteSpace(nativeName) && !includeUnnamed)
                    {
                        ReportProgress(progress, index + 1, systemHandles.Length);
                        continue;
                    }

                    var displayName = ConvertNativeName(nativeName, devicePaths);
                    if (!IsMatch(displayName, nativeName, normalizedQuery, matchMode))
                    {
                        ReportProgress(progress, index + 1, systemHandles.Length);
                        continue;
                    }

                    var match = new HandleEntry(
                        processId,
                        systemHandle.HandleValue,
                        systemHandle.Object,
                        systemHandle.GrantedAccess,
                        objectType,
                        displayName,
                        nativeName)
                    {
                        ProcessCreationTimeUtcFileTime = processCreationTime
                    };
                    if (resultFilter is null || resultFilter(match))
                    {
                        matches.Add(new ResolvedHandleMatch(match, systemHandle));
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(duplicate);
                }

                ReportProgress(progress, index + 1, systemHandles.Length);
                if (matches.Count >= maximumMatches)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var revalidatedMatches = RevalidateResolvedMatches(processId, matches);

            var finalCreationTime =
                ProcessIdentityService.GetCreationTimeUtcFileTime(processHandle);
            if (finalCreationTime != processCreationTime)
            {
                throw new InvalidOperationException(
                    "The target process identity changed during the scan. Refresh the process list and try again.");
            }

            EnsureExpectedProcessIdentity(
                finalCreationTime,
                expectedProcessCreationTimeUtcFileTime);

            var results = revalidatedMatches
                .OrderBy(entry => entry.ObjectType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            EnsureProcessIdStillRefersTo(
                processId,
                processCreationTime);
            return results;
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    public void CloseHandle(HandleEntry expected)
    {
        if (expected.ProcessId is 0 or 4)
        {
            throw new InvalidOperationException("Handles owned by the Windows System process cannot be closed.");
        }

        if (expected.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("HandleScope will not close one of its own handles.");
        }

        if (expected.ProcessCreationTimeUtcFileTime <= 0)
        {
            throw new InvalidOperationException(
                "The handle result has no process identity. Refresh the results before trying again.");
        }

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessDuplicateHandle | NativeMethods.ProcessQueryLimitedInformation,
            false,
            expected.ProcessId);

        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open PID {expected.ProcessId}. The process may be protected or may have exited.");
        }

        try
        {
            var currentCreationTime =
                ProcessIdentityService.GetCreationTimeUtcFileTime(processHandle);
            if (currentCreationTime != expected.ProcessCreationTimeUtcFileTime)
            {
                throw new InvalidOperationException(
                    "The process ID now belongs to a different process. Refresh the results; nothing was closed.");
            }

            var current = QuerySystemHandles().FirstOrDefault(entry =>
                entry.UniqueProcessId == (nuint)(uint)expected.ProcessId &&
                entry.HandleValue == expected.HandleValue);

            if (current.HandleValue != expected.HandleValue ||
                current.UniqueProcessId != (nuint)(uint)expected.ProcessId)
            {
                throw new InvalidOperationException(
                    "That handle no longer exists. Refresh the results before trying again.");
            }

            if (current.Object != expected.ObjectAddress)
            {
                throw new InvalidOperationException(
                    "The handle value has been reused for a different object. Refresh the results; nothing was closed.");
            }

            if (current.GrantedAccess != expected.GrantedAccess)
            {
                throw new InvalidOperationException(
                    "The handle access rights changed after the scan. Refresh the results; nothing was closed.");
            }

            var closeReported = NativeMethods.CloseRemoteHandle(
                    processHandle,
                    ToIntPtr(expected.HandleValue),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    false,
                    NativeMethods.DuplicateCloseSource);
            var closeError = Marshal.GetLastWin32Error();

            var stillOpen = QuerySystemHandles().Any(entry =>
                entry.UniqueProcessId == (nuint)(uint)expected.ProcessId &&
                entry.HandleValue == expected.HandleValue &&
                entry.Object == expected.ObjectAddress);

            if (stillOpen)
            {
                throw new Win32Exception(
                    closeReported ? 0 : closeError,
                    "The handle is still present after the close request.");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    private static IReadOnlyList<HandleEntry> RevalidateResolvedMatches(
        int processId,
        IReadOnlyList<ResolvedHandleMatch> resolvedMatches)
    {
        if (resolvedMatches.Count == 0)
        {
            return [];
        }

        var currentHandles = QuerySystemHandles()
            .Where(entry => entry.UniqueProcessId == (nuint)(uint)processId)
            .GroupBy(entry => entry.HandleValue)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return resolvedMatches
            .Where(resolved =>
                currentHandles.TryGetValue(resolved.Entry.HandleValue, out var candidates) &&
                candidates.Any(current =>
                    current.Object == resolved.Snapshot.Object &&
                    current.UniqueProcessId == resolved.Snapshot.UniqueProcessId &&
                    current.HandleValue == resolved.Snapshot.HandleValue &&
                    current.GrantedAccess == resolved.Snapshot.GrantedAccess &&
                    current.CreatorBackTraceIndex ==
                        resolved.Snapshot.CreatorBackTraceIndex &&
                    current.ObjectTypeIndex == resolved.Snapshot.ObjectTypeIndex &&
                    current.HandleAttributes == resolved.Snapshot.HandleAttributes &&
                    current.Reserved == resolved.Snapshot.Reserved))
            .Select(resolved => resolved.Entry)
            .ToArray();
    }

    private static void EnsureExpectedProcessIdentity(
        long currentCreationTimeUtcFileTime,
        long? expectedCreationTimeUtcFileTime)
    {
        if (expectedCreationTimeUtcFileTime.HasValue &&
            currentCreationTimeUtcFileTime != expectedCreationTimeUtcFileTime.Value)
        {
            throw new InvalidOperationException(
                "The process ID now belongs to a different process. Refresh the process list; no handles were scanned.");
        }
    }

    private static void EnsureProcessIdStillRefersTo(
        int processId,
        long expectedCreationTimeUtcFileTime)
    {
        var verificationHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);

        if (verificationHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"PID {processId} exited before the scan results could be verified.");
        }

        try
        {
            var currentCreationTime =
                ProcessIdentityService.GetCreationTimeUtcFileTime(verificationHandle);
            if (currentCreationTime != expectedCreationTimeUtcFileTime)
            {
                throw new InvalidOperationException(
                    "The process ID was reassigned during the scan. Refresh the process list; no results were returned.");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(verificationHandle);
        }
    }

    private sealed record ResolvedHandleMatch(
        HandleEntry Entry,
        NativeMethods.SystemHandleTableEntryInfoEx Snapshot);

    private static NativeMethods.SystemHandleTableEntryInfoEx[] QuerySystemHandles()
    {
        var bufferSize = InitialSystemBufferSize;
        var buffer = IntPtr.Zero;

        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(bufferSize);
                var status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemExtendedHandleInformation,
                    buffer,
                    bufferSize,
                    out var requiredSize);

                if (status >= 0)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                if (status != NativeMethods.StatusInfoLengthMismatch)
                {
                    throw new InvalidOperationException(
                        $"Windows could not enumerate the system handle table (NTSTATUS 0x{status:X8}).");
                }

                if (requiredSize < 0 || requiredSize > MaximumSystemBufferSize)
                {
                    throw new InvalidOperationException(
                        "Windows requested an unsafe system-handle buffer size.");
                }

                bufferSize = Math.Max(
                    checked(bufferSize * 2),
                    checked(requiredSize + (64 * 1024)));
                if (bufferSize > MaximumSystemBufferSize)
                {
                    throw new InvalidOperationException(
                        "The system handle table exceeds the reviewed memory limit.");
                }
            }

            var count64 = unchecked((ulong)Marshal.ReadIntPtr(buffer).ToInt64());
            if (count64 > int.MaxValue)
            {
                throw new InvalidOperationException("The system handle table is too large to inspect.");
            }

            var count = (int)count64;
            var entrySize = Marshal.SizeOf<NativeMethods.SystemHandleTableEntryInfoEx>();
            var availableBytes = bufferSize - (IntPtr.Size * 2);
            if (availableBytes < 0 || count > availableBytes / entrySize)
            {
                throw new InvalidOperationException(
                    "Windows returned an inconsistent system handle table.");
            }

            var entries = new NativeMethods.SystemHandleTableEntryInfoEx[count];
            var firstEntry = IntPtr.Add(buffer, IntPtr.Size * 2);

            for (var index = 0; index < count; index++)
            {
                entries[index] = Marshal.PtrToStructure<NativeMethods.SystemHandleTableEntryInfoEx>(
                    IntPtr.Add(firstEntry, index * entrySize));
            }

            return entries;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string QueryObjectString(IntPtr handle, int informationClass)
    {
        var bufferSize = InitialObjectBufferSize;
        var buffer = IntPtr.Zero;

        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                buffer = Marshal.AllocHGlobal(bufferSize);
                var status = NativeMethods.NtQueryObject(
                    handle,
                    informationClass,
                    buffer,
                    bufferSize,
                    out var requiredSize);

                if (status >= 0)
                {
                    var unicode = Marshal.PtrToStructure<NativeMethods.UnicodeString>(buffer);
                    return unicode.Buffer == IntPtr.Zero || unicode.Length == 0
                        ? string.Empty
                        : Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / sizeof(char)) ?? string.Empty;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                if (status is not (
                    NativeMethods.StatusInfoLengthMismatch or
                    NativeMethods.StatusBufferOverflow or
                    NativeMethods.StatusBufferTooSmall))
                {
                    return string.Empty;
                }

                if (requiredSize < 0 || requiredSize > MaximumObjectBufferSize)
                {
                    return string.Empty;
                }

                bufferSize = Math.Max(
                    checked(bufferSize * 2),
                    checked(requiredSize + 256));
                if (bufferSize > MaximumObjectBufferSize)
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string QueryFileName(IntPtr handle)
    {
        // File-type handles also represent pipes and console streams. Asking the file-system
        // path API about those objects can block indefinitely, so only disk-backed handles
        // are resolved here. Other named object types continue through NtQueryObject.
        if (NativeMethods.GetFileType(handle) != NativeMethods.FileTypeDisk)
        {
            return string.Empty;
        }

        var bufferSize = 1024;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var path = new StringBuilder(bufferSize);
            var length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                path,
                path.Capacity,
                flags: 0);

            if (length == 0)
            {
                return string.Empty;
            }

            if (length < path.Capacity)
            {
                var result = path.ToString();
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + result[@"\\?\UNC\".Length..];
                }

                return result.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? result[4..]
                    : result;
            }

            bufferSize = checked((int)length + 1);
        }

        return string.Empty;
    }

    private static Dictionary<string, string> BuildDevicePathMap()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            var driveName = drive.Name.TrimEnd('\\');
            var target = new StringBuilder(1024);

            if (NativeMethods.QueryDosDevice(driveName, target, target.Capacity) != 0)
            {
                var nativePath = target.ToString().Split('\0', 2)[0];
                if (!string.IsNullOrWhiteSpace(nativePath))
                {
                    paths[nativePath] = driveName;
                }
            }
        }

        return paths;
    }

    private static string ConvertNativeName(
        string nativeName,
        IReadOnlyDictionary<string, string> devicePaths)
    {
        foreach (var mapping in devicePaths.OrderByDescending(pair => pair.Key.Length))
        {
            if (nativeName.StartsWith(mapping.Key, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Value + nativeName[mapping.Key.Length..];
            }
        }

        if (nativeName.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + nativeName[@"\Device\Mup\".Length..];
        }

        if (nativeName.StartsWith(@"\REGISTRY\MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            return @"HKLM\" + nativeName[@"\REGISTRY\MACHINE\".Length..];
        }

        if (nativeName.StartsWith(@"\REGISTRY\USER\", StringComparison.OrdinalIgnoreCase))
        {
            return @"HKU\" + nativeName[@"\REGISTRY\USER\".Length..];
        }

        return nativeName;
    }

    private static bool IsMatch(
        string displayName,
        string nativeName,
        string query,
        HandleMatchMode matchMode)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return matchMode switch
        {
            HandleMatchMode.Exact =>
                string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nativeName, query, StringComparison.OrdinalIgnoreCase),
            _ =>
                displayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                nativeName.Contains(query, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static IntPtr ToIntPtr(nuint value) =>
        unchecked((IntPtr)(nint)value);

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        int completed,
        int total)
    {
        if (progress is not null && (completed == total || completed % 25 == 0))
        {
            progress.Report(new ScanProgress(completed, total));
        }
    }
}
