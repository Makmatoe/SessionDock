using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class MacroRecordingHotkeyTests
{
    [Theory]
    [InlineData("F8", 0x77, 0, "F8")]
    [InlineData("ctrl+f11", 0x7A, 2,
        "Ctrl+F11")]
    [InlineData(" shift + ALT + control + f6 ", 0x75, 7,
        "Ctrl+Alt+Shift+F6")]
    public void TryParse_AcceptsAndCanonicalizesSafeFunctionKeyChord(
        string value,
        int expectedVirtualKey,
        int expectedModifiers,
        string expectedPersistedValue)
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse(value, out var hotkey));

        Assert.Equal(expectedVirtualKey, hotkey.VirtualKey);
        Assert.Equal((MacroRecordingHotkeyModifiers)expectedModifiers,
            hotkey.Modifiers);
        Assert.Equal(expectedPersistedValue, hotkey.PersistedValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F5")]
    [InlineData("F12")]
    [InlineData("Ctrl+F12")]
    [InlineData("F13")]
    [InlineData("Win+F8")]
    [InlineData("Ctrl+Ctrl+F8")]
    [InlineData("Ctrl+S")]
    public void TryParse_RejectsUnsafeOrAmbiguousChord(string? value)
    {
        Assert.False(MacroRecordingHotkeyPolicy.TryParse(value, out _));
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            MacroRecordingHotkeyPolicy.Normalize(value));
    }

    [Fact]
    public void ModifierVirtualKeys_RecognizeGenericAndSidedKeys()
    {
        Assert.True(MacroRecordingHotkeyPolicy.TryParse(
            "Ctrl+Alt+Shift+F8",
            out var hotkey));

        Assert.True(
            new HashSet<int>
            {
                0x10, 0xA0, 0xA1,
                0x11, 0xA2, 0xA3,
                0x12, 0xA4, 0xA5
            }.SetEquals(hotkey.ModifierVirtualKeys));
    }
}
