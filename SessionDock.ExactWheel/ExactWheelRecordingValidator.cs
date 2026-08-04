namespace SessionDock.ExactWheel;

public static class ExactWheelRecordingValidator
{
    private const ExactWheelKeyboardFlags KnownKeyboardFlags =
        ExactWheelKeyboardFlags.Extended |
        ExactWheelKeyboardFlags.System |
        ExactWheelKeyboardFlags.AltContext;

    public static void Validate(ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.DurationMicroseconds >
            ExactWheelLimits.MaximumDurationMicroseconds)
        {
            throw new InvalidDataException(
                "Recording duration exceeds 24 hours.");
        }

        if ((ulong)recording.Events.Count > ExactWheelLimits.MaximumEventCount)
        {
            throw new InvalidDataException(
                "Recording event count exceeds the safety limit.");
        }

        ValidateDisplay(recording.Display);
        ValidateTarget(recording.Target);

        ulong previousTimestamp = 0;
        ulong previousSequence = 0;
        var havePrevious = false;
        for (var index = 0; index < recording.Events.Count; index++)
        {
            var inputEvent = recording.Events[index];
            if (!Enum.IsDefined(inputEvent.Type))
            {
                throw InvalidEvent(index, "Event type is unknown.");
            }

            if (inputEvent.TimestampMicroseconds >
                recording.DurationMicroseconds)
            {
                throw InvalidEvent(
                    index,
                    "Event timestamp exceeds the recorded stop time.");
            }

            if (havePrevious &&
                inputEvent.TimestampMicroseconds < previousTimestamp)
            {
                throw InvalidEvent(
                    index,
                    "Event timestamps are not monotonically nondecreasing.");
            }

            if (havePrevious && inputEvent.Sequence <= previousSequence)
            {
                throw InvalidEvent(
                    index,
                    "Event sequence numbers are not strictly increasing.");
            }

            ValidateEvent(inputEvent, recording.Display, index);
            previousTimestamp = inputEvent.TimestampMicroseconds;
            previousSequence = inputEvent.Sequence;
            havePrevious = true;
        }
    }

    public static void ValidatePlayable(ExactWheelRecording recording)
    {
        Validate(recording);
        if (recording.Events.Count == 0)
        {
            throw new InvalidDataException(
                "A playable macro must contain at least one input event.");
        }
    }

    public static ExactWheelRecording Finalize(
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IEnumerable<ExactWheelInputEvent> events,
        ulong actualStopOffsetMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events
            .OrderBy(inputEvent => inputEvent.TimestampMicroseconds)
            .ThenBy(inputEvent => inputEvent.Sequence)
            .ToArray();
        var recording = new ExactWheelRecording(
            actualStopOffsetMicroseconds,
            display,
            target,
            ordered);
        Validate(recording);
        return recording;
    }

    private static void ValidateDisplay(ExactWheelDisplayTopology display)
    {
        if (display.VirtualWidth <= 0 ||
            display.VirtualHeight <= 0 ||
            display.VirtualWidth > ExactWheelLimits.MaximumVirtualExtent ||
            display.VirtualHeight > ExactWheelLimits.MaximumVirtualExtent ||
            display.Monitors.Count is 0 or > ExactWheelLimits.MaximumMonitorCount)
        {
            throw new InvalidDataException(
                "Virtual desktop topology is invalid.");
        }

        var virtualRight = (long)display.VirtualLeft + display.VirtualWidth;
        var virtualBottom = (long)display.VirtualTop + display.VirtualHeight;
        if (virtualRight > int.MaxValue || virtualRight < int.MinValue ||
            virtualBottom > int.MaxValue || virtualBottom < int.MinValue)
        {
            throw new InvalidDataException(
                "Virtual desktop topology is invalid.");
        }

        foreach (var monitor in display.Monitors)
        {
            var bounds = monitor.Bounds;
            if (bounds.Right <= bounds.Left ||
                bounds.Bottom <= bounds.Top ||
                bounds.Left < display.VirtualLeft ||
                bounds.Top < display.VirtualTop ||
                bounds.Right > virtualRight ||
                bounds.Bottom > virtualBottom ||
                monitor.DpiX is 0 or > ExactWheelLimits.MaximumPlausibleDpi ||
                monitor.DpiY is 0 or > ExactWheelLimits.MaximumPlausibleDpi)
            {
                throw new InvalidDataException(
                    "A monitor snapshot is invalid.");
            }
        }
    }

    private static void ValidateTarget(ExactWheelTargetMetadata target)
    {
        if (string.IsNullOrEmpty(target.ProcessBasename) ||
            target.ProcessBasename.Length >
                ExactWheelLimits.MaximumProcessBasenameUtf16Units ||
            target.WindowClass.Length >
                ExactWheelLimits.MaximumWindowClassUtf16Units ||
            target.ProcessBasename.IndexOfAny(['\\', '/', '\0']) >= 0 ||
            target.WindowClass.Contains('\0', StringComparison.Ordinal) ||
            !HasValidUtf16(target.ProcessBasename) ||
            !HasValidUtf16(target.WindowClass) ||
            IsInverted(target.WindowRect) ||
            IsInverted(target.ClientRect))
        {
            throw new InvalidDataException(
                "Foreground target metadata is invalid.");
        }
    }

    private static void ValidateEvent(
        ExactWheelInputEvent inputEvent,
        ExactWheelDisplayTopology display,
        int index)
    {
        if (inputEvent.IsMouseEvent)
        {
            var right = (long)display.VirtualLeft + display.VirtualWidth;
            var bottom = (long)display.VirtualTop + display.VirtualHeight;
            if (inputEvent.X < display.VirtualLeft ||
                inputEvent.X >= right ||
                inputEvent.Y < display.VirtualTop ||
                inputEvent.Y >= bottom ||
                inputEvent.Flags != ExactWheelKeyboardFlags.None ||
                inputEvent.Data2 != 0)
            {
                throw InvalidEvent(
                    index,
                    "Mouse event fields or coordinates are outside the recorded display.");
            }

            switch (inputEvent.Type)
            {
                case ExactWheelInputEventType.MouseMove
                    when inputEvent.Data1 != 0:
                    throw InvalidEvent(
                        index,
                        "Mouse-move data must be zero.");
                case ExactWheelInputEventType.MouseButtonDown:
                case ExactWheelInputEventType.MouseButtonUp:
                    if (inputEvent.Data1 <
                            (int)ExactWheelMouseButton.Left ||
                        inputEvent.Data1 >
                            (int)ExactWheelMouseButton.X2)
                    {
                        throw InvalidEvent(
                            index,
                            "Mouse-button identifier is invalid.");
                    }

                    break;
                case ExactWheelInputEventType.VerticalWheel:
                case ExactWheelInputEventType.HorizontalWheel:
                    if (inputEvent.Data1 == 0 ||
                        inputEvent.Data1 < short.MinValue ||
                        inputEvent.Data1 > short.MaxValue)
                    {
                        throw InvalidEvent(
                            index,
                            "Wheel delta must be a nonzero signed 16-bit value.");
                    }

                    break;
            }

            return;
        }

        if (inputEvent.IsKeyboardEvent)
        {
            if (inputEvent.X != 0 ||
                inputEvent.Y != 0 ||
                inputEvent.Data1 is < 0 or > 0xFF ||
                inputEvent.Data2 is < 0 or > 0xFFFF ||
                (inputEvent.Flags & ~KnownKeyboardFlags) != 0)
            {
                throw InvalidEvent(
                    index,
                    "Keyboard event contains an invalid key, scan code, coordinate, or flag.");
            }

            return;
        }

        throw InvalidEvent(index, "Event type is unknown.");
    }

    private static InvalidDataException InvalidEvent(
        int index,
        string message) =>
        new($"Event {index}: {message}");

    private static bool IsInverted(ExactWheelRect rectangle) =>
        rectangle.Right < rectangle.Left ||
        rectangle.Bottom < rectangle.Top;

    private static bool HasValidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= value.Length ||
                    !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
