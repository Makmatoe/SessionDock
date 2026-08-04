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

    [Fact]
    public void PooledEventBuffer_ShortMacro_DoesNotReserveMaximumCapture()
    {
        using var buffer = new PooledExactWheelEventBuffer(
            ExactWheelLimits.DefaultCaptureEventCapacity);

        Assert.Equal(1, buffer.SegmentCount);
        Assert.Equal(
            PooledExactWheelEventBuffer.SegmentEventCapacity,
            buffer.ReservedEventCapacity);
        Assert.True(
            buffer.ReservedEventCapacity <
            ExactWheelLimits.DefaultCaptureEventCapacity / 100);

        Assert.True(buffer.TryAdd(Candidate));
        Assert.Equal([Candidate], buffer.ToArray());
    }

    [Fact]
    public void PooledEventBuffer_GrowsWithoutLosingOrder_AndRemainsBounded()
    {
        var maximum =
            PooledExactWheelEventBuffer.SegmentEventCapacity + 2;
        using var buffer = new PooledExactWheelEventBuffer(maximum);
        var expected = new ExactWheelInputEvent[maximum];
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] = Candidate with
            {
                TimestampMicroseconds = checked((ulong)index),
                Sequence = checked((ulong)index + 1)
            };
            Assert.True(buffer.TryAdd(expected[index]));
        }

        Assert.Equal(2, buffer.SegmentCount);
        Assert.Equal(maximum, buffer.ReservedEventCapacity);
        Assert.Equal(maximum, buffer.Count);
        Assert.False(buffer.TryAdd(Candidate));
        Assert.Equal(expected, buffer.ToArray());
    }
}
