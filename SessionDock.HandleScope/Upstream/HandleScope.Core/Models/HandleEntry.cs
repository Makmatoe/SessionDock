namespace HandleScope.Models;

public enum HandleMatchMode
{
    Contains,
    Exact
}

public sealed record HandleEntry(
    int ProcessId,
    nuint HandleValue,
    nuint ObjectAddress,
    uint GrantedAccess,
    string ObjectType,
    string Name,
    string NativeName)
{
    public long ProcessCreationTimeUtcFileTime { get; init; }

    public string HandleDisplay => $"0x{HandleValue:X}";

    public string AccessDisplay => $"0x{GrantedAccess:X8}";
}

public sealed record ScanProgress(int Completed, int Total);
