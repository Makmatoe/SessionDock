using System.Windows;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class MacroRecorderSanitizationTests
{
    private static readonly Rect StopButtonBounds = new(700, 700, 100, 60);

    [Theory]
    [InlineData(ExactWheelInputEventType.MouseMove)]
    [InlineData(ExactWheelInputEventType.MouseButtonDown)]
    [InlineData(ExactWheelInputEventType.MouseButtonUp)]
    [InlineData(ExactWheelInputEventType.VerticalWheel)]
    [InlineData(ExactWheelInputEventType.HorizontalWheel)]
    public void SanitizeClientRecording_RemainingOutsideMouseEventIsRejected(
        ExactWheelInputEventType outsideType)
    {
        var (recording, target) = CreateRecordingAndTarget(
        [
            KeyEvent(0, 1, ExactWheelInputEventType.KeyDown),
            MouseEvent(10, 2, outsideType, 600, 600),
            // This non-move event is the deterministic boundary between the
            // unrelated outside interaction and the final Stop approach.
            KeyEvent(20, 3, ExactWheelInputEventType.KeyUp),
            MouseEvent(30, 4, ExactWheelInputEventType.MouseMove, 450, 450),
            MouseEvent(40, 5, ExactWheelInputEventType.MouseMove, 650, 650),
            MouseEvent(50, 6, ExactWheelInputEventType.MouseMove, 720, 720),
            MouseEvent(60, 7, ExactWheelInputEventType.MouseButtonDown, 730, 730),
            MouseEvent(70, 8, ExactWheelInputEventType.MouseButtonUp, 730, 730)
        ]);

        Assert.Throws<ClientMacroOutsideTargetException>(() =>
            MacroRecorderDialog.SanitizeControlInteraction(
                recording,
                target,
                SessionMacroKind.Client,
                StopButtonBounds));
    }

    [Fact]
    public void SanitizeClientRecording_RemovesOnlyValidStopTailAndApproach()
    {
        var retainedEvents = new[]
        {
            KeyEvent(0, 1, ExactWheelInputEventType.KeyDown),
            MouseEvent(
                10,
                2,
                ExactWheelInputEventType.MouseButtonDown,
                250,
                250),
            MouseEvent(
                20,
                3,
                ExactWheelInputEventType.MouseButtonUp,
                250,
                250),
            KeyEvent(30, 4, ExactWheelInputEventType.KeyUp)
        };
        var (recording, target) = CreateRecordingAndTarget(
        [
            .. retainedEvents,
            MouseEvent(40, 5, ExactWheelInputEventType.MouseMove, 450, 450),
            MouseEvent(50, 6, ExactWheelInputEventType.MouseMove, 650, 650),
            MouseEvent(60, 7, ExactWheelInputEventType.MouseMove, 720, 720),
            MouseEvent(70, 8, ExactWheelInputEventType.MouseButtonDown, 730, 730),
            MouseEvent(80, 9, ExactWheelInputEventType.MouseButtonUp, 730, 730)
        ]);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            StopButtonBounds);

        Assert.Equal(retainedEvents, sanitized.Events.ToArray());
        Assert.Equal(30UL, sanitized.DurationMicroseconds);
        Assert.Same(recording.Display, sanitized.Display);
        Assert.Same(recording.Target, sanitized.Target);
    }

    [Fact]
    public void SanitizeWholeLayoutRecording_RemovesStopTailButKeepsOutsideInput()
    {
        var outsideMove = MouseEvent(
            10,
            1,
            ExactWheelInputEventType.MouseMove,
            600,
            600);
        var (recording, target) = CreateRecordingAndTarget(
        [
            outsideMove,
            KeyEvent(20, 2, ExactWheelInputEventType.KeyDown),
            MouseEvent(30, 3, ExactWheelInputEventType.MouseMove, 720, 720),
            MouseEvent(40, 4, ExactWheelInputEventType.MouseButtonDown, 730, 730),
            MouseEvent(50, 5, ExactWheelInputEventType.MouseButtonUp, 730, 730)
        ]);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.WholeLayout,
            StopButtonBounds);

        Assert.Equal(
            [outsideMove, KeyEvent(20, 2, ExactWheelInputEventType.KeyDown)],
            sanitized.Events.ToArray());
    }

    [Theory]
    [InlineData(0x0D, false)]
    [InlineData(0x20, true)]
    public void SanitizeWholeLayoutRecording_RemovesOnlyTerminalKeyboardStop(
        int stopVirtualKey,
        bool includeKeyUp)
    {
        var retained = new[]
        {
            MouseEvent(
                10,
                1,
                ExactWheelInputEventType.MouseButtonDown,
                730,
                730),
            MouseEvent(
                20,
                2,
                ExactWheelInputEventType.MouseButtonUp,
                730,
                730),
            KeyEvent(30, 3, ExactWheelInputEventType.KeyDown)
        };
        var terminal = new List<ExactWheelInputEvent>
        {
            KeyEvent(40, 4, ExactWheelInputEventType.KeyDown, stopVirtualKey)
        };
        if (includeKeyUp)
        {
            terminal.Add(KeyEvent(
                50,
                5,
                ExactWheelInputEventType.KeyUp,
                stopVirtualKey));
        }

        var (recording, target) = CreateRecordingAndTarget(
            [.. retained, .. terminal]);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.WholeLayout,
            StopButtonBounds,
            StopActivationKind.Keyboard);

        Assert.Equal(retained, sanitized.Events.ToArray());
        Assert.Equal(30UL, sanitized.DurationMicroseconds);
    }

    [Fact]
    public void SanitizeMouseStop_DoesNotDeleteOlderStopBoundsClick()
    {
        var events = new[]
        {
            MouseEvent(
                10,
                1,
                ExactWheelInputEventType.MouseButtonDown,
                730,
                730),
            MouseEvent(
                20,
                2,
                ExactWheelInputEventType.MouseButtonUp,
                730,
                730),
            KeyEvent(30, 3, ExactWheelInputEventType.KeyDown)
        };
        var (recording, target) = CreateRecordingAndTarget(events);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.WholeLayout,
            StopButtonBounds,
            StopActivationKind.Mouse);

        Assert.Equal(events, sanitized.Events.ToArray());
    }

    [Theory]
    [InlineData("F8", 0x77)]
    [InlineData("Ctrl+Shift+F8", 0x77)]
    [InlineData("Alt+F11", 0x7A)]
    public void SanitizeGlobalHotkeyStop_RemovesCompleteTerminalChord(
        string hotkeyText,
        int primaryVirtualKey)
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse(
            hotkeyText,
            out var hotkey));
        var retained = new[]
        {
            KeyEvent(10, 1, ExactWheelInputEventType.KeyDown),
            KeyEvent(20, 2, ExactWheelInputEventType.KeyUp)
        };
        var terminal = new List<ExactWheelInputEvent>();
        ulong timestamp = 30;
        ulong sequence = 3;
        if (hotkey.Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Control))
        {
            terminal.Add(KeyEvent(
                timestamp,
                sequence++,
                ExactWheelInputEventType.KeyDown,
                0xA2));
            timestamp += 10;
        }
        if (hotkey.Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Alt))
        {
            terminal.Add(KeyEvent(
                timestamp,
                sequence++,
                ExactWheelInputEventType.KeyDown,
                0xA4));
            timestamp += 10;
        }
        if (hotkey.Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Shift))
        {
            terminal.Add(KeyEvent(
                timestamp,
                sequence++,
                ExactWheelInputEventType.KeyDown,
                0xA0));
            timestamp += 10;
        }
        terminal.Add(KeyEvent(
            timestamp,
            sequence,
            ExactWheelInputEventType.KeyDown,
            primaryVirtualKey));
        var (recording, target) = CreateRecordingAndTarget(
            [.. retained, .. terminal]);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            Rect.Empty,
            StopActivationKind.GlobalHotkey,
            hotkey);

        Assert.Equal(retained, sanitized.Events.ToArray());
        Assert.Equal(20UL, sanitized.DurationMicroseconds);
    }

    [Fact]
    public void SanitizeGlobalHotkeyStop_RemovesPostedKeyUpAndMouseMoveTail()
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse(
            "Ctrl+Shift+F8",
            out var hotkey));
        var retained = new[]
        {
            KeyEvent(10, 1, ExactWheelInputEventType.KeyDown),
            KeyEvent(20, 2, ExactWheelInputEventType.KeyUp)
        };
        var (recording, target) = CreateRecordingAndTarget(
        [
            .. retained,
            KeyEvent(30, 3, ExactWheelInputEventType.KeyDown, 0xA2),
            MouseEvent(35, 4, ExactWheelInputEventType.MouseMove, 200, 200),
            KeyEvent(40, 5, ExactWheelInputEventType.KeyDown, 0xA0),
            KeyEvent(50, 6, ExactWheelInputEventType.KeyDown, 0x77),
            MouseEvent(60, 7, ExactWheelInputEventType.MouseMove, 210, 210),
            KeyEvent(70, 8, ExactWheelInputEventType.KeyUp, 0x77),
            KeyEvent(80, 9, ExactWheelInputEventType.KeyUp, 0xA0),
            KeyEvent(90, 10, ExactWheelInputEventType.KeyUp, 0xA2),
            MouseEvent(100, 11, ExactWheelInputEventType.MouseMove, 220, 220)
        ]);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            Rect.Empty,
            StopActivationKind.GlobalHotkey,
            hotkey);

        Assert.Equal(retained, sanitized.Events.ToArray());
        Assert.Equal(20UL, sanitized.DurationMicroseconds);
    }

    [Fact]
    public void SanitizeGlobalHotkeyStop_DoesNotGuessAcrossWrongFinalKey()
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse("F8", out var hotkey));
        var events = new[]
        {
            KeyEvent(10, 1, ExactWheelInputEventType.KeyDown, 0x77),
            KeyEvent(20, 2, ExactWheelInputEventType.KeyDown, 0x41)
        };
        var (recording, target) = CreateRecordingAndTarget(events);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            Rect.Empty,
            StopActivationKind.GlobalHotkey,
            hotkey);

        Assert.Equal(events, sanitized.Events.ToArray());
    }

    [Fact]
    public void SanitizeGlobalHotkeyStop_DoesNotRemoveIncompleteChord()
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse(
            "Ctrl+Shift+F8",
            out var hotkey));
        var events = new[]
        {
            KeyEvent(10, 1, ExactWheelInputEventType.KeyDown, 0xA2),
            KeyEvent(20, 2, ExactWheelInputEventType.KeyDown, 0x77),
            KeyEvent(30, 3, ExactWheelInputEventType.KeyUp, 0x77),
            KeyEvent(40, 4, ExactWheelInputEventType.KeyUp, 0xA2)
        };
        var (recording, target) = CreateRecordingAndTarget(events);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            Rect.Empty,
            StopActivationKind.GlobalHotkey,
            hotkey);

        Assert.Equal(events, sanitized.Events.ToArray());
    }

    [Fact]
    public void SanitizeGlobalHotkeyStop_DoesNotScanBeyondBoundedSuffix()
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse("F8", out var hotkey));
        var events = new List<ExactWheelInputEvent>
        {
            KeyEvent(10, 1, ExactWheelInputEventType.KeyDown, 0x77)
        };
        for (var index = 0; index < 64; index++)
        {
            events.Add(MouseEvent(
                checked((ulong)(20 + index)),
                checked((ulong)(2 + index)),
                ExactWheelInputEventType.MouseMove,
                200,
                200));
        }
        var eventArray = events.ToArray();
        var (recording, target) = CreateRecordingAndTarget(eventArray);

        var sanitized = MacroRecorderDialog.SanitizeControlInteraction(
            recording,
            target,
            SessionMacroKind.Client,
            Rect.Empty,
            StopActivationKind.GlobalHotkey,
            hotkey);

        Assert.Equal(eventArray, sanitized.Events.ToArray());
    }

    [Theory]
    [InlineData(0x0201, 730, 730, 3)]
    [InlineData(0x0201, 650, 650, 0)]
    [InlineData(0x0204, 730, 730, 0)]
    public void StopActivation_PreventsActivationOnlyForExactLeftPress(
        int mouseMessage,
        int cursorX,
        int cursorY,
        int expected)
    {
        var parameter = (nint)((long)mouseMessage << 16);

        Assert.Equal(
            new nint(expected),
            MacroRecorderDialog.GetStopButtonMouseActivationResult(
                parameter,
                StopButtonBounds,
                new Point(cursorX, cursorY)));
    }

    private static (
        ExactWheelRecording Recording,
        ExactWheelRecordingTarget Target) CreateRecordingAndTarget(
            ExactWheelInputEvent[] events)
    {
        var display = ExactWheelTestData.Display();
        var metadata = ExactWheelTestData.Target(
            new ExactWheelRect(100, 100, 500, 500));
        return (
            ExactWheelTestData.Recording(
                events,
                events[^1].TimestampMicroseconds,
                display,
                metadata),
            new ExactWheelRecordingTarget((nint)123, display, metadata));
    }

    private static ExactWheelInputEvent MouseEvent(
        ulong timestamp,
        ulong sequence,
        ExactWheelInputEventType type,
        int x,
        int y)
    {
        var data1 = type switch
        {
            ExactWheelInputEventType.MouseButtonDown or
                ExactWheelInputEventType.MouseButtonUp =>
                (int)ExactWheelMouseButton.Left,
            ExactWheelInputEventType.VerticalWheel or
                ExactWheelInputEventType.HorizontalWheel => 120,
            _ => 0
        };
        return new ExactWheelInputEvent(
            timestamp,
            sequence,
            type,
            x,
            y,
            data1,
            0);
    }

    private static ExactWheelInputEvent KeyEvent(
        ulong timestamp,
        ulong sequence,
        ExactWheelInputEventType type,
        int virtualKey = 0x41) =>
        new(
            timestamp,
            sequence,
            type,
            0,
            0,
            virtualKey,
            0x1E);
}
