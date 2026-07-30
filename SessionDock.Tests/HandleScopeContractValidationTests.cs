using System.Text.Json;
using System.Text.Json.Nodes;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeContractValidationTests
{
    private const int ExpectedPid = 1234;
    private const string Policy = "roblox-singleton-event-v1";
    private const string ValidToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ValidPlanId =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Theory]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("http://127.0.0.1:51327")]
    [InlineData("http://127.0.0.1:65535")]
    public void TryValidateBaseUrl_ExactIpv4LoopbackEndpoint_IsAccepted(
        string value)
    {
        Assert.True(HandleScopeConnectionLoader.TryValidateBaseUrl(
            value,
            out var parsed));
        Assert.Equal(value, parsed!.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://127.0.0.1:51327")]
    [InlineData("http://localhost:51327")]
    [InlineData("http://[::1]:51327")]
    [InlineData("http://127.0.0.2:51327")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://user@127.0.0.1:51327")]
    [InlineData("http://127.0.0.1:51327/v1")]
    [InlineData("http://127.0.0.1:51327?query=1")]
    [InlineData("http://127.0.0.1:51327#fragment")]
    public void TryValidateBaseUrl_NonContractEndpoint_IsRejected(string? value)
    {
        Assert.False(HandleScopeConnectionLoader.TryValidateBaseUrl(
            value,
            out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void ConnectionLoader_ValidDiscoveryDocument_IsAccepted()
    {
        var connection = LoadConnection($$"""
            {
              "apiVersion": "v1",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{ValidToken}}",
              "processId": {{ExpectedPid}},
              "startedAtUtc": "2026-07-21T12:00:00+00:00"
            }
            """);

        Assert.NotNull(connection);
        Assert.Equal("http://127.0.0.1:51327/", connection.BaseUrl.AbsoluteUri);
        Assert.Equal(ValidToken, connection.Token);
        Assert.Equal("v1", connection.ApiVersion);
        Assert.Equal(ExpectedPid, connection.ApiProcessId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-21T12:00:00+00:00"),
            connection.StartedAtUtc);
    }

    [Theory]
    [InlineData("V1", ValidToken, ExpectedPid)]
    [InlineData("v1", "short", ExpectedPid)]
    [InlineData("v1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+", ExpectedPid)]
    [InlineData("v1", ValidToken, 0)]
    [InlineData("v1", ValidToken, -1)]
    public void ConnectionLoader_InvalidCredentialOrIdentity_IsRejected(
        string apiVersion,
        string token,
        int processId)
    {
        Assert.Null(LoadConnection($$"""
            {
              "apiVersion": "{{apiVersion}}",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{token}}",
              "processId": {{processId}},
              "startedAtUtc": "2026-07-21T12:00:00+00:00"
            }
            """));
    }

    [Fact]
    public void ConnectionLoader_AmbiguousOrNonContractDocument_IsRejected()
    {
        Assert.Null(LoadConnection($$"""
            {
              "apiVersion": "v1",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{ValidToken}}",
              "processId": {{ExpectedPid}}
            }
            """));
        Assert.Null(LoadConnection($$"""
            {
              "apiVersion": "v1",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{ValidToken}}",
              "processId": {{ExpectedPid}},
              "startedAtUtc": "2026-07-21T12:00:00+00:00",
              "unexpected": true
            }
            """));
        Assert.Null(LoadConnection($$"""
            {
              "apiVersion": "v1",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{ValidToken}}",
              "processId": {{ExpectedPid}},
              "processId": {{ExpectedPid}},
              "startedAtUtc": "2026-07-21T12:00:00+00:00"
            }
            """));
        Assert.Null(LoadConnection($$"""
            {
              "ApiVersion": "v1",
              "baseUrl": "http://127.0.0.1:51327",
              "token": "{{ValidToken}}",
              "processId": {{ExpectedPid}},
              "startedAtUtc": "2026-07-21T12:00:00+00:00"
            }
            """));
    }

    [Fact]
    public void ConnectionLoader_OversizedDocument_IsRejected()
    {
        Assert.Null(LoadConnection(new string(' ', (16 * 1024) + 1)));
    }

    [Fact]
    public void IsValidHealthDocument_ExactV1Policy_IsAccepted()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "ready",
              "apiVersion": "v1",
              "policy": "{{Policy}}"
            }
            """);

        Assert.True(HandleScopeApiBootstrapper.IsValidHealthDocument(
            document.RootElement));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"status\":\"READY\",\"apiVersion\":\"v1\",\"policy\":\"roblox-singleton-event-v1\"}")]
    [InlineData("{\"status\":\"ready\",\"apiVersion\":\"V1\",\"policy\":\"roblox-singleton-event-v1\"}")]
    [InlineData("{\"status\":\"ready\",\"apiVersion\":\"v1\",\"policy\":\"other-policy\"}")]
    [InlineData("{\"status\":\"ready\",\"status\":\"ready\",\"apiVersion\":\"v1\",\"policy\":\"roblox-singleton-event-v1\"}")]
    public void IsValidHealthDocument_MalformedOrIncompatibleDocument_IsRejected(
        string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(HandleScopeApiBootstrapper.IsValidHealthDocument(
            document.RootElement));
    }

    [Fact]
    public void TryValidateOperationDocument_ValidDryRun_IsAcceptedWithoutClosure()
    {
        var response = CreateOperationResponse(dryRun: true);

        using var document = JsonDocument.Parse(response.ToJsonString());
        Assert.True(HandleScopeLaunchHook.TryValidateOperationDocument(
            document.RootElement,
            ExpectedPid,
            expectedDryRun: true,
            out var closedExpectedProcess,
            out var planId));
        Assert.False(closedExpectedProcess);
        Assert.Equal(ValidPlanId, planId);
    }

    [Fact]
    public void TryValidateOperationDocument_ValidExecutionConfirmsExpectedPid()
    {
        var response = CreateOperationResponse(dryRun: false);

        Assert.True(ValidateOperation(
            response,
            ExpectedPid,
            expectedDryRun: false,
            out var closedExpectedProcess));
        Assert.True(closedExpectedProcess);
    }

    [Fact]
    public void TryValidateOperationDocument_NestedOrDifferentPid_IsRejected()
    {
        var dryRun = CreateOperationResponse(dryRun: true);
        dryRun["matches"] = new JsonArray(
            new JsonObject { ["nested"] = new JsonObject { ["pid"] = ExpectedPid } });

        Assert.False(ValidateOperation(
            dryRun,
            ExpectedPid,
            expectedDryRun: true,
            out _));

        var execution = CreateOperationResponse(dryRun: false);
        execution["closed"] = new JsonArray(new JsonObject { ["pid"] = 9999 });

        Assert.False(ValidateOperation(
            execution,
            ExpectedPid,
            expectedDryRun: false,
            out _));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("dryRun")]
    [InlineData("planId")]
    [InlineData("matchCount")]
    [InlineData("closedCount")]
    [InlineData("failedCount")]
    [InlineData("matches")]
    [InlineData("closed")]
    [InlineData("failures")]
    public void TryValidateOperationDocument_MissingRequiredRootField_IsRejected(
        string propertyName)
    {
        var response = CreateOperationResponse(dryRun: false);
        response.Remove(propertyName);

        Assert.False(ValidateOperation(
            response,
            ExpectedPid,
            expectedDryRun: false,
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB+")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void TryValidateOperationDocument_InvalidDryRunPlanId_IsRejected(
        string planId)
    {
        var response = CreateOperationResponse(dryRun: true);
        response["planId"] = planId;

        Assert.False(ValidateOperation(
            response,
            ExpectedPid,
            expectedDryRun: true,
            out _));
    }

    [Fact]
    public void TryValidateOperationDocument_MissingDryRunPlanId_IsRejected()
    {
        var response = CreateOperationResponse(dryRun: true);
        response.Remove("planId");

        Assert.False(ValidateOperation(
            response,
            ExpectedPid,
            expectedDryRun: true,
            out _));
    }

    [Fact]
    public void TryValidateOperationDocument_NonNullExecutionPlanId_IsRejected()
    {
        var response = CreateOperationResponse(dryRun: false);
        response["planId"] = ValidPlanId;

        Assert.False(ValidateOperation(
            response,
            ExpectedPid,
            expectedDryRun: false,
            out _));
    }

    [Fact]
    public void TryValidateOperationDocument_FailureOrCountMismatch_IsRejected()
    {
        var failed = CreateOperationResponse(dryRun: false);
        failed["failedCount"] = 1;
        failed["failures"] = new JsonArray(new JsonObject { ["error"] = "close_failed" });
        Assert.False(ValidateOperation(
            failed,
            ExpectedPid,
            expectedDryRun: false,
            out _));

        var mismatched = CreateOperationResponse(dryRun: false);
        mismatched["closedCount"] = 2;
        Assert.False(ValidateOperation(
            mismatched,
            ExpectedPid,
            expectedDryRun: false,
            out _));
    }

    [Fact]
    public void TryValidateOperationDocument_DuplicateRootField_IsRejected()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "policy": "{{Policy}}",
              "policy": "{{Policy}}",
              "dryRun": false,
              "planId": null,
              "processCount": 1,
              "matchedProcessCount": 1,
              "matchCount": 1,
              "closedCount": 1,
              "failedCount": 0,
              "matches": [{ "pid": {{ExpectedPid}} }],
              "closed": [{ "pid": {{ExpectedPid}} }],
              "failures": []
            }
            """);

        Assert.False(HandleScopeLaunchHook.TryValidateOperationDocument(
            document.RootElement,
            ExpectedPid,
            expectedDryRun: false,
            out _));
    }

    private static HandleScopeConnection? LoadConnection(string json)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.HandleScope.Connection.{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return new HandleScopeConnectionLoader(path).Load();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonObject CreateOperationResponse(bool dryRun)
    {
        var matches = new JsonArray(new JsonObject { ["pid"] = ExpectedPid });
        var closed = dryRun
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["pid"] = ExpectedPid });
        return new JsonObject
        {
            ["policy"] = Policy,
            ["dryRun"] = dryRun,
            ["planId"] = dryRun ? ValidPlanId : null,
            ["processCount"] = 1,
            ["matchedProcessCount"] = 1,
            ["matchCount"] = 1,
            ["closedCount"] = dryRun ? 0 : 1,
            ["failedCount"] = 0,
            ["skippedCount"] = 0,
            ["matches"] = matches,
            ["closed"] = closed,
            ["failures"] = new JsonArray(),
            ["skipped"] = new JsonArray()
        };
    }

    private static bool ValidateOperation(
        JsonNode response,
        int? expectedProcessId,
        bool expectedDryRun,
        out bool closedExpectedProcess)
    {
        using var document = JsonDocument.Parse(response.ToJsonString());
        return HandleScopeLaunchHook.TryValidateOperationDocument(
            document.RootElement,
            expectedProcessId,
            expectedDryRun,
            out closedExpectedProcess);
    }
}
