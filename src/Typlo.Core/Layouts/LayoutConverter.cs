using System.Text;

namespace Typlo.Core.Layouts;

public sealed record ConversionResult(string Text, KeyboardLayout? Source, KeyboardLayout Target)
{
    public bool Changed => Source is not null && Text.Length > 0;
}

/// <summary>
/// Re-types text as if the same physical keys had been pressed in another layout
/// ("ghbdtn" -> "привет"). Works token by token, so words that are already in the
/// target layout are left alone in mixed text.
/// </summary>
public sealed class LayoutConverter
{
    private readonly IReadOnlyList<KeyboardLayout> _layouts;

    /// <param name="layouts">Candidate layouts in priority order (earlier wins ties).</param>
    public LayoutConverter(IReadOnlyList<KeyboardLayout> layouts)
    {
        if (layouts.Count < 2) throw new ArgumentException("At least two layouts are required.");
        _layouts = layouts;
    }

    public IReadOnlyList<KeyboardLayout> Layouts => _layouts;

    /// <summary>Converts text into <paramref name="target"/>; unchanged if it is already there.</summary>
    public ConversionResult ConvertTo(string text, KeyboardLayout target)
    {
        // Without letters (e.g. "`" or "?") there is nothing to detect: assume the first other layout.
        var source = DetectSource(text, target)
            ?? (DetectLayout(text) is null ? _layouts.First(l => l != target) : null);
        if (source is null) return new ConversionResult(text, null, target);
        return new ConversionResult(Convert(text, source, target), source, target);
    }

    /// <summary>Converts text to the "other" layout: the first candidate that is not the detected one.</summary>
    public ConversionResult Toggle(string text)
    {
        var current = DetectLayout(text) ?? _layouts[0];
        var target = _layouts.First(l => l != current);
        return ConvertTo(text, target);
    }

    /// <summary>Layout whose letters dominate the text, or null if there are no known letters.</summary>
    public KeyboardLayout? DetectLayout(string text)
    {
        KeyboardLayout? best = null;
        var bestScore = 0;
        foreach (var layout in _layouts)
        {
            var score = text.Count(layout.HasLetter);
            if (score > bestScore) { best = layout; bestScore = score; }
        }
        return best;
    }

    /// <summary>Most likely layout the text was typed in, excluding the target; null if none.</summary>
    public KeyboardLayout? DetectSource(string text, KeyboardLayout target)
    {
        KeyboardLayout? best = null;
        var bestScore = 0;
        foreach (var layout in _layouts)
        {
            if (layout == target) continue;
            var score = text.Count(c => layout.HasLetter(c) && !target.HasLetter(c));
            if (score > bestScore) { best = layout; bestScore = score; }
        }
        return best;
    }

    /// <summary>Mechanical conversion of whitespace-separated tokens from source to target.</summary>
    public static string Convert(string text, KeyboardLayout source, KeyboardLayout target)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i++]); continue; }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            AppendToken(sb, text.AsSpan(start, i - start), source, target);
        }
        return sb.ToString();
    }

    private static void AppendToken(StringBuilder sb, ReadOnlySpan<char> token,
        KeyboardLayout source, KeyboardLayout target)
    {
        var hasSourceLetters = false;
        var hasOtherLetters = false;
        foreach (var c in token)
        {
            if (!char.IsLetter(c)) continue;
            if (source.HasLetter(c) && !target.HasLetter(c)) hasSourceLetters = true;
            else hasOtherLetters = true;
        }
        // A word written entirely in the target (or another) script is kept as is.
        if (!hasSourceLetters && hasOtherLetters) { sb.Append(token); return; }

        foreach (var c in token)
        {
            var code = source.CodeOf(c);
            sb.Append(code >= 0 ? target.CharAt(code) : c);
        }
    }
}
