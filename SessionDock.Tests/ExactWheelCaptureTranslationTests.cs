using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelCaptureTranslationTests
{
    [Fact]
    public void IsInjected_MouseAndKeyboardFlagsOrPrivateMarker_AreIgnored()
    {
        var mouseByFlag = new ExactWheelNativeMethods.MouseLowLevelHookData
        {
            Flags = ExactWheelNativeMethods.LlMouseInjected
        };
        var mouseByMarker = new ExactWheelNativeMethods.MouseLowLevelHookData
        {
            ExtraInfo = unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
        };
        var keyboardByFlag = new ExactWheelNativeMethods.KeyboardLowLevelHookData
        {
            Flags = ExactWheelNativeMethods.LlKeyboardInjected
        };
        var keyboardByMarker = new ExactWheelNativeMethods.KeyboardLowLevelHookData
        {
            ExtraInfo = unchecked((nuint)ExactWheelLimits.PrivateInputMarker)
        };

        Assert.True(LowLevelInputCapture.IsInjected(mouseByFlag));
        Assert.True(LowLevelInputCapture.IsInjected(mouseByMarker));
        Assert.True(LowLevelInputCapture.IsInjected(keyboardByFlag));
        Assert.True(LowLevelInputCapture.IsInjected(keyboardByMarker));
        Assert.False(LowLevelInputCapture.IsInjected(default(
            ExactWheelNativeMethods.MouseLowLevelHookData)));
        Assert.False(LowLevelInputCapture.IsInjected(default(
            ExactWheelNativeMethods.KeyboardLowLevelHookData)));
    }

    [Theory]
    [InlineData(ExactWheelNativeMethods.WmMouseWheel, -240, ExactWheelInputEventType.VerticalWheel)]
    [InlineData(ExactWheelNativeMethods.WmMouseHorizontalWheel, 30, ExactWheelInputEventType.HorizontalWheel)]
    public void TryTranslateMouse_WheelDeltaRemainsSigned(
        uint message,
        short delta,
        ExactWheelInputEventType expectedType)
    {
        var data = new ExactWheelNativeMethods.MouseLowLevelHookData
        {
            Point = new ExactWheelNativeMethods.NativePoint { X = -20, Y = 50 },
            MouseData = (uint)(unchecked((ushort)delta) << 16)
        };

        var translated = LowLevelInputCapture.TryTranslateMouse(
            message,
            data,
            123,
            7,
            out var inputEvent);

        Assert.True(translated);
        Assert.Equal(expectedType, inputEvent.Type);
        Assert.Equal(delta, inputEvent.Data1);
        Assert.Equal((-20, 50), (inputEvent.X, inputEvent.Y));
        Assert.Equal(123UL, inputEvent.TimestampMicroseconds);
        Assert.Equal(7UL, inputEvent.Sequence);
    }

    [Theory]
    [InlineData(ExactWheelNativeMethods.WmLeftButtonDown, ExactWheelInputEventType.MouseButtonDown, ExactWheelMouseButton.Left)]
    [InlineData(ExactWheelNativeMethods.WmRightButtonUp, ExactWheelInputEventType.MouseButtonUp, ExactWheelMouseButton.Right)]
    [InlineData(ExactWheelNativeMethods.WmMiddleButtonDown, ExactWheelInputEventType.MouseButtonDown, ExactWheelMouseButton.Middle)]
    public void TryTranslateMouse_ButtonMessages_AreExact(
        uint message,
        ExactWheelInputEventType type,
        ExactWheelMouseButton button)
    {
        Assert.True(LowLevelInputCapture.TryTranslateMouse(
            message,
            new ExactWheelNativeMethods.MouseLowLevelHookData(),
            1,
            2,
            out var inputEvent));
        Assert.Equal(type, inputEvent.Type);
        Assert.Equal((int)button, inputEvent.Data1);
    }

    [Fact]
    public void TryTranslateMouse_UnknownMessageAndXButton_AreRejected()
    {
        Assert.False(LowLevelInputCapture.TryTranslateMouse(
            0xFFFF,
            default,
            1,
            1,
            out _));
        Assert.False(LowLevelInputCapture.TryTranslateMouse(
            ExactWheelNativeMethods.WmXButtonDown,
            new ExactWheelNativeMethods.MouseLowLevelHookData
            {
                MouseData = 3U << 16
            },
            1,
            1,
            out _));
    }

    [Fact]
    public void TryTranslateKeyboard_PreservesScanVirtualKeyAndContextFlags()
    {
        var data = new ExactWheelNativeMethods.KeyboardLowLevelHookData
        {
            VirtualKey = 0x12,
            ScanCode = 0x38,
            Flags = ExactWheelNativeMethods.LlKeyboardExtended |
                ExactWheelNativeMethods.LlKeyboardAltDown
        };

        var translated = LowLevelInputCapture.TryTranslateKeyboard(
            ExactWheelNativeMethods.WmSysKeyUp,
            data,
            500,
            9,
            out var inputEvent);

        Assert.True(translated);
        Assert.Equal(ExactWheelInputEventType.KeyUp, inputEvent.Type);
        Assert.Equal(0x12, inputEvent.Data1);
        Assert.Equal(0x38, inputEvent.Data2);
        Assert.Equal(
            ExactWheelKeyboardFlags.Extended |
            ExactWheelKeyboardFlags.System |
            ExactWheelKeyboardFlags.AltContext,
            inputEvent.Flags);
        Assert.Equal(500UL, inputEvent.TimestampMicroseconds);
        Assert.Equal(9UL, inputEvent.Sequence);
    }

    [Fact]
    public void TryTranslateKeyboard_UnknownMessage_IsRejected()
    {
        Assert.False(LowLevelInputCapture.TryTranslateKeyboard(
            0xFFFF,
            default,
            0,
            0,
            out _));
    }
}
