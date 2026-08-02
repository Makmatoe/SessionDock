using System.Collections.Concurrent;
using System.Security.Cryptography;
using HandleScope.Models;

namespace HandleScope.Api;

internal sealed record AuthorizedProcessPlan(
    ProcessIdentity Identity,
    IReadOnlyList<HandleEntry> Handles);

internal sealed record DryRunPlan(
    string PlanId,
    string CanonicalKey,
    long CreatedTimestamp,
    long Sequence,
    int ProcessCount,
    int SkippedCount,
    IReadOnlyList<AuthorizedProcessPlan> Processes);

internal sealed class DryRunPlanStore
{
    private const int MaximumPlans = 32;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DryRunPlan> _plans =
        new(StringComparer.Ordinal);
    private long _sequence;

    public DryRunPlanStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    internal string Put(
        string canonicalKey,
        int processCount,
        int skippedCount,
        IReadOnlyList<AuthorizedProcessPlan> processes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        CleanupExpired();
        if (_plans.Count >= MaximumPlans)
        {
            var oldest = _plans.Values
                .OrderBy(plan => plan.Sequence)
                .FirstOrDefault();
            if (oldest is not null)
            {
                _plans.TryRemove(oldest.PlanId, out _);
            }
        }

        string planId;
        do
        {
            planId = CreatePlanId();
        }
        while (!_plans.TryAdd(
            planId,
            new DryRunPlan(
                planId,
                canonicalKey,
                _timeProvider.GetTimestamp(),
                Interlocked.Increment(ref _sequence),
                processCount,
                skippedCount,
                processes)));

        return planId;
    }

    internal bool TryTake(
        string planId,
        string canonicalKey,
        out DryRunPlan? plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        plan = null;
        if (!IsCanonicalPlanId(planId) ||
            !_plans.TryRemove(planId, out plan))
        {
            return false;
        }

        if (!string.Equals(
                plan.CanonicalKey,
                canonicalKey,
                StringComparison.Ordinal) ||
            IsExpired(plan, _timeProvider.GetTimestamp()))
        {
            plan = null;
            return false;
        }

        return true;
    }

    internal static bool IsCanonicalPlanId(string? planId) =>
        planId is { Length: 43 } &&
        planId.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-');

    private static string CreatePlanId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private bool IsExpired(DryRunPlan plan, long nowTimestamp) =>
        _timeProvider.GetElapsedTime(plan.CreatedTimestamp, nowTimestamp) >= Lifetime;

    private void CleanupExpired()
    {
        var now = _timeProvider.GetTimestamp();
        foreach (var plan in _plans.Values)
        {
            if (IsExpired(plan, now))
            {
                _plans.TryRemove(plan.PlanId, out _);
            }
        }
    }
}

/*
 * Keep operation serialization independent from plan storage. A plan is bound
 * to its random identifier; the gate only prevents concurrent native scans and
 * close operations from racing inside this API process.
 */
internal sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task<IDisposable?> TryEnterAsync(
        CancellationToken cancellationToken)
    {
        return await _semaphore.WaitAsync(0, cancellationToken)
            ? new Releaser(_semaphore)
            : null;
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
