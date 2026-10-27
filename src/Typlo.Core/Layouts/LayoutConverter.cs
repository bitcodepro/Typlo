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
    private readonly IReadOnlyList<KeyboardLayout> _cycle;

    /// <param name="layouts">Candidate layouts in priority order (earlier wins ties).</param>
    public LayoutConverter(IReadOnlyList<KeyboardLayout> layouts, IReadOnlyList<KeyboardLayout>? cycle = null)
    {
        _layouts = layouts.Where(l => l.SupportsConversion).DistinctBy(l => l.Id).ToArray();
        _cycle = (cycle ?? layouts).Where(l => l.SupportsConversion).DistinctBy(l => l.Id).ToArray();
    }

    public IReadOnlyList<KeyboardLayout> Layouts => _layouts;

    /// <summary>Converts text into <paramref name="target"/>; unchanged if it is already there.</summary>
    public ConversionResult ConvertTo(string text, KeyboardLayout target, KeyboardLayout? sourceHint = null)
    {
        if (!target.SupportsConversion) return new ConversionResult(text, null, target);
        // Shared alphabets cannot identify the source (AZERTY/QWERTY, or common RU/UA words).
        // A validated input/current-layout hint makes mechanical conversion unambiguous.
        if (sourceHint is not null && text.Where(char.IsLetter).All(sourceHint.Contains))
            return sourceHint.Id == target.Id ? new(text, null, target) :
                new(Convert(text, sourceHint, target, forceSource: true), sourceHint, target);
        if (DetectLayout(text) is null && sourceHint == target)
            return new ConversionResult(text, null, target);
        // Without letters (e.g. "`" or "?") there is nothing to detect: assume the first other layout.
        var source = DetectSource(text, target)
            ?? (DetectLayout(text) is null ? sourceHint ?? _layouts.FirstOrDefault(l => l.Id != target.Id) : null);
        if (source is null) return new ConversionResult(text, null, target);
        return new ConversionResult(Convert(text, source, target), source, target);
    }

    /// <summary>Converts to the next layout in the configured cycle, wrapping at its end.</summary>
    public ConversionResult Toggle(string text, KeyboardLayout? sourceHint = null)
    {
        var current = DetectLayout(text, sourceHint) ?? sourceHint ?? _cycle.FirstOrDefault() ?? KeyboardLayouts.English;
        if (_cycle.Count < 2) return new(text, null, current);
        var index = _cycle.ToList().FindIndex(l => l.Id == current.Id);
        var target = _cycle[(index + 1) % _cycle.Count];
        return ConvertTo(text, target, sourceHint);
    }

    /// <summary>Layout whose letters dominate the text, or null if there are no known letters.</summary>
    public KeyboardLayout? DetectLayout(string text, KeyboardLayout? preferred = null)
    {
        KeyboardLayout? best = null;
        var bestScore = 0;
        foreach (var layout in (preferred is null ? _layouts : new[] { preferred }.Concat(_layouts).DistinctBy(l => l.Id)))
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
    public static string Convert(string text, KeyboardLayout source, KeyboardLayout target, bool forceSource = false)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i++]); continue; }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            AppendToken(sb, text.AsSpan(start, i - start), source, target, forceSource);
        }
        return sb.ToString();
    }

    private static void AppendToken(StringBuilder sb, ReadOnlySpan<char> token,
        KeyboardLayout source, KeyboardLayout target, bool forceSource)
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
        if (!forceSource && !hasSourceLetters && hasOtherLetters) { sb.Append(token); return; }

        foreach (var c in token)
        {
            var code = source.CodeOf(c);
            var mapped = code >= 0 && code < target.Normal.Length * 2 ? target.CharAt(code) : '\0';
            sb.Append(mapped != '\0' ? mapped : c);
        }
    }
}
