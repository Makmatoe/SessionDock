using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandleScope.Compatibility;
using HandleScope.Models;
using HandleScope.Services;
using Microsoft.AspNetCore.Http.Json;

namespace HandleScope.Api;

public sealed record ApiRuntimeOptions(
    int Port,
    string Token,
    IHandleAutomationPolicy? Policy = null,
    TimeProvider? TimeProvider = null,
    ApiCompatibilityMode CompatibilityMode = ApiCompatibilityMode.Automatic);

internal sealed record CandidateAuthorizationResult(
    IReadOnlyList<ProcessIdentity> Authorized,
    bool SelectedPidDenied,
    bool TooMany);

public static class ApiHost
{
    public static WebApplication Build(ApiRuntimeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, IPEndPoint.MaxPort);
        if (options.Token.Length != 43 ||
            options.Token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('_' or '-')))
        {
            throw new ArgumentException(
                "The API token must be a canonical 256-bit base64url value.",
                nameof(options));
        }

        var builder = WebApplication.CreateSlimBuilder();
        // The API has no user-configurable server settings. In particular, do not
        // inherit Kestrel__Endpoints__* or ASPNETCORE_URLS from the launching
        // environment: either could add a listener outside the reviewed loopback
        // boundary.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = StrictCloseRequestReader.MaximumBodyBytes;
            server.Limits.MaxRequestHeaderCount = 32;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Limits.MaxRequestLineSize = 2 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            server.Limits.MaxConcurrentConnections = 16;
            server.Limits.MaxConcurrentUpgradedConnections = 0;
        });
        builder.Services.AddSingleton<HandleService>();
        builder.Services.AddSingleton<ProcessIdentityService>();
        builder.Services.AddSingleton<IRobloxExecutableVerifier, RobloxExecutableVerifier>();
        if (options.Policy is null)
        {
            builder.Services.AddSingleton<
                IHandleAutomationPolicy,
                RobloxSingletonAutomationPolicy>();
        }
        else
        {
            builder.Services.AddSingleton(options.Policy);
        }

        builder.Services.AddSingleton<DryRunPlanStore>();
        builder.Services.AddSingleton<OperationGate>();
        builder.Services.AddSingleton(options.TimeProvider ?? TimeProvider.System);
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.PropertyNameCaseInsensitive = false;
            json.SerializerOptions.MaxDepth = 8;
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            SetSecurityHeaders(context.Response);
            if (!IsExpectedLoopbackRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (IsBrowserRequest(context.Request))
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "browser_request_denied");
                return;
            }

            if (context.Request.Path.Equals("/v1/health") ||
                context.Request.Path.Equals("/v2/health"))
            {
                await next();
                return;
            }

            var authorization = context.Request.Headers.Authorization;
            const string bearerPrefix = "Bearer ";
            var suppliedToken = authorization.Count == 1 &&
                                authorization[0]?.StartsWith(
                                    bearerPrefix,
                                    StringComparison.Ordinal) == true
                ? authorization[0]![bearerPrefix.Length..]
                : string.Empty;
            if (!FixedTimeEquals(suppliedToken, options.Token))
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "authentication_required");
                return;
            }

            await next();
        });

        app.MapGet(
            "/v1/health",
            (IHandleAutomationPolicy policy) => Results.Ok(new
            {
                status = "ready",
                apiVersion = "v1",
                policy = policy.PolicyId
            }));
        app.MapGet(
            "/v2/health",
            (IHandleAutomationPolicy policy) => Results.Ok(new
            {
                status = "ready",
                apiVersion = ApiCompatibilityPolicy.CurrentApiVersion,
                policy = policy.PolicyId,
                productVersion = ProductVersion,
                supportedApiVersions = ApiCompatibilityPolicy.SupportedApiVersions,
                preferredApiVersion = ApiCompatibilityPolicy.Resolve(
                    options.CompatibilityMode)
            }));
        app.MapGet(
            "/v1/metadata",
            (IHandleAutomationPolicy policy) => Results.Ok(new
            {
                schemaVersion = 1,
                productVersion = ProductVersion,
                discoveryApiVersion = ApiCompatibilityPolicy.DiscoveryApiVersion,
                supportedApiVersions = ApiCompatibilityPolicy.SupportedApiVersions,
                preferredApiVersion = ApiCompatibilityPolicy.Resolve(
                    options.CompatibilityMode),
                policies = new[] { policy.PolicyId },
                capabilities = new[]
                {
                    "handlescope.http.v1",
                    "handlescope.http.v2",
                    "handlescope.plan.single-use.v1",
                    "handlescope.policy.roblox-singleton-event.v1",
                    "handlescope.setup.native.v1"
                }
            }));
        app.MapPost("/v1/handles/close", CloseHandlesAsync);
        app.MapPost("/v2/handles/close", CloseHandlesAsync);
        app.MapPost(
            "/v1/shutdown",
            (IHostApplicationLifetime lifetime) =>
            {
                lifetime.StopApplication();
                return Results.Accepted();
            });
        app.MapPost(
            "/v2/shutdown",
            (IHostApplicationLifetime lifetime) =>
            {
                lifetime.StopApplication();
                return Results.Accepted();
            });

        return app;
    }

    private static string ProductVersion =>
        typeof(ApiHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<IResult> CloseHandlesAsync(
        HttpContext context,
        IHandleAutomationPolicy policy,
        HandleService handleService,
        DryRunPlanStore planStore,
        OperationGate operationGate,
        CancellationToken cancellationToken)
    {
        var readResult = await StrictCloseRequestReader.ReadAsync(
            context.Request,
            cancellationToken);
        if (readResult.Request is null)
        {
            return Error(
                readResult.ErrorStatus ?? StatusCodes.Status400BadRequest,
                readResult.ErrorCode ?? "invalid_request");
        }

        var request = readResult.Request;
        var authorization = policy.AuthorizeRequest(request);
        if (!authorization.IsAllowed)
        {
            return Error(StatusCodes.Status403Forbidden, authorization.ErrorCode);
        }

        using var operation = await operationGate.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Error(StatusCodes.Status429TooManyRequests, "operation_in_progress");
        }

        try
        {
            return request.DryRun == true
                ? await CreateDryRunPlanAsync(
                    request,
                    authorization,
                    policy,
                    handleService,
                    planStore,
                    cancellationToken)
                : ExecuteDryRunPlan(
                    request,
                    authorization,
                    policy,
                    handleService,
                    planStore);
        }
        catch (OperationCanceledException)
        {
            return Error(StatusCodes.Status499ClientClosedRequest, "operation_canceled");
        }
        catch
        {
            return Error(
                StatusCodes.Status500InternalServerError,
                "operation_failed");
        }
    }

    private static async Task<IResult> CreateDryRunPlanAsync(
        CloseHandlesRequest request,
        AutomationRequestAuthorization requestAuthorization,
        IHandleAutomationPolicy policy,
        HandleService handleService,
        DryRunPlanStore planStore,
        CancellationToken cancellationToken)
    {
        var candidatePids = ResolveCandidatePids(request, requestAuthorization);
        var candidateAuthorization = AuthorizeCandidateProcesses(
            candidatePids,
            request,
            requestAuthorization,
            policy);
        if (candidateAuthorization.TooMany)
        {
            return Error(
                StatusCodes.Status409Conflict,
                "too_many_target_processes");
        }

        if (candidateAuthorization.SelectedPidDenied)
        {
            return Error(StatusCodes.Status403Forbidden, "policy_denied");
        }

        var authorized = candidateAuthorization.Authorized;

        if (authorized.Count == 0)
        {
            return Error(StatusCodes.Status404NotFound, "no_matching_process");
        }

        var processPlans = new List<AuthorizedProcessPlan>();
        var matches = new List<HandleResponse>();
        var failures = new List<object>();
        var skipped = new List<object>();

        foreach (var identity in authorized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<HandleEntry> processHandles;
            try
            {
                processHandles = await Task.Run(
                    () => FindApprovedHandles(
                        handleService,
                        identity,
                        request.Handle!,
                        requestAuthorization,
                        cancellationToken),
                    cancellationToken);
            }
            catch
            {
                failures.Add(new
                {
                    pid = identity.ProcessId,
                    error = "scan_failed"
                });
                continue;
            }

            if (processHandles.Count == 0)
            {
                skipped.Add(new
                {
                    pid = identity.ProcessId,
                    reason = "no_match"
                });
                continue;
            }

            matches.AddRange(processHandles.Select(HandleResponse.FromEntry));
            if (processHandles.Count != 1)
            {
                failures.Add(new
                {
                    pid = identity.ProcessId,
                    error = "ambiguous_match"
                });
                continue;
            }

            processPlans.Add(new AuthorizedProcessPlan(identity, processHandles));
        }

        if (matches.Count == 0 && failures.Count == 0)
        {
            return Results.Json(
                new
                {
                    error = "no_matching_handle",
                    processCount = authorized.Count,
                    skipped
                },
                statusCode: StatusCodes.Status404NotFound);
        }

        if (failures.Count > 0)
        {
            return Results.Json(
                CreateOperationResponse(
                    policy.PolicyId,
                    dryRun: true,
                    planId: null,
                    authorized.Count,
                    matches,
                    closed: [],
                    failures,
                    skipped),
                statusCode: StatusCodes.Status207MultiStatus);
        }

        var planId = planStore.Put(
            requestAuthorization.CanonicalKey,
            authorized.Count,
            skipped.Count,
            processPlans);
        return Results.Ok(CreateOperationResponse(
            policy.PolicyId,
            dryRun: true,
            planId,
            authorized.Count,
            matches,
            closed: [],
            failures,
            skipped));
    }

    private static IResult ExecuteDryRunPlan(
        CloseHandlesRequest request,
        AutomationRequestAuthorization requestAuthorization,
        IHandleAutomationPolicy policy,
        HandleService handleService,
        DryRunPlanStore planStore)
    {
        if (!planStore.TryTake(
                request.PlanId!,
                requestAuthorization.CanonicalKey,
                out var plan) ||
            plan is null)
        {
            return Error(StatusCodes.Status409Conflict, "dry_run_required");
        }

        var matches = GetPlannedMatches(plan);
        var closed = new List<HandleResponse>();
        var failures = new List<object>();

        foreach (var processPlan in plan.Processes)
        {
            var authorization = policy.AuthorizeProcess(
                processPlan.Identity.ProcessId,
                requestAuthorization);
            if (!authorization.IsAllowed ||
                authorization.Identity is null ||
                authorization.Identity.CreationTimeUtcFileTime !=
                processPlan.Identity.CreationTimeUtcFileTime ||
                processPlan.Handles.Any(handle =>
                    handle.ProcessCreationTimeUtcFileTime !=
                    processPlan.Identity.CreationTimeUtcFileTime))
            {
                failures.Add(new
                {
                    pid = processPlan.Identity.ProcessId,
                    error = "process_identity_changed"
                });
                continue;
            }

            foreach (var handle in processPlan.Handles)
            {
                try
                {
                    handleService.CloseHandle(handle);
                    closed.Add(HandleResponse.FromEntry(handle));
                }
                catch
                {
                    failures.Add(new
                    {
                        pid = processPlan.Identity.ProcessId,
                        error = "close_failed"
                    });
                }
            }
        }

        var skipped = Enumerable.Range(0, plan.SkippedCount)
            .Select(_ => new { reason = "no_match" })
            .Cast<object>()
            .ToArray();
        var response = CreateOperationResponse(
            policy.PolicyId,
            dryRun: false,
            planId: null,
            plan.ProcessCount,
            matches,
            closed,
            failures,
            skipped);
        return failures.Count == 0
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status207MultiStatus);
    }

    private static IReadOnlyList<HandleEntry> FindApprovedHandles(
        HandleService handleService,
        ProcessIdentity identity,
        HandleSelector selector,
        AutomationRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var handles = handleService.FindHandles(
            identity.ProcessId,
            identity.CreationTimeUtcFileTime,
            selector.Name!,
            HandleMatchMode.Exact,
            progress: null,
            cancellationToken,
            includeUnnamed: false,
            resultFilter: handle =>
                handle.ProcessCreationTimeUtcFileTime ==
                    identity.CreationTimeUtcFileTime &&
                string.Equals(
                    handle.ObjectType,
                    authorization.ExpectedHandleType,
                    StringComparison.OrdinalIgnoreCase) &&
                handle.GrantedAccess == authorization.ExpectedHandleAccess,
            maximumMatches: 2);
        return handles
            .Where(handle =>
                handle.ProcessCreationTimeUtcFileTime ==
                identity.CreationTimeUtcFileTime)
            .Where(handle => string.Equals(
                handle.ObjectType,
                authorization.ExpectedHandleType,
                StringComparison.OrdinalIgnoreCase))
            .Where(handle =>
                handle.GrantedAccess == authorization.ExpectedHandleAccess)
            .Take(2)
            .ToArray();
    }

    internal static CandidateAuthorizationResult AuthorizeCandidateProcesses(
        IEnumerable<int> candidatePids,
        CloseHandlesRequest request,
        AutomationRequestAuthorization requestAuthorization,
        IHandleAutomationPolicy policy)
    {
        var authorized = new List<ProcessIdentity>();
        foreach (var pid in candidatePids)
        {
            var processAuthorization = policy.AuthorizeProcess(pid, requestAuthorization);
            if (processAuthorization.IsAllowed && processAuthorization.Identity is not null)
            {
                authorized.Add(processAuthorization.Identity);
                if (authorized.Count > policy.MaximumProcessCount)
                {
                    return new CandidateAuthorizationResult(
                        authorized,
                        SelectedPidDenied: false,
                        TooMany: true);
                }
            }
            else if (request.Process?.Pid is not null)
            {
                return new CandidateAuthorizationResult(
                    authorized,
                    SelectedPidDenied: true,
                    TooMany: false);
            }
        }

        return new CandidateAuthorizationResult(
            authorized,
            SelectedPidDenied: false,
            TooMany: false);
    }

    internal static HandleResponse[] GetPlannedMatches(DryRunPlan plan) =>
        plan.Processes
            .SelectMany(process => process.Handles)
            .Select(HandleResponse.FromEntry)
            .ToArray();

    private static int[] ResolveCandidatePids(
        CloseHandlesRequest request,
        AutomationRequestAuthorization authorization)
    {
        if (request.Process?.Pid is int pid)
        {
            return [pid];
        }

        var processes = Process.GetProcessesByName(authorization.ExpectedProcessName);
        try
        {
            return processes
                .Select(process => process.Id)
                .Distinct()
                .ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static object CreateOperationResponse(
        string policy,
        bool dryRun,
        string? planId,
        int processCount,
        IReadOnlyCollection<HandleResponse> matches,
        IReadOnlyCollection<HandleResponse> closed,
        IReadOnlyCollection<object> failures,
        IReadOnlyCollection<object> skipped) =>
        new
        {
            policy,
            dryRun,
            planId,
            processCount,
            matchedProcessCount = matches.Select(handle => handle.Pid).Distinct().Count(),
            matchCount = matches.Count,
            closedCount = closed.Count,
            failedCount = failures.Count,
            skippedCount = skipped.Count,
            matches,
            closed,
            failures,
            skipped
        };

    private static IResult Error(int statusCode, string error) =>
        Results.Json(new { error }, statusCode: statusCode);

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string error)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error });
    }

    private static bool IsExpectedLoopbackRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is not null &&
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress) &&
        string.Equals(
            context.Request.Host.Host,
            IPAddress.Loopback.ToString(),
            StringComparison.Ordinal);

    private static bool IsBrowserRequest(HttpRequest request) =>
        request.Headers.ContainsKey("Origin") ||
        request.Headers.ContainsKey("Referer") ||
        request.Headers.ContainsKey("Sec-Fetch-Site");

    private static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'";
        response.Headers.Append("Referrer-Policy", "no-referrer");
        response.Headers.Append("Cross-Origin-Resource-Policy", "same-origin");
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
