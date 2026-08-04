using SessionDock.ExactWheel;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ClientRecordingAdmissionPolicyTests
{
    private static readonly ExactWheelRect ClientRect = new(0, 0, 800, 600);

    [Fact]
    public void FocusAcquisitionClick_RejectsDownAndOrphanUpThenRecovers()
    {
        var authorization =
            ExactWheelDispatchAuthorization.TemporarilyUnavailable;
        var policy = CreatePolicy(() => authorization);

        Assert.False(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseButtonDown,
            ExactWheelMouseButton.Left)));
        authorization = ExactWheelDispatchAuthorization.Authorized;
        Assert.False(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseMove,
            ExactWheelMouseButton.Left)));
        Assert.False(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseButtonUp,
            ExactWheelMouseButton.Left)));
        Assert.True(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseMove,
            ExactWheelMouseButton.Left)));
        Assert.Null(policy.Failure);
    }

    [Fact]
    public void BalancedKeyboardPair_IsAdmitted()
    {
        var policy = CreatePolicy(
            static () => ExactWheelDispatchAuthorization.Authorized);

        Assert.True(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));
        Assert.True(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x41)));
        Assert.Null(policy.Failure);
    }

    [Fact]
    public void HeldInputThenFocusLoss_IsTerminalAndFailsClosed()
    {
        var authorization = ExactWheelDispatchAuthorization.Authorized;
        var policy = CreatePolicy(() => authorization);
        Assert.True(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));

        authorization =
            ExactWheelDispatchAuthorization.TemporarilyUnavailable;
        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x41)));
        var failure = Assert.IsType<ClientRecordingAdmissionFailure>(
            policy.Failure);
        Assert.Equal(
            ClientRecordingAdmissionFailureKind.FocusLostWhileInputHeld,
            failure.Kind);

        authorization = ExactWheelDispatchAuthorization.Authorized;
        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x41)));
        Assert.Same(failure, policy.Failure);
    }

    [Fact]
    public void UnrelatedKeyUp_IsRejectedWithoutPoisoningLaterPair()
    {
        var policy = CreatePolicy(
            static () => ExactWheelDispatchAuthorization.Authorized);

        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x42)));
        Assert.True(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));
        Assert.True(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x41)));
        Assert.Null(policy.Failure);
    }

    [Fact]
    public void RejectedKeyDown_SuppressesRepeatAndUpAfterRefocus()
    {
        var authorization =
            ExactWheelDispatchAuthorization.TemporarilyUnavailable;
        var policy = CreatePolicy(() => authorization);
        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));

        authorization = ExactWheelDispatchAuthorization.Authorized;
        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));
        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyUp,
            0x41)));
        Assert.Null(policy.Failure);
    }

    [Fact]
    public void HeldMouseButtonLeavingClient_IsTerminal()
    {
        var policy = CreatePolicy(
            static () => ExactWheelDispatchAuthorization.Authorized);
        Assert.True(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseButtonDown,
            ExactWheelMouseButton.Left)));

        Assert.False(policy.TryAdmit(Mouse(
            ExactWheelInputEventType.MouseMove,
            ExactWheelMouseButton.Left,
            x: 900,
            y: 700)));
        Assert.Equal(
            ClientRecordingAdmissionFailureKind.PointerLeftWhileButtonHeld,
            policy.Failure?.Kind);
    }

    [Fact]
    public void Complete_AllowsOnlyTheGlobalStopHotkeyTailToRemainHeld()
    {
        var stopOnly = CreatePolicy(
            static () => ExactWheelDispatchAuthorization.Authorized);
        Assert.True(stopOnly.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x77)));
        stopOnly.Complete(
            new HashSet<int> { 0x77 },
            requiredTerminalKey: 0x77,
            maximumTerminalKeyboardKeys: 1);
        Assert.Null(stopOnly.Failure);

        var unrelatedHeld = CreatePolicy(
            static () => ExactWheelDispatchAuthorization.Authorized);
        Assert.True(unrelatedHeld.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));
        unrelatedHeld.Complete(
            new HashSet<int> { 0x77 },
            requiredTerminalKey: 0x77,
            maximumTerminalKeyboardKeys: 1);
        Assert.Equal(
            ClientRecordingAdmissionFailureKind.InputStillHeldAtStop,
            unrelatedHeld.Failure?.Kind);
    }

    [Fact]
    public void AuthorizationException_IsTerminalAndFailsClosed()
    {
        var policy = CreatePolicy(static () =>
            throw new InvalidOperationException("test"));

        Assert.False(policy.TryAdmit(Key(
            ExactWheelInputEventType.KeyDown,
            0x41)));
        Assert.Equal(
            ClientRecordingAdmissionFailureKind.AuthorizationUnavailable,
            policy.Failure?.Kind);
    }

    private static ClientRecordingAdmissionPolicy CreatePolicy(
        Func<ExactWheelDispatchAuthorization> authorization) =>
        new(
            ClientRect,
            _ => authorization());

    private static ExactWheelInputEvent Key(
        ExactWheelInputEventType type,
        int virtualKey) =>
        new(0, 1, type, 0, 0, virtualKey, 0);

    private static ExactWheelInputEvent Mouse(
        ExactWheelInputEventType type,
        ExactWheelMouseButton button,
        int x = 100,
        int y = 100) =>
        new(0, 1, type, x, y, (int)button, 0);
}
