namespace HandleScope.Models;

public sealed record ProcessRow(
    int ProcessId,
    string Name,
    int? HandleCount,
    long? WorkingSetBytes)
{
    public long ProcessCreationTimeUtcFileTime { get; init; }

    public string PidDisplay => ProcessId.ToString();

    public string HandleCountDisplay => HandleCount?.ToString("N0") ?? "—";

    public string MemoryDisplay => WorkingSetBytes is long bytes
        ? FormatBytes(bytes)
        : "—";

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var suffixIndex = 0;

        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return $"{value:0.#} {suffixes[suffixIndex]}";
    }
}
