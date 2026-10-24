using System.Globalization;
using System.Text;
using Typlo.Core.Layouts;

namespace Typlo.Core.Text;

/// <summary>Cycles the case of a text: lower → Title → UPPER → lower.</summary>
public static class CaseConverter
{
    public static string Cycle(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return text;
        if (letters.All(char.IsUpper)) return text.ToLower(CultureInfo.CurrentCulture);
        if (letters.All(char.IsLower)) return ToTitle(text);
        return text.ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>Capitalizes the first letter of every word and lowers the rest.</summary>
    public static string ToTitle(string text)
    {
        var sb = new StringBuilder(text.Length);
        var startOfWord = true;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(startOfWord ? char.ToUpper(c, CultureInfo.CurrentCulture) : char.ToLower(c, CultureInfo.CurrentCulture));
                startOfWord = false;
            }
            else
            {
                sb.Append(c);
                startOfWord = !char.IsDigit(c) && c != '\'' && c != '’';
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Transliteration between Cyrillic (Russian/Ukrainian) and Latin: «Привет» ⇄ "Privet".
/// The direction is picked from the text: anything with Cyrillic letters goes to Latin.
/// </summary>
public static class Transliterator
{
    private static readonly Dictionary<char, string> Russian = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };

    // Ukrainian national transliteration differs for г, и and has its own letters.
    private static readonly Dictionary<char, string> UkrainianOverrides = new()
    {
        ['г'] = "h", ['ґ'] = "g", ['и'] = "y", ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['\''] = "", ['’'] = "",
    };

    private static readonly (string Latin, string Cyrillic)[] Reverse =
    [
        ("shch", "щ"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"), ("yo", "ё"), ("yu", "ю"),
        ("ya", "я"), ("ye", "е"), ("a", "а"), ("b", "б"), ("v", "в"), ("w", "в"), ("g", "г"), ("d", "д"),
        ("e", "е"), ("z", "з"), ("i", "и"), ("y", "й"), ("k", "к"), ("q", "к"), ("l", "л"), ("m", "м"),
        ("n", "н"), ("o", "о"), ("p", "п"), ("r", "р"), ("s", "с"), ("t", "т"), ("u", "у"), ("f", "ф"),
        ("h", "х"), ("c", "к"), ("x", "кс"), ("j", "дж"),
    ];

    public static bool HasCyrillic(string text) => text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    public static string Toggle(string text) => HasCyrillic(text) ? ToLatin(text) : ToCyrillic(text);

    public static string ToLatin(string text)
    {
        var ukrainian = text.Any(c => "ґіїєҐІЇЄ".Contains(c));
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lower = char.ToLowerInvariant(c);
            string? mapped = null;
            var wordStart = i == 0 || !char.IsLetter(text[i - 1]);
            if (ukrainian && lower is 'ї' or 'є' && !wordStart) mapped = lower == 'ї' ? "i" : "ie"; // official: "Київ" → "Kyiv"
            else if (ukrainian && UkrainianOverrides.TryGetValue(lower, out var uk)) mapped = uk;
            else if (Russian.TryGetValue(lower, out var ru)) mapped = ru;
            if (mapped is null) { sb.Append(c); continue; }
            if (char.IsUpper(c) && mapped.Length > 0)
            {
                // "Щука" → "Shchuka", but "ЩУКА" → "SHCHUKA".
                var wholeWordUpper = (i + 1 < text.Length && char.IsUpper(text[i + 1])) || (i > 0 && char.IsUpper(text[i - 1]));
                mapped = wholeWordUpper ? mapped.ToUpperInvariant() : char.ToUpperInvariant(mapped[0]) + mapped[1..];
            }
            sb.Append(mapped);
        }
        return sb.ToString();
    }

    public static string ToCyrillic(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;
            foreach (var (latin, cyrillic) in Reverse)
            {
                if (i + latin.Length > text.Length || string.Compare(text, i, latin, 0, latin.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                var source = text.AsSpan(i, latin.Length);
                var upper = char.IsUpper(source[0]);
                var allUpper = upper && (latin.Length > 1 ? char.IsUpper(source[^1]) : i + 1 < text.Length && char.IsUpper(text[i + 1]));
                sb.Append(!upper ? cyrillic : allUpper ? cyrillic.ToUpperInvariant() : char.ToUpperInvariant(cyrillic[0]) + cyrillic[1..]);
                i += latin.Length;
                matched = true;
                break;
            }
            if (!matched) sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}

/// <summary>A text snippet: typing <see cref="Trigger"/> replaces it with <see cref="Text"/>.</summary>
public sealed class Snippet
{
    public string Trigger { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Watches typed characters and reports when the end of the input matches a snippet trigger.
/// Matching ignores case and the keyboard layout: ";фввк" typed in the Russian layout matches ";addr".
/// </summary>
public sealed class SnippetMatcher
{
    private const int MaxBuffer = 64;
    private readonly StringBuilder _buffer = new();
    private List<(string Key, Snippet Snippet)> _snippets = [];

    public void SetSnippets(IEnumerable<Snippet> snippets) =>
        _snippets = snippets
            .Where(s => s.Trigger.Trim().Length >= 2 && s.Text.Length > 0)
            .Select(s => (Normalize(s.Trigger.Trim()), s))
            .OrderByDescending(s => s.Item1.Length)
            .ToList();

    public bool HasSnippets => _snippets.Count > 0;

    /// <summary>Feeds one typed character; returns the snippet whose trigger was just completed.</summary>
    public Snippet? OnChar(char c)
    {
        if (char.IsControl(c)) { Reset(); return null; }
        _buffer.Append(NormalizeChar(c));
        if (_buffer.Length > MaxBuffer) _buffer.Remove(0, _buffer.Length - MaxBuffer);
        var text = _buffer.ToString();
        foreach (var (key, snippet) in _snippets)
        {
            if (!text.EndsWith(key, StringComparison.Ordinal)) continue;
            Reset();
            return snippet;
        }
        return null;
    }

    /// <summary>Forgets what was typed (caret moved, window changed, Enter, mouse click…).</summary>
    public void Reset() => _buffer.Clear();

    public void Backspace()
    {
        if (_buffer.Length > 0) _buffer.Length--;
    }

    public static string Normalize(string text) => string.Concat(text.Select(NormalizeChar));

    /// <summary>Lower case, and Cyrillic letters mapped to the Latin letter on the same physical key.</summary>
    private static char NormalizeChar(char c)
    {
        c = char.ToLowerInvariant(c);
        foreach (var layout in new[] { KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian })
        {
            var code = layout.CodeOf(c);
            if (code >= 0) return char.ToLowerInvariant(KeyboardLayouts.English.CharAt(code));
        }
        return c;
    }
}
