using System.Buffers.Binary;
using System.Text;

namespace SessionDock.ExactWheel;

public static class ExactWheelMacroSerializer
{
    public const ushort FormatVersion = 1;
    public const uint FixedHeaderBytes = 96;
    public const uint EventRecordBytes = 48;
    public const uint MonitorRecordBytes = 24;

    private static ReadOnlySpan<byte> Magic =>
        [0x45, 0x57, 0x4D, 0x41, 0x43, 0x52, 0x4F, 0x00];

    private static readonly UnicodeEncoding StrictUtf16 =
        new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Serialize(ExactWheelRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ExactWheelRecordingValidator.ValidatePlayable(recording);

        byte[] processBytes;
        byte[] classBytes;
        try
        {
            processBytes = StrictUtf16.GetBytes(
                recording.Target.ProcessBasename);
            classBytes = StrictUtf16.GetBytes(recording.Target.WindowClass);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Target metadata is not valid UTF-16.",
                exception);
        }

        var monitorBytes = checked(
            (ulong)recording.Display.Monitors.Count * MonitorRecordBytes);
        var stringBytes = checked(
            (ulong)processBytes.Length + (ulong)classBytes.Length);
        var headerBytes = checked(
            (ulong)FixedHeaderBytes + monitorBytes + stringBytes);
        var eventBytes = checked(
            (ulong)recording.Events.Count * EventRecordBytes);
        var fileBytes = checked(headerBytes + eventBytes + sizeof(uint));
        if (headerBytes > uint.MaxValue ||
            fileBytes > (ulong)ExactWheelLimits.MaximumMacroFileBytes ||
            fileBytes > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Macro exceeds the {ExactWheelLimits.MaximumMacroFileMebibytes} MiB file limit.");
        }

        var output = new byte[(int)fileBytes];
        var writer = new SpanWriter(output);
        writer.Bytes(Magic);
        writer.UInt16(FormatVersion);
        writer.UInt16(0);
        writer.UInt32((uint)headerBytes);
        writer.UInt32(EventRecordBytes);
        writer.UInt64(recording.DurationMicroseconds);
        writer.UInt64((ulong)recording.Events.Count);
        writer.Int32(recording.Display.VirtualLeft);
        writer.Int32(recording.Display.VirtualTop);
        writer.Int32(recording.Display.VirtualWidth);
        writer.Int32(recording.Display.VirtualHeight);
        writer.UInt32((uint)recording.Display.Monitors.Count);
        writer.UInt32((uint)recording.Target.ProcessBasename.Length);
        writer.UInt32((uint)recording.Target.WindowClass.Length);
        WriteRect(ref writer, recording.Target.WindowRect);
        WriteRect(ref writer, recording.Target.ClientRect);

        foreach (var monitor in recording.Display.Monitors)
        {
            WriteRect(ref writer, monitor.Bounds);
            writer.UInt32(monitor.DpiX);
            writer.UInt32(monitor.DpiY);
        }

        writer.Bytes(processBytes);
        writer.Bytes(classBytes);
        if (writer.Position != (int)headerBytes)
        {
            throw new InvalidDataException(
                "The macro header length was calculated incorrectly.");
        }

        foreach (var inputEvent in recording.Events)
        {
            writer.UInt32(EventRecordBytes);
            writer.Byte((byte)inputEvent.Type);
            writer.Byte(0);
            writer.Byte(0);
            writer.Byte(0);
            writer.UInt32((uint)inputEvent.Flags);
            writer.UInt32(0);
            writer.UInt64(inputEvent.TimestampMicroseconds);
            writer.UInt64(inputEvent.Sequence);
            writer.Int32(inputEvent.X);
            writer.Int32(inputEvent.Y);
            writer.Int32(inputEvent.Data1);
            writer.Int32(inputEvent.Data2);
        }

        var checksum = ComputeCrc32(output.AsSpan(0, writer.Position));
        writer.UInt32(checksum);
        if (writer.Position != output.Length)
        {
            throw new InvalidDataException(
                "The macro length was calculated incorrectly.");
        }

        return output;
    }

    public static ExactWheelRecording Deserialize(ReadOnlySpan<byte> input)
    {
        if (input.Length > ExactWheelLimits.MaximumMacroFileBytes)
        {
            throw new InvalidDataException(
                $"Macro exceeds the {ExactWheelLimits.MaximumMacroFileMebibytes} MiB file limit.");
        }

        if (input.Length < FixedHeaderBytes + sizeof(uint))
            throw new InvalidDataException("Macro is truncated.");

        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
            input[^sizeof(uint)..]);
        var calculatedChecksum = ComputeCrc32(input[..^sizeof(uint)]);
        if (storedChecksum != calculatedChecksum)
            throw new InvalidDataException("Macro checksum does not match.");

        var reader = new SpanReader(input[..^sizeof(uint)]);
        if (!reader.Bytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Macro magic is invalid.");

        var version = reader.UInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException(
                $"Macro format version {version} is not supported.");
        }

        if (reader.UInt16() != 0)
            throw new InvalidDataException("Macro reserved field is not zero.");

        var headerBytes = reader.UInt32();
        var eventRecordBytes = reader.UInt32();
        if (eventRecordBytes != EventRecordBytes)
        {
            throw new InvalidDataException(
                "Macro event-record length is unsupported.");
        }

        var durationMicroseconds = reader.UInt64();
        var eventCount = reader.UInt64();
        var virtualLeft = reader.Int32();
        var virtualTop = reader.Int32();
        var virtualWidth = reader.Int32();
        var virtualHeight = reader.Int32();
        var monitorCount = reader.UInt32();
        var processUnits = reader.UInt32();
        var classUnits = reader.UInt32();
        var windowRect = ReadRect(ref reader);
        var clientRect = ReadRect(ref reader);

        if (eventCount > ExactWheelLimits.MaximumEventCount ||
            monitorCount is 0 or > ExactWheelLimits.MaximumMonitorCount ||
            processUnits >
                ExactWheelLimits.MaximumProcessBasenameUtf16Units ||
            classUnits > ExactWheelLimits.MaximumWindowClassUtf16Units)
        {
            throw new InvalidDataException(
                "Macro contains a count above a safety limit.");
        }

        ulong expectedHeaderBytes;
        ulong expectedFileBytes;
        try
        {
            expectedHeaderBytes = checked(
                (ulong)FixedHeaderBytes +
                (ulong)monitorCount * MonitorRecordBytes +
                ((ulong)processUnits + classUnits) * 2UL);
            expectedFileBytes = checked(
                expectedHeaderBytes +
                eventCount * EventRecordBytes +
                sizeof(uint));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Macro length arithmetic overflowed.",
                exception);
        }

        if (headerBytes != expectedHeaderBytes)
        {
            throw new InvalidDataException(
                "Macro header length is invalid.");
        }

        if (expectedFileBytes != (ulong)input.Length)
        {
            throw new InvalidDataException(
                expectedFileBytes > (ulong)input.Length
                    ? "Macro is truncated."
                    : "Macro contains trailing data.");
        }

        var monitors = new ExactWheelMonitorSnapshot[monitorCount];
        for (var index = 0; index < monitors.Length; index++)
        {
            monitors[index] = new ExactWheelMonitorSnapshot(
                ReadRect(ref reader),
                reader.UInt32(),
                reader.UInt32());
        }

        var processName = ReadUtf16(
            ref reader,
            checked((int)processUnits));
        var windowClass = ReadUtf16(
            ref reader,
            checked((int)classUnits));
        if (reader.Position != headerBytes)
        {
            throw new InvalidDataException(
                "Macro header length is invalid.");
        }

        var events = new ExactWheelInputEvent[checked((int)eventCount)];
        for (var index = 0; index < events.Length; index++)
        {
            if (reader.UInt32() != EventRecordBytes)
            {
                throw new InvalidDataException(
                    $"Event {index}: record length is invalid.");
            }

            var type = (ExactWheelInputEventType)reader.Byte();
            if (reader.Byte() != 0 ||
                reader.Byte() != 0 ||
                reader.Byte() != 0)
            {
                throw new InvalidDataException(
                    $"Event {index}: reserved field is not zero.");
            }

            var flags = (ExactWheelKeyboardFlags)reader.UInt32();
            if (reader.UInt32() != 0)
            {
                throw new InvalidDataException(
                    $"Event {index}: reserved field is not zero.");
            }

            events[index] = new ExactWheelInputEvent(
                reader.UInt64(),
                reader.UInt64(),
                type,
                reader.Int32(),
                reader.Int32(),
                reader.Int32(),
                reader.Int32(),
                flags);
        }

        if (reader.Remaining != 0)
            throw new InvalidDataException("Macro contains trailing data.");

        var recording = ExactWheelRecording.CreateFromOwnedEvents(
            durationMicroseconds,
            new ExactWheelDisplayTopology(
                virtualLeft,
                virtualTop,
                virtualWidth,
                virtualHeight,
                monitors),
            new ExactWheelTargetMetadata(
                processName,
                windowClass,
                windowRect,
                clientRect),
            events);
        ExactWheelRecordingValidator.Validate(recording);
        return recording;
    }

    public static ExactWheelRecording Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > ExactWheelLimits.MaximumMacroFileBytes)
        {
            throw new InvalidDataException(
                $"Macro exceeds the {ExactWheelLimits.MaximumMacroFileMebibytes} MiB file limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Deserialize(bytes);
    }

    public static void SaveAtomic(
        string path,
        ExactWheelRecording recording)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(recording);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The macro destination directory does not exist.");
        }

        if (File.Exists(fullPath) &&
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The macro destination cannot be a reparse point.");
        }

        var bytes = Serialize(recording);
        var temporaryPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the primary exception; a uniquely named inert
                // temporary macro can be cleaned up on the next maintenance run.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the primary exception.
            }
        }
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            var index = (byte)(crc ^ value);
            crc = (crc >> 8) ^ CrcTable[index];
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var entry = value;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0
                    ? 0xEDB88320U ^ (entry >> 1)
                    : entry >> 1;
            }

            table[value] = entry;
        }

        return table;
    }

    private static void WriteRect(
        ref SpanWriter writer,
        ExactWheelRect rectangle)
    {
        writer.Int32(rectangle.Left);
        writer.Int32(rectangle.Top);
        writer.Int32(rectangle.Right);
        writer.Int32(rectangle.Bottom);
    }

    private static ExactWheelRect ReadRect(ref SpanReader reader) =>
        new(
            reader.Int32(),
            reader.Int32(),
            reader.Int32(),
            reader.Int32());

    private static string ReadUtf16(
        ref SpanReader reader,
        int utf16Units)
    {
        var bytes = reader.Bytes(checked(utf16Units * 2));
        try
        {
            return StrictUtf16.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Macro target metadata contains invalid UTF-16.",
                exception);
        }
    }

    private ref struct SpanWriter
    {
        private readonly Span<byte> _destination;

        internal SpanWriter(Span<byte> destination)
        {
            _destination = destination;
        }

        internal int Position { get; private set; }

        internal void Byte(byte value) =>
            _destination[Position++] = value;

        internal void Bytes(ReadOnlySpan<byte> values)
        {
            values.CopyTo(_destination[Position..]);
            Position += values.Length;
        }

        internal void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                _destination[Position..],
                value);
            Position += sizeof(ushort);
        }

        internal void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                _destination[Position..],
                value);
            Position += sizeof(uint);
        }

        internal void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                _destination[Position..],
                value);
            Position += sizeof(ulong);
        }

        internal void Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                _destination[Position..],
                value);
            Position += sizeof(int);
        }
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _source;

        internal SpanReader(ReadOnlySpan<byte> source)
        {
            _source = source;
        }

        internal int Position { get; private set; }

        internal int Remaining => _source.Length - Position;

        internal byte Byte()
        {
            Ensure(sizeof(byte));
            return _source[Position++];
        }

        internal ReadOnlySpan<byte> Bytes(int count)
        {
            Ensure(count);
            var value = _source.Slice(Position, count);
            Position += count;
            return value;
        }

        internal ushort UInt16()
        {
            Ensure(sizeof(ushort));
            var value = BinaryPrimitives.ReadUInt16LittleEndian(
                _source[Position..]);
            Position += sizeof(ushort);
            return value;
        }

        internal uint UInt32()
        {
            Ensure(sizeof(uint));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(
                _source[Position..]);
            Position += sizeof(uint);
            return value;
        }

        internal ulong UInt64()
        {
            Ensure(sizeof(ulong));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(
                _source[Position..]);
            Position += sizeof(ulong);
            return value;
        }

        internal int Int32()
        {
            Ensure(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(
                _source[Position..]);
            Position += sizeof(int);
            return value;
        }

        private void Ensure(int count)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException("Macro is truncated.");
        }
    }
}
