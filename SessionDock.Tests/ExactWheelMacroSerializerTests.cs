using System.Buffers.Binary;
using System.Text;
using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelMacroSerializerTests
{
    private static readonly byte[] Magic =
        [0x45, 0x57, 0x4D, 0x41, 0x43, 0x52, 0x4F, 0x00];

    [Fact]
    public void SafetyLimits_BoundSerializedMacroAndCaptureCapacity()
    {
        Assert.Equal(500_000UL, ExactWheelLimits.MaximumEventCount);
        Assert.Equal(
            64L * 1024L * 1024L,
            ExactWheelLimits.MaximumMacroFileBytes);
        Assert.Equal(
            checked((int)ExactWheelLimits.MaximumEventCount),
            ExactWheelLimits.DefaultCaptureEventCapacity);
        Assert.True(
            ExactWheelMacroSerializer.FixedHeaderBytes +
            ExactWheelLimits.MaximumEventCount *
                ExactWheelMacroSerializer.EventRecordBytes +
            sizeof(uint) < (ulong)ExactWheelLimits.MaximumMacroFileBytes);
    }

    [Fact]
    public void Serialize_EmptyRecording_IsRejectedBeforeFileCreation()
    {
        var recording = ExactWheelTestData.Recording(
            events: [],
            durationMicroseconds: 0);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelMacroSerializer.Serialize(recording));

        Assert.Contains(
            "at least one input event",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_UnreleasedInputTransition_IsRejected()
    {
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E)
            ],
            durationMicroseconds: 10_000);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelMacroSerializer.Serialize(recording));

        Assert.Contains(
            "release every key and mouse button",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_LegacyDuplicatedMouseDown_DropsDuplicateAndClosesHold()
    {
        var bytes = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        var firstEvent = checked((int)ReadUInt32(bytes, 12));
        var mouseUpTypeOffset = checked(
            firstEvent + 2 * (int)ExactWheelMacroSerializer.EventRecordBytes +
            4);
        bytes[mouseUpTypeOffset] =
            (byte)ExactWheelInputEventType.MouseButtonDown;
        RewriteCrc(bytes);

        var loaded = ExactWheelMacroSerializer.Deserialize(bytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Equal(7, loaded.Events.Count);
        Assert.Single(
            loaded.Events,
            inputEvent =>
                inputEvent.Type == ExactWheelInputEventType.MouseButtonDown);
        Assert.Single(
            loaded.Events,
            inputEvent =>
                inputEvent.Type == ExactWheelInputEventType.MouseButtonUp);
        var release = loaded.Events[^1];
        Assert.Equal(ExactWheelInputEventType.MouseButtonUp, release.Type);
        Assert.Equal(loaded.DurationMicroseconds, release.TimestampMicroseconds);
        Assert.Equal(8UL, release.Sequence);
    }

    [Fact]
    public void ValidatePlayable_AllowsKeyboardTypematicDownsBeforeOneRelease()
    {
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E),
                new ExactWheelInputEvent(
                    5_000,
                    2,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E),
                new ExactWheelInputEvent(
                    10_000,
                    3,
                    ExactWheelInputEventType.KeyUp,
                    0,
                    0,
                    0x41,
                    0x1E)
            ],
            durationMicroseconds: 10_000);

        ExactWheelRecordingValidator.ValidatePlayable(recording);
    }

    [Fact]
    public void Deserialize_LegacyUnmatchedKeyDown_PreservesRepeatsAndAppendsRelease()
    {
        var keyDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E,
            ExactWheelKeyboardFlags.Extended |
            ExactWheelKeyboardFlags.AltContext);
        var keyRepeat = keyDown with
        {
            TimestampMicroseconds = 200,
            Sequence = 3
        };
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                keyDown,
                keyRepeat,
                keyDown with
                {
                    TimestampMicroseconds = 300,
                    Sequence = 4,
                    Type = ExactWheelInputEventType.KeyUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = RemoveLastEvent(
            ExactWheelMacroSerializer.Serialize(recording));

        var loaded = ExactWheelMacroSerializer.Deserialize(legacyBytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Equal(4, loaded.Events.Count);
        Assert.Equal(keyDown, loaded.Events[1]);
        Assert.Equal(keyRepeat, loaded.Events[2]);
        Assert.Equal(
            keyDown with
            {
                TimestampMicroseconds = 1_000,
                Sequence = 4,
                Type = ExactWheelInputEventType.KeyUp
            },
            loaded.Events[3]);
    }

    [Fact]
    public void Deserialize_LegacyUnmatchedMouseDown_AppendsExactRelease()
    {
        var mouseDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.MouseButtonDown,
            777,
            444,
            (int)ExactWheelMouseButton.X2,
            0);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                mouseDown,
                mouseDown with
                {
                    TimestampMicroseconds = 200,
                    Sequence = 3,
                    Type = ExactWheelInputEventType.MouseButtonUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = RemoveLastEvent(
            ExactWheelMacroSerializer.Serialize(recording));

        var loaded = ExactWheelMacroSerializer.Deserialize(legacyBytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Equal(3, loaded.Events.Count);
        Assert.Equal(mouseDown, loaded.Events[1]);
        Assert.Equal(
            mouseDown with
            {
                TimestampMicroseconds = 1_000,
                Sequence = 3,
                Type = ExactWheelInputEventType.MouseButtonUp
            },
            loaded.Events[2]);
    }

    [Fact]
    public void Deserialize_LegacyMixedHolds_AppendsReleasesInReversePressOrder()
    {
        var controlDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x11,
            0x1D);
        var mouseDown = new ExactWheelInputEvent(
            200,
            3,
            ExactWheelInputEventType.MouseButtonDown,
            777,
            444,
            (int)ExactWheelMouseButton.Left,
            0);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                controlDown,
                mouseDown,
                mouseDown with
                {
                    TimestampMicroseconds = 300,
                    Sequence = 4,
                    Type = ExactWheelInputEventType.MouseButtonUp
                },
                controlDown with
                {
                    TimestampMicroseconds = 400,
                    Sequence = 5,
                    Type = ExactWheelInputEventType.KeyUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = RemoveLastEvent(RemoveLastEvent(
            ExactWheelMacroSerializer.Serialize(recording)));

        var loaded = ExactWheelMacroSerializer.Deserialize(legacyBytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Equal(
            mouseDown with
            {
                TimestampMicroseconds = 1_000,
                Sequence = 4,
                Type = ExactWheelInputEventType.MouseButtonUp
            },
            loaded.Events[^2]);
        Assert.Equal(
            controlDown with
            {
                TimestampMicroseconds = 1_000,
                Sequence = 5,
                Type = ExactWheelInputEventType.KeyUp
            },
            loaded.Events[^1]);
    }

    [Fact]
    public void Deserialize_LegacyUnmatchedUp_IsDropped()
    {
        var mouseDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.MouseButtonDown,
            777,
            444,
            (int)ExactWheelMouseButton.Left,
            0);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                mouseDown,
                mouseDown with
                {
                    TimestampMicroseconds = 200,
                    Sequence = 3,
                    Type = ExactWheelInputEventType.MouseButtonUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = ExactWheelMacroSerializer.Serialize(recording);
        WriteEvent(
            legacyBytes,
            1,
            new ExactWheelInputEvent(
                100,
                2,
                ExactWheelInputEventType.MouseMove,
                777,
                444,
                0,
                0));

        var loaded = ExactWheelMacroSerializer.Deserialize(legacyBytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Equal(2, loaded.Events.Count);
        Assert.All(
            loaded.Events,
            inputEvent => Assert.Equal(
                ExactWheelInputEventType.MouseMove,
                inputEvent.Type));
    }

    [Fact]
    public void Deserialize_BalancedTypematicV1_RoundTripIsUnchanged()
    {
        var keyDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                keyDown,
                keyDown with
                {
                    TimestampMicroseconds = 200,
                    Sequence = 3
                },
                keyDown with
                {
                    TimestampMicroseconds = 300,
                    Sequence = 4,
                    Type = ExactWheelInputEventType.KeyUp
                }
            ],
            durationMicroseconds: 1_000);

        var loaded = ExactWheelMacroSerializer.Deserialize(
            ExactWheelMacroSerializer.Serialize(recording));

        Assert.Equal(recording.Events, loaded.Events);
    }

    [Fact]
    public void Deserialize_LegacyDownAtSequenceLimit_RemovesOnlyThatTransaction()
    {
        var keyDown = new ExactWheelInputEvent(
            100,
            2,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1),
                keyDown,
                keyDown with
                {
                    TimestampMicroseconds = 200,
                    Sequence = 3,
                    Type = ExactWheelInputEventType.KeyUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = RemoveLastEvent(
            ExactWheelMacroSerializer.Serialize(recording));
        WriteEvent(
            legacyBytes,
            0,
            MouseMove(0, ulong.MaxValue - 1));
        WriteEvent(
            legacyBytes,
            1,
            keyDown with { Sequence = ulong.MaxValue });

        var loaded = ExactWheelMacroSerializer.Deserialize(legacyBytes);

        ExactWheelRecordingValidator.ValidatePlayable(loaded);
        Assert.Single(loaded.Events);
        Assert.Equal(ExactWheelInputEventType.MouseMove, loaded.Events[0].Type);
        Assert.Equal(ulong.MaxValue - 1, loaded.Events[0].Sequence);
    }

    [Fact]
    public void Deserialize_LegacyOnlyDownAtSequenceLimit_NeverReturnsEmptyMacro()
    {
        var keyDown = new ExactWheelInputEvent(
            100,
            1,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                keyDown,
                keyDown with
                {
                    TimestampMicroseconds = 200,
                    Sequence = 2,
                    Type = ExactWheelInputEventType.KeyUp
                }
            ],
            durationMicroseconds: 1_000);
        var legacyBytes = RemoveLastEvent(
            ExactWheelMacroSerializer.Serialize(recording));
        WriteEvent(
            legacyBytes,
            0,
            keyDown with { Sequence = ulong.MaxValue });

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelMacroSerializer.Deserialize(legacyBytes));

        Assert.Contains(
            "at least one input event",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_V1LayoutAndCrc_AreExact()
    {
        var recording = ExactWheelTestData.Recording();

        var bytes = ExactWheelMacroSerializer.Serialize(recording);

        var expectedHeaderBytes = checked(
            ExactWheelMacroSerializer.FixedHeaderBytes +
            (uint)recording.Display.Monitors.Count *
                ExactWheelMacroSerializer.MonitorRecordBytes +
            (uint)(recording.Target.ProcessBasename.Length +
                recording.Target.WindowClass.Length) * 2U);
        var expectedLength = checked(
            expectedHeaderBytes +
            (uint)recording.Events.Count *
                ExactWheelMacroSerializer.EventRecordBytes +
            sizeof(uint));

        Assert.Equal(expectedLength, (uint)bytes.Length);
        Assert.Equal(Magic, bytes[..Magic.Length]);
        Assert.Equal((ushort)1, ReadUInt16(bytes, 8));
        Assert.Equal((ushort)0, ReadUInt16(bytes, 10));
        Assert.Equal(expectedHeaderBytes, ReadUInt32(bytes, 12));
        Assert.Equal(48U, ReadUInt32(bytes, 16));
        Assert.Equal(recording.DurationMicroseconds, ReadUInt64(bytes, 20));
        Assert.Equal((ulong)recording.Events.Count, ReadUInt64(bytes, 28));
        Assert.Equal(recording.Display.VirtualLeft, ReadInt32(bytes, 36));
        Assert.Equal(recording.Display.VirtualTop, ReadInt32(bytes, 40));
        Assert.Equal(recording.Display.VirtualWidth, ReadInt32(bytes, 44));
        Assert.Equal(recording.Display.VirtualHeight, ReadInt32(bytes, 48));
        Assert.Equal((uint)recording.Display.Monitors.Count, ReadUInt32(bytes, 52));
        Assert.Equal((uint)recording.Target.ProcessBasename.Length, ReadUInt32(bytes, 56));
        Assert.Equal((uint)recording.Target.WindowClass.Length, ReadUInt32(bytes, 60));

        var firstEvent = checked((int)expectedHeaderBytes);
        Assert.Equal(48U, ReadUInt32(bytes, firstEvent));
        Assert.Equal((byte)ExactWheelInputEventType.MouseMove, bytes[firstEvent + 4]);
        Assert.Equal(0U, ReadUInt32(bytes, firstEvent + 8));
        Assert.Equal(0U, ReadUInt32(bytes, firstEvent + 12));
        Assert.Equal(0UL, ReadUInt64(bytes, firstEvent + 16));
        Assert.Equal(1UL, ReadUInt64(bytes, firstEvent + 24));
        Assert.Equal(100, ReadInt32(bytes, firstEvent + 32));
        Assert.Equal(80, ReadInt32(bytes, firstEvent + 36));

        var storedCrc = ReadUInt32(bytes, bytes.Length - sizeof(uint));
        Assert.Equal(
            ExactWheelMacroSerializer.ComputeCrc32(bytes[..^sizeof(uint)]),
            storedCrc);
    }

    [Fact]
    public void SerializeDeserialize_AllV1Fields_RoundTrip()
    {
        var original = ExactWheelTestData.Recording(
            target: ExactWheelTestData.Target(
                processBasename: "Roblox🎮.exe",
                windowClass: "WINDOWSCLIENT-Ω"));

        var loaded = ExactWheelMacroSerializer.Deserialize(
            ExactWheelMacroSerializer.Serialize(original));

        Assert.Equal(original.DurationMicroseconds, loaded.DurationMicroseconds);
        Assert.Equal(original.Display.VirtualLeft, loaded.Display.VirtualLeft);
        Assert.Equal(original.Display.VirtualTop, loaded.Display.VirtualTop);
        Assert.Equal(original.Display.VirtualWidth, loaded.Display.VirtualWidth);
        Assert.Equal(original.Display.VirtualHeight, loaded.Display.VirtualHeight);
        Assert.Equal(original.Display.Monitors, loaded.Display.Monitors);
        Assert.Equal(original.Target.ProcessBasename, loaded.Target.ProcessBasename);
        Assert.Equal(original.Target.WindowClass, loaded.Target.WindowClass);
        Assert.Equal(original.Target.WindowRect, loaded.Target.WindowRect);
        Assert.Equal(original.Target.ClientRect, loaded.Target.ClientRect);
        Assert.Equal(original.Events, loaded.Events);
    }

    [Fact]
    public void ComputeCrc32_StandardCheckVector_Matches()
    {
        Assert.Equal(
            0xCBF43926U,
            ExactWheelMacroSerializer.ComputeCrc32(
                Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Deserialize_ChecksumCorruption_IsRejectedBeforeParsing()
    {
        var bytes = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        bytes[36] ^= 0x01;

        var exception = Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Deserialize(bytes));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_EventCountAboveSafetyLimit_IsRejectedBeforeAllocation()
    {
        var bytes = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(28),
            ExactWheelLimits.MaximumEventCount + 1);
        RewriteCrc(bytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ExactWheelMacroSerializer.Deserialize(bytes));

        Assert.Contains(
            "count",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(10, 1)]
    [InlineData(16, 47)]
    [InlineData(12, 97)]
    public void Deserialize_UnsupportedOrReservedHeaderValue_IsRejected(
        int offset,
        ushort value)
    {
        var bytes = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        RewriteCrc(bytes);

        Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_EventReservedByte_IsRejected()
    {
        var bytes = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        var eventOffset = checked((int)ReadUInt32(bytes, 12));
        bytes[eventOffset + 5] = 1;
        RewriteCrc(bytes);

        Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_TrailingDataWithValidCrc_IsRejected()
    {
        var original = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        var bytes = new byte[original.Length + 1];
        original.AsSpan(0, original.Length - sizeof(uint)).CopyTo(bytes);
        bytes[bytes.Length - sizeof(uint) - 1] = 0xA5;
        RewriteCrc(bytes);

        var exception = Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Deserialize(bytes));

        Assert.Contains("trailing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_TruncatedData_IsRejected()
    {
        var original = ExactWheelMacroSerializer.Serialize(
            ExactWheelTestData.Recording());
        var bytes = original[..^5];
        RewriteCrc(bytes);

        var exception = Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Deserialize(bytes));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_InvalidTimelineAndUtf16_AreRejected()
    {
        var descending = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    2,
                    1,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0),
                new ExactWheelInputEvent(
                    1,
                    2,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ]);
        var invalidUtf16 = ExactWheelTestData.Recording(
            target: ExactWheelTestData.Target(
                processBasename: "Roblox\uD800.exe"));

        Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Serialize(descending));
        Assert.Throws<InvalidDataException>(
            () => ExactWheelMacroSerializer.Serialize(invalidUtf16));
    }

    [Fact]
    public void SaveAtomic_LoadsExactDataAndLeavesNoTemporaryFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.ExactWheel.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "macro.ewmacro");
            var recording = ExactWheelTestData.Recording();

            ExactWheelMacroSerializer.SaveAtomic(path, recording);
            var loaded = ExactWheelMacroSerializer.Load(path);

            Assert.Equal(recording.Events, loaded.Events);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp.*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAtomic_InvalidReplacement_PreservesExistingMacro()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.ExactWheel.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "macro.ewmacro");
            var valid = ExactWheelTestData.Recording();
            ExactWheelMacroSerializer.SaveAtomic(path, valid);
            var originalBytes = File.ReadAllBytes(path);
            var invalid = ExactWheelTestData.Recording(
                events:
                [
                    new ExactWheelInputEvent(
                        1,
                        1,
                        ExactWheelInputEventType.VerticalWheel,
                        100,
                        80,
                        0,
                        0)
                ]);

            Assert.Throws<InvalidDataException>(
                () => ExactWheelMacroSerializer.SaveAtomic(path, invalid));

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp.*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExactWheelInputEvent MouseMove(
        ulong timestampMicroseconds,
        ulong sequence) =>
        new(
            timestampMicroseconds,
            sequence,
            ExactWheelInputEventType.MouseMove,
            100,
            80,
            0,
            0);

    private static byte[] RemoveLastEvent(byte[] bytes)
    {
        var eventCount = ReadUInt64(bytes, 28);
        if (eventCount == 0)
            throw new ArgumentException("The macro has no event to remove.");
        var retainedBytes = checked(
            bytes.Length - sizeof(uint) -
            (int)ExactWheelMacroSerializer.EventRecordBytes);
        var result = new byte[checked(
            bytes.Length -
            (int)ExactWheelMacroSerializer.EventRecordBytes)];
        bytes.AsSpan(0, retainedBytes).CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(28),
            eventCount - 1);
        RewriteCrc(result);
        return result;
    }

    private static void WriteEvent(
        byte[] bytes,
        int eventIndex,
        ExactWheelInputEvent inputEvent)
    {
        var eventOffset = checked(
            (int)ReadUInt32(bytes, 12) +
            eventIndex * (int)ExactWheelMacroSerializer.EventRecordBytes);
        bytes[eventOffset + 4] = (byte)inputEvent.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(eventOffset + 8),
            (uint)inputEvent.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(eventOffset + 16),
            inputEvent.TimestampMicroseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(eventOffset + 24),
            inputEvent.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(eventOffset + 32),
            inputEvent.X);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(eventOffset + 36),
            inputEvent.Y);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(eventOffset + 40),
            inputEvent.Data1);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(eventOffset + 44),
            inputEvent.Data2);
        RewriteCrc(bytes);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static ulong ReadUInt64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static void RewriteCrc(byte[] bytes)
    {
        var checksum = ExactWheelMacroSerializer.ComputeCrc32(
            bytes.AsSpan(0, bytes.Length - sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - sizeof(uint)),
            checksum);
    }
}
