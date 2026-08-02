namespace SessionDock.SystemProcesses;

public enum HandleScopeRuntimeSource
{
    Bundled,
    Standalone
}

public enum HandleScopeApiContract
{
    Automatic,
    V2,
    V1
}

public enum HandleScopeRuntimeState
{
    Off,
    Starting,
    Ready,
    NeedsAttention,
    StandaloneUnavailable,
    ConfigurationError
}

public sealed record HandleScopeRuntimeSnapshot(
    HandleScopeRuntimeState State,
    HandleScopeRuntimeSource Source,
    string ComponentVersion,
    string? StandaloneVersion,
    HandleScopeApiContract ApiContract,
    HandleScopeVersionSelectionMode RuntimeVersionMode,
    Version? ExactRuntimeVersion,
    IReadOnlyList<Version> CompatibleStandaloneVersions,
    bool CanRepairConfiguration = false);
