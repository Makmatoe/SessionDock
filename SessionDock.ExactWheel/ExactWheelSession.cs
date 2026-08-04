using System.ComponentModel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.ExactWheel;

public enum ExactWheelSessionState
{
    Idle,
    Recording,
    Playing,
    Disposed
}

public sealed class ExactWheelSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IExactWheelInputCapture _recordingCapture;
    private readonly ExactWheelPlaybackEngine _playback;
    private readonly Func<nint, bool> _isForeground;
    private ExactWheelRecordingTarget? _recordingTarget;
    private ExactWheelSessionState _state;

    public ExactWheelSession()
        : this(
            new LowLevelInputCapture(),
            new ExactWheelPlaybackEngine(),
            ExactWheelDesktopCapture.IsForeground)
    {
    }

    internal ExactWheelSession(
        IExactWheelInputCapture recordingCapture,
        ExactWheelPlaybackEngine playback,
        Func<nint, bool> isForeground)
    {
        _recordingCapture = recordingCapture ??
            throw new ArgumentNullException(nameof(recordingCapture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _isForeground = isForeground ??
            throw new ArgumentNullException(nameof(isForeground));
    }

    public ExactWheelSessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public void StartRecording(
        ExactWheelRecordingTarget target,
        ExactWheelRecordingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ExactWheelRecordingOptions();
        if (target.WindowHandle == 0)
            throw new ArgumentException("A target window is required.", nameof(target));
        if (!_isForeground(target.WindowHandle))
        {
            throw new InvalidOperationException(
                "SessionDock must focus and verify the selected client before recording starts.");
        }

        var diagnostic = new ExactWheelRecording(
            0,
            target.Display,
            target.Metadata,
            []);
        ExactWheelRecordingValidator.Validate(diagnostic);
        lock (_gate)
        {
            EnsureIdle();
            _recordingCapture.StartRecording(
                options.MaximumEvents,
                options.ArmUntilReleasedVirtualKeys,
                options.EventAdmission);
            _recordingTarget = target;
            _state = ExactWheelSessionState.Recording;
        }
    }

    public ExactWheelRecording StopRecording()
    {
        ExactWheelRecordingTarget target;
        InputCaptureResult captured;
        lock (_gate)
        {
            if (_state != ExactWheelSessionState.Recording ||
                _recordingTarget is null)
            {
                throw new InvalidOperationException("Recording is not active.");
            }

            target = _recordingTarget;
            captured = _recordingCapture.StopRecording();
            _recordingTarget = null;
            _state = ExactWheelSessionState.Idle;
        }

        if (captured.Overflowed)
        {
            throw new InvalidOperationException(
                "Recording reached its bounded event limit and stopped without dropping input silently.");
        }

        if (captured.Win32Error != 0)
        {
            throw new Win32Exception(
                captured.Win32Error,
                "The ExactWheel input hook stopped with an error.");
        }

        return ExactWheelRecordingValidator.Finalize(
            target.Display,
            target.Metadata,
            captured.Events,
            captured.DurationMicroseconds);
    }

    public async Task<ExactWheelPlaybackResult> PlayAsync(
        ExactWheelRecording recording,
        ExactWheelPlaybackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        options ??= new ExactWheelPlaybackOptions();
        lock (_gate)
        {
            EnsureIdle();
            _state = ExactWheelSessionState.Playing;
        }

        try
        {
            return await _playback.PlayAsync(
                    recording,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_state != ExactWheelSessionState.Disposed)
                    _state = ExactWheelSessionState.Idle;
            }
        }
    }

    public void EmergencyStop() => _playback.RequestStop();

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == ExactWheelSessionState.Disposed)
                return;
            _state = ExactWheelSessionState.Disposed;
            _recordingTarget = null;
        }

        _recordingCapture.Dispose();
        await _playback.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureIdle()
    {
        if (_state == ExactWheelSessionState.Disposed)
            throw new ObjectDisposedException(nameof(ExactWheelSession));
        if (_state != ExactWheelSessionState.Idle)
        {
            throw new InvalidOperationException(
                "Finish the active ExactWheel operation first.");
        }
    }
}
