using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.SystemProcesses;

internal readonly record struct WindowsProcessParentSnapshot(
    int ProcessId,
    int ParentProcessId);

internal static class WindowsProcessParentVerifier
{
    internal const int MaximumSnapshotEntries = 65_536;
    private const uint SnapshotProcesses = 0x00000002;

    internal static bool IsCurrentProcessCreatedBy(int expectedParentProcessId)
    {
        if (expectedParentProcessId <= 0)
            return false;

        var currentProcessId = Environment.ProcessId;
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
            return false;

        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>()
        };
        if (!Process32First(snapshot, ref entry))
            return false;

        for (var inspected = 0;
             inspected < MaximumSnapshotEntries;
             inspected++)
        {
            if (entry.ProcessId <= int.MaxValue &&
                entry.ParentProcessId <= int.MaxValue &&
                MatchesExpectedCreator(
                    currentProcessId,
                    expectedParentProcessId,
                    new WindowsProcessParentSnapshot(
                        (int)entry.ProcessId,
                        (int)entry.ParentProcessId)))
            {
                return true;
            }

            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            if (!Process32Next(snapshot, ref entry))
                return false;
        }

        return false;
    }

    internal static bool MatchesExpectedCreator(
        int currentProcessId,
        int expectedParentProcessId,
        WindowsProcessParentSnapshot snapshot) =>
        currentProcessId > 0 &&
        expectedParentProcessId > 0 &&
        currentProcessId != expectedParentProcessId &&
        snapshot.ProcessId == currentProcessId &&
        snapshot.ParentProcessId == expectedParentProcessId;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "Process32FirstW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "Process32NextW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint UsageCount;
        internal uint ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint ThreadCount;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

    private sealed class SafeSnapshotHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
