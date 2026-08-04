namespace SessionDock.ExactWheel;

public static class ExactWheelCoordinateTransforms
{
    public static ExactWheelRecording TransformClientRelative(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destinationDisplay);
        ArgumentNullException.ThrowIfNull(destinationTarget);
        EnsureClientRelativeSource(recording);
        ExactWheelRecordingValidator.ValidatePlaybackDestination(
            destinationDisplay,
            destinationTarget);
        EnsurePositive(destinationTarget.ClientRect, "destination client");

        var transformed = new ExactWheelInputEvent[recording.Events.Count];
        for (var index = 0; index < transformed.Length; index++)
        {
            var inputEvent = recording.Events[index];
            if (inputEvent.IsMouseEvent)
            {
                inputEvent = inputEvent with
                {
                    X = MapAxis(
                        inputEvent.X,
                        recording.Target.ClientRect.Left,
                        recording.Target.ClientRect.Width,
                        destinationTarget.ClientRect.Left,
                        destinationTarget.ClientRect.Width),
                    Y = MapAxis(
                        inputEvent.Y,
                        recording.Target.ClientRect.Top,
                        recording.Target.ClientRect.Height,
                        destinationTarget.ClientRect.Top,
                        destinationTarget.ClientRect.Height)
                };
            }

            transformed[index] = inputEvent;
        }

        return CreateValidatedCopy(
            recording,
            destinationDisplay,
            destinationTarget,
            transformed);
    }

    public static ExactWheelRecording TransformVirtualDesktopNormalized(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destinationDisplay);
        ArgumentNullException.ThrowIfNull(destinationTarget);
        ExactWheelRecordingValidator.Validate(recording);
        ExactWheelRecordingValidator.ValidatePlaybackDestination(
            destinationDisplay,
            destinationTarget);

        var transformed = new ExactWheelInputEvent[recording.Events.Count];
        for (var index = 0; index < transformed.Length; index++)
        {
            var inputEvent = recording.Events[index];
            transformed[index] = inputEvent.IsMouseEvent
                ? inputEvent with
                {
                    X = MapAxis(
                        inputEvent.X,
                        recording.Display.VirtualLeft,
                        recording.Display.VirtualWidth,
                        destinationDisplay.VirtualLeft,
                        destinationDisplay.VirtualWidth),
                    Y = MapAxis(
                        inputEvent.Y,
                        recording.Display.VirtualTop,
                        recording.Display.VirtualHeight,
                        destinationDisplay.VirtualTop,
                        destinationDisplay.VirtualHeight)
                }
                : inputEvent;
        }

        return CreateValidatedCopy(
            recording,
            destinationDisplay,
            destinationTarget,
            transformed);
    }

    /// <summary>
    /// Creates a small immutable playback mapper instead of copying the full
    /// recording for every destination client. The mapper is safe to reuse
    /// for the same source geometry and maps only events that are actually
    /// dispatched by the playback scheduler.
    /// </summary>
    public static ExactWheelPlaybackCoordinateTransform
        CreateClientRelativePlaybackTransform(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget) =>
        CreateClientRelativePlaybackTransformCancellable(
            recording,
            destinationDisplay,
            destinationTarget,
            CancellationToken.None);

    internal static ExactWheelPlaybackCoordinateTransform
        CreateClientRelativePlaybackTransformCancellable(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destinationDisplay);
        ArgumentNullException.ThrowIfNull(destinationTarget);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureClientRelativeSourceCancellable(
            recording,
            cancellationToken);
        ExactWheelRecordingValidator.ValidatePlaybackDestination(
            destinationDisplay,
            destinationTarget);
        EnsurePositive(destinationTarget.ClientRect, "destination client");
        return new ExactWheelPlaybackCoordinateTransform(
            ExactWheelPlaybackCoordinateTransformKind.ClientRelative,
            recording.Display,
            recording.Target,
            destinationDisplay,
            destinationTarget);
    }

    /// <summary>
    /// Creates a small immutable virtual-desktop mapper instead of copying
    /// the full recording for every destination topology.
    /// </summary>
    public static ExactWheelPlaybackCoordinateTransform
        CreateVirtualDesktopNormalizedPlaybackTransform(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destinationDisplay);
        ArgumentNullException.ThrowIfNull(destinationTarget);
        ExactWheelRecordingValidator.ValidatePlayable(recording);
        ExactWheelRecordingValidator.ValidatePlaybackDestination(
            destinationDisplay,
            destinationTarget);
        return new ExactWheelPlaybackCoordinateTransform(
            ExactWheelPlaybackCoordinateTransformKind.VirtualDesktopNormalized,
            recording.Display,
            recording.Target,
            destinationDisplay,
            destinationTarget);
    }

    public static ExactWheelRecording TransformMonitorNormalized(
        ExactWheelRecording recording,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(destinationDisplay);
        ArgumentNullException.ThrowIfNull(destinationTarget);
        ExactWheelRecordingValidator.Validate(recording);
        if (recording.Display.Monitors.Count !=
            destinationDisplay.Monitors.Count)
        {
            throw new InvalidDataException(
                "Monitor-normalized playback requires the same monitor count. " +
                "Use explicit virtual-desktop scaling or change the layout.");
        }

        var transformed = new ExactWheelInputEvent[recording.Events.Count];
        for (var index = 0; index < transformed.Length; index++)
        {
            var inputEvent = recording.Events[index];
            if (inputEvent.IsMouseEvent)
            {
                var sourceMonitorIndex = FindContainingMonitor(
                    recording.Display.Monitors,
                    inputEvent.X,
                    inputEvent.Y);
                if (sourceMonitorIndex < 0)
                {
                    throw new InvalidDataException(
                        $"Event {index} lies in a virtual-desktop gap and " +
                        "cannot be monitor-normalized safely.");
                }

                var source = recording.Display
                    .Monitors[sourceMonitorIndex]
                    .Bounds;
                var destination = destinationDisplay
                    .Monitors[sourceMonitorIndex]
                    .Bounds;
                inputEvent = inputEvent with
                {
                    X = MapAxis(
                        inputEvent.X,
                        source.Left,
                        source.Width,
                        destination.Left,
                        destination.Width),
                    Y = MapAxis(
                        inputEvent.Y,
                        source.Top,
                        source.Height,
                        destination.Top,
                        destination.Height)
                };
            }

            transformed[index] = inputEvent;
        }

        return CreateValidatedCopy(
            recording,
            destinationDisplay,
            destinationTarget,
            transformed);
    }

    internal static int MapAxis(
        int pixel,
        int sourceOrigin,
        int sourceExtent,
        int destinationOrigin,
        int destinationExtent)
    {
        if (sourceExtent <= 0 || destinationExtent <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceExtent));

        if (sourceExtent == 1 || destinationExtent == 1)
            return destinationOrigin;

        var relative = (long)pixel - sourceOrigin;
        if (relative < 0 || relative >= sourceExtent)
            throw new ArgumentOutOfRangeException(nameof(pixel));

        var numerator = checked(relative * (destinationExtent - 1L));
        var denominator = sourceExtent - 1L;
        var mapped = (numerator + denominator / 2L) / denominator;
        return checked(destinationOrigin + (int)mapped);
    }

    internal static int NormalizeForSendInput(
        int pixel,
        int virtualOrigin,
        int virtualExtent)
    {
        var denominator = Math.Max(1L, virtualExtent - 1L);
        var relative = (long)pixel - virtualOrigin;
        long normalized;
        if (relative >= 0)
        {
            normalized = (relative * 65_535L + denominator / 2L) /
                denominator;
        }
        else
        {
            normalized = -((-relative * 65_535L + denominator / 2L) /
                denominator);
        }

        return (int)Math.Clamp(normalized, 0L, 65_535L);
    }

    private static ExactWheelRecording CreateValidatedCopy(
        ExactWheelRecording source,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget,
        ExactWheelInputEvent[] transformed)
    {
        var result = ExactWheelRecording.CreateFromOwnedEvents(
            source.DurationMicroseconds,
            destinationDisplay,
            destinationTarget,
            transformed);
        ExactWheelRecordingValidator.Validate(result);
        return result;
    }

    internal static void EnsureClientRelativeSource(
        ExactWheelRecording recording) =>
        EnsureClientRelativeSourceCore(recording, CancellationToken.None);

    internal static void EnsureClientRelativeSourceCancellable(
        ExactWheelRecording recording,
        CancellationToken cancellationToken) =>
        EnsureClientRelativeSourceCore(recording, cancellationToken);

    private static void EnsureClientRelativeSourceCore(
        ExactWheelRecording recording,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExactWheelRecordingValidator.ValidatePlayable(recording);
        if (recording.IsClientRelativeValidated)
            return;

        EnsurePositive(recording.Target.ClientRect, "recorded client");
        for (var index = 0; index < recording.Events.Count; index++)
        {
            if ((index & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var inputEvent = recording.Events[index];
            if (inputEvent.IsMouseEvent &&
                !recording.Target.ClientRect.Contains(
                    inputEvent.X,
                    inputEvent.Y))
            {
                throw new InvalidDataException(
                    $"Event {index} is outside the recorded client area; " +
                    "client-relative playback was rejected instead of clamping it.");
            }
        }

        recording.MarkClientRelativeValidated();
    }

    private static int FindContainingMonitor(
        IReadOnlyList<ExactWheelMonitorSnapshot> monitors,
        int x,
        int y)
    {
        for (var index = 0; index < monitors.Count; index++)
        {
            if (monitors[index].Bounds.Contains(x, y))
                return index;
        }

        return -1;
    }

    private static void EnsurePositive(
        ExactWheelRect rectangle,
        string description)
    {
        if (rectangle.Right <= rectangle.Left ||
            rectangle.Bottom <= rectangle.Top)
        {
            throw new InvalidDataException(
                $"The {description} rectangle must have a positive size.");
        }
    }
}

internal enum ExactWheelPlaybackCoordinateTransformKind
{
    ClientRelative,
    VirtualDesktopNormalized
}

/// <summary>
/// An immutable, allocation-free per-event coordinate mapper used by
/// ExactWheel playback. It retains only display and rectangle metadata, not a
/// source event array, so memory grows with unique macros plus a small amount
/// per destination instead of multiplying every macro event by client count.
/// </summary>
public sealed class ExactWheelPlaybackCoordinateTransform
{
    private readonly ExactWheelPlaybackCoordinateTransformKind _kind;
    private readonly ExactWheelDisplayTopology _sourceDisplay;
    private readonly ExactWheelTargetMetadata _sourceTarget;

    internal ExactWheelPlaybackCoordinateTransform(
        ExactWheelPlaybackCoordinateTransformKind kind,
        ExactWheelDisplayTopology sourceDisplay,
        ExactWheelTargetMetadata sourceTarget,
        ExactWheelDisplayTopology destinationDisplay,
        ExactWheelTargetMetadata destinationTarget)
    {
        _kind = kind;
        _sourceDisplay = sourceDisplay;
        _sourceTarget = sourceTarget;
        DestinationDisplay = destinationDisplay;
        DestinationTarget = destinationTarget;
    }

    public ExactWheelDisplayTopology DestinationDisplay { get; }

    public ExactWheelTargetMetadata DestinationTarget { get; }

    internal void ValidateRecording(ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ExactWheelRecordingValidator.ValidatePlayable(recording);
        if (!DisplaysEqual(_sourceDisplay, recording.Display) ||
            !TargetsEqual(_sourceTarget, recording.Target))
        {
            throw new InvalidDataException(
                "The playback coordinate transform does not match the source recording geometry.");
        }

        if (_kind ==
            ExactWheelPlaybackCoordinateTransformKind.ClientRelative)
        {
            ExactWheelCoordinateTransforms.EnsureClientRelativeSource(
                recording);
        }
    }

    internal ExactWheelInputEvent TransformEvent(
        ExactWheelInputEvent inputEvent)
    {
        if (!inputEvent.IsMouseEvent)
            return inputEvent;

        var transformed = _kind switch
        {
            ExactWheelPlaybackCoordinateTransformKind.ClientRelative =>
                inputEvent with
                {
                    X = ExactWheelCoordinateTransforms.MapAxis(
                        inputEvent.X,
                        _sourceTarget.ClientRect.Left,
                        _sourceTarget.ClientRect.Width,
                        DestinationTarget.ClientRect.Left,
                        DestinationTarget.ClientRect.Width),
                    Y = ExactWheelCoordinateTransforms.MapAxis(
                        inputEvent.Y,
                        _sourceTarget.ClientRect.Top,
                        _sourceTarget.ClientRect.Height,
                        DestinationTarget.ClientRect.Top,
                        DestinationTarget.ClientRect.Height)
                },
            ExactWheelPlaybackCoordinateTransformKind
                .VirtualDesktopNormalized => inputEvent with
                {
                    X = ExactWheelCoordinateTransforms.MapAxis(
                        inputEvent.X,
                        _sourceDisplay.VirtualLeft,
                        _sourceDisplay.VirtualWidth,
                        DestinationDisplay.VirtualLeft,
                        DestinationDisplay.VirtualWidth),
                    Y = ExactWheelCoordinateTransforms.MapAxis(
                        inputEvent.Y,
                        _sourceDisplay.VirtualTop,
                        _sourceDisplay.VirtualHeight,
                        DestinationDisplay.VirtualTop,
                        DestinationDisplay.VirtualHeight)
                },
            _ => throw new InvalidDataException(
                "The playback coordinate transform kind is invalid.")
        };

        var destinationRight =
            (long)DestinationDisplay.VirtualLeft +
            DestinationDisplay.VirtualWidth;
        var destinationBottom =
            (long)DestinationDisplay.VirtualTop +
            DestinationDisplay.VirtualHeight;
        if (transformed.X < DestinationDisplay.VirtualLeft ||
            transformed.X >= destinationRight ||
            transformed.Y < DestinationDisplay.VirtualTop ||
            transformed.Y >= destinationBottom)
        {
            // The materialized transform path validates the complete result
            // and rejects this condition. Preserve that fail-closed contract
            // without copying the source event array: SendInput normalization
            // clamps out-of-range pixels and must never silently turn an
            // off-desktop destination into an edge click.
            throw new InvalidDataException(
                "The transformed mouse event is outside the destination display.");
        }

        return transformed;
    }

    private static bool DisplaysEqual(
        ExactWheelDisplayTopology left,
        ExactWheelDisplayTopology right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.VirtualLeft != right.VirtualLeft ||
            left.VirtualTop != right.VirtualTop ||
            left.VirtualWidth != right.VirtualWidth ||
            left.VirtualHeight != right.VirtualHeight ||
            left.Monitors.Count != right.Monitors.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Monitors.Count; index++)
        {
            if (left.Monitors[index] != right.Monitors[index])
                return false;
        }

        return true;
    }

    private static bool TargetsEqual(
        ExactWheelTargetMetadata left,
        ExactWheelTargetMetadata right) =>
        ReferenceEquals(left, right) ||
        (string.Equals(
             left.ProcessBasename,
             right.ProcessBasename,
             StringComparison.Ordinal) &&
         string.Equals(
             left.WindowClass,
             right.WindowClass,
             StringComparison.Ordinal) &&
         left.WindowRect == right.WindowRect &&
         left.ClientRect == right.ClientRect);
}
