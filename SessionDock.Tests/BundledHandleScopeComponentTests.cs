using System.Net;
using System.Text.Json;
using HandleScope.Api;
using HandleScope.Models;
using SessionDock.HandleScope;

namespace SessionDock.Tests;

public sealed class BundledHandleScopeComponentTests
{
    [Fact]
    public async Task ApiHost_UsesEphemeralIpv4LoopbackAndReportsPinnedVersion()
    {
        var token = HandleScopeBroker.CreateToken();
        await using var app = ApiHost.Build(new ApiRuntimeOptions(
            0,
            token,
            Policy: new TestPolicy()));

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = Assert.Single(app.Urls);
            var baseUrl = new Uri(address);
            Assert.Equal(Uri.UriSchemeHttp, baseUrl.Scheme);
            Assert.Equal(IPAddress.Loopback.ToString(), baseUrl.Host);
            Assert.InRange(baseUrl.Port, 1, IPEndPoint.MaxPort);

            using var client = new HttpClient
            {
                BaseAddress = baseUrl,
                Timeout = TimeSpan.FromSeconds(5)
            };
            using var response = await client.GetAsync(
                "/v2/health",
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(
                TestContext.Current.CancellationToken);
            using var json = await JsonDocument.ParseAsync(
                body,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                "ready",
                json.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                HandleScopeBroker.ComponentVersion,
                json.RootElement.GetProperty("productVersion").GetString());
            Assert.Equal(
                TestPolicy.Id,
                json.RootElement.GetProperty("policy").GetString());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Handshake_IsOneBoundedStrictJsonLine()
    {
        const string token =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var startedAt = new DateTimeOffset(
            2026,
            8,
            2,
            12,
            34,
            56,
            TimeSpan.Zero);
        await using var stream = new MemoryStream();

        await HandleScopeBroker.WriteHandshakeAsync(
            stream,
            new Uri("http://127.0.0.1:51824/"),
            token,
            4242,
            startedAt,
            TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.InRange(bytes.Length, 1, HandleScopeBroker.MaximumHandshakeBytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));

        using var json = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            [
                "schemaVersion",
                "componentVersion",
                "apiVersion",
                "baseUrl",
                "token",
                "processId",
                "startedAtUtc"
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            HandleScopeBroker.HandshakeSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            HandleScopeBroker.ComponentVersion,
            root.GetProperty("componentVersion").GetString());
        Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
        Assert.Equal(
            "http://127.0.0.1:51824",
            root.GetProperty("baseUrl").GetString());
        Assert.Equal(token, root.GetProperty("token").GetString());
        Assert.Equal(4242, root.GetProperty("processId").GetInt32());
        Assert.Equal(
            startedAt,
            root.GetProperty("startedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Handshake_RejectsNonCanonicalLoopbackUrlBeforeWriting()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandleScopeBroker.WriteHandshakeAsync(
                stream,
                new Uri("http://localhost:51824/"),
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                4242,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(false, 1, "S-1-5-21-1", true)]
    [InlineData(true, 1, "S-1-5-21-1", false)]
    [InlineData(false, 0, "S-1-5-21-1", false)]
    [InlineData(false, 1, "S-1-5-18", false)]
    [InlineData(false, 1, "S-1-5-19", false)]
    [InlineData(false, 1, "S-1-5-20", false)]
    public void RuntimeGuard_RequiresStandardInteractiveUser(
        bool elevated,
        int sessionId,
        string ownerSid,
        bool expected)
    {
        var identity = CreateIdentity(elevated, (uint)sessionId, ownerSid);

        Assert.Equal(expected, HandleScopeBrokerRuntimeGuard.IsAllowed(identity));
    }

    [Fact]
    public void RuntimeGuard_UsesDedicatedSessionDockInstanceName()
    {
        var first = HandleScopeBrokerRuntimeGuard.GetInstanceName(
            CreateIdentity(false, 3, "S-1-5-21-1"));
        var second = HandleScopeBrokerRuntimeGuard.GetInstanceName(
            CreateIdentity(false, 4, "S-1-5-21-1"));

        Assert.StartsWith("Local\\SessionDock.HandleScope.Broker.", first);
        Assert.False(first.StartsWith("Local\\HandleScope.Api.", StringComparison.Ordinal));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Token_IsCanonical256BitBase64Url()
    {
        var token = HandleScopeBroker.CreateToken();

        Assert.Equal(43, token.Length);
        Assert.All(token, character => Assert.True(
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
    }

    private static ProcessIdentity CreateIdentity(
        bool elevated,
        uint sessionId,
        string ownerSid) =>
        new(
            1234,
            "SessionDock",
            @"C:\Program Files\SessionDock\SessionDock.exe",
            sessionId,
            ownerSid,
            elevated,
            DateTime.UtcNow.ToFileTimeUtc());

    private sealed class TestPolicy : IHandleAutomationPolicy
    {
        internal const string Id = "test-policy";

        public string PolicyId => Id;

        public int MaximumProcessCount => 1;

        public AutomationRequestAuthorization AuthorizeRequest(
            CloseHandlesRequest request) =>
            AutomationRequestAuthorization.Denied("policy_denied");

        public AutomationProcessAuthorization AuthorizeProcess(
            int processId,
            AutomationRequestAuthorization request) =>
            AutomationProcessAuthorization.Denied("policy_denied");
    }
}
