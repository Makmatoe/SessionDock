using System.ComponentModel;
using System.Runtime.ExceptionServices;
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
    private PlaybackSequenceLease? _playbackSequence;
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
            if (_playbackSequence is not null)
            {
                throw new InvalidOperationException(
                    "Finish the retained playback sequence before recording.");
            }
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

        return captured.Events is ExactWheelInputEvent[] ownedEvents
            ? ExactWheelRecordingValidator.FinalizeOwned(
                target.Display,
                target.Metadata,
                ownedEvents,
                captured.DurationMicroseconds)
            : ExactWheelRecordingValidator.Finalize(
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

    /// <summary>
    /// Retains one physical-input intervention monitor across a serial group
    /// of PlayAsync calls. Dispose the returned lease after the group ends.
    /// One-off playback does not need this optimization.
    /// When physical-input intervention monitoring is enabled, playback in a
    /// retained sequence must use an empty WaitForReleaseVirtualKeys
    /// collection. Release those control keys before beginning the sequence
    /// or use one-off playback for arming behavior.
    /// </summary>
    public IAsyncDisposable BeginPlaybackSequence()
    {
        lock (_gate)
        {
            EnsureIdle();
            if (_playbackSequence is not null)
            {
                throw new InvalidOperationException(
                    "A playback sequence is already active.");
            }

            var sequence = new PlaybackSequenceLease(
                this,
                _playback.BeginInterventionSequence());
            _playbackSequence = sequence;
            return sequence;
        }
    }

    public void EmergencyStop() => _playback.RequestStop();

    public async ValueTask DisposeAsync()
    {
        PlaybackSequenceLease? playbackSequence;
        lock (_gate)
        {
            if (_state == ExactWheelSessionState.Disposed)
                return;
            _state = ExactWheelSessionState.Disposed;
            _recordingTarget = null;
            playbackSequence = _playbackSequence;
            _playbackSequence = null;
        }

        Exception? disposalFailure = null;
        if (playbackSequence is not null)
        {
            try
            {
                await playbackSequence.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
        }
        try
        {
            _recordingCapture.Dispose();
        }
        catch (Exception exception)
        {
            disposalFailure ??= exception;
        }
        try
        {
            await _playback.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalFailure ??= exception;
        }
        if (disposalFailure is not null)
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
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

    private void ReleasePlaybackSequence(PlaybackSequenceLease sequence)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_playbackSequence, sequence))
                _playbackSequence = null;
        }
    }

    private sealed class PlaybackSequenceLease(
        ExactWheelSession owner,
        IAsyncDisposable inner) : IAsyncDisposable
    {
        private readonly object _disposeGate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                owner.ReleasePlaybackSequence(this);
            }
        }
    }
}
