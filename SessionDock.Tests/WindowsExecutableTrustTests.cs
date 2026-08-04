using Microsoft.Win32.SafeHandles;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WindowsExecutableTrustTests
{
    private static readonly TrustedWindowsSigner RobloxSigner = new(
        "CN=Roblox Corporation, O=Roblox Corporation, C=US",
        "Roblox Corporation");

    [Fact]
    public void SuccessfulOnlineTrust_IsReturnedAndBoundedCached()
    {
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"),
            () => now);

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var first));
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var cached));

        Assert.Equal(RobloxSigner, first);
        Assert.Equal(RobloxSigner, cached);
        Assert.Equal(1, native.Requests);
    }

    [Theory]
    [InlineData((int)WindowsTrustStatus.Revoked)]
    [InlineData((int)WindowsTrustStatus.RevocationOffline)]
    [InlineData((int)WindowsTrustStatus.RevocationUnknown)]
    [InlineData((int)WindowsTrustStatus.Untrusted)]
    public void NonSuccessfulTrustStatus_FailsClosedAndIsNotCached(
        int statusValue)
    {
        var status = (WindowsTrustStatus)statusValue;
        var native = new FakeNativeVerifier(
            new(status, TrustedWindowsSigner.Empty, -1));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"));

        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out _));
        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void NativeApiFailure_FailsClosed()
    {
        var verifier = new WindowsExecutableTrustVerifier(
            new ThrowingNativeVerifier(),
            _ => Identity("AA"));

        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var signer));
        Assert.Equal(TrustedWindowsSigner.Empty, signer);
    }

    [Fact]
    public void ChangedFileIdentity_InvalidatesSuccessfulCache()
    {
        var hash = "AA";
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity(hash));

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        hash = "BB";
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void ForceRefresh_RevalidatesUnchangedFileBeforeSensitiveAction()
    {
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"));

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", true, out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void ForceRefreshFailure_InvalidatesLegacyPositiveCache()
    {
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"));

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        native.Result = new(
            WindowsTrustStatus.Revoked,
            TrustedWindowsSigner.Empty,
            unchecked((int)0x80092010));
        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", true, out _));
        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));

        Assert.Equal(3, native.Requests);
    }

    [Fact]
    public void SuccessfulCache_Expires()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"),
            () => now);

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        now += WindowsExecutableTrustVerifier.SuccessfulValidationLifetime +
            TimeSpan.FromSeconds(1);
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void FileProof_UnchangedCacheHitSkipsNativeTrust()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(
                path,
                forceRefresh: false,
                out _));
            Assert.True(verifier.TryGetTrustedSigner(
                path,
                forceRefresh: false,
                out _));

            Assert.Equal(1, native.Requests);
            Assert.Equal([true], native.FileHandlesProvided);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_SuccessfulCacheExpiresWithoutChangingFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var now = new DateTimeOffset(
                2026,
                8,
                4,
                12,
                0,
                0,
                TimeSpan.Zero);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(
                native,
                getFileIdentity: null,
                getUtcNow: () => now);

            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));
            now += WindowsExecutableTrustVerifier
                .SuccessfulValidationLifetime + TimeSpan.FromSeconds(1);
            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));

            Assert.Equal(2, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_InPlaceWriteInvalidatesCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));
            File.WriteAllBytes(path, [9, 9, 9, 9]);
            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));

            Assert.Equal(2, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_PathReplacementInvalidatesCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            var replacement = Path.Combine(root, "replacement.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            File.WriteAllBytes(replacement, [5, 6, 7, 8]);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));
            File.Move(replacement, path, overwrite: true);
            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));

            Assert.Equal(2, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_ForceRefreshRevalidatesAuthenticode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));
            Assert.True(verifier.TryGetTrustedSigner(path, true, out _));

            Assert.Equal(2, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_ForceRefreshFailureInvalidatesPositiveCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(path, false, out _));
            native.Result = new(
                WindowsTrustStatus.Revoked,
                TrustedWindowsSigner.Empty,
                unchecked((int)0x80092010));
            Assert.False(verifier.TryGetTrustedSigner(path, true, out _));
            Assert.False(verifier.TryGetTrustedSigner(path, false, out _));

            Assert.Equal(3, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileProof_RetainedHandleControlsProofAndWinTrust()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            var retainedPath = Path.Combine(root, "retained.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            File.WriteAllBytes(retainedPath, [5, 6, 7, 8]);
            using var retainedHandle = File.OpenHandle(
                retainedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            var native = new FakeNativeVerifier(
                new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
            var verifier = new WindowsExecutableTrustVerifier(native);

            Assert.True(verifier.TryGetTrustedSigner(
                path,
                retainedHandle,
                forceRefresh: false,
                out _));
            File.WriteAllBytes(path, [9, 9, 9, 9]);
            Assert.True(verifier.TryGetTrustedSigner(
                path,
                retainedHandle,
                forceRefresh: false,
                out _));

            Assert.Equal(1, native.Requests);
            Assert.Same(retainedHandle, Assert.Single(native.FileHandles));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileProof_ParallelOrdinaryChecksCollapseBehindForcedCheck()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            using var native = new BlockingNativeVerifier();
            var verifier = new WindowsExecutableTrustVerifier(native);
            var forced = Task.Run(() => verifier.TryGetTrustedSigner(
                path,
                forceRefresh: true,
                out _));
            Assert.True(native.WaitUntilEntered(TimeSpan.FromSeconds(5)));

            using var followersStarted = new CountdownEvent(8);
            var followers = Enumerable.Range(0, 8)
                .Select(index => Task.Factory.StartNew(
                    () =>
                    {
                        followersStarted.Signal();
                        return verifier.TryGetTrustedSigner(
                            path,
                            forceRefresh: false,
                            out _);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            Assert.True(followersStarted.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            native.Release();

            Assert.True(await forced);
            Assert.All(await Task.WhenAll(followers), Assert.True);
            Assert.Equal(1, native.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80092010), (int)WindowsTrustStatus.Revoked)]
    [InlineData(unchecked((int)0x80092012), (int)WindowsTrustStatus.RevocationUnknown)]
    [InlineData(unchecked((int)0x80092013), (int)WindowsTrustStatus.RevocationOffline)]
    [InlineData(unchecked((int)0x800B010E), (int)WindowsTrustStatus.RevocationUnknown)]
    public void WinTrustStatusMapping_RejectsRevocationFailures(
        int nativeStatus,
        int expectedValue)
    {
        var expected = (WindowsTrustStatus)expectedValue;
        Assert.Equal(expected, WinTrustNativeVerifier.MapStatus(nativeStatus));
    }

    [Fact]
    public void ProviderFlags_EnableChainRevocationWithoutUnsupportedOrCacheOnlyFlags()
    {
        var flags =
            WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
            WinTrustProviderFlags.DisableMd2Md4;

        Assert.Equal((WinTrustProviderFlags)0x2080, flags);
        Assert.Equal((WinTrustProviderFlags)0, flags & (WinTrustProviderFlags)0x0100);
        Assert.Equal((WinTrustProviderFlags)0, flags & (WinTrustProviderFlags)0x1000);
        Assert.Equal(WinTrustRevocationChecks.WholeChain, (WinTrustRevocationChecks)1);
    }

    [Fact]
    public void FileProof_ContentDigestParticipatesInCacheIdentity()
    {
        var first = new WindowsExecutableFileStamp(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            Length: 1024,
            LastWriteTime: 1,
            ChangeTime: 2,
            VolumeSerialNumber: 3,
            FileIdLow: 4,
            FileIdHigh: 5,
            Sha256Part0: 6);
        var changedContent = first with { Sha256Part0 = 7 };

        Assert.False(WindowsExecutableFileStampComparer.Instance.Equals(
            first,
            changedContent));
    }

    private static WindowsExecutableFileIdentity Identity(string hash) => new(
        @"C:\Roblox\RobloxPlayerBeta.exe",
        1024,
        638889552000000000,
        hash.PadRight(64, hash[0]));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.WindowsTrust.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeNativeVerifier(WindowsTrustNativeResult result) :
        IWindowsTrustNativeVerifier
    {
        public WindowsTrustNativeResult Result { get; set; } = result;

        public int Requests { get; private set; }

        public List<bool> FileHandlesProvided { get; } = [];

        public List<SafeFileHandle?> FileHandles { get; } = [];

        public WindowsTrustNativeResult Verify(
            string path,
            SafeFileHandle? fileHandle)
        {
            FileHandles.Add(fileHandle);
            FileHandlesProvided.Add(
                fileHandle is not null && !fileHandle.IsInvalid);
            Requests++;
            return Result;
        }
    }

    private sealed class ThrowingNativeVerifier : IWindowsTrustNativeVerifier
    {
        public WindowsTrustNativeResult Verify(
            string path,
            SafeFileHandle? fileHandle)
        {
            _ = path;
            _ = fileHandle;
            throw new InvalidOperationException("simulated native failure");
        }
    }

    private sealed class BlockingNativeVerifier :
        IWindowsTrustNativeVerifier,
        IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _requests;

        internal int Requests => Volatile.Read(ref _requests);

        public WindowsTrustNativeResult Verify(
            string path,
            SafeFileHandle? fileHandle)
        {
            _ = path;
            _ = fileHandle;
            Interlocked.Increment(ref _requests);
            _entered.Set();
            _release.Wait();
            return new WindowsTrustNativeResult(
                WindowsTrustStatus.Trusted,
                RobloxSigner,
                0);
        }

        internal bool WaitUntilEntered(TimeSpan timeout) =>
            _entered.Wait(timeout);

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
