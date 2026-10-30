namespace Typlo.Core.Hotkeys;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global shortcut. <see cref="Key"/> is a platform-neutral key name:
/// A-Z, 0-9, F1-F24 or one of <see cref="NamedKeys"/>.
/// </summary>
public sealed record Hotkey(KeyModifiers Modifiers, string Key)
{
    public bool IsModifierOnly => Key.Length == 0 && Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift);
    public static readonly IReadOnlyList<string> NamedKeys =
    [
        "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
        "Pause", "Tilde", "Minus", "Plus", "Comma", "Period", "Slash", "Backslash", "Semicolon", "Quote",
        "OpenBracket", "CloseBracket",
    ];

    public static bool IsValidKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key.Length == 1) return char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]);
        if (IsFunctionKey(key)) return true;
        return NamedKeys.Contains(key);
    }

    public static bool IsFunctionKey(string key) =>
        key.Length > 1 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24;

    /// <summary>A global shortcut without Ctrl/Alt/Win would swallow normal typing (F-keys excepted).</summary>
    public bool IsUsable =>
        IsModifierOnly || IsValidKey(Key) &&
        ((Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0 || IsFunctionKey(Key));

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        if (Key.Length > 0) parts.Add(Key);
        return string.Join('+', parts);
    }

    public static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = KeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Ctrl; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "win" or "meta" or "cmd": mods |= KeyModifiers.Win; break;
                default:
                    if (key is not null) return null;
                    key = Normalize(raw);
                    break;
            }
        }
        return key is not null && IsValidKey(key) ? new Hotkey(mods, key) :
            key is null && mods == (KeyModifiers.Ctrl | KeyModifiers.Shift) ? new Hotkey(mods, "") : null;
    }

    private static string Normalize(string raw)
    {
        if (raw.Length == 1) return raw.ToUpperInvariant();
        if (raw[0] is 'f' or 'F' && int.TryParse(raw.AsSpan(1), out _)) return "F" + raw[1..];
        return NamedKeys.FirstOrDefault(k => k.Equals(raw, StringComparison.OrdinalIgnoreCase)) ?? raw;
    }
}
