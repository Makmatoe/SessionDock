using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO.MemoryMappedFiles;

namespace SessionDock.ExactWheel;

public static class ExactWheelLimits
{
    // A 500k-event macro is roughly 24 MiB on disk before object overhead and
    // comfortably covers about an hour of high-frequency raw input. Keeping
    // this below the old multi-million-event limit prevents validation and
    // coordinate transforms from multiplying hundreds of MiB per target.
    public const ulong MaximumEventCount = 500_000;
    public const ulong MaximumDurationMicroseconds =
        24UL * 60UL * 60UL * 1_000_000UL;
    public const int MaximumMacroFileMebibytes = 64;
    public const long MaximumMacroFileBytes =
        MaximumMacroFileMebibytes * 1024L * 1024L;
    public const int DefaultCaptureEventCapacity =
        (int)MaximumEventCount;
    public const int MaximumMonitorCount = 64;
    public const int MaximumProcessBasenameUtf16Units = 260;
    public const int MaximumWindowClassUtf16Units = 256;
    public const int MaximumVirtualExtent = 1_000_000;
    public const uint MaximumPlausibleDpi = 9_600;
    public const ulong PrivateInputMarker = 0x455741435457484C;
}

public enum ExactWheelInputEventType : byte
{
    MouseMove = 1,
    MouseButtonDown = 2,
    MouseButtonUp = 3,
    VerticalWheel = 4,
    HorizontalWheel = 5,
    KeyDown = 6,
    KeyUp = 7
}

public enum ExactWheelMouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5
}

[Flags]
public enum ExactWheelKeyboardFlags : uint
{
    None = 0,
    Extended = 1U << 0,
    System = 1U << 1,
    AltContext = 1U << 2
}

public readonly record struct ExactWheelInputEvent(
    ulong TimestampMicroseconds,
    ulong Sequence,
    ExactWheelInputEventType Type,
    int X,
    int Y,
    int Data1,
    int Data2,
    ExactWheelKeyboardFlags Flags = ExactWheelKeyboardFlags.None)
{
    public bool IsMouseEvent => Type is
        ExactWheelInputEventType.MouseMove or
        ExactWheelInputEventType.MouseButtonDown or
        ExactWheelInputEventType.MouseButtonUp or
        ExactWheelInputEventType.VerticalWheel or
        ExactWheelInputEventType.HorizontalWheel;

    public bool IsKeyboardEvent => Type is
        ExactWheelInputEventType.KeyDown or
        ExactWheelInputEventType.KeyUp;
}

public readonly record struct ExactWheelRect(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => checked(Right - Left);

    public int Height => checked(Bottom - Top);

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct ExactWheelMonitorSnapshot(
    ExactWheelRect Bounds,
    uint DpiX = 96,
    uint DpiY = 96);

public sealed class ExactWheelDisplayTopology
{
    private readonly ReadOnlyCollection<ExactWheelMonitorSnapshot> _monitors;

    public ExactWheelDisplayTopology(
        int virtualLeft,
        int virtualTop,
        int virtualWidth,
        int virtualHeight,
        IEnumerable<ExactWheelMonitorSnapshot> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        VirtualLeft = virtualLeft;
        VirtualTop = virtualTop;
        VirtualWidth = virtualWidth;
        VirtualHeight = virtualHeight;
        _monitors = Array.AsReadOnly(monitors.ToArray());
    }

    public int VirtualLeft { get; }

    public int VirtualTop { get; }

    public int VirtualWidth { get; }

    public int VirtualHeight { get; }

    public IReadOnlyList<ExactWheelMonitorSnapshot> Monitors => _monitors;

    public ExactWheelRect VirtualBounds => new(
        VirtualLeft,
        VirtualTop,
        checked(VirtualLeft + VirtualWidth),
        checked(VirtualTop + VirtualHeight));
}

public sealed class ExactWheelTargetMetadata
{
    public ExactWheelTargetMetadata(
        string processBasename,
        string windowClass,
        ExactWheelRect windowRect,
        ExactWheelRect clientRect)
    {
        ProcessBasename = processBasename ??
            throw new ArgumentNullException(nameof(processBasename));
        WindowClass = windowClass ??
            throw new ArgumentNullException(nameof(windowClass));
        WindowRect = windowRect;
        ClientRect = clientRect;
    }

    public string ProcessBasename { get; }

    public string WindowClass { get; }

    public ExactWheelRect WindowRect { get; }

    public ExactWheelRect ClientRect { get; }
}

public sealed class ExactWheelRecording
{
    private readonly IReadOnlyList<ExactWheelInputEvent> _events;
    private int _clientRelativeValidationState;
    private int _playableValidationState;
    private int _validationState;

    public ExactWheelRecording(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IEnumerable<ExactWheelInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(events);
        DurationMicroseconds = durationMicroseconds;
        Display = display;
        Target = target;
        _events = Array.AsReadOnly(events.ToArray());
    }

    public ulong DurationMicroseconds { get; }

    public ExactWheelDisplayTopology Display { get; }

    public ExactWheelTargetMetadata Target { get; }

    public IReadOnlyList<ExactWheelInputEvent> Events => _events;

    internal bool IsValidated => Volatile.Read(ref _validationState) != 0;

    internal bool IsClientRelativeValidated =>
        Volatile.Read(ref _clientRelativeValidationState) != 0;

    internal bool IsPlayableValidated =>
        Volatile.Read(ref _playableValidationState) != 0;

    internal void MarkValidated() =>
        Volatile.Write(ref _validationState, 1);

    internal void MarkClientRelativeValidated() =>
        Volatile.Write(ref _clientRelativeValidationState, 1);

    internal void MarkPlayableValidated() =>
        Volatile.Write(ref _playableValidationState, 1);

    internal static ExactWheelRecording CreateFromOwnedEvents(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        ExactWheelInputEvent[] ownedEvents) =>
        new(
            durationMicroseconds,
            display,
            target,
            ownedEvents,
            takeOwnership: true);

    internal static ExactWheelRecording CreateFromOwnedEventSource(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IReadOnlyList<ExactWheelInputEvent> ownedEvents) =>
        new(
            durationMicroseconds,
            display,
            target,
            ownedEvents,
            takeOwnership: true);

    private ExactWheelRecording(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        ExactWheelInputEvent[] ownedEvents,
        bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedEvents);
        if (!takeOwnership)
            throw new ArgumentException("Event ownership is required.");
        DurationMicroseconds = durationMicroseconds;
        Display = display;
        Target = target;
        _events = Array.AsReadOnly(ownedEvents);
    }

    private ExactWheelRecording(
        ulong durationMicroseconds,
        ExactWheelDisplayTopology display,
        ExactWheelTargetMetadata target,
        IReadOnlyList<ExactWheelInputEvent> ownedEvents,
        bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedEvents);
        if (!takeOwnership)
            throw new ArgumentException("Event ownership is required.");
        DurationMicroseconds = durationMicroseconds;
        Display = display;
        Target = target;
        _events = ownedEvents;
    }
}

public sealed record ExactWheelRecordingTarget(
    nint WindowHandle,
    ExactWheelDisplayTopology Display,
    ExactWheelTargetMetadata Metadata);

/// <summary>
/// Owns a page-backed copy of one already validated recording. Only a small
/// decoded page stays in managed memory; Windows may page the immutable event
/// payload without rebuilding the recording between playback loops.
/// </summary>
internal sealed class ExactWheelPageableRecording : IDisposable
{
    private const int EncodedEventBytes = 40;
    private const int WriteBufferBytes = 64 * 1024;

    private readonly FileStream _backingStream;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedExactWheelEventList _events;
    private bool _disposed;

    private ExactWheelPageableRecording(
        FileStream backingStream,
        MemoryMappedFile mapping,
        MemoryMappedExactWheelEventList events,
        ExactWheelRecording recording,
        string backingPath)
    {
        _backingStream = backingStream;
        _mapping = mapping;
        _events = events;
        Recording = recording;
        BackingPath = backingPath;
    }

    internal ExactWheelRecording Recording { get; }

    internal string BackingPath { get; }

    internal static ExactWheelPageableRecording CreateCancellable(
        ExactWheelRecording source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ExactWheelRecordingValidator.ValidatePlayable(source);
        cancellationToken.ThrowIfCancellationRequested();

        var capacity = checked(
            (long)source.Events.Count * EncodedEventBytes);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.ExactWheel.{Guid.NewGuid():N}.tmp");
        FileStream? stream = null;
        MemoryMappedFile? mapping = null;
        MemoryMappedExactWheelEventList? events = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Delete,
                WriteBufferBytes,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            WriteEvents(stream, source.Events, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Flush();
            mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);
            var accessor = mapping.CreateViewAccessor(
                0,
                capacity,
                MemoryMappedFileAccess.Read);
            try
            {
                events = new MemoryMappedExactWheelEventList(
                    accessor,
                    source.Events.Count);
            }
            catch
            {
                accessor.Dispose();
                throw;
            }

            var recording = ExactWheelRecording.CreateFromOwnedEventSource(
                source.DurationMicroseconds,
                source.Display,
                source.Target,
                events);
            if (source.IsValidated)
                recording.MarkValidated();
            if (source.IsClientRelativeValidated)
                recording.MarkClientRelativeValidated();
            if (source.IsPlayableValidated)
                recording.MarkPlayableValidated();
            return new ExactWheelPageableRecording(
                stream,
                mapping,
                events,
                recording,
                path);
        }
        catch
        {
            events?.Dispose();
            mapping?.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.Dispose();
        _mapping.Dispose();
        _backingStream.Dispose();
    }

    private static void WriteEvents(
        Stream destination,
        IReadOnlyList<ExactWheelInputEvent> events,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(WriteBufferBytes);
        try
        {
            var written = 0;
            for (var index = 0; index < events.Count; index++)
            {
                if (buffer.Length - written < EncodedEventBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, written);
                    written = 0;
                }

                WriteEvent(
                    buffer.AsSpan(written, EncodedEventBytes),
                    events[index]);
                written += EncodedEventBytes;
            }

            if (written > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, written);
            }
        }
        finally
        {
            Array.Clear(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteEvent(
        Span<byte> destination,
        ExactWheelInputEvent inputEvent)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination,
            inputEvent.TimestampMicroseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[8..],
            inputEvent.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[16..],
            (int)inputEvent.Type);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[20..],
            inputEvent.X);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[24..],
            inputEvent.Y);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[28..],
            inputEvent.Data1);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[32..],
            inputEvent.Data2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[36..],
            (uint)inputEvent.Flags);
    }

    private sealed class MemoryMappedExactWheelEventList :
        IReadOnlyList<ExactWheelInputEvent>,
        IDisposable
    {
        // A 512-event page is 20 KiB encoded and approximately 20 KiB decoded.
        // Even the maximum 129-source run therefore keeps only a few MiB of
        // spill-page buffers in addition to the fixed managed hot cache.
        private const int EventsPerPage = 512;

        private readonly byte[] _encodedPage =
            new byte[EventsPerPage * EncodedEventBytes];
        private readonly ExactWheelInputEvent[] _decodedPage =
            new ExactWheelInputEvent[EventsPerPage];
        private MemoryMappedViewAccessor? _accessor;
        private int _pageStart = -1;
        private int _pageCount;

        internal MemoryMappedExactWheelEventList(
            MemoryMappedViewAccessor accessor,
            int count)
        {
            _accessor = accessor ??
                throw new ArgumentNullException(nameof(accessor));
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Count = count;
        }

        public int Count { get; }

        public ExactWheelInputEvent this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                if (index < _pageStart ||
                    index >= _pageStart + _pageCount)
                {
                    LoadPage(index);
                }
                return _decodedPage[index - _pageStart];
            }
        }

        public IEnumerator<ExactWheelInputEvent> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            _accessor?.Dispose();
            _accessor = null;
            _pageStart = -1;
            _pageCount = 0;
        }

        private void LoadPage(int requestedIndex)
        {
            var accessor = _accessor ??
                throw new ObjectDisposedException(
                    nameof(ExactWheelPageableRecording));
            var pageStart = requestedIndex / EventsPerPage * EventsPerPage;
            var pageCount = Math.Min(EventsPerPage, Count - pageStart);
            var byteCount = checked(pageCount * EncodedEventBytes);
            var read = accessor.ReadArray(
                checked((long)pageStart * EncodedEventBytes),
                _encodedPage,
                0,
                byteCount);
            if (read != byteCount)
                throw new EndOfStreamException("The pageable macro is truncated.");

            for (var index = 0; index < pageCount; index++)
            {
                var encoded = _encodedPage.AsSpan(
                    index * EncodedEventBytes,
                    EncodedEventBytes);
                _decodedPage[index] = new ExactWheelInputEvent(
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded),
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded[8..]),
                    (ExactWheelInputEventType)
                        BinaryPrimitives.ReadInt32LittleEndian(encoded[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(encoded[20..]),
                    BinaryPrimitives.ReadInt32LittleEndian(encoded[24..]),
                    BinaryPrimitives.ReadInt32LittleEndian(encoded[28..]),
                    BinaryPrimitives.ReadInt32LittleEndian(encoded[32..]),
                    (ExactWheelKeyboardFlags)
                        BinaryPrimitives.ReadUInt32LittleEndian(encoded[36..]));
            }

            _pageStart = pageStart;
            _pageCount = pageCount;
        }
    }
}
