namespace HandleScope.Models;

public sealed record ProcessIdentity(
    int ProcessId,
    string ProcessName,
    string ImagePath,
    uint WindowsSessionId,
    string OwnerSid,
    bool IsElevated,
    long CreationTimeUtcFileTime)
{
    public DateTimeOffset CreationTimeUtc =>
        new(DateTime.FromFileTimeUtc(CreationTimeUtcFileTime));
}
