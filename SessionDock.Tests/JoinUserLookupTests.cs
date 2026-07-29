using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class JoinUserLookupTests
{
    private const string JobId = "A18C877E-4070-4A84-A5F7-36668B46A77D";
    private static readonly JoinUserIdentity Identity = new(
        42,
        "Builderman",
        "Builder Man");
    private static readonly JoinUserIdentifier UsernameIdentifier = new(
        null,
        "Builderman",
        "@Builderman");

    [Fact]
    public void ParseIdentityResponse_AvailableResultValidatesUser()
    {
        using var document = JsonDocument.Parse("""
            {
              "status": "available",
              "user": {
                "id": 42,
                "name": "Builderman",
                "displayName": "Builder Man"
              }
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
            document.RootElement,
            UsernameIdentifier);

        Assert.Equal(JoinUserIdentityAvailability.Available, result.Availability);
        Assert.Equal(Identity, result.Identity);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData("user-not-found", "UserNotFound")]
    [InlineData("rate-limited", "RateLimited")]
    [InlineData("session-unavailable", "SessionUnavailable")]
    [InlineData("unexpected", "ServiceUnavailable")]
    public void ParseIdentityResponse_UnavailableStatusFailsClosed(
        string status,
        string expected)
    {
        using var document = JsonDocument.Parse($$"""
            { "status": "{{status}}", "user": null }
            """);

        var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
            document.RootElement,
            UsernameIdentifier);

        Assert.Equal(expected, result.Availability.ToString());
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(600, 300)]
    public void ParseIdentityResponse_RetryAfterIsBounded(
        double retryAfterSeconds,
        double expectedSeconds)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "rate-limited",
              "retryAfterSeconds": {{retryAfterSeconds}}
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
            document.RootElement,
            UsernameIdentifier);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryAfter);
    }

    [Fact]
    public void ParseIdentityResponse_MalformedAvailableUserFailsClosed()
    {
        var malformedResponses = new[]
        {
            """{ "status": "available", "user": null }""",
            """{ "status": "available", "user": { "id": 0, "name": "Builderman" } }""",
            """{ "status": "available", "user": { "id": 42 } }"""
        };

        foreach (var json in malformedResponses)
        {
            using var document = JsonDocument.Parse(json);
            var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
                document.RootElement,
                UsernameIdentifier);

            Assert.Equal(
                JoinUserIdentityAvailability.ServiceUnavailable,
                result.Availability);
            Assert.Null(result.Identity);
        }
    }

    [Fact]
    public void ParsePresenceResponse_AvailableResultValidatesAndNormalizesFields()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "available",
              "userId": 42,
              "placeId": 123456,
              "gameId": "{{JobId}}"
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserPresenceResponse(
            document.RootElement,
            Identity);

        Assert.Equal(JoinUserAvailability.Available, result.Availability);
        Assert.Equal(42, result.Resolution!.UserId);
        Assert.Equal("Builderman", result.Resolution.Username);
        Assert.Equal("Builder Man", result.Resolution.DisplayName);
        Assert.Equal(123456, result.Resolution.PlaceId);
        Assert.Equal(JobId.ToLowerInvariant(), result.Resolution.ServerJobId);
    }

    [Theory]
    [InlineData("offline", "Offline")]
    [InlineData("not-in-experience", "NotInExperience")]
    [InlineData("not-joinable", "NotJoinable")]
    [InlineData("rate-limited", "RateLimited")]
    [InlineData("session-unavailable", "SessionUnavailable")]
    [InlineData("unexpected", "ServiceUnavailable")]
    public void ParsePresenceResponse_UnavailableStatusDoesNotInventLocation(
        string status,
        string expected)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "{{status}}",
              "userId": 42,
              "placeId": 123,
              "gameId": "{{JobId}}"
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserPresenceResponse(
            document.RootElement,
            Identity);

        Assert.Equal(expected, result.Availability.ToString());
        Assert.Null(result.Resolution);
    }

    [Theory]
    [InlineData(41, 123, "a18c877e-4070-4a84-a5f7-36668b46a77d")]
    [InlineData(42, 0, "a18c877e-4070-4a84-a5f7-36668b46a77d")]
    [InlineData(42, 123, "not-a-guid")]
    public void ParsePresenceResponse_MismatchedOrInvalidLocationFailsClosed(
        long userId,
        long placeId,
        string gameId)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "available",
              "userId": {{userId}},
              "placeId": {{placeId}},
              "gameId": "{{gameId}}"
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserPresenceResponse(
            document.RootElement,
            Identity);

        Assert.Equal(JoinUserAvailability.ServiceUnavailable, result.Availability);
        Assert.Null(result.Resolution);
    }

    [Theory]
    [InlineData("rate-limited", 1, 15)]
    [InlineData("rate-limited", 600, 300)]
    public void ParsePresenceResponse_RetryAfterIsBounded(
        string status,
        double retryAfterSeconds,
        double expectedSeconds)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "{{status}}",
              "retryAfterSeconds": {{retryAfterSeconds}}
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserPresenceResponse(
            document.RootElement,
            Identity);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryAfter);
    }

    [Fact]
    public void ParsePresenceResponse_NonStringStatusFailsClosed()
    {
        using var document = JsonDocument.Parse("""
            { "status": 2, "userId": 42 }
            """);

        var result = RobloxWebSessionService.ParseJoinUserPresenceResponse(
            document.RootElement,
            Identity);

        Assert.Equal(
            JoinUserAvailability.ServiceUnavailable,
            result.Availability);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public void ParseIdentityResponse_NonStringStatusFailsClosed()
    {
        using var document = JsonDocument.Parse("""
            { "status": 2, "user": {} }
            """);

        var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
            document.RootElement,
            UsernameIdentifier);

        Assert.Equal(
            JoinUserIdentityAvailability.ServiceUnavailable,
            result.Availability);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(41, "Builderman", 42, null)]
    [InlineData(42, "SomeoneElse", 0, "Builderman")]
    public void ParseIdentityResponse_MismatchedRequestedUserFailsClosed(
        long responseUserId,
        string responseUsername,
        long requestedUserId,
        string? requestedUsername)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "available",
              "user": {
                "id": {{responseUserId}},
                "name": "{{responseUsername}}",
                "displayName": "Target"
              }
            }
            """);
        var identifier = requestedUserId > 0
            ? new JoinUserIdentifier(
                requestedUserId,
                null,
                requestedUserId.ToString())
            : new JoinUserIdentifier(
                null,
                requestedUsername,
                $"@{requestedUsername}");

        var result = RobloxWebSessionService.ParseJoinUserIdentityResponse(
            document.RootElement,
            identifier);

        Assert.Equal(
            JoinUserIdentityAvailability.ServiceUnavailable,
            result.Availability);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void ResolveIdentityScript_UsesOfficialEndpointSelectedCookiesAndTimeout()
    {
        var script = RobloxWebScripts.ResolveJoinUserIdentity(
            "request-id",
            new JoinUserIdentifier(null, "Builderman", "@Builderman"));

        Assert.Contains("https://users.roblox.com/v1/usernames/users", script);
        Assert.DoesNotContain("presence.roblox.com", script);
        Assert.Equal(2, CountOccurrences(script, "credentials: 'include'"));
        Assert.Contains("const requestedUsername = \"Builderman\";", script);
        Assert.Contains("new AbortController()", script);
        Assert.Contains("10000", script);
    }

    [Fact]
    public void PresenceScript_PinsUserIdAndUsesOnlyOfficialPresenceEndpoint()
    {
        var script = RobloxWebScripts.GetJoinUserPresence(
            "request-id",
            42);

        Assert.Contains("https://presence.roblox.com/v1/presence/users", script);
        Assert.DoesNotContain("users.roblox.com", script);
        Assert.Equal(1, CountOccurrences(script, "credentials: 'include'"));
        Assert.Contains("const userId = 42;", script);
        Assert.Contains("responseUserId !== userId", script);
        Assert.Contains("new AbortController()", script);
    }

    [Fact]
    public void ResolveIdentityScript_PinsNumericUserIdEndpoint()
    {
        var script = RobloxWebScripts.ResolveJoinUserIdentity(
            "request-id",
            new JoinUserIdentifier(42, null, "42"));

        Assert.Contains("const requestedUserId = \"42\";", script);
        Assert.Contains(
            "https://users.roblox.com/v1/users/${requestedUserId}",
            script);
    }

    [Theory]
    [InlineData("UserNotFound", "UserNotFound")]
    [InlineData("RateLimited", "RateLimited")]
    [InlineData("SessionUnavailable", "SessionUnavailable")]
    [InlineData("ServiceUnavailable", "ServiceUnavailable")]
    public void IdentityFailuresMapToManualJoinAvailability(
        string identityAvailability,
        string expectedAvailability)
    {
        var result = RobloxWebSessionService.MapJoinUserIdentityFailure(
            JoinUserIdentityLookupResult.Unavailable(
                Enum.Parse<JoinUserIdentityAvailability>(identityAvailability),
                TimeSpan.FromSeconds(45)));

        Assert.Equal(expectedAvailability, result.Availability.ToString());
        Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public void PreResolvedTargetMustMatchTheRequestedUser()
    {
        var numericMatch = MainWindow.DoesJoinUserResolutionMatch(
            new JoinUserIdentifier(42, null, "42"),
            new JoinUserResolution(
                42,
                "Builderman",
                "Builder Man",
                123,
                JobId));
        var usernameMatch = MainWindow.DoesJoinUserResolutionMatch(
            new JoinUserIdentifier(null, "BUILDERMAN", "@BUILDERMAN"),
            new JoinUserResolution(
                42,
                "Builderman",
                "Builder Man",
                123,
                JobId));
        var mismatch = MainWindow.DoesJoinUserResolutionMatch(
            new JoinUserIdentifier(43, null, "43"),
            new JoinUserResolution(
                42,
                "Builderman",
                "Builder Man",
                123,
                JobId));

        Assert.True(numericMatch);
        Assert.True(usernameMatch);
        Assert.False(mismatch);
    }

    [Fact]
    public void ResolveIdentityScript_JsonEncodesUsername()
    {
        var script = RobloxWebScripts.ResolveJoinUserIdentity(
            "request-id",
            new JoinUserIdentifier(
                null,
                "bad\"; window.evil = true; //",
                "unused"));

        Assert.DoesNotContain(
            "const requestedUsername = \"bad\"; window.evil",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\\u0022", script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;
}
