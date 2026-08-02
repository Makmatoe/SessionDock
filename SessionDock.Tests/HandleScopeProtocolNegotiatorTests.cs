using System.Net;
using System.Text;
using System.Text.Json;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeProtocolNegotiatorTests
{
    private const string Token =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string[] V1Capabilities =
    [
        "handlescope.http.v1",
        "handlescope.plan.single-use.v1",
        "handlescope.policy.roblox-singleton-event.v1"
    ];
    private static readonly string[] V2Capabilities =
    [
        "handlescope.http.v1",
        "handlescope.http.v2",
        "handlescope.plan.single-use.v1",
        "handlescope.policy.roblox-singleton-event.v1"
    ];
    private static readonly string[] NativeV2Capabilities =
    [
        "handlescope.http.v1",
        "handlescope.http.v2",
        "handlescope.plan.single-use.v1",
        "handlescope.policy.roblox-singleton-event.v1",
        "handlescope.setup.native.v1"
    ];

    [Fact]
    public void MetadataParser_RequiresTheExactCanonicalSevenFieldContract()
    {
        using var document = JsonDocument.Parse(MetadataJson("0.2.2"));

        Assert.True(HandleScopeProtocolNegotiator.TryParseMetadataDocument(
            document.RootElement,
            out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(new Version(0, 2, 2), metadata.ProductVersion);
        Assert.Equal(["v1", "v2"], metadata.SupportedApiVersions);
        Assert.Equal(V2Capabilities, metadata.Capabilities);
    }

    [Theory]
    [InlineData("\"extra\":true,")]
    [InlineData("\"schemaVersion\":1,")]
    public void MetadataParser_RejectsExtraOrDuplicateFields(string injected)
    {
        var json = MetadataJson("0.2.2").Replace(
            "\"schemaVersion\":1,",
            $"\"schemaVersion\":1,{injected}",
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        Assert.False(HandleScopeProtocolNegotiator.TryParseMetadataDocument(
            document.RootElement,
            out _));
    }

    [Fact]
    public void MetadataParser_RejectsUnsortedCapabilities()
    {
        var json = MetadataJson("0.2.2").Replace(
            "\"handlescope.plan.single-use.v1\",\"handlescope.policy.roblox-singleton-event.v1\"",
            "\"handlescope.policy.roblox-singleton-event.v1\",\"handlescope.plan.single-use.v1\"",
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        Assert.False(HandleScopeProtocolNegotiator.TryParseMetadataDocument(
            document.RootElement,
            out _));
    }

    [Fact]
    public void AutoNegotiation_UsesPreferredCompiledAdapter()
    {
        var identity = Identity("0.2.2", ["v1", "v2"], V2Capabilities);
        var metadata = Metadata(
            "0.2.2",
            ["v1", "v2"],
            preferred: "v1",
            V2Capabilities);

        Assert.True(HandleScopeProtocolNegotiator.TryNegotiate(
            metadata,
            identity,
            HandleScopeSelection.Default,
            out var adapter));
        Assert.Equal("v1", adapter!.ApiVersion);
        Assert.Equal("/v1/handles/close", adapter.CloseEndpoint);
    }

    [Fact]
    public void Negotiation_RequiresExactNativeSetupCapabilityMetadata()
    {
        var identity = Identity(
            "0.3.0",
            ["v1", "v2"],
            NativeV2Capabilities);

        Assert.True(HandleScopeProtocolNegotiator.TryNegotiate(
            Metadata(
                "0.3.0",
                ["v1", "v2"],
                "v2",
                NativeV2Capabilities),
            identity,
            HandleScopeSelection.Default,
            out var adapter));
        Assert.Equal("v2", adapter!.ApiVersion);
        Assert.False(HandleScopeProtocolNegotiator.TryNegotiate(
            Metadata("0.3.0", ["v1", "v2"], "v2", V2Capabilities),
            identity,
            HandleScopeSelection.Default,
            out _));
    }

    [Fact]
    public void AutoNegotiation_FallsBackToHighestCompiledCommonAdapter()
    {
        string[] capabilities =
        [
            "handlescope.http.v1",
            "handlescope.http.v2",
            "handlescope.http.v3",
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        ];
        var identity = Identity("0.3.0", ["v1", "v2", "v3"], capabilities);
        var metadata = Metadata(
            "0.3.0",
            ["v1", "v2", "v3"],
            preferred: "v3",
            capabilities);

        Assert.True(HandleScopeProtocolNegotiator.TryNegotiate(
            metadata,
            identity,
            HandleScopeSelection.Default,
            out var adapter));
        Assert.Equal("v2", adapter!.ApiVersion);
    }

    [Theory]
    [InlineData("v1", "/v1/handles/close")]
    [InlineData("v2", "/v2/handles/close")]
    public void ExactNegotiation_UsesOnlyTheCompiledRequestedAdapter(
        string exactApi,
        string expectedEndpoint)
    {
        var identity = Identity("0.2.2", ["v1", "v2"], V2Capabilities);
        var metadata = Metadata(
            "0.2.2",
            ["v1", "v2"],
            preferred: "v2",
            V2Capabilities);
        var selection = new HandleScopeSelection(
            HandleScopeVersionSelectionMode.Exact,
            new Version(0, 2, 2),
            exactApi);

        Assert.True(HandleScopeProtocolNegotiator.TryNegotiate(
            metadata,
            identity,
            selection,
            out var adapter));
        Assert.Equal(expectedEndpoint, adapter!.CloseEndpoint);
    }

    [Fact]
    public void Negotiation_RejectsVersionPolicyAndCapabilityMismatches()
    {
        var identity = Identity("0.2.2", ["v1", "v2"], V2Capabilities);

        Assert.False(HandleScopeProtocolNegotiator.TryNegotiate(
            Metadata("0.2.3", ["v1", "v2"], "v2", V2Capabilities),
            identity,
            HandleScopeSelection.Default,
            out _));
        Assert.False(HandleScopeProtocolNegotiator.TryNegotiate(
            Metadata(
                "0.2.2",
                ["v1", "v2"],
                "v2",
                V2Capabilities,
                policy: "different-policy-v1"),
            identity,
            HandleScopeSelection.Default,
            out _));
        Assert.False(HandleScopeProtocolNegotiator.TryNegotiate(
            Metadata("0.2.2", ["v1"], "v1", V1Capabilities),
            identity,
            HandleScopeSelection.Default,
            out _));
    }

    [Fact]
    public void LegacyFallback_IsRestrictedToAuthenticatedV014IdentityAndV1()
    {
        var legacy = Identity("0.1.4", ["v1"], V1Capabilities);

        Assert.True(HandleScopeProtocolNegotiator.TryUseLegacyV014(
            legacy,
            HandleScopeSelection.Default,
            out var adapter));
        Assert.Equal("v1", adapter!.ApiVersion);
        Assert.False(HandleScopeProtocolNegotiator.TryUseLegacyV014(
            Identity("0.1.5", ["v1"], V1Capabilities),
            HandleScopeSelection.Default,
            out _));
        Assert.False(HandleScopeProtocolNegotiator.TryUseLegacyV014(
            legacy,
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.Automatic,
                null,
                "v2"),
            out _));
    }

    [Theory]
    [InlineData("0.1.4", HttpStatusCode.NotFound, true, "v1")]
    [InlineData("0.1.5", HttpStatusCode.NotFound, false, null)]
    [InlineData("0.2.2", HttpStatusCode.OK, true, "v2")]
    public async Task Bootstrapper_AuthenticatesMetadataAndRestrictsLegacy404(
        string version,
        HttpStatusCode metadataStatus,
        bool expectedSuccess,
        string? expectedApi)
    {
        var capabilities = version == "0.2.2" ? V2Capabilities : V1Capabilities;
        var apiContracts = version == "0.2.2" ? new[] { "v1", "v2" } : ["v1"];
        var identity = Identity(version, apiContracts, capabilities);
        var metadataJson = MetadataJson(
            version,
            apiContracts,
            version == "0.2.2" ? "v2" : "v1",
            capabilities);

        var result = await RunBootstrapperAsync(
            identity,
            metadataStatus,
            metadataJson);

        Assert.Equal(expectedSuccess, result.Connection is not null);
        Assert.Equal(expectedApi, result.Connection?.NegotiatedProtocol?.ApiVersion);
        Assert.Equal(["/v1/health", "/v1/metadata"], result.Paths);
        Assert.Null(result.Authorizations[0]);
        Assert.Equal($"Bearer {Token}", result.Authorizations[1]);
    }

    [Fact]
    public void LaunchHook_UsesOnlyTheNegotiatedCompiledEndpoint()
    {
        var connection = new HandleScopeConnection(
            new Uri("http://127.0.0.1:43120"),
            Token,
            "v1",
            123,
            DateTimeOffset.UtcNow)
        {
            NegotiatedProtocol = new HandleScopeProtocolAdapter(
                "v2",
                "/v2/handles/close",
                "handlescope.http.v2",
                2)
        };

        Assert.Equal(
            "/v2/handles/close",
            HandleScopeLaunchHook.CreateNegotiatedCloseEndpoint(connection)!
                .AbsolutePath);
        Assert.Null(HandleScopeLaunchHook.CreateNegotiatedCloseEndpoint(
            connection with
            {
                NegotiatedProtocol = new HandleScopeProtocolAdapter(
                    "v2",
                    "/attacker/endpoint",
                    "handlescope.http.v2",
                    2)
            }));
    }

    private static async Task<BootstrapResult> RunBootstrapperAsync(
        HandleScopeRuntimeIdentity identity,
        HttpStatusCode metadataStatus,
        string metadataJson)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-Protocol-{Guid.NewGuid():N}");
        var connectionPath = Path.Combine(root, "HandleScope", "connection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(connectionPath)!);
        await File.WriteAllTextAsync(
            connectionPath,
            $$"""
              {
                "baseUrl": "http://127.0.0.1:43120",
                "token": "{{Token}}",
                "apiVersion": "v1",
                "processId": 123,
                "startedAtUtc": "2026-08-02T00:00:00+00:00"
              }
              """,
            TestContext.Current.CancellationToken);
        try
        {
            using var handler = new ProtocolHandler(metadataStatus, metadataJson);
            using var client = new HttpClient(handler, disposeHandler: false);
            var bootstrapper = new HandleScopeApiBootstrapper(
                new HandleScopeConnectionLoader(
                    connectionPath,
                    root,
                    isReparsePoint: null),
                client,
                new ResolvedVerifier(identity),
                new HandleScopeSelectionStore(Path.Combine(root, "preferences.json")));
            var connection = await bootstrapper.GetExistingAsync(
                TestContext.Current.CancellationToken);
            return new(
                connection,
                handler.Paths.ToArray(),
                handler.Authorizations.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HandleScopeRuntimeIdentity Identity(
        string version,
        IReadOnlyList<string> apiContracts,
        IReadOnlyList<string> capabilities) => new(
        new Version(version),
        $"v{version}",
        apiContracts,
        capabilities);

    private static HandleScopeApiMetadata Metadata(
        string version,
        IReadOnlyList<string> apiContracts,
        string preferred,
        IReadOnlyList<string> capabilities,
        string policy = HandleScopeApiBootstrapper.RequiredPolicy) => new(
        new Version(version),
        "v1",
        apiContracts,
        preferred,
        [policy],
        capabilities);

    private static string MetadataJson(
        string version,
        IReadOnlyList<string>? apiContracts = null,
        string preferred = "v2",
        IReadOnlyList<string>? capabilities = null)
    {
        apiContracts ??= ["v1", "v2"];
        capabilities ??= V2Capabilities;
        return $$"""
          {"schemaVersion":1,"productVersion":"{{version}}","discoveryApiVersion":"v1","supportedApiVersions":{{JsonSerializer.Serialize(apiContracts)}},"preferredApiVersion":"{{preferred}}","policies":["{{HandleScopeApiBootstrapper.RequiredPolicy}}"],"capabilities":{{JsonSerializer.Serialize(capabilities)}}}
          """;
    }

    private sealed record BootstrapResult(
        HandleScopeConnection? Connection,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string?> Authorizations);

    private sealed class ResolvedVerifier(HandleScopeRuntimeIdentity identity) :
        IHandleScopeResolvedProcessVerifier
    {
        public bool IsExpected(HandleScopeConnection connection) => true;

        public bool TryResolveExpected(
            HandleScopeConnection connection,
            out HandleScopeRuntimeIdentity? runtimeIdentity)
        {
            runtimeIdentity = identity;
            return true;
        }
    }

    private sealed class ProtocolHandler(
        HttpStatusCode metadataStatus,
        string metadataJson) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var response = request.RequestUri.AbsolutePath switch
            {
                "/v1/health" => JsonResponse(
                    HttpStatusCode.OK,
                    "{\"status\":\"ready\",\"apiVersion\":\"v1\",\"policy\":\"roblox-singleton-event-v1\"}"),
                "/v1/metadata" => JsonResponse(metadataStatus, metadataJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage JsonResponse(
            HttpStatusCode status,
            string json) => new(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
