using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelSessionTests
{
    [Fact]
    public async Task Recording_FocusedSuppliedTarget_UsesBoundedCaptureAndFinalizesTimeline()
    {
        var capture = new FakeCapture
        {
            Result = new InputCaptureResult(
            [
                new ExactWheelInputEvent(
                    20,
                    2,
                    ExactWheelInputEventType.MouseMove,
                    101,
                    81,
                    0,
                    0),
                new ExactWheelInputEvent(
                    10,
                    1,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ],
            50,
            Overflowed: false,
            Win32Error: 0)
        };
        await using var session = new ExactWheelSession(
            capture,
            new ExactWheelPlaybackEngine(),
            static handle => handle == 123);
        var target = new ExactWheelRecordingTarget(
            123,
            ExactWheelTestData.Display(),
            ExactWheelTestData.Target());

        session.StartRecording(
            target,
            new ExactWheelRecordingOptions
            {
                MaximumEvents = 42,
                ArmUntilReleasedVirtualKeys = [0x11],
                EventAdmission = static inputEvent =>
                    inputEvent.Type == ExactWheelInputEventType.MouseMove
            });
        var recording = session.StopRecording();

        Assert.Equal(ExactWheelSessionState.Idle, session.State);
        Assert.Equal(42, capture.MaximumEvents);
        Assert.Equal([0x11], capture.ArmKeys);
        Assert.NotNull(capture.EventAdmission);
        Assert.True(capture.EventAdmission!(recording.Events[0]));
        Assert.Equal(50UL, recording.DurationMicroseconds);
        Assert.Equal([1UL, 2UL], recording.Events.Select(item => item.Sequence));
        Assert.Same(target.Display, recording.Display);
        Assert.Same(target.Metadata, recording.Target);
    }

    [Fact]
    public async Task StartRecording_NonForegroundTarget_IsRejectedBeforeHooksStart()
    {
        var capture = new FakeCapture();
        await using var session = new ExactWheelSession(
            capture,
            new ExactWheelPlaybackEngine(),
            static _ => false);
        var target = new ExactWheelRecordingTarget(
            123,
            ExactWheelTestData.Display(),
            ExactWheelTestData.Target());

        Assert.Throws<InvalidOperationException>(() =>
            session.StartRecording(target));

        Assert.False(capture.Started);
        Assert.Equal(ExactWheelSessionState.Idle, session.State);
    }

    [Fact]
    public async Task StopRecording_Overflow_IsExplicitlyRejected()
    {
        var capture = new FakeCapture
        {
            Result = new InputCaptureResult(
                [],
                1,
                Overflowed: true,
                Win32Error: 0)
        };
        await using var session = new ExactWheelSession(
            capture,
            new ExactWheelPlaybackEngine(),
            static _ => true);
        session.StartRecording(new ExactWheelRecordingTarget(
            123,
            ExactWheelTestData.Display(),
            ExactWheelTestData.Target()));

        var exception = Assert.Throws<InvalidOperationException>(
            session.StopRecording);

        Assert.Contains("bounded event limit", exception.Message);
        Assert.Equal(ExactWheelSessionState.Idle, session.State);
    }

    private sealed class FakeCapture : IExactWheelInputCapture
    {
        internal InputCaptureResult Result { get; init; } =
            new([], 0, false, 0);

        internal bool Started { get; private set; }

        internal int MaximumEvents { get; private set; }

        internal IReadOnlyCollection<int> ArmKeys { get; private set; } = [];

        internal Func<ExactWheelInputEvent, bool>? EventAdmission
        { get; private set; }

        public InputCaptureMode Mode =>
            Started ? InputCaptureMode.Recording : InputCaptureMode.Idle;

        public void StartRecording(
            int maximumEvents,
            IReadOnlyCollection<int> waitForReleaseVirtualKeys,
            Func<ExactWheelInputEvent, bool>? eventAdmission)
        {
            Started = true;
            MaximumEvents = maximumEvents;
            ArmKeys = waitForReleaseVirtualKeys.ToArray();
            EventAdmission = eventAdmission;
        }

        public InputCaptureResult StopRecording()
        {
            Started = false;
            return Result;
        }

        public void StartInterventionMonitor(EventWaitHandle interventionEvent) =>
            throw new NotSupportedException();

        public void StopInterventionMonitor()
        {
        }

        public void Dispose()
        {
            Started = false;
        }
    }
}
