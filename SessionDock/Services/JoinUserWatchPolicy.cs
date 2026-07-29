namespace SessionDock.Services;

internal enum JoinUserWatchAction
{
    Continue,
    IdentityReady,
    Join,
    StopUserNotFound,
    StopSessionUnavailable,
    Expired
}

internal sealed record JoinUserWatchDecision(
    JoinUserWatchAction Action,
    TimeSpan Delay);

internal sealed class JoinUserWatchPolicy
{
    internal static readonly TimeSpan NormalPollInterval =
        TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan NotJoinablePollInterval =
        TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaximumFailureBackoff =
        TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumWatchDuration =
        TimeSpan.FromHours(4);
    private static readonly TimeSpan MinimumPollInterval =
        TimeSpan.FromSeconds(15);
    private int _consecutiveServiceFailures;

    internal JoinUserWatchDecision ObserveIdentity(
        JoinUserIdentityLookupResult result,
        TimeSpan elapsed,
        double randomUnitInterval = 0.5)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (HasExpired(elapsed))
            return ExpiredDecision;

        return result.Availability switch
        {
            JoinUserIdentityAvailability.Available
                when result.Identity is not null =>
                ResetAndReturn(JoinUserWatchAction.IdentityReady),
            JoinUserIdentityAvailability.UserNotFound =>
                new(JoinUserWatchAction.StopUserNotFound, TimeSpan.Zero),
            JoinUserIdentityAvailability.SessionUnavailable =>
                new(JoinUserWatchAction.StopSessionUnavailable, TimeSpan.Zero),
            JoinUserIdentityAvailability.RateLimited =>
                ContinueAfter(ClampRetryAfter(result.RetryAfter)),
            _ => ContinueAfter(NextFailureDelay(randomUnitInterval))
        };
    }

    internal JoinUserWatchDecision ObservePresence(
        JoinUserLookupResult result,
        TimeSpan elapsed,
        double randomUnitInterval = 0.5)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (HasExpired(elapsed))
            return ExpiredDecision;

        return result.Availability switch
        {
            JoinUserAvailability.Available
                when result.Resolution is not null =>
                ResetAndReturn(JoinUserWatchAction.Join),
            JoinUserAvailability.UserNotFound =>
                new(JoinUserWatchAction.StopUserNotFound, TimeSpan.Zero),
            JoinUserAvailability.SessionUnavailable =>
                new(JoinUserWatchAction.StopSessionUnavailable, TimeSpan.Zero),
            JoinUserAvailability.RateLimited =>
                ContinueAfter(ClampRetryAfter(result.RetryAfter)),
            JoinUserAvailability.NotJoinable =>
                ResetAndContinue(NotJoinablePollInterval, randomUnitInterval),
            JoinUserAvailability.Offline or
            JoinUserAvailability.NotInExperience =>
                ResetAndContinue(NormalPollInterval, randomUnitInterval),
            _ => ContinueAfter(NextFailureDelay(randomUnitInterval))
        };
    }

    internal bool HasExpired(TimeSpan elapsed) =>
        elapsed >= MaximumWatchDuration;

    internal TimeSpan BoundDelayToExpiry(
        TimeSpan delay,
        TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        var remaining = MaximumWatchDuration - elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay < remaining ? delay : remaining;
    }

    private static JoinUserWatchDecision ExpiredDecision =>
        new(JoinUserWatchAction.Expired, TimeSpan.Zero);

    private JoinUserWatchDecision ResetAndReturn(JoinUserWatchAction action)
    {
        _consecutiveServiceFailures = 0;
        return new JoinUserWatchDecision(action, TimeSpan.Zero);
    }

    private JoinUserWatchDecision ResetAndContinue(
        TimeSpan delay,
        double randomUnitInterval)
    {
        _consecutiveServiceFailures = 0;
        return ContinueAfter(ApplyJitter(delay, randomUnitInterval));
    }

    private static JoinUserWatchDecision ContinueAfter(TimeSpan delay) =>
        new(JoinUserWatchAction.Continue, delay);

    private TimeSpan NextFailureDelay(double randomUnitInterval)
    {
        _consecutiveServiceFailures = Math.Min(
            _consecutiveServiceFailures + 1,
            5);
        var seconds = Math.Min(
            NormalPollInterval.TotalSeconds *
            Math.Pow(2, _consecutiveServiceFailures - 1),
            MaximumFailureBackoff.TotalSeconds);
        var jittered = ApplyJitter(
            TimeSpan.FromSeconds(seconds),
            randomUnitInterval);
        return jittered > MaximumFailureBackoff
            ? MaximumFailureBackoff
            : jittered;
    }

    private static TimeSpan ClampRetryAfter(TimeSpan? requested)
    {
        var delay = requested ?? NotJoinablePollInterval;
        if (delay < MinimumPollInterval)
            return MinimumPollInterval;
        return delay > MaximumFailureBackoff
            ? MaximumFailureBackoff
            : delay;
    }

    private static TimeSpan ApplyJitter(
        TimeSpan delay,
        double randomUnitInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            randomUnitInterval,
            0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            randomUnitInterval,
            1);
        var multiplier = 0.8 + (randomUnitInterval * 0.4);
        var jittered = TimeSpan.FromMilliseconds(
            delay.TotalMilliseconds * multiplier);
        return jittered < MinimumPollInterval
            ? MinimumPollInterval
            : jittered;
    }
}
