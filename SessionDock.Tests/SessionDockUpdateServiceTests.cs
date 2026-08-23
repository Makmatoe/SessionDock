using System.Security.Cryptography;
using SessionDock.ReleaseTrust;
using SessionDock.Services;
using Velopack;

namespace SessionDock.Tests;

public sealed class SessionDockUpdateServiceTests : IDisposable
{
    private const string PackageFile =
        "SessionDockApp-3.1.2-win-x64-sessiondock-full.nupkg";
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.UpdateService.{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateLocalPackageIdentityAsync_NullFeedHashUsesLocalPackage()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        packageBytes[^1] = 0x5a;
        await File.WriteAllBytesAsync(
            Path.Combine(packagesDirectory, PackageFile),
            packageBytes,
            TestContext.Current.CancellationToken);
        var pending = CreatePendingAsset(PackageFile);

        var identity = await SessionDockUpdateService
            .CreateLocalPackageIdentityAsync(
                pending,
                packagesDirectory,
                TestContext.Current.CancellationToken);

        Assert.Equal("3.1.2", identity.Version);
        Assert.Equal(PackageFile, identity.FileName);
        Assert.Equal(packageBytes.LongLength, identity.Size);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(packageBytes)),
            identity.Sha256);
        Assert.NotEmpty(identity.Sha256);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(CreateDescriptor(identity), key);
        var verified = ReleaseDescriptorPolicy.Verify(
            ReleaseDescriptorPolicy.Serialize(descriptor),
            identity,
            key.ExportSubjectPublicKeyInfoPem(),
            PublishedAt.AddMinutes(1));
        Assert.Equal(new Version(3, 1, 2), verified.Version);
    }

    [Fact]
    public async Task CreateLocalPackageIdentityAsync_ActualHashMismatchIsRejected()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        packageBytes[0] = 0x5a;
        await File.WriteAllBytesAsync(
            Path.Combine(packagesDirectory, PackageFile),
            packageBytes,
            TestContext.Current.CancellationToken);
        var identity = await SessionDockUpdateService
            .CreateLocalPackageIdentityAsync(
                CreatePendingAsset(PackageFile),
                packagesDirectory,
                TestContext.Current.CancellationToken);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateDescriptor(identity) with
            {
                PackageSha256 = new string('A', SHA256.HashSizeInBytes * 2)
            },
            key);

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                identity,
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));

        Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateLocalPackageIdentityAsync_ActualSizeMismatchIsRejected()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        await File.WriteAllBytesAsync(
            Path.Combine(packagesDirectory, PackageFile),
            packageBytes,
            TestContext.Current.CancellationToken);
        var identity = await SessionDockUpdateService
            .CreateLocalPackageIdentityAsync(
                CreatePendingAsset(PackageFile),
                packagesDirectory,
                TestContext.Current.CancellationToken);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateDescriptor(identity) with
            {
                PackageSize = identity.Size + 1
            },
            key);

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                identity,
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));

        Assert.Contains("size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateLocalPackageIdentityAsync_EmptyFileStillReturnsSha256()
    {
        var packagesDirectory = CreatePackagesDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(packagesDirectory, PackageFile),
            [],
            TestContext.Current.CancellationToken);

        var identity = await SessionDockUpdateService
            .CreateLocalPackageIdentityAsync(
                CreatePendingAsset(PackageFile),
                packagesDirectory,
                TestContext.Current.CancellationToken);

        Assert.Equal(SHA256.HashSizeInBytes * 2, identity.Sha256.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData([])),
            identity.Sha256);
    }

    [Fact]
    public async Task CreateLocalPackageIdentityAsync_TraversalOutsidePackagesIsRejected()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var outsideFile = Path.Combine(_root, "outside.nupkg");
        await File.WriteAllBytesAsync(
            outsideFile,
            [0x01],
            TestContext.Current.CancellationToken);
        var pending = CreatePendingAsset(
            Path.Combine("..", Path.GetFileName(outsideFile)));

        var exception = await Assert.ThrowsAsync<ReleaseTrustException>(() =>
            SessionDockUpdateService.CreateLocalPackageIdentityAsync(
                pending,
                packagesDirectory,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            "safely",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenVerifiedPackageLeaseAsync_ReturnsWriteBlockingLease()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packagePath = Path.Combine(packagesDirectory, PackageFile);
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        packageBytes[^1] = 0x5a;
        await File.WriteAllBytesAsync(
            packagePath,
            packageBytes,
            TestContext.Current.CancellationToken);
        var release = CreateVerifiedRelease(packageBytes);
        var lease = await SessionDockUpdateService.OpenVerifiedPackageLeaseAsync(
            CreatePendingAsset(PackageFile),
            release,
            packagesDirectory,
            TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => File.Open(
            packagePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete));
        Assert.Throws<IOException>(() => File.Move(
            packagePath,
            packagePath + ".moved"));
        Assert.Throws<IOException>(() => File.Delete(packagePath));

        await lease.DisposeAsync();
        using var writable = File.Open(
            packagePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task OpenVerifiedPackageLeaseAsync_ChangedBytesAreRejected()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packagePath = Path.Combine(packagesDirectory, PackageFile);
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        packageBytes[0] = 0x11;
        await File.WriteAllBytesAsync(
            packagePath,
            packageBytes,
            TestContext.Current.CancellationToken);
        var release = CreateVerifiedRelease(packageBytes);
        packageBytes[0] = 0x22;
        await File.WriteAllBytesAsync(
            packagePath,
            packageBytes,
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ReleaseTrustException>(() =>
            SessionDockUpdateService.OpenVerifiedPackageLeaseAsync(
                CreatePendingAsset(PackageFile),
                release,
                packagesDirectory,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            "changed",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenVerifiedPackageLeaseAsync_MismatchedAssetIsRejected()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packageBytes = new byte[ReleaseDescriptorPolicy.MinimumPackageSize];
        await File.WriteAllBytesAsync(
            Path.Combine(packagesDirectory, PackageFile),
            packageBytes,
            TestContext.Current.CancellationToken);
        var release = CreateVerifiedRelease(packageBytes);
        await Assert.ThrowsAsync<ReleaseTrustException>(() =>
            SessionDockUpdateService.OpenVerifiedPackageLeaseAsync(
                CreatePendingAsset("SessionDockApp-3.1.2-other-full.nupkg"),
                release,
                packagesDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProcessLifetimePackageLease_ScheduleFailureReleasesFile()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packagePath = Path.Combine(packagesDirectory, PackageFile);
        File.WriteAllBytes(packagePath, [0x01]);
        using var slot = new ProcessLifetimePackageLease();
        var lease = OpenReadLease(packagePath);

        Assert.Throws<InvalidOperationException>(() =>
            slot.Schedule(
                lease,
                () => throw new InvalidOperationException("schedule failed")));

        using var writable = File.Open(
            packagePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public void ProcessLifetimePackageLease_RejectsSecondScheduleAndRetainsFirst()
    {
        var packagesDirectory = CreatePackagesDirectory();
        var packagePath = Path.Combine(packagesDirectory, PackageFile);
        File.WriteAllBytes(packagePath, [0x01]);
        using var slot = new ProcessLifetimePackageLease();
        var firstLease = OpenReadLease(packagePath);
        var scheduled = 0;
        slot.Schedule(firstLease, () => scheduled++);
        var secondLease = OpenReadLease(packagePath);

        Assert.Throws<InvalidOperationException>(() =>
            slot.Schedule(secondLease, () => scheduled++));
        Assert.Equal(1, scheduled);
        Assert.Throws<IOException>(() => File.Open(
            packagePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete));

        slot.Dispose();
        using var writable = File.Open(
            packagePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreatePackagesDirectory()
    {
        var packagesDirectory = Path.Combine(_root, "packages");
        Directory.CreateDirectory(packagesDirectory);
        return packagesDirectory;
    }

    private static FileStream OpenReadLease(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);

    private static VelopackAsset CreatePendingAsset(string fileName) => new()
    {
        PackageId = ReleaseDescriptorPolicy.VelopackPackageId,
        Version = SemanticVersion.Parse("3.1.2"),
        Type = VelopackAssetType.Full,
        FileName = fileName,
        SHA1 = string.Empty,
        SHA256 = null!,
        Size = 17
    };

    private static ReleaseDescriptor CreateDescriptor(
        ReleaseAssetIdentity identity) => new(
        ReleaseDescriptorPolicy.SchemaVersion,
        ReleaseDescriptorPolicy.Product,
        ReleaseDescriptorPolicy.Repository,
        ReleaseDescriptorPolicy.Channel,
        ReleaseDescriptorPolicy.KeyId,
        identity.Version,
        $"v{identity.Version}",
        PublishedAt.ToString("O"),
        identity.FileName,
        identity.Size,
        identity.Sha256,
        "Pending update verification.",
        string.Empty);

    private static ReleaseDescriptor SignDescriptor(
        ReleaseDescriptor descriptor,
        ECDsa key) => descriptor with
    {
        Signature = Convert.ToBase64String(key.SignData(
            ReleaseDescriptorPolicy.CreateCanonicalPayload(descriptor),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    };

    private static VerifiedReleaseDescriptor CreateVerifiedRelease(
        byte[] packageBytes)
    {
        var identity = new ReleaseAssetIdentity(
            "3.1.2",
            PackageFile,
            packageBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(packageBytes)));
        return new VerifiedReleaseDescriptor(
            CreateDescriptor(identity),
            new Version(3, 1, 2),
            PublishedAt);
    }
}
