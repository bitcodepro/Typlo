namespace Typlo.App.Platform.Windows;

/// <summary>Maps the platform-neutral key names of <see cref="Core.Hotkeys.Hotkey"/> to Windows virtual-key codes.</summary>
internal static class VirtualKeys
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Pause"] = 0x13, ["Semicolon"] = 0xBA, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD,
        ["Period"] = 0xBE, ["Slash"] = 0xBF, ["Tilde"] = 0xC0, ["OpenBracket"] = 0xDB, ["Backslash"] = 0xDC,
        ["CloseBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    public static uint FromName(string key)
    {
        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]))) return key[0];
        if (Core.Hotkeys.Hotkey.IsFunctionKey(key)) return 0x70u + uint.Parse(key.AsSpan(1)) - 1;
        return Named.GetValueOrDefault(key);
    }
}
