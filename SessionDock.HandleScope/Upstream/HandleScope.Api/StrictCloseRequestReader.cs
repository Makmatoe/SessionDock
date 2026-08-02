using System.Net.Http.Headers;
using System.Text.Json;

namespace HandleScope.Api;

internal sealed record CloseRequestReadResult(
    CloseHandlesRequest? Request,
    int? ErrorStatus,
    string? ErrorCode)
{
    internal static CloseRequestReadResult Success(CloseHandlesRequest request) =>
        new(request, null, null);

    internal static CloseRequestReadResult Failure(int status, string code) =>
        new(null, status, code);
}

internal static class StrictCloseRequestReader
{
    internal const int MaximumBodyBytes = 8 * 1024;
    private const int MaximumProcessNameLength = 128;
    private const int MaximumHandleNameLength = 512;
    private const int MaximumSelectorLength = 64;
    private const int MaximumPlanIdLength = 64;

    internal static async Task<CloseRequestReadResult> ReadAsync(
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (httpRequest.ContentLength is > MaximumBodyBytes)
        {
            return CloseRequestReadResult.Failure(
                StatusCodes.Status413PayloadTooLarge,
                "request_too_large");
        }

        if (!MediaTypeHeaderValue.TryParse(
                httpRequest.ContentType,
                out var contentType) ||
            !string.Equals(
                contentType.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            return CloseRequestReadResult.Failure(
                StatusCodes.Status415UnsupportedMediaType,
                "json_required");
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var read = await httpRequest.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumBodyBytes)
            {
                return CloseRequestReadResult.Failure(
                    StatusCodes.Status413PayloadTooLarge,
                    "request_too_large");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return CloseRequestReadResult.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_request");
        }

        try
        {
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 5
                },
                cancellationToken);

            return TryRead(document.RootElement, out var request)
                ? CloseRequestReadResult.Success(request!)
                : CloseRequestReadResult.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_request");
        }
        catch (JsonException)
        {
            return CloseRequestReadResult.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_json");
        }
    }

    private static bool TryRead(
        JsonElement root,
        out CloseHandlesRequest? request)
    {
        request = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperties(
                root,
                ["process", "handle", "dryRun", "closeAll", "allProcesses", "planId"],
                out var properties) ||
            !properties.TryGetValue("process", out var processElement) ||
            !properties.TryGetValue("handle", out var handleElement) ||
            !TryGetRequiredBoolean(properties, "dryRun", out var dryRun) ||
            !TryGetRequiredBoolean(properties, "closeAll", out var closeAll) ||
            !TryGetRequiredBoolean(properties, "allProcesses", out var allProcesses) ||
            !TryGetOptionalString(
                properties,
                "planId",
                MaximumPlanIdLength,
                out var planId) ||
            (dryRun && planId is not null) ||
            (!dryRun && !DryRunPlanStore.IsCanonicalPlanId(planId)) ||
            !TryReadProcess(processElement, out var process) ||
            !TryReadHandle(handleElement, out var handle))
        {
            return false;
        }

        request = new CloseHandlesRequest
        {
            Process = process,
            Handle = handle,
            DryRun = dryRun,
            CloseAll = closeAll,
            AllProcesses = allProcesses,
            PlanId = planId
        };
        return true;
    }

    private static bool TryReadProcess(
        JsonElement element,
        out ProcessSelector? selector)
    {
        selector = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperties(element, ["pid", "name"], out var properties))
        {
            return false;
        }

        int? pid = null;
        if (properties.TryGetValue("pid", out var pidElement))
        {
            if (pidElement.ValueKind != JsonValueKind.Number ||
                !pidElement.TryGetInt32(out var parsedPid) ||
                parsedPid <= 0)
            {
                return false;
            }

            pid = parsedPid;
        }

        string? name = null;
        if (properties.TryGetValue("name", out var nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.Null &&
                !TryGetString(nameElement, MaximumProcessNameLength, out name))
            {
                return false;
            }
        }

        if (pid is null && string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        selector = new ProcessSelector { Pid = pid, Name = name };
        return true;
    }

    private static bool TryReadHandle(
        JsonElement element,
        out HandleSelector? selector)
    {
        selector = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperties(
                element,
                ["name", "match", "handle", "type", "access"],
                out var properties))
        {
            return false;
        }

        if (!TryGetOptionalString(
                properties,
                "name",
                MaximumHandleNameLength,
                out var name) ||
            !TryGetOptionalString(
                properties,
                "match",
                MaximumSelectorLength,
                out var match) ||
            !TryGetOptionalString(
                properties,
                "handle",
                MaximumSelectorLength,
                out var handle) ||
            !TryGetOptionalString(
                properties,
                "type",
                MaximumSelectorLength,
                out var type) ||
            !TryGetOptionalString(
                properties,
                "access",
                MaximumSelectorLength,
                out var access))
        {
            return false;
        }

        selector = new HandleSelector
        {
            Name = name,
            Match = match,
            Handle = handle,
            Type = type,
            Access = access
        };
        return true;
    }

    private static bool TryGetUniqueProperties(
        JsonElement element,
        string[] allowed,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) ||
                !properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetRequiredBoolean(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out bool value)
    {
        value = false;
        if (!properties.TryGetValue(name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetOptionalString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return TryGetString(element, maximumLength, out value);
    }

    private static bool TryGetString(
        JsonElement element,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null && value.Length <= maximumLength;
    }
}
