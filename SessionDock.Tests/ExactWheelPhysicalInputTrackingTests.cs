using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelPhysicalInputTrackingTests
{
    [Fact]
    public void TrackedState_UpdatesKeysAndButtonsWithoutNativeRescans()
    {
        var state = new TrackedPhysicalInputState();

        Assert.True(state.AreReleased([]));

        state.Set(0x41, isDown: true);
        state.Set(0x01, isDown: true);

        Assert.False(state.AreReleased([]));
        Assert.False(state.AreReleased([0x41]));
        Assert.False(state.AreKeysReleased([0x41]));

        state.Set(0x01, isDown: false);

        Assert.True(state.AreReleased([0x41]));
        Assert.False(state.AreKeysReleased([0x41]));

        state.Set(0x41, isDown: false);

        Assert.True(state.AreReleased([]));
        Assert.True(state.AreKeysReleased([0x41]));

        state.Set(0xA2, isDown: true);
        Assert.False(state.AreKeysReleased([0x11]));
        state.Set(0xA2, isDown: false);
        Assert.True(state.AreKeysReleased([0x11]));
    }

    [Fact]
    public void TrackedState_ResetClearsEveryVirtualKeyWord()
    {
        var state = new TrackedPhysicalInputState();
        for (var virtualKey = 1; virtualKey < 256; virtualKey++)
            state.Set(virtualKey, isDown: true);

        state.Reset();

        Assert.True(state.AreReleased([]));
        Assert.True(state.AreKeysReleased(Enumerable.Range(1, 255).ToArray()));
    }

    [Theory]
    [InlineData(0x11, 0xA2)]
    [InlineData(0xA2, 0x11)]
    [InlineData(0x10, 0xA1)]
    [InlineData(0xA0, 0x10)]
    [InlineData(0x12, 0xA5)]
    [InlineData(0xA4, 0x12)]
    public void TrackedState_IgnoreModifierAppliesToWholeVirtualKeyFamily(
        int ignoredVirtualKey,
        int heldVirtualKey)
    {
        var state = new TrackedPhysicalInputState();
        state.Set(heldVirtualKey, isDown: true);

        Assert.True(state.AreReleased([ignoredVirtualKey]));
    }
}
