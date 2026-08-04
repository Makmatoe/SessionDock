using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SessionDock.Services;

[Flags]
internal enum MacroRecordingHotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004
}

internal readonly record struct MacroRecordingHotkey(
    int VirtualKey,
    MacroRecordingHotkeyModifiers Modifiers,
    string PersistedValue)
{
    internal string DisplayName => PersistedValue;

    internal IReadOnlySet<int> ModifierVirtualKeys
    {
        get
        {
            var keys = new HashSet<int>();
            if (Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Control))
            {
                keys.UnionWith([0x11, 0xA2, 0xA3]);
            }
            if (Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Alt))
                keys.UnionWith([0x12, 0xA4, 0xA5]);
            if (Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Shift))
                keys.UnionWith([0x10, 0xA0, 0xA1]);
            return keys;
        }
    }
}

internal static class MacroRecordingHotkeyPolicy
{
    internal const string DefaultValue = "F8";

    private static readonly string[] SuggestedValues =
    [
        "F6", "F7", "F8", "F9", "F10", "F11",
        "Ctrl+F6", "Ctrl+F7", "Ctrl+F8", "Ctrl+F9", "Ctrl+F10",
        "Ctrl+F11",
        "Ctrl+Shift+F6", "Ctrl+Shift+F7", "Ctrl+Shift+F8",
        "Ctrl+Shift+F9", "Ctrl+Shift+F10", "Ctrl+Shift+F11"
    ];

    internal static IReadOnlyList<string> Suggestions => SuggestedValues;

    internal static string Normalize(string? value) =>
        TryParse(value, out var hotkey)
            ? hotkey.PersistedValue
            : DefaultValue;

    internal static bool TryParse(
        string? value,
        out MacroRecordingHotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;

        var tokens = value.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 ||
            tokens.Length > 4 ||
            tokens.Any(token => token.Length == 0))
            return false;

        var modifiers = MacroRecordingHotkeyModifiers.None;
        var functionKey = 0;
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Control))
                    return false;
                modifiers |= MacroRecordingHotkeyModifiers.Control;
                continue;
            }
            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Alt))
                    return false;
                modifiers |= MacroRecordingHotkeyModifiers.Alt;
                continue;
            }
            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Shift))
                    return false;
                modifiers |= MacroRecordingHotkeyModifiers.Shift;
                continue;
            }
            if (functionKey != 0 ||
                token.Length is < 2 or > 3 ||
                (token[0] != 'F' && token[0] != 'f') ||
                !int.TryParse(token.AsSpan(1), out var functionNumber) ||
                functionNumber is < 6 or > 11)
            {
                return false;
            }

            functionKey = functionNumber;
        }

        if (functionKey == 0)
            return false;

        var canonical = new List<string>(4);
        if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Control))
            canonical.Add("Ctrl");
        if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Alt))
            canonical.Add("Alt");
        if (modifiers.HasFlag(MacroRecordingHotkeyModifiers.Shift))
            canonical.Add("Shift");
        canonical.Add($"F{functionKey}");
        hotkey = new MacroRecordingHotkey(
            0x6F + functionKey,
            modifiers,
            string.Join("+", canonical));
        return true;
    }
}

internal sealed class GlobalRecordingHotkeyRegistration : IDisposable
{
    private const uint NoRepeatModifier = 0x4000;
    private readonly nint _windowHandle;
    private readonly int _identifier;
    private bool _registered;

    private GlobalRecordingHotkeyRegistration(
        nint windowHandle,
        int identifier)
    {
        _windowHandle = windowHandle;
        _identifier = identifier;
        _registered = true;
    }

    internal static GlobalRecordingHotkeyRegistration Register(
        nint windowHandle,
        int identifier,
        MacroRecordingHotkey hotkey)
    {
        if (windowHandle == nint.Zero)
            throw new InvalidOperationException(
                "The recorder window is not ready for a global stop keybind.");
        if (identifier is <= 0 or > 0xBFFF)
            throw new ArgumentOutOfRangeException(nameof(identifier));

        var nativeModifiers = (uint)hotkey.Modifiers | NoRepeatModifier;
        if (!RegisterHotKey(
                windowHandle,
                identifier,
                nativeModifiers,
                checked((uint)hotkey.VirtualKey)))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"The {hotkey.DisplayName} stop keybind is already in use or unavailable.");
        }

        return new GlobalRecordingHotkeyRegistration(
            windowHandle,
            identifier);
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        _registered = false;
        if (!UnregisterHotKey(_windowHandle, _identifier))
        {
            System.Diagnostics.Trace.WriteLine(
                $"Global recording hotkey cleanup failed with {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        nint windowHandle,
        int identifier);
}
