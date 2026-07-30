using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeIntegrationServiceTests
{
    private const int ApiProcessId = 2468;
    private const string ValidToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task InspectAsync_MissingExactInstall_ReturnsNotInstalled()
    {
        using var environment = new TestEnvironment();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var result = await service.InspectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HandleScopeIntegrationState.NotInstalled, result.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InspectAsync_NeverTestsTheConnectionOrProcess()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(
            _ => throw new InvalidOperationException("HTTP must remain explicit."));
        using var service = environment.CreateService(
            handler,
            isExpectedProcess: (_, _) =>
                throw new InvalidOperationException("Process inspection must remain explicit."));

        var result = await service.InspectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            result.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InspectAsync_DisabledSetting_ReturnsRunningDisabledWithoutProbing()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: false);
        environment.WriteConnection();
        var processInspectionCount = 0;
        using var handler = new RecordingHandler(
            _ => throw new InvalidOperationException("HTTP requires opt-in."));
        using var service = environment.CreateService(
            handler,
            isExpectedProcess: (_, _) =>
            {
                processInspectionCount++;
                return true;
            });

        var result = await service.InspectAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.RunningDisabled,
            result.State);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, processInspectionCount);
    }

    [Fact]
    public async Task TestConnectionAsync_MissingDiscovery_ReturnsInstalledStopped()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            result.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_UnexpectedProcessIdentityStopsBeforeNetwork()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(
            handler,
            isExpectedProcess: (_, _) => false);

        var result = await service.TestConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            result.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_DisabledSetting_ReturnsRunningDisabledWithoutProbing()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: false);
        environment.WriteConnection();
        var processInspectionCount = 0;
        using var handler = new RecordingHandler(
            _ => throw new InvalidOperationException("HTTP requires opt-in."));
        using var service = environment.CreateService(
            handler,
            isExpectedProcess: (_, _) =>
            {
                processInspectionCount++;
                return true;
            });

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.RunningDisabled,
            result.State);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, processInspectionCount);
    }

    [Fact]
    public async Task TestConnectionAsync_HealthyApiAndEnabledSetting_ReturnsReady()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HandleScopeIntegrationState.Ready, result.State);
    }

    [Theory]
    [InlineData("v2", "roblox-singleton-event-v1")]
    [InlineData("v1", "another-policy")]
    public async Task TestConnectionAsync_IncompatibleContract_ReturnsUpdateRequired(
        string apiVersion,
        string policy)
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ => JsonResponse($$"""
            {
              "status": "ready",
              "apiVersion": "{{apiVersion}}",
              "policy": "{{policy}}"
            }
            """));
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.UpdateRequired,
            result.State);
    }

    [Fact]
    public async Task TestConnectionAsync_InvalidConfiguration_ReturnsConfigurationErrorWithoutNetwork()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteRawConfiguration("{ invalid");
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            result.State);
        Assert.True(result.CanRepairConfiguration);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\",\"apiVersion\":\"v1\",\"policy\":\"roblox-singleton-event-v1\",\"extra\":true}")]
    [InlineData("{\"status\":\"ready\",\"status\":\"ready\",\"apiVersion\":\"v1\",\"policy\":\"roblox-singleton-event-v1\"}")]
    [InlineData("not-json")]
    public async Task TestConnectionAsync_NonStrictHealthDocument_ReturnsConfigurationError(
        string responseBody)
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(
            _ => JsonResponse(responseBody));
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            result.State);
        Assert.False(result.CanRepairConfiguration);
    }

    [Fact]
    public async Task TestConnectionAsync_OversizedHealthDocument_IsBounded()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ =>
            JsonResponse(new string(' ', (16 * 1024) + 1)));
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            result.State);
        Assert.False(result.CanRepairConfiguration);
    }

    [Fact]
    public async Task TestConnectionAsync_SendsOnlyUnauthenticatedBodylessHealthGet()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var result = await service.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HandleScopeIntegrationState.Ready, result.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http", request.Scheme);
        Assert.Equal("127.0.0.1", request.Host);
        Assert.Equal("/v1/health", request.AbsolutePath);
        Assert.Equal(string.Empty, request.Query);
        Assert.False(request.HasContent);
        Assert.False(request.HasAuthorization);
        Assert.DoesNotContain(ValidToken, request.HeaderText, StringComparison.Ordinal);
        Assert.DoesNotContain("close", request.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dry", request.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateSecureHandler_DisablesAmbientNetworkFeatures()
    {
        using var handler = HandleScopeIntegrationService.CreateSecureHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
        Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InstallPathReportedAsReparsePoint_IsRejectedBeforeUse()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(
            handler,
            isReparsePoint: path => path.Equals(
                environment.InstallRoot,
                StringComparison.OrdinalIgnoreCase));

        var inspect = await service.InspectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            inspect.State);
        Assert.False(inspect.CanRepairConfiguration);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionPathOrParentReportedAsReparsePoint_IsRejected(
        bool rejectParent)
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        var rejectedPath = rejectParent
            ? Path.GetDirectoryName(environment.ConnectionPath)!
            : environment.ConnectionPath;
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(
            handler,
            isReparsePoint: path => path.Equals(
                rejectedPath,
                StringComparison.OrdinalIgnoreCase));

        var result = await service.TestConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            result.State);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurationPathOrParentReportedAsReparsePoint_CannotBeRepaired(
        bool rejectParent)
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        var original = File.ReadAllBytes(environment.ConfigurationPath);
        var rejectedPath = rejectParent
            ? environment.SessionDockDataRoot
            : environment.ConfigurationPath;
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(
            handler,
            isReparsePoint: path => path.Equals(
                rejectedPath,
                StringComparison.OrdinalIgnoreCase));

        var inspect = await service.InspectAsync(
            TestContext.Current.CancellationToken);
        var repair = await service.EnableAsync(
            repairExisting: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            inspect.State);
        Assert.False(inspect.CanRepairConfiguration);
        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            repair.State);
        Assert.False(repair.CanRepairConfiguration);
        Assert.Equal(original, File.ReadAllBytes(environment.ConfigurationPath));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ConfigurationPathThatIsNotARegularFileCannotBeRepaired()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        Directory.CreateDirectory(environment.ConfigurationPath);
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var inspect = await service.InspectAsync(
            TestContext.Current.CancellationToken);
        var repair = await service.EnableAsync(
            repairExisting: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            inspect.State);
        Assert.False(inspect.CanRepairConfiguration);
        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            repair.State);
        Assert.False(repair.CanRepairConfiguration);
        Assert.True(Directory.Exists(environment.ConfigurationPath));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EnableAndDisable_WriteOnlyMinimalConfigurationWithoutProbing()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var enabled = await service.EnableAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var enabledJson = File.ReadAllText(environment.ConfigurationPath);
        var disabled = await service.DisableAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var disabledJson = File.ReadAllText(environment.ConfigurationPath);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            enabled.State);
        Assert.Equal("{\"enabled\":true}\n", enabledJson);
        Assert.Equal(
            HandleScopeIntegrationState.RunningDisabled,
            disabled.State);
        Assert.Equal("{\"enabled\":false}\n", disabledJson);
        Assert.Empty(Directory.EnumerateFiles(
            environment.SessionDockDataRoot,
            ".handlescope.*.tmp"));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InvalidConfiguration_IsPreservedUnlessRepairIsExplicit()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        const string original = "{\"enabled\":true,broken}";
        environment.WriteRawConfiguration(original);
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var refused = await service.EnableAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            refused.State);
        Assert.True(refused.CanRepairConfiguration);
        Assert.Equal(original, File.ReadAllText(environment.ConfigurationPath));

        var repaired = await service.EnableAsync(
            repairExisting: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            repaired.State);
        Assert.Equal(
            "{\"enabled\":true}\n",
            File.ReadAllText(environment.ConfigurationPath));
    }

    [Fact]
    public async Task NonminimalConfiguration_IsPreservedUntilOppositeStateIsExplicitlyRepaired()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        var original = $$"""
            {
              "enabled": true,
              "processName": "RobloxPlayerBeta",
              "handleName": "\\Sessions\\{SESSION_ID}\\BaseNamedObjects\\ROBLOX_singletonEvent",
              "handleType": "Event",
              "access": "0x001F0003",
              "match": "exact",
              "closeAll": false,
              "allProcesses": true
            }
            """;
        environment.WriteRawConfiguration(original);
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(handler);

        var alreadyEnabled = await service.EnableAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var refusedDisable = await service.DisableAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            alreadyEnabled.State);
        Assert.Equal(
            HandleScopeIntegrationState.ConfigurationError,
            refusedDisable.State);
        Assert.True(refusedDisable.CanRepairConfiguration);
        Assert.Equal(original, File.ReadAllText(environment.ConfigurationPath));

        var repairedDisable = await service.DisableAsync(
            repairExisting: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.RunningDisabled,
            repairedDisable.State);
        Assert.Equal(
            "{\"enabled\":false}\n",
            File.ReadAllText(environment.ConfigurationPath));
    }

    [Fact]
    public async Task DisableAsync_DoesNotProbeOrModifyTheApiConnection()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        var connectionBefore = File.ReadAllBytes(environment.ConnectionPath);
        var processInspectionCount = 0;
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        using var service = environment.CreateService(
            handler,
            isExpectedProcess: (_, _) =>
            {
                processInspectionCount++;
                return true;
            });

        var result = await service.DisableAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.RunningDisabled,
            result.State);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, processInspectionCount);
        Assert.Equal(connectionBefore, File.ReadAllBytes(environment.ConnectionPath));
    }

    [Fact]
    public async Task ConnectionFailuresRemainIsolated()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var failingHandler = new RecordingHandler(
            _ => throw new HttpRequestException("isolated"));
        using var connectionService = environment.CreateService(failingHandler);

        var connectionResult = await connectionService.TestConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            HandleScopeIntegrationState.InstalledStopped,
            connectionResult.State);
    }

    [Fact]
    public async Task TestConnectionAsync_CancellationDuringProbe_IsPropagated()
    {
        using var environment = new TestEnvironment();
        environment.InstallApi();
        environment.WriteConfiguration(enabled: true);
        environment.WriteConnection();
        using var handler = new CancellableHandler();
        using var service = environment.CreateService(handler);
        using var cancellation = new CancellationTokenSource();

        var test = service.TestConnectionAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);
    }

    [Fact]
    public async Task PublicActions_RejectUseAfterDisposal()
    {
        using var environment = new TestEnvironment();
        using var handler = new RecordingHandler(_ => ValidHealthResponse());
        var service = environment.CreateService(handler);
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = service.InspectAsync(TestContext.Current.CancellationToken);
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.TestConnectionAsync(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = service.EnableAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        });
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = service.DisableAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public void UiResult_ContainsOnlyTheNonSensitiveState()
    {
        var properties = typeof(HandleScopeIntegrationResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[] { "State", "CanRepairConfiguration" },
            properties);
    }

    private static HttpResponseMessage ValidHealthResponse() => JsonResponse("""
        {
          "status": "ready",
          "apiVersion": "v1",
          "policy": "roblox-singleton-event-v1"
        }
        """);

    private static HttpResponseMessage JsonResponse(string json) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.HandleScope.Integration.{Guid.NewGuid():N}");

        internal string LocalAppDataRoot => Path.Combine(_root, "LocalAppData");
        internal string SessionDockDataRoot =>
            Path.Combine(LocalAppDataRoot, "SessionDock");
        internal string InstallRoot => Path.Combine(
            LocalAppDataRoot,
            "Programs",
            "HandleScope",
            "Api");
        internal string ExecutablePath =>
            Path.Combine(InstallRoot, "HandleScope.Api.exe");
        internal string ConfigurationPath =>
            Path.Combine(SessionDockDataRoot, "handlescope.json");
        internal string ConnectionPath => Path.Combine(
            LocalAppDataRoot,
            "HandleScope",
            "connection.json");

        internal TestEnvironment()
        {
            Directory.CreateDirectory(SessionDockDataRoot);
        }

        internal void InstallApi()
        {
            Directory.CreateDirectory(InstallRoot);
            var executable = new byte[132];
            executable[0] = (byte)'M';
            executable[1] = (byte)'Z';
            BitConverter.TryWriteBytes(executable.AsSpan(60, 4), 128);
            executable[128] = (byte)'P';
            executable[129] = (byte)'E';
            File.WriteAllBytes(ExecutablePath, executable);
        }

        internal void InstallMalformedApi()
        {
            Directory.CreateDirectory(InstallRoot);
            File.WriteAllText(ExecutablePath, "not a portable executable");
        }

        internal void WriteConfiguration(bool enabled)
        {
            File.WriteAllText(
                ConfigurationPath,
                enabled ? "{\"enabled\":true}\n" : "{\"enabled\":false}\n");
        }

        internal void WriteRawConfiguration(string contents)
        {
            File.WriteAllText(ConfigurationPath, contents);
        }

        internal void WriteConnection()
        {
            var runtimeRoot = Path.GetDirectoryName(ConnectionPath)!;
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(ConnectionPath, $$"""
                {
                  "apiVersion": "v1",
                  "baseUrl": "http://127.0.0.1:51327",
                  "token": "{{ValidToken}}",
                  "processId": {{ApiProcessId}},
                  "startedAtUtc": "2026-07-21T12:00:00+00:00"
                }
                """);
        }

        internal HandleScopeIntegrationService CreateService(
            HttpMessageHandler handler,
            Func<HandleScopeConnection, string, bool>? isExpectedProcess = null,
            Func<string, bool>? isReparsePoint = null)
        {
            var processVerifier = new TestProcessVerifier(
                connection => (isExpectedProcess ?? ((_, path) => path.Equals(
                    ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)))(
                        connection,
                        ExecutablePath));
            return new HandleScopeIntegrationService(
                LocalAppDataRoot,
                SessionDockDataRoot,
                handler,
                processVerifier,
                isReparsePoint,
                new TestInstalledRuntimeVerifier());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup is best effort.
            }
        }
    }

    private sealed class TestInstalledRuntimeVerifier :
        IHandleScopeInstalledRuntimeVerifier
    {
        public bool IsAuthorized(string executablePath) => true;
    }

    private sealed class TestProcessVerifier(
        Func<HandleScopeConnection, bool> connectionVerifier)
        : IHandleScopeProcessVerifier
    {
        private readonly Func<HandleScopeConnection, bool> _connectionVerifier =
            connectionVerifier;
        public bool IsExpected(HandleScopeConnection connection) =>
            _connectionVerifier(connection);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder =
            responder;

        internal List<RequestSnapshot> Requests { get; } = [];
        internal int RequestCount => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri?.Scheme ?? string.Empty,
                request.RequestUri?.Host ?? string.Empty,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.RequestUri?.Query ?? string.Empty,
                request.Content is not null,
                request.Headers.Authorization is not null,
                request.Headers.ToString()));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class CancellableHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string Scheme,
        string Host,
        string AbsolutePath,
        string Query,
        bool HasContent,
        bool HasAuthorization,
        string HeaderText);

}
