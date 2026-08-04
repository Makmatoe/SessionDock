namespace SessionDock.Services;

public sealed record RobloxClientProcessIdentity(
    int ProcessId,
    DateTime StartTimeUtc,
    string ExecutablePath);

public sealed record RunningRobloxClient(
    RobloxClientProcessIdentity Identity,
    bool HasVisibleWindow,
    string? WindowTitle);

public sealed record RunningRobloxClientsResult(
    IReadOnlyList<RunningRobloxClient> Clients,
    int UnverifiedCount);

public enum CloseRobloxClientStatus
{
    Closed,
    AlreadyExited,
    IdentityMismatch,
    Failed
}

public sealed record CloseRobloxClientResult(CloseRobloxClientStatus Status)
{
    public bool Removed =>
        Status is CloseRobloxClientStatus.Closed or
            CloseRobloxClientStatus.AlreadyExited;
}

internal sealed record RunningClientAttribution(
    string AccountKey,
    long AccountUserId,
    string AccountUsername,
    string? AccountLabel,
    string? AccountColorHex,
    long PlaceId,
    string? ExperienceName,
    string LaunchDestination,
    DateTimeOffset LaunchedAt);

internal sealed record AttributedRunningClient(
    RobloxClientProcessIdentity Identity,
    RunningClientAttribution Attribution);

internal sealed class RunningClientRegistry
{
    private readonly Dictionary<
        RobloxClientProcessIdentity,
        RunningClientAttribution> _entries = new(
            RobloxClientProcessIdentityComparer.Instance);

    public int Count => _entries.Count;

    public void Track(
        RobloxClientProcessIdentity identity,
        RunningClientAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(attribution);
        _entries[identity] = attribution;
    }

    public bool TryGet(
        RobloxClientProcessIdentity identity,
        out RunningClientAttribution? attribution) =>
        _entries.TryGetValue(identity, out attribution);

    public bool Remove(RobloxClientProcessIdentity identity) =>
        _entries.Remove(identity);

    public void Clear() => _entries.Clear();

    public IReadOnlyList<AttributedRunningClient> Snapshot() =>
        _entries
            .Select(entry => new AttributedRunningClient(
                entry.Key,
                entry.Value))
            .OrderBy(entry => entry.Attribution.LaunchedAt)
            .ThenBy(entry => entry.Identity.ProcessId)
            .ToArray();

    public void Prune(IEnumerable<RobloxClientProcessIdentity> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        var runningSet = new HashSet<RobloxClientProcessIdentity>(
            running,
            RobloxClientProcessIdentityComparer.Instance);
        foreach (var identity in _entries.Keys
                     .Where(identity => !runningSet.Contains(identity))
                     .ToArray())
        {
            _entries.Remove(identity);
        }
    }

    public void Reconcile(
        IEnumerable<RobloxClientProcessIdentity> running,
        bool scanIsComplete)
    {
        ArgumentNullException.ThrowIfNull(running);
        if (scanIsComplete)
            Prune(running);
    }
}

internal sealed class RobloxClientProcessIdentityComparer
    : IEqualityComparer<RobloxClientProcessIdentity>
{
    public static RobloxClientProcessIdentityComparer Instance { get; } =
        new();

    public bool Equals(
        RobloxClientProcessIdentity? left,
        RobloxClientProcessIdentity? right) =>
        ReferenceEquals(left, right) ||
        left is not null &&
        right is not null &&
        left.ProcessId == right.ProcessId &&
        left.StartTimeUtc == right.StartTimeUtc &&
        left.ExecutablePath.Equals(
            right.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(RobloxClientProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return HashCode.Combine(
            identity.ProcessId,
            identity.StartTimeUtc,
            StringComparer.OrdinalIgnoreCase.GetHashCode(
                identity.ExecutablePath));
    }
}
