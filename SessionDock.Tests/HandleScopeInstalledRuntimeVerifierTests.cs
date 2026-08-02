using System.Net;
using System.Security.Cryptography;
using System.Text;
using SessionDock.ReleaseTrust;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeInstalledRuntimeVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.RuntimeVerifier.{Guid.NewGuid():N}");

    [Fact]
    public void TryIdentify_MultipleVersionsIncludingSameSizeReturnsExactIdentity()
    {
        Directory.CreateDirectory(_root);
        var firstBytes = Encoding.UTF8.GetBytes("runtime-one");
        var secondBytes = Encoding.UTF8.GetBytes("runtime-two");
        var thirdBytes = Encoding.UTF8.GetBytes("runtime-three");
        Assert.Equal(firstBytes.Length, secondBytes.Length);
        var firstIdentity = CreateIdentity("0.1.3", "v1");
        var secondIdentity = CreateIdentity("0.1.4", "v2");
        var thirdIdentity = CreateIdentity("0.1.5", "v2");
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            [
                CreateCandidate(firstBytes, firstIdentity),
                CreateCandidate(secondBytes, secondIdentity),
                CreateCandidate(thirdBytes, thirdIdentity)
            ]);
        var firstPath = Write("first.exe", firstBytes);
        var secondPath = Write("second.exe", secondBytes);
        var thirdPath = Write("third.exe", thirdBytes);

        Assert.True(verifier.TryIdentify(firstPath, out var firstMatch));
        Assert.Equal(firstIdentity, firstMatch);
        Assert.True(verifier.TryIdentify(secondPath, out var secondMatch));
        Assert.Equal(secondIdentity, secondMatch);
        Assert.True(verifier.TryIdentify(thirdPath, out var thirdMatch));
        Assert.Equal(thirdIdentity, thirdMatch);
        Assert.True(verifier.IsAuthorized(secondPath));
    }

    [Fact]
    public void TryIdentify_UnknownSameSizeHashFailsClosedWithoutIdentity()
    {
        Directory.CreateDirectory(_root);
        var expected = Encoding.UTF8.GetBytes("runtime-one");
        var sameSizeUnknown = Encoding.UTF8.GetBytes("runtime-bad");
        Assert.Equal(expected.Length, sameSizeUnknown.Length);
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            [CreateCandidate(expected, CreateIdentity("0.1.4", "v1"))]);
        var path = Write("unknown.exe", sameSizeUnknown);

        var identified = verifier.TryIdentify(path, out var identity);

        Assert.False(identified);
        Assert.Null(identity);
        Assert.False(verifier.IsAuthorized(path));
    }

    [Fact]
    public void Constructor_RejectsDuplicateVersionOrAmbiguousExecutableIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes("same-runtime");
        var first = CreateCandidate(bytes, CreateIdentity("0.1.3", "v1"));
        var duplicateVersion = CreateCandidate(
            Encoding.UTF8.GetBytes("other-bytes!"),
            CreateIdentity("0.1.3", "v2"));
        var ambiguousRuntime = CreateCandidate(
            bytes,
            CreateIdentity("0.1.4", "v2"));

        Assert.Throws<ArgumentException>(() =>
            new HandleScopeInstalledRuntimeVerifier(
                [first, duplicateVersion]));
        Assert.Throws<ArgumentException>(() =>
            new HandleScopeInstalledRuntimeVerifier(
                [first, ambiguousRuntime]));
    }

    [Fact]
    public void TryIdentify_EmptyCompatibleCatalogFailsClosedWithoutConstructionError()
    {
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            Array.Empty<HandleScopeRuntimeIdentityCandidate>());

        var identified = verifier.TryIdentify(
            Path.Combine(_root, "not-installed.exe"),
            out var identity);

        Assert.False(identified);
        Assert.Null(identity);
        Assert.False(verifier.IsAuthorized(
            Path.Combine(_root, "not-installed.exe")));
    }

    [Fact]
    public void TryIdentify_ExpiredEmbeddedCatalogCannotAuthorizeKnownHash()
    {
        Directory.CreateDirectory(_root);
        var bytes = Encoding.UTF8.GetBytes("expired-catalog-runtime");
        var release = HandleScopeCompatibilityCatalogTestData.CreateRelease(
            executableSize: bytes.LongLength,
            executableSha256: Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant());
        var now = DateTimeOffset.UtcNow;
        var expired = HandleScopeCompatibilityCatalogTestData.CreateCatalog(
            [release],
            generatedAt: now.AddDays(-30),
            expiresAt: now.AddMinutes(-1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = new HandleScopeCompatibilityCatalogService(
            new NoNetworkHandler(),
            Path.Combine(_root, "expired-catalog.json"),
            HandleScopeCompatibilityCatalogPolicy.Serialize(expired),
            key.ExportSubjectPublicKeyInfoPem());
        var verifier = new HandleScopeInstalledRuntimeVerifier(service);
        var executablePath = Write("expired-runtime.exe", bytes);

        var identified = verifier.TryIdentify(executablePath, out var identity);

        Assert.False(identified);
        Assert.Null(identity);
        Assert.False(verifier.IsAuthorized(executablePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static HandleScopeRuntimeIdentity CreateIdentity(
        string version,
        string apiContract) => new(
        new Version(version),
        $"v{version}",
        [apiContract],
        [
            $"handlescope.http.{apiContract}",
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        ]);

    private static HandleScopeRuntimeIdentityCandidate CreateCandidate(
        byte[] bytes,
        HandleScopeRuntimeIdentity identity) => new(
        bytes.LongLength,
        SHA256.HashData(bytes),
        identity);

    private string Write(string fileName, byte[] bytes)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
    }
}
