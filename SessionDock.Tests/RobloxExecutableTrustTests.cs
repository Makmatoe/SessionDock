using Microsoft.Win32.SafeHandles;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxExecutableTrustTests
{
    private const string LocalRoot = @"C:\TestData\AppData\Local\Roblox\Versions";
    private const string ProgramRoot = @"C:\Program Files\Roblox";
    private static readonly string ValidPath = Path.Combine(
        LocalRoot,
        "version-a",
        "RobloxPlayerBeta.exe");

    [Fact]
    public void ExpectedPathAndRobloxSigner_AreAccepted()
    {
        var requestedForceRefresh = false;
        var trusted = RobloxExecutableTrust.IsTrustedPlayerPath(
            ValidPath,
            LocalRoot,
            ProgramRoot,
            forceRefresh: true,
            TryGetSigner);

        Assert.True(trusted);
        Assert.True(requestedForceRefresh);
        return;

        bool TryGetSigner(
            string path,
            out TrustedWindowsSigner signer,
            bool forceRefresh)
        {
            requestedForceRefresh = forceRefresh;
            signer = new("CN=Roblox Corporation", "Roblox Corporation");
            return true;
        }
    }

    [Theory]
    [InlineData(@"C:\Temp\RobloxPlayerBeta.exe")]
    [InlineData(@"C:\TestData\AppData\Local\Roblox\Versions\version-a\NotRoblox.exe")]
    public void WrongLocationOrFilename_IsRejectedWithoutNativeTrust(string path)
    {
        var nativeCalled = false;
        var trusted = RobloxExecutableTrust.IsTrustedPlayerPath(
            path,
            LocalRoot,
            ProgramRoot,
            forceRefresh: false,
            TryGetSigner);

        Assert.False(trusted);
        Assert.False(nativeCalled);
        return;

        bool TryGetSigner(
            string candidate,
            out TrustedWindowsSigner signer,
            bool forceRefresh)
        {
            nativeCalled = true;
            signer = new("CN=Roblox Corporation", "Roblox Corporation");
            return true;
        }
    }

    [Fact]
    public void DifferentlyNamedSigner_IsRejected()
    {
        var trusted = RobloxExecutableTrust.IsTrustedPlayerPath(
            ValidPath,
            LocalRoot,
            ProgramRoot,
            forceRefresh: false,
            TryGetSigner);

        Assert.False(trusted);
        return;

        static bool TryGetSigner(
            string path,
            out TrustedWindowsSigner signer,
            bool forceRefresh)
        {
            signer = new("CN=Unrelated Publisher", "Unrelated Publisher");
            return true;
        }
    }

    [Fact]
    public void RetainedHandleOverload_ForwardsExactHandleAndRefreshRequest()
    {
        using var retainedHandle = File.OpenHandle(
            typeof(RobloxExecutableTrustTests).Assembly.Location,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        SafeFileHandle? requestedHandle = null;
        var requestedForceRefresh = false;

        var trusted = RobloxExecutableTrust.IsTrustedPlayerPath(
            ValidPath,
            LocalRoot,
            ProgramRoot,
            retainedHandle,
            forceRefresh: true,
            TryGetSigner);

        Assert.True(trusted);
        Assert.Same(retainedHandle, requestedHandle);
        Assert.True(requestedForceRefresh);
        return;

        bool TryGetSigner(
            string path,
            SafeFileHandle handle,
            out TrustedWindowsSigner signer,
            bool forceRefresh)
        {
            requestedHandle = handle;
            requestedForceRefresh = forceRefresh;
            signer = new("CN=Roblox Corporation", "Roblox Corporation");
            return true;
        }
    }
}
