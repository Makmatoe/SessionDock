namespace SessionDock.Services;

internal sealed class AutoJoinLaunchGate
{
    private const int Armed = 0;
    private const int Stopped = 1;
    private const int Claimed = 2;
    private int _state = Armed;

    internal bool IsArmed => Volatile.Read(ref _state) == Armed;

    internal bool TryClaimLaunch() =>
        Interlocked.CompareExchange(ref _state, Claimed, Armed) == Armed;

    internal bool TryStop() =>
        Interlocked.CompareExchange(ref _state, Stopped, Armed) == Armed;
}
