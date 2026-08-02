using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeRuntimeCoordinatorTests
{
    [Fact]
    public void ReadyConnection_RequiresEnabledAndValidConfiguration()
    {
        Assert.True(HandleScopeRuntimeCoordinator.CanUseReadyConnection(
            EnabledConfiguration(),
            ValidSource(),
            ValidSelection()));
    }

    [Fact]
    public void ReadyConnection_FailsClosedForDisabledOrInvalidState()
    {
        Assert.False(HandleScopeRuntimeCoordinator.CanUseReadyConnection(
            HandleScopeConfigurationSnapshot.Missing,
            ValidSource(),
            ValidSelection()));
        Assert.False(HandleScopeRuntimeCoordinator.CanUseReadyConnection(
            HandleScopeConfigurationSnapshot.Invalid,
            ValidSource(),
            ValidSelection()));
        Assert.False(HandleScopeRuntimeCoordinator.CanUseReadyConnection(
            EnabledConfiguration(),
            new HandleScopeRuntimeSourceReadResult(
                HandleScopeRuntimeSource.Bundled,
                Exists: true,
                IsValid: false),
            ValidSelection()));
        Assert.False(HandleScopeRuntimeCoordinator.CanUseReadyConnection(
            EnabledConfiguration(),
            ValidSource(),
            new HandleScopeSelectionReadResult(
                HandleScopeSelection.Default,
                Exists: true,
                IsValid: false)));
    }

    private static HandleScopeConfigurationSnapshot EnabledConfiguration() =>
        new(
            Exists: true,
            IsValid: true,
            IsEnabled: true,
            IsMinimal: true,
            CanRepair: false,
            Fingerprint: []);

    private static HandleScopeRuntimeSourceReadResult ValidSource() =>
        new(
            HandleScopeRuntimeSource.Bundled,
            Exists: true,
            IsValid: true);

    private static HandleScopeSelectionReadResult ValidSelection() =>
        new(
            HandleScopeSelection.Default,
            Exists: true,
            IsValid: true);
}
