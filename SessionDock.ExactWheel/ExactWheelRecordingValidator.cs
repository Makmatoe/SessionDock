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
        if (recording.IsValidated)
            return;
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

        recording.MarkValidated();
    }

    public static void ValidatePlayable(ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.IsPlayableValidated)
            return;
        Validate(recording);
        if (recording.Events.Count == 0)
        {
            throw new InvalidDataException(
                "A playable macro must contain at least one input event.");
        }

        ValidateBalancedTransitions(recording.Events);
        recording.MarkPlayableValidated();
    }

    internal static ExactWheelRecording NormalizeLegacyV1Playable(
        ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        Validate(recording);

        // Current recordings are already balanced. Keep that overwhelmingly
        // common path allocation-free; only legacy files that fail transition
        // validation need the migration structures below.
        if (recording.Events.Count == 0)
        {
            ValidatePlayable(recording);
        }
        try
        {
            ValidatePlayable(recording);
            return recording;
        }
        catch (InvalidDataException)
        {
            // The base format has already passed Validate and the recording is
            // nonempty, so the remaining failure is a legacy held-transition
            // imbalance that can be normalized safely below.
        }

        var normalizedEvents = new List<ExactWheelInputEvent>(
            recording.Events.Count);
        var heldKeys = new Dictionary<
            KeyboardIdentity,
            LegacyHeldTransition>();
        var heldMouseButtons = new Dictionary<
            ExactWheelMouseButton,
            LegacyHeldTransition>();
        var heldOrder = new List<LegacyHeldTransition>();
        var changed = false;

        foreach (var inputEvent in recording.Events)
        {
            switch (inputEvent.Type)
            {
                case ExactWheelInputEventType.KeyDown:
                    {
                        var identity = GetKeyboardIdentity(inputEvent);
                        var normalizedIndex = normalizedEvents.Count;
                        normalizedEvents.Add(inputEvent);
                        if (heldKeys.TryGetValue(identity, out var heldKey))
                        {
                            // Typematic key-down events are real input and
                            // remain part of the original held transaction.
                            heldKey.AddRepeat(normalizedIndex);
                            break;
                        }

                        var transition = new LegacyHeldTransition(
                            inputEvent,
                            normalizedIndex);
                        heldKeys.Add(identity, transition);
                        heldOrder.Add(transition);
                        break;
                    }
                case ExactWheelInputEventType.KeyUp:
                    {
                        var identity = GetKeyboardIdentity(inputEvent);
                        if (!heldKeys.Remove(identity, out var heldKey))
                        {
                            changed = true;
                            break;
                        }

                        heldKey.Complete();
                        normalizedEvents.Add(inputEvent);
                        break;
                    }
                case ExactWheelInputEventType.MouseButtonDown:
                    {
                        var button =
                            (ExactWheelMouseButton)inputEvent.Data1;
                        if (heldMouseButtons.ContainsKey(button))
                        {
                            // Mouse buttons do not have typematic repeats.
                            // A second Down cannot form another transaction.
                            changed = true;
                            break;
                        }

                        var normalizedIndex = normalizedEvents.Count;
                        normalizedEvents.Add(inputEvent);
                        var transition = new LegacyHeldTransition(
                            inputEvent,
                            normalizedIndex);
                        heldMouseButtons.Add(button, transition);
                        heldOrder.Add(transition);
                        break;
                    }
                case ExactWheelInputEventType.MouseButtonUp:
                    {
                        var button =
                            (ExactWheelMouseButton)inputEvent.Data1;
                        if (!heldMouseButtons.Remove(
                                button,
                                out var heldButton))
                        {
                            changed = true;
                            break;
                        }

                        heldButton.Complete();
                        normalizedEvents.Add(inputEvent);
                        break;
                    }
                default:
                    normalizedEvents.Add(inputEvent);
                    break;
            }
        }

        var unresolvedCount = 0;
        foreach (var transition in heldOrder)
        {
            if (transition.IsHeld)
                unresolvedCount++;
        }

        if (unresolvedCount == 0 && !changed)
        {
            ValidatePlayable(recording);
            return recording;
        }

        if (unresolvedCount > 0)
        {
            var lastSequence = normalizedEvents.Count == 0
                ? 0UL
                : normalizedEvents[^1].Sequence;
            var canAppendEveryRelease =
                (ulong)normalizedEvents.Count <=
                    ExactWheelLimits.MaximumEventCount -
                    (ulong)unresolvedCount &&
                lastSequence <= ulong.MaxValue - (ulong)unresolvedCount;
            if (canAppendEveryRelease)
            {
                for (var index = heldOrder.Count - 1;
                     index >= 0;
                     index--)
                {
                    var transition = heldOrder[index];
                    if (!transition.IsHeld)
                        continue;
                    lastSequence++;
                    normalizedEvents.Add(transition.CreateRelease(
                        recording.DurationMicroseconds,
                        lastSequence));
                }
            }
            else
            {
                var remove = new bool[normalizedEvents.Count];
                var removeCount = 0;
                foreach (var transition in heldOrder)
                {
                    if (transition.IsHeld)
                    {
                        removeCount += transition
                            .MarkDownEventsForRemoval(remove);
                    }
                }

                var retained = new List<ExactWheelInputEvent>(
                    normalizedEvents.Count - removeCount);
                for (var index = 0; index < normalizedEvents.Count; index++)
                {
                    if (!remove[index])
                        retained.Add(normalizedEvents[index]);
                }
                normalizedEvents = retained;
            }
        }

        var normalized = ExactWheelRecording.CreateFromOwnedEvents(
            recording.DurationMicroseconds,
            recording.Display,
            recording.Target,
            normalizedEvents.ToArray());
        ValidatePlayable(normalized);
        return normalized;
    }

    private static void ValidateBalancedTransitions(
        IReadOnlyList<ExactWheelInputEvent> events)
    {
        var heldKeys = new HashSet<KeyboardIdentity>();
        var heldMouseButtons = new HashSet<ExactWheelMouseButton>();
        for (var index = 0; index < events.Count; index++)
        {
            var inputEvent = events[index];
            switch (inputEvent.Type)
            {
                case ExactWheelInputEventType.KeyDown:
                    // Repeated key-down messages while a key remains held are
                    // legitimate typematic input. They do not create another
                    // logical hold and still require only one final KeyUp.
                    _ = heldKeys.Add(GetKeyboardIdentity(inputEvent));
                    break;
                case ExactWheelInputEventType.KeyUp:
                    if (!heldKeys.Remove(GetKeyboardIdentity(inputEvent)))
                    {
                        throw InvalidEvent(
                            index,
                            "Key-up does not have a matching key-down.");
                    }
                    break;
                case ExactWheelInputEventType.MouseButtonDown:
                    if (!heldMouseButtons.Add(
                            (ExactWheelMouseButton)inputEvent.Data1))
                    {
                        throw InvalidEvent(
                            index,
                            "Mouse-button down is duplicated before its release.");
                    }
                    break;
                case ExactWheelInputEventType.MouseButtonUp:
                    if (!heldMouseButtons.Remove(
                            (ExactWheelMouseButton)inputEvent.Data1))
                    {
                        throw InvalidEvent(
                            index,
                            "Mouse-button up does not have a matching down.");
                    }
                    break;
            }
        }

        if (heldKeys.Count != 0 || heldMouseButtons.Count != 0)
        {
            throw new InvalidDataException(
                "A playable macro must release every key and mouse button before it ends.");
        }
    }

    private static KeyboardIdentity GetKeyboardIdentity(
        ExactWheelInputEvent inputEvent)
    {
        var usesScanCode = inputEvent.Data2 is > 0 and <= ushort.MaxValue;
        return new KeyboardIdentity(
            checked((ushort)(usesScanCode
                ? inputEvent.Data2
                : inputEvent.Data1)),
            usesScanCode,
            (inputEvent.Flags & ExactWheelKeyboardFlags.Extended) != 0);
    }

    internal static void ValidatePlaybackDestination(
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ValidateDisplay(display);
        ValidateTarget(target);
    }

    public static ExactWheelRecording Finalize(
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IEnumerable<ExactWheelInputEvent> events,
        ulong actualStopOffsetMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(events);
        return FinalizeOwned(
            display,
            target,
            events.ToArray(),
            actualStopOffsetMicroseconds);
    }

    internal static ExactWheelRecording FinalizeOwned(
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        ExactWheelInputEvent[] ownedEvents,
        ulong actualStopOffsetMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(ownedEvents);
        if (!IsInTimelineOrder(ownedEvents))
            Array.Sort(ownedEvents, CompareTimelineEvents);
        var recording = ExactWheelRecording.CreateFromOwnedEvents(
            actualStopOffsetMicroseconds,
            display,
            target,
            ownedEvents);
        Validate(recording);
        return recording;
    }

    internal static bool IsInTimelineOrder(
        IReadOnlyList<ExactWheelInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count < 2)
            return true;

        var previous = events[0];
        for (var index = 1; index < events.Count; index++)
        {
            var current = events[index];
            if (CompareTimelineEvents(previous, current) > 0)
                return false;
            previous = current;
        }

        return true;
    }

    private static int CompareTimelineEvents(
        ExactWheelInputEvent left,
        ExactWheelInputEvent right)
    {
        var timestampComparison = left.TimestampMicroseconds.CompareTo(
            right.TimestampMicroseconds);
        return timestampComparison != 0
            ? timestampComparison
            : left.Sequence.CompareTo(right.Sequence);
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

        for (var index = 0; index < display.Monitors.Count; index++)
        {
            var monitor = display.Monitors[index];
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
            target.ProcessBasename.Contains('\\') ||
            target.ProcessBasename.Contains('/') ||
            target.ProcessBasename.Contains('\0') ||
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

    private readonly record struct KeyboardIdentity(
        ushort Code,
        bool UsesScanCode,
        bool Extended);

    private sealed class LegacyHeldTransition(
        ExactWheelInputEvent downEvent,
        int normalizedIndex)
    {
        private List<int>? _repeatIndexes;

        internal bool IsHeld { get; private set; } = true;

        internal void AddRepeat(int index)
        {
            (_repeatIndexes ??= []).Add(index);
        }

        internal void Complete() => IsHeld = false;

        internal ExactWheelInputEvent CreateRelease(
            ulong timestampMicroseconds,
            ulong sequence) =>
            downEvent with
            {
                TimestampMicroseconds = timestampMicroseconds,
                Sequence = sequence,
                Type = downEvent.Type == ExactWheelInputEventType.KeyDown
                    ? ExactWheelInputEventType.KeyUp
                    : ExactWheelInputEventType.MouseButtonUp
            };

        internal int MarkDownEventsForRemoval(bool[] remove)
        {
            remove[normalizedIndex] = true;
            if (_repeatIndexes is null)
                return 1;
            foreach (var index in _repeatIndexes)
                remove[index] = true;
            return checked(1 + _repeatIndexes.Count);
        }
    }
}
