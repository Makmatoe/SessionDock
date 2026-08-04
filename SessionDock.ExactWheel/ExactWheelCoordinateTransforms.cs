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
        ExactWheelRecordingValidator.Validate(recording);
        EnsurePositive(recording.Target.ClientRect, "recorded client");
        EnsurePositive(destinationTarget.ClientRect, "destination client");

        var transformed = new ExactWheelInputEvent[recording.Events.Count];
        for (var index = 0; index < transformed.Length; index++)
        {
            var inputEvent = recording.Events[index];
            if (inputEvent.IsMouseEvent)
            {
                if (!recording.Target.ClientRect.Contains(
                        inputEvent.X,
                        inputEvent.Y))
                {
                    throw new InvalidDataException(
                        $"Event {index} is outside the recorded client area; " +
                        "client-relative playback was rejected instead of clamping it.");
                }

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
