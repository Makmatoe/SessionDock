using HandleScope.Models;

namespace HandleScope.Api;

public sealed class ProcessSelector
{
    public int? Pid { get; init; }

    public string? Name { get; init; }
}

public sealed class HandleSelector
{
    public string? Name { get; init; }

    public string? Match { get; init; }

    public string? Handle { get; init; }

    public string? Type { get; init; }

    public string? Access { get; init; }
}

public sealed class CloseHandlesRequest
{
    public ProcessSelector? Process { get; init; }

    public HandleSelector? Handle { get; init; }

    public bool? DryRun { get; init; }

    public bool CloseAll { get; init; }

    public bool AllProcesses { get; init; }

    public string? PlanId { get; init; }
}

public sealed record ProcessResponse(
    int Pid,
    string Name,
    int? HandleCount,
    string Memory);

public sealed record HandleResponse(
    int Pid,
    string Handle,
    string Object,
    string Access,
    string Type,
    string Name,
    string NativeName)
{
    public static HandleResponse FromEntry(HandleEntry entry) =>
        new(
            entry.ProcessId,
            "redacted",
            "redacted",
            entry.AccessDisplay,
            entry.ObjectType,
            "ROBLOX_singletonEvent",
            string.Empty);
}
