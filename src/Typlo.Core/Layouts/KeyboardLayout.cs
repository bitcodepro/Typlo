namespace Typlo.Core.Layouts;

/// <summary>
/// A keyboard layout described by the characters each physical key produces.
/// All layouts share the same key order (US ANSI: ` 1-0 - = / Q-] / A-' / Z-/ / \),
/// so the same index in two layouts means the same physical key.
/// </summary>
public sealed class KeyboardLayout
{
    public string Id { get; }
    public string Name { get; }
    /// <summary>Windows LANGID (low word of HKL), used to switch the system layout.</summary>
    public int LangId { get; }
    public string Normal { get; }
    public string Shifted { get; }
    public string LanguageCode { get; }
    public bool SupportsConversion { get; }

    private readonly Dictionary<char, int> _index = new();

    public KeyboardLayout(string id, string name, int langId, string normal, string shifted,
        string? languageCode = null, bool supportsConversion = true)
    {
        if (normal.Length != shifted.Length)
            throw new ArgumentException($"Layout {id}: normal/shifted rows differ in length.");
        Id = id; Name = name; LangId = langId; Normal = normal; Shifted = shifted;
        LanguageCode = languageCode ?? id;
        SupportsConversion = supportsConversion && normal.Any(char.IsLetter);
        // Shifted first, then normal, so that a char present in both maps to the unshifted key.
        for (var i = 0; i < shifted.Length; i++) if (IsMapped(shifted[i])) _index[shifted[i]] = i + normal.Length;
        for (var i = 0; i < normal.Length; i++) if (IsMapped(normal[i])) _index[normal[i]] = i;
    }

    private static bool IsMapped(char c) => !char.IsControl(c) && !char.IsSurrogate(c);

    /// <summary>Position code of a char: 0..N-1 unshifted, N..2N-1 shifted; -1 if absent.</summary>
    public int CodeOf(char c) => _index.TryGetValue(c, out var i) ? i : -1;

    public char CharAt(int code) => code < Normal.Length ? Normal[code] : Shifted[code - Normal.Length];

    public bool Contains(char c) => _index.ContainsKey(c);

    public bool HasLetter(char c) => char.IsLetter(c) && Contains(c);

    public override string ToString() => Name;
}

public static class KeyboardLayouts
{
    public static readonly KeyboardLayout English = new("en", "English", 0x0409,
        "`1234567890-=qwertyuiop[]asdfghjkl;'zxcvbnm,./\\",
        "~!@#$%^&*()_+QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?|");

    public static readonly KeyboardLayout Russian = new("ru", "Русский", 0x0419,
        "ё1234567890-=йцукенгшщзхъфывапролджэячсмитьбю.\\",
        "Ё!\"№;%:?*()_+ЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,/");

    public static readonly KeyboardLayout Ukrainian = new("uk", "Українська", 0x0422,
        "'1234567890-=йцукенгшщзхїфівапролджєячсмитьбю.ґ",
        "₴!\"№;%:?*()_+ЙЦУКЕНГШЩЗХЇФІВАПРОЛДЖЄЯЧСМИТЬБЮ,Ґ");

    public static IReadOnlyList<KeyboardLayout> All { get; } = [English, Russian, Ukrainian];

    public static KeyboardLayout? Find(string id) =>
        All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
}
