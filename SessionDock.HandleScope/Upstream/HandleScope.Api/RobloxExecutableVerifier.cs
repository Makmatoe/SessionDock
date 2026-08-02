using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandleScope.Api;

public interface IRobloxExecutableVerifier
{
    bool IsTrusted(string imagePath);
}

internal interface IRobloxExecutableTrustServices
{
    string GetCanonicalPath(SafeFileHandle handle);

    bool ContainsReparsePoint(string root, string path);

    bool HasExpectedVersionIdentity(string path);

    bool IsSignedAndTrusted(string path, SafeFileHandle fileHandle);

    bool HasExpectedSigner(string path);
}

public sealed class RobloxExecutableVerifier : IRobloxExecutableVerifier
{
    private const string ExpectedFileName = "RobloxPlayerBeta.exe";
    private const string ExpectedOriginalFileName = "RobloxApp.exe";
    private const string ExpectedPublisher = "Roblox Corporation";
    private readonly string[] _allowedRoots;
    private readonly IRobloxExecutableTrustServices _trustServices;

    public RobloxExecutableVerifier()
        : this(BuildAllowedRoots(), new WindowsExecutableTrustServices())
    {
    }

    internal RobloxExecutableVerifier(
        IEnumerable<string> allowedRoots,
        IRobloxExecutableTrustServices trustServices)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        ArgumentNullException.ThrowIfNull(trustServices);
        _allowedRoots = allowedRoots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _trustServices = trustServices;
    }

    public bool IsTrusted(string imagePath)
    {
        try
        {
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                // Keep the verified path from being replaced between version,
                // WinVerifyTrust, and signer checks.
                FileShare.Read);
            var canonicalPath = _trustServices.GetCanonicalPath(stream.SafeFileHandle);
            if (!IsAllowedPath(canonicalPath))
            {
                return false;
            }

            return _trustServices.HasExpectedVersionIdentity(canonicalPath) &&
                   _trustServices.IsSignedAndTrusted(
                       canonicalPath,
                       stream.SafeFileHandle) &&
                   _trustServices.HasExpectedSigner(canonicalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private bool IsAllowedPath(string canonicalPath)
    {
        if (!string.Equals(
                Path.GetFileName(canonicalPath),
                ExpectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var root in _allowedRoots)
        {
            var relative = Path.GetRelativePath(root, canonicalPath);
            var parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                parts[0].StartsWith("version-", StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length > "version-".Length &&
                string.Equals(parts[1], ExpectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                return !_trustServices.ContainsReparsePoint(root, canonicalPath);
            }
        }

        return false;
    }

    private static bool HasExpectedVersionIdentity(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return string.Equals(
                   version.CompanyName,
                   ExpectedPublisher,
                   StringComparison.Ordinal) &&
               string.Equals(
                   version.OriginalFilename,
                   ExpectedOriginalFileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExpectedSigner(string path)
    {
#pragma warning disable SYSLIB0057 // The signer is read only after WinVerifyTrust succeeds.
        using var signer = new X509Certificate2(
            X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        if (!string.Equals(
                signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                ExpectedPublisher,
                StringComparison.Ordinal))
        {
            return false;
        }

        var decoded = signer.SubjectName.Decode(
            X500DistinguishedNameFlags.UseNewLines |
            X500DistinguishedNameFlags.DoNotUseQuotes |
            X500DistinguishedNameFlags.DoNotUsePlusSign);
        return decoded
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => string.Equals(
                line,
                $"O={ExpectedPublisher}",
                StringComparison.Ordinal));
    }

    private static string[] BuildAllowedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(
            roots,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(
            roots,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(
            roots,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return [.. roots];
    }

    private static void AddRoot(ISet<string> roots, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return;
        }

        roots.Add(Path.GetFullPath(Path.Combine(basePath, "Roblox", "Versions")));
    }

    private static bool ContainsReparsePoint(string root, string path)
    {
        var current = new FileInfo(path).Directory;
        while (current is not null &&
               current.FullName.StartsWith(
                   root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current.Parent;
        }

        return current is null ||
               !string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase) ||
               (current.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string GetCanonicalPath(SafeFileHandle handle)
    {
        var capacity = 1024;
        while (capacity <= 32768)
        {
            var path = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                path,
                path.Capacity,
                FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                throw new IOException(
                    "Windows could not resolve the Roblox executable path.",
                    Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }

            if (length < path.Capacity)
            {
                var result = path.ToString();
                return result.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? result[4..]
                    : result;
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException("The Roblox executable path is too long.");
    }

    internal sealed class WindowsExecutableTrustServices : IRobloxExecutableTrustServices
    {
        public string GetCanonicalPath(SafeFileHandle handle) =>
            RobloxExecutableVerifier.GetCanonicalPath(handle);

        public bool ContainsReparsePoint(string root, string path) =>
            RobloxExecutableVerifier.ContainsReparsePoint(root, path);

        public bool HasExpectedVersionIdentity(string path) =>
            RobloxExecutableVerifier.HasExpectedVersionIdentity(path);

        public bool IsSignedAndTrusted(string path, SafeFileHandle fileHandle) =>
            Authenticode.IsSignedAndTrusted(path, fileHandle);

        public bool HasExpectedSigner(string path) =>
            RobloxExecutableVerifier.HasExpectedSigner(path);
    }

    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        int filePathLength,
        uint flags);

    private static class Authenticode
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
        private const uint WtdDisableMd2Md4 = 0x00002000;
        private static readonly Guid GenericVerifyV2 =
            new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        internal static bool IsSignedAndTrusted(
            string path,
            SafeFileHandle fileHandle)
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = path,
                FileHandle = fileHandle.DangerousGetHandle()
            };
            var fileInfoPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());

            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
                var trustData = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    File = fileInfoPointer,
                    StateAction = WtdStateActionVerify,
                    ProviderFlags = WtdCacheOnlyUrlRetrieval | WtdDisableMd2Md4
                };

                var result = WinVerifyTrust(
                    new IntPtr(-1),
                    GenericVerifyV2,
                    ref trustData);

                trustData.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(
                    new IntPtr(-1),
                    GenericVerifyV2,
                    ref trustData);
                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            internal uint StructSize;

            [MarshalAs(UnmanagedType.LPWStr)]
            internal string FilePath;

            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal uint StructSize;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr StateData;

            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? UrlReference;

            internal uint ProviderFlags;
            internal uint UiContext;
            internal IntPtr SignatureSettings;
        }

        [DllImport(
            "wintrust.dll",
            ExactSpelling = true,
            PreserveSig = true,
            SetLastError = false)]
        private static extern int WinVerifyTrust(
            IntPtr window,
            [MarshalAs(UnmanagedType.LPStruct)] Guid action,
            ref WinTrustData data);
    }
}
