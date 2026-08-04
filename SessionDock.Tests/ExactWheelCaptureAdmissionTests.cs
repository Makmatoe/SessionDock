using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelCaptureAdmissionTests
{
    private static readonly ExactWheelInputEvent Candidate = new(
        1_000,
        4,
        ExactWheelInputEventType.MouseButtonDown,
        120,
        240,
        (int)ExactWheelMouseButton.Left,
        0);

    [Fact]
    public void IsEventAdmitted_NoPolicy_PreservesGlobalCaptureBehavior()
    {
        Assert.True(LowLevelInputCapture.IsEventAdmitted(Candidate, null));
    }

    [Fact]
    public void IsEventAdmitted_PolicyControlsWhetherTranslatedEventIsStored()
    {
        ExactWheelInputEvent observed = default;

        Assert.True(LowLevelInputCapture.IsEventAdmitted(
            Candidate,
            inputEvent =>
            {
                observed = inputEvent;
                return true;
            }));
        Assert.Equal(Candidate, observed);
        Assert.False(LowLevelInputCapture.IsEventAdmitted(
            Candidate,
            static _ => false));
    }

    [Fact]
    public void IsEventAdmitted_PolicyException_FailsClosed()
    {
        Assert.False(LowLevelInputCapture.IsEventAdmitted(
            Candidate,
            static _ => throw new InvalidOperationException("test")));
    }
}
