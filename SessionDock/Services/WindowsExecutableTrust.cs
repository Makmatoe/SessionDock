using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.Services;

internal static class WindowsExecutableTrust
{
    private static readonly WindowsExecutableTrustVerifier Verifier =
        new(new WinTrustNativeVerifier());

    public static bool TryGetTrustedSigner(
        string path,
        out TrustedWindowsSigner signer,
        bool forceRefresh = false) =>
        Verifier.TryGetTrustedSigner(path, forceRefresh, out signer);

    internal static bool TryGetTrustedSigner(
        string path,
        SafeFileHandle retainedFileHandle,
        out TrustedWindowsSigner signer,
        bool forceRefresh = false) =>
        Verifier.TryGetTrustedSigner(
            path,
            retainedFileHandle,
            forceRefresh,
            out signer);
}

internal sealed class WindowsExecutableTrustVerifier
{
    internal static readonly TimeSpan SuccessfulValidationLifetime =
        TimeSpan.FromMinutes(10);
    private const int MaximumCacheEntries = 64;

    private readonly IWindowsTrustNativeVerifier _nativeVerifier;
    private readonly Func<string, WindowsExecutableFileIdentity?>?
        _getFileIdentity;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly object _cacheLock = new();
    private readonly Dictionary<WindowsExecutableFileIdentity, CacheEntry>
        _legacyCache = [];
    private readonly Dictionary<WindowsExecutableFileStamp, CacheEntry>
        _proofCache = new(WindowsExecutableFileStampComparer.Instance);

    internal WindowsExecutableTrustVerifier(
        IWindowsTrustNativeVerifier nativeVerifier,
        Func<string, WindowsExecutableFileIdentity?>? getFileIdentity = null,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _nativeVerifier = nativeVerifier ??
            throw new ArgumentNullException(nameof(nativeVerifier));
        _getFileIdentity = getFileIdentity;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal bool TryGetTrustedSigner(
        string path,
        bool forceRefresh,
        out TrustedWindowsSigner signer) =>
        TryGetTrustedSigner(
            path,
            retainedFileHandle: null,
            forceRefresh,
            out signer);

    internal bool TryGetTrustedSigner(
        string path,
        SafeFileHandle? retainedFileHandle,
        bool forceRefresh,
        out TrustedWindowsSigner signer)
    {
        signer = TrustedWindowsSigner.Empty;
        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }

        return _getFileIdentity is null
            ? TryGetTrustedSignerWithFileProof(
                canonicalPath,
                retainedFileHandle,
                forceRefresh,
                out signer)
            : TryGetTrustedSignerLegacy(
                canonicalPath,
                forceRefresh,
                out signer);
    }

    private bool TryGetTrustedSignerLegacy(
        string canonicalPath,
        bool forceRefresh,
        out TrustedWindowsSigner signer)
    {
        signer = TrustedWindowsSigner.Empty;
        if (forceRefresh)
        {
            lock (_cacheLock)
                InvalidateLegacyPathNoLock(canonicalPath);
        }

        WindowsExecutableFileIdentity? identity;
        try
        {
            identity = _getFileIdentity!(canonicalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException or
                CryptographicException)
        {
            return false;
        }
        if (identity is null)
            return false;

        lock (_cacheLock)
        {
            var now = _getUtcNow();
            if (forceRefresh)
            {
                // A forced result is authoritative for this path. Remove a
                // positive result that an ordinary concurrent lookup may have
                // populated while the file identity was being captured.
                InvalidateLegacyPathNoLock(canonicalPath);
            }
            else
            {
                if (_legacyCache.TryGetValue(identity, out var cached))
                {
                    if (now - cached.ValidatedAtUtc <=
                        SuccessfulValidationLifetime)
                    {
                        signer = cached.Signer;
                        return true;
                    }

                    _legacyCache.Remove(identity);
                }
            }

            WindowsTrustNativeResult result;
            try
            {
                result = _nativeVerifier.Verify(
                    identity.CanonicalPath,
                    fileHandle: null);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or
                    UnauthorizedAccessException or InvalidOperationException or
                    CryptographicException or ExternalException or
                    DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            if (result.Status != WindowsTrustStatus.Trusted ||
                string.IsNullOrWhiteSpace(result.Signer.Subject) ||
                string.IsNullOrWhiteSpace(result.Signer.SimpleName))
            {
                return false;
            }

            signer = result.Signer;
            EvictOldestLegacyEntryNoLock();
            _legacyCache[identity] = new CacheEntry(
                signer,
                now);
            return true;
        }
    }

    private bool TryGetTrustedSignerWithFileProof(
        string canonicalPath,
        SafeFileHandle? retainedFileHandle,
        bool forceRefresh,
        out TrustedWindowsSigner signer)
    {
        signer = TrustedWindowsSigner.Empty;
        if (forceRefresh)
        {
            lock (_cacheLock)
                InvalidateProofPathNoLock(canonicalPath);
        }

        SafeFileHandle? ownedFileHandle = null;
        var fileHandleReferenceAdded = false;
        try
        {
            // When an operation already retained the executable, every part
            // of the proof must use that exact kernel file object. Otherwise,
            // open one non-write/delete-shared handle for this lookup.
            var proofHandle = retainedFileHandle;
            if (proofHandle is null)
            {
                ownedFileHandle = File.OpenHandle(
                    canonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.RandomAccess);
                proofHandle = ownedFileHandle;
            }
            if (proofHandle.IsInvalid || proofHandle.IsClosed)
                return false;

            proofHandle.DangerousAddRef(ref fileHandleReferenceAdded);
            var proofStamp = CaptureStamp(
                canonicalPath,
                proofHandle);
            if (proofStamp.Length <= 0)
                return false;
            Span<byte> digest = stackalloc byte[32];
            HashFile(proofHandle, proofStamp.Length, digest);
            proofStamp = proofStamp with
            {
                Sha256Part0 = BinaryPrimitives.ReadUInt64LittleEndian(
                    digest[..8]),
                Sha256Part1 = BinaryPrimitives.ReadUInt64LittleEndian(
                    digest.Slice(8, 8)),
                Sha256Part2 = BinaryPrimitives.ReadUInt64LittleEndian(
                    digest.Slice(16, 8)),
                Sha256Part3 = BinaryPrimitives.ReadUInt64LittleEndian(
                    digest.Slice(24, 8))
            };

            // Collapse concurrent checks for the same Roblox executable. A
            // batch can discover n clients in parallel; after the one forced
            // verification, all ordinary callers observe the populated proof
            // instead of launching n simultaneous Authenticode walks.
            lock (_cacheLock)
            {
                var now = _getUtcNow();
                if (forceRefresh)
                {
                    // Do this again under the verification lock so an
                    // ordinary lookup cannot repopulate a stale positive
                    // result between forced invalidation and WinVerifyTrust.
                    InvalidateProofPathNoLock(canonicalPath);
                }
                else if (_proofCache.TryGetValue(proofStamp, out var cached))
                {
                    if (now - cached.ValidatedAtUtc <=
                        SuccessfulValidationLifetime)
                    {
                        signer = cached.Signer;
                        return true;
                    }

                    _proofCache.Remove(proofStamp);
                }

                WindowsTrustNativeResult result;
                try
                {
                    result = _nativeVerifier.Verify(
                        canonicalPath,
                        proofHandle);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or
                        UnauthorizedAccessException or InvalidOperationException or
                        CryptographicException or ExternalException or
                        DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }
                if (result.Status != WindowsTrustStatus.Trusted ||
                    string.IsNullOrWhiteSpace(result.Signer.Subject) ||
                    string.IsNullOrWhiteSpace(result.Signer.SimpleName))
                {
                    return false;
                }

                signer = result.Signer;
                EvictOldestProofEntryNoLock();
                _proofCache[proofStamp] = new CacheEntry(
                    signer,
                    now);
                return true;
            }
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return false;
        }
        finally
        {
            if (fileHandleReferenceAdded)
                (retainedFileHandle ?? ownedFileHandle)!.DangerousRelease();
            ownedFileHandle?.Dispose();
            GC.KeepAlive(retainedFileHandle);
        }
    }

    private static void HashFile(
        SafeFileHandle handle,
        long length,
        Span<byte> destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < length)
            {
                var requested = checked((int)Math.Min(
                    buffer.Length,
                    length - offset));
                var read = RandomAccess.Read(
                    handle,
                    buffer.AsSpan(0, requested),
                    offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The executable changed while its trust proof was being captured.");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            if (!hash.TryGetHashAndReset(destination, out var written) ||
                written != destination.Length)
            {
                throw new CryptographicException(
                    "The executable SHA-256 proof could not be captured.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void InvalidateLegacyPathNoLock(string canonicalPath)
    {
        foreach (var identity in _legacyCache.Keys
                     .Where(identity => string.Equals(
                         identity.CanonicalPath,
                         canonicalPath,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _legacyCache.Remove(identity);
        }
    }

    private void InvalidateProofPathNoLock(string canonicalPath)
    {
        foreach (var stamp in _proofCache.Keys
                     .Where(stamp => string.Equals(
                         stamp.CanonicalPath,
                         canonicalPath,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _proofCache.Remove(stamp);
        }
    }

    private void EvictOldestLegacyEntryNoLock()
    {
        if (_legacyCache.Count < MaximumCacheEntries)
            return;
        var oldest = _legacyCache
            .MinBy(pair => pair.Value.ValidatedAtUtc)
            .Key;
        _legacyCache.Remove(oldest);
    }

    private void EvictOldestProofEntryNoLock()
    {
        if (_proofCache.Count < MaximumCacheEntries)
            return;
        var oldest = _proofCache
            .MinBy(pair => pair.Value.ValidatedAtUtc)
            .Key;
        _proofCache.Remove(oldest);
    }

    private static WindowsExecutableFileStamp CaptureStamp(
        string canonicalPath,
        SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                out FileBasicInfo basic,
                checked((uint)Marshal.SizeOf<FileBasicInfo>())) ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out FileIdInfo id,
                checked((uint)Marshal.SizeOf<FileIdInfo>())))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new WindowsExecutableFileStamp(
            canonicalPath,
            RandomAccess.GetLength(handle),
            basic.LastWriteTime,
            basic.ChangeTime,
            id.VolumeSerialNumber,
            id.FileId.Low,
            id.FileId.High);
    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is ArgumentException or IOException or
            NotSupportedException or UnauthorizedAccessException or
            CryptographicException or Win32Exception or
            ObjectDisposedException;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FileBasicInfo information,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FileIdInfo information,
        uint bufferSize);

    private sealed record CacheEntry(
        TrustedWindowsSigner Signer,
        DateTimeOffset ValidatedAtUtc);

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileBasicInfo
    {
        internal readonly long CreationTime;
        internal readonly long LastAccessTime;
        internal readonly long LastWriteTime;
        internal readonly long ChangeTime;
        internal readonly uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdInfo
    {
        internal readonly ulong VolumeSerialNumber;
        internal readonly FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileId128
    {
        internal readonly ulong Low;
        internal readonly ulong High;
    }
}

internal readonly record struct WindowsExecutableFileStamp(
    string CanonicalPath,
    long Length,
    long LastWriteTime,
    long ChangeTime,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    ulong Sha256Part0 = 0,
    ulong Sha256Part1 = 0,
    ulong Sha256Part2 = 0,
    ulong Sha256Part3 = 0);

internal sealed class WindowsExecutableFileStampComparer :
    IEqualityComparer<WindowsExecutableFileStamp>
{
    internal static WindowsExecutableFileStampComparer Instance { get; } =
        new();

    public bool Equals(
        WindowsExecutableFileStamp left,
        WindowsExecutableFileStamp right) =>
        string.Equals(
            left.CanonicalPath,
            right.CanonicalPath,
            StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length &&
        left.LastWriteTime == right.LastWriteTime &&
        left.ChangeTime == right.ChangeTime &&
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileIdLow == right.FileIdLow &&
        left.FileIdHigh == right.FileIdHigh &&
        left.Sha256Part0 == right.Sha256Part0 &&
        left.Sha256Part1 == right.Sha256Part1 &&
        left.Sha256Part2 == right.Sha256Part2 &&
        left.Sha256Part3 == right.Sha256Part3;

    public int GetHashCode(WindowsExecutableFileStamp value)
    {
        var hash = new HashCode();
        hash.Add(value.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        hash.Add(value.Length);
        hash.Add(value.LastWriteTime);
        hash.Add(value.ChangeTime);
        hash.Add(value.VolumeSerialNumber);
        hash.Add(value.FileIdLow);
        hash.Add(value.FileIdHigh);
        hash.Add(value.Sha256Part0);
        hash.Add(value.Sha256Part1);
        hash.Add(value.Sha256Part2);
        hash.Add(value.Sha256Part3);
        return hash.ToHashCode();
    }
}

internal interface IWindowsTrustNativeVerifier
{
    WindowsTrustNativeResult Verify(
        string path,
        SafeFileHandle? fileHandle);
}

internal sealed class WinTrustNativeVerifier : IWindowsTrustNativeVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public WindowsTrustNativeResult Verify(
        string path,
        SafeFileHandle? fileHandle)
    {
        var filePathPointer = IntPtr.Zero;
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        var verificationAttempted = false;
        var fileHandleReferenceAdded = false;
        try
        {
            if (fileHandle is not null && !fileHandle.IsInvalid)
                fileHandle.DangerousAddRef(ref fileHandleReferenceAdded);
            filePathPointer = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WinTrustFileInfo(
                filePathPointer,
                fileHandle);
            fileInfoPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(
                fileInfo,
                fileInfoPointer,
                fDeleteOld: false);
            var trustData = new WinTrustData(
                fileInfoPointer,
                WinTrustStateAction.Verify);
            trustDataPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(
                trustData,
                trustDataPointer,
                fDeleteOld: false);
            verificationAttempted = true;
            var statusCode = WinVerifyTrust(
                IntPtr.Zero,
                WinTrustActionGenericVerifyV2,
                trustDataPointer);
            var status = MapStatus(statusCode);
            if (status != WindowsTrustStatus.Trusted)
                return new(status, TrustedWindowsSigner.Empty, statusCode);

            trustData = Marshal.PtrToStructure<WinTrustData>(
                trustDataPointer);
            var signer = ReadVerifiedSigner(trustData.StateData);
            return new(WindowsTrustStatus.Trusted, signer, statusCode);
        }
        finally
        {
            try
            {
                if (verificationAttempted && trustDataPointer != IntPtr.Zero)
                {
                    var trustData = Marshal.PtrToStructure<WinTrustData>(
                        trustDataPointer);
                    trustData.StateAction = WinTrustStateAction.Close;
                    Marshal.StructureToPtr(
                        trustData,
                        trustDataPointer,
                        fDeleteOld: false);
                    _ = WinVerifyTrust(
                        IntPtr.Zero,
                        WinTrustActionGenericVerifyV2,
                        trustDataPointer);
                }
            }
            finally
            {
                if (trustDataPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(trustDataPointer);
                if (fileInfoPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(fileInfoPointer);
                if (filePathPointer != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(filePathPointer);
                // WINTRUST_FILE_INFO receives the raw kernel handle. Keep the
                // SafeHandle rooted until both VERIFY and CLOSE have returned.
                if (fileHandleReferenceAdded)
                    fileHandle!.DangerousRelease();
                GC.KeepAlive(fileHandle);
            }
        }
    }

    internal static WindowsTrustStatus MapStatus(int statusCode) => statusCode switch
    {
        0 => WindowsTrustStatus.Trusted,
        unchecked((int)0x80092010) => WindowsTrustStatus.Revoked,
        unchecked((int)0x80092012) => WindowsTrustStatus.RevocationUnknown,
        unchecked((int)0x80092013) => WindowsTrustStatus.RevocationOffline,
        unchecked((int)0x800B010E) => WindowsTrustStatus.RevocationUnknown,
        _ => WindowsTrustStatus.Untrusted
    };

    private static TrustedWindowsSigner ReadVerifiedSigner(
        IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
            throw new CryptographicException(
                "Windows returned no Authenticode provider state.");
        var providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
            throw new CryptographicException(
                "Windows returned no Authenticode provider data.");
        var providerSigner = WTHelperGetProvSignerFromChain(
            providerData,
            signerIndex: 0,
            counterSigner: false,
            counterSignerIndex: 0);
        if (providerSigner == IntPtr.Zero)
            throw new CryptographicException(
                "Windows returned no Authenticode signer chain.");

        var signer = Marshal.PtrToStructure<CryptProviderSigner>(
            providerSigner);
        if (signer.CertificateChainCount == 0 ||
            signer.CertificateChain == IntPtr.Zero)
        {
            throw new CryptographicException(
                "Windows returned an empty Authenticode signer chain.");
        }

        var providerCertificate =
            Marshal.PtrToStructure<CryptProviderCertificateHeader>(
                signer.CertificateChain);
        if (providerCertificate.CertificateContext == IntPtr.Zero)
        {
            throw new CryptographicException(
                "Windows returned no Authenticode signing certificate.");
        }

        var certificateContext = Marshal.PtrToStructure<CertificateContext>(
            providerCertificate.CertificateContext);
        if (certificateContext.EncodedBytes == IntPtr.Zero ||
            certificateContext.EncodedByteCount == 0 ||
            certificateContext.EncodedByteCount > 1024 * 1024)
        {
            throw new CryptographicException(
                "Windows returned an invalid Authenticode signing certificate.");
        }

        var encoded = GC.AllocateUninitializedArray<byte>(
            checked((int)certificateContext.EncodedByteCount));
        Marshal.Copy(
            certificateContext.EncodedBytes,
            encoded,
            startIndex: 0,
            encoded.Length);
        using var certificate = X509CertificateLoader.LoadCertificate(encoded);
        return new TrustedWindowsSigner(
            certificate.Subject,
            certificate.GetNameInfo(
                X509NameType.SimpleName,
                forIssuer: false));
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(
        IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustFileInfo
    {
        private readonly uint StructSize;
        private readonly IntPtr FilePath;
        private readonly IntPtr FileHandle;
        private readonly IntPtr KnownSubject;

        public WinTrustFileInfo(
            IntPtr filePath,
            SafeFileHandle? fileHandle)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = fileHandle is null || fileHandle.IsInvalid
                ? IntPtr.Zero
                : fileHandle.DangerousGetHandle();
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        private uint StructSize;
        private IntPtr PolicyCallbackData;
        private IntPtr SipClientData;
        private WinTrustUiChoice UiChoice;
        private WinTrustRevocationChecks RevocationChecks;
        private WinTrustUnionChoice UnionChoice;
        private IntPtr FileInfo;
        internal WinTrustStateAction StateAction;
        internal IntPtr StateData;
        private IntPtr UrlReference;
        private WinTrustProviderFlags ProviderFlags;
        private uint UiContext;
        private IntPtr SignatureSettings;

        public WinTrustData(
            IntPtr fileInfo,
            WinTrustStateAction stateAction)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WinTrustUiChoice.None;
            RevocationChecks = WinTrustRevocationChecks.WholeChain;
            UnionChoice = WinTrustUnionChoice.File;
            FileInfo = fileInfo;
            StateAction = stateAction;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags =
                WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
                WinTrustProviderFlags.DisableMd2Md4;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CryptProviderSigner
    {
        private readonly uint StructSize;
        private readonly FileTime VerifyAsOf;
        internal readonly uint CertificateChainCount;
        internal readonly IntPtr CertificateChain;
        private readonly uint SignerType;
        private readonly IntPtr SignerInfo;
        private readonly uint Error;
        private readonly uint CounterSignerCount;
        private readonly IntPtr CounterSigners;
        private readonly IntPtr ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint LowDateTime;
        private readonly uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CryptProviderCertificateHeader
    {
        private readonly uint StructSize;
        internal readonly IntPtr CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CertificateContext
    {
        private readonly uint EncodingType;
        internal readonly IntPtr EncodedBytes;
        internal readonly uint EncodedByteCount;
        private readonly IntPtr CertificateInfo;
        private readonly IntPtr CertificateStore;
    }
}

internal enum WinTrustUiChoice : uint
{
    None = 2
}

internal enum WinTrustRevocationChecks : uint
{
    WholeChain = 1
}

internal enum WinTrustUnionChoice : uint
{
    File = 1
}

internal enum WinTrustStateAction : uint
{
    Ignore = 0,
    Verify = 1,
    Close = 2
}

[Flags]
internal enum WinTrustProviderFlags : uint
{
    RevocationCheckChainExcludeRoot = 0x00000080,
    DisableMd2Md4 = 0x00002000
}

internal enum WindowsTrustStatus
{
    Trusted,
    Revoked,
    RevocationUnknown,
    RevocationOffline,
    Untrusted
}

internal sealed record WindowsTrustNativeResult(
    WindowsTrustStatus Status,
    TrustedWindowsSigner Signer,
    int NativeStatusCode);

internal sealed record WindowsExecutableFileIdentity(
    string CanonicalPath,
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256);

internal sealed record TrustedWindowsSigner(string Subject, string SimpleName)
{
    public static TrustedWindowsSigner Empty { get; } = new(string.Empty, string.Empty);
}
