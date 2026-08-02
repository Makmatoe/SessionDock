using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using HandleScope.Models;

namespace HandleScope.Services;

public sealed class ProcessIdentityService
{
    private const int MaximumWindowsPathLength = 32768;

    public ProcessIdentity GetIdentity(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);

        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not query PID {processId}. The process may be protected or may have exited.");
        }

        try
        {
            var imagePath = GetImagePath(processHandle);
            var processName = Path.GetFileNameWithoutExtension(imagePath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                throw new InvalidOperationException(
                    $"Windows returned no executable name for PID {processId}.");
            }

            var creationTime = GetCreationTimeUtcFileTime(processHandle);
            var (ownerSid, isElevated, sessionId) =
                GetTokenIdentity(processHandle, processId);

            return new ProcessIdentity(
                processId,
                processName,
                imagePath,
                sessionId,
                ownerSid,
                isElevated,
                creationTime);
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    internal static long GetCreationTimeUtcFileTime(IntPtr processHandle)
    {
        if (!NativeMethods.GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not determine the process creation time.");
        }

        var value = unchecked(
            ((ulong)creationTime.HighDateTime << 32) | creationTime.LowDateTime);
        if (value is 0 or > long.MaxValue)
        {
            throw new InvalidOperationException(
                "Windows returned an invalid process creation time.");
        }

        return (long)value;
    }

    private static string GetImagePath(IntPtr processHandle)
    {
        var path = new StringBuilder(MaximumWindowsPathLength);
        var length = path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(
                processHandle,
                flags: 0,
                path,
                ref length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not determine the process image path.");
        }

        return path.ToString(0, length);
    }

    private static (string OwnerSid, bool IsElevated, uint SessionId) GetTokenIdentity(
        IntPtr processHandle,
        int processId)
    {
        if (!NativeMethods.OpenProcessToken(
                processHandle,
                NativeMethods.TokenQuery,
                out var tokenHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not query the security token for PID {processId}.");
        }

        try
        {
            return (
                GetOwnerSid(tokenHandle),
                GetElevationState(tokenHandle),
                GetSessionId(tokenHandle));
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
    }

    private static string GetOwnerSid(IntPtr tokenHandle)
    {
        NativeMethods.GetTokenInformation(
            tokenHandle,
            NativeMethods.TokenUserInformation,
            IntPtr.Zero,
            0,
            out var requiredSize);

        var error = Marshal.GetLastWin32Error();
        if (requiredSize <= 0 || error != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "Could not size the process owner SID.");
        }

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenUserInformation,
                    buffer,
                    requiredSize,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not determine the process owner SID.");
            }

            var tokenUser = Marshal.PtrToStructure<NativeMethods.TokenUser>(buffer);
            if (tokenUser.User.Sid == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows returned an invalid process owner SID.");
            }

            return new SecurityIdentifier(tokenUser.User.Sid).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool GetElevationState(IntPtr tokenHandle)
    {
        var size = Marshal.SizeOf<NativeMethods.TokenElevation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenElevationInformation,
                    buffer,
                    size,
                    out var returnedSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not determine the process elevation state.");
            }

            if (returnedSize < size)
            {
                throw new InvalidOperationException(
                    "Windows returned an incomplete process elevation state.");
            }

            return Marshal.PtrToStructure<NativeMethods.TokenElevation>(buffer)
                .TokenIsElevated != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint GetSessionId(IntPtr tokenHandle)
    {
        var size = sizeof(uint);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenSessionIdInformation,
                    buffer,
                    size,
                    out var returnedSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not determine the process Windows session.");
            }

            if (returnedSize < size)
            {
                throw new InvalidOperationException(
                    "Windows returned an incomplete process session identifier.");
            }

            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
