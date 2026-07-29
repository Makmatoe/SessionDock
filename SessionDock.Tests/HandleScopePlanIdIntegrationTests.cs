using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopePlanIdIntegrationTests
{
    private const string Policy = "roblox-singleton-event-v1";
    private const string Token =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PidPlanId =
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string SweepPlanId =
        "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

    [Fact]
    public async Task NotifyLaunchAsync_ForwardsEachDryRunPlanIdExactlyOnce()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.HandleScope.PlanId.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "RobloxPlayerBeta.exe");
        File.Copy(
            Path.Combine(Environment.SystemDirectory, "ping.exe"),
            targetPath);
        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            Arguments = "-n 30 127.0.0.1",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException(
            "The controlled Roblox-named process did not start.");
        var configurationPath = Path.Combine(root, "handlescope.json");
        var connectionPath = Path.Combine(root, "HandleScope", "connection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(connectionPath)!);
        await File.WriteAllTextAsync(
            configurationPath,
            "{\"enabled\":true,\"retryTimeoutSeconds\":1," +
            "\"retryIntervalMilliseconds\":100}\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            connectionPath,
            $$"""
                {
                  "apiVersion": "v1",
                  "baseUrl": "http://127.0.0.1:51327",
                  "token": "{{Token}}",
                  "processId": {{target.Id}},
                  "startedAtUtc": "2026-07-29T21:32:01+00:00"
                }
                """,
            TestContext.Current.CancellationToken);

        try
        {
            using var handler = new PlanIdHandler(target.Id);
            using var hook = new HandleScopeLaunchHook(
                new HandleScopeConfigurationLoader(configurationPath),
                new HandleScopeConnectionLoader(
                    connectionPath,
                    root,
                    _ => false),
                handler,
                new AcceptProcessVerifier());

            await hook.NotifyLaunchAsync(
                new LaunchHookEvent(
                    "plan-id-regression",
                    DateTimeOffset.UtcNow,
                    target.Id,
                    1,
                    "Plan ID regression",
                    false,
                    1,
                    "test-account",
                    null),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, handler.HealthRequestCount);
            Assert.Collection(
                handler.PostRequests,
                request => AssertCloseRequest(
                    request,
                    dryRun: true,
                    allProcesses: false,
                    expectedPlanId: null),
                request => AssertCloseRequest(
                    request,
                    dryRun: false,
                    allProcesses: false,
                    PidPlanId),
                request => AssertCloseRequest(
                    request,
                    dryRun: true,
                    allProcesses: true,
                    expectedPlanId: null),
                request => AssertCloseRequest(
                    request,
                    dryRun: false,
                    allProcesses: true,
                    SweepPlanId));
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                target.WaitForExit();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertCloseRequest(
        RequestSnapshot request,
        bool dryRun,
        bool allProcesses,
        string? expectedPlanId)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/handles/close", request.Path);
        Assert.Equal("Bearer", request.Authorization.Scheme);
        Assert.Equal(Token, request.Authorization.Parameter);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(dryRun, root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(allProcesses, root.GetProperty("allProcesses").GetBoolean());
        if (expectedPlanId is null)
        {
            Assert.False(root.TryGetProperty("planId", out _));
        }
        else
        {
            Assert.Equal(
                expectedPlanId,
                root.GetProperty("planId").GetString());
        }
    }

    private static HttpResponseMessage JsonResponse(object value) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value),
            Encoding.UTF8,
            "application/json")
    };

    private sealed class PlanIdHandler(int processId) : HttpMessageHandler
    {
        private readonly int _processId = processId;

        internal int HealthRequestCount { get; private set; }
        internal List<RequestSnapshot> PostRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == "/v1/health")
            {
                HealthRequestCount++;
                return JsonResponse(new
                {
                    status = "ready",
                    apiVersion = "v1",
                    policy = Policy
                });
            }

            if (request.Method != HttpMethod.Post ||
                request.RequestUri?.AbsolutePath != "/v1/handles/close" ||
                request.Content is null ||
                request.Headers.Authorization is null)
            {
                throw new InvalidOperationException("Unexpected HandleScope request.");
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            PostRequests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri.AbsolutePath,
                request.Headers.Authorization,
                body));

            return PostRequests.Count switch
            {
                1 => OperationResponse(dryRun: true, PidPlanId),
                2 => OperationResponse(dryRun: false, planId: null),
                3 => OperationResponse(dryRun: true, SweepPlanId),
                4 => OperationResponse(dryRun: false, planId: null),
                _ => throw new InvalidOperationException(
                    "SessionDock sent an unexpected extra close request.")
            };
        }

        private HttpResponseMessage OperationResponse(
            bool dryRun,
            string? planId) => JsonResponse(new
            {
                policy = Policy,
                dryRun,
                planId,
                processCount = 1,
                matchedProcessCount = 1,
                matchCount = 1,
                closedCount = dryRun ? 0 : 1,
                failedCount = 0,
                skippedCount = 0,
                matches = new object[] { new { pid = _processId } },
                closed = dryRun
                    ? Array.Empty<object>()
                    : new object[] { new { pid = _processId } },
                failures = Array.Empty<object>(),
                skipped = Array.Empty<object>()
            });
    }

    private sealed class AcceptProcessVerifier : IHandleScopeProcessVerifier
    {
        public bool IsExpected(HandleScopeConnection connection) => true;

        public bool IsExpectedStartedProcess(int processId) => true;

        public int? FindExpectedRunningProcessId() => Environment.ProcessId;
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string Path,
        AuthenticationHeaderValue Authorization,
        string Body);
}
