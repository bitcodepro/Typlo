using Typlo.Core.Layouts;

namespace Typlo.Core.Languages;

/// <summary>Conservative keyboard correction from exact disk vocabulary evidence, not translation.</summary>
public sealed class LayoutLanguageDetector(LanguageLexicon lexicon)
{
    private const int Reliable = 120, Margin = 32, AmbiguityMargin = 24;
    private sealed record Candidate(KeyboardLayout Layout, string Text, int Score);

    public KeyboardLayout? Suggest(string physicalKeys, KeyboardLayout source, IReadOnlyList<KeyboardLayout> allowed)
    {
        if (physicalKeys.Length is < 2 or > 128 || physicalKeys.Any(char.IsWhiteSpace) || !source.SupportsConversion) return null;
        if (!lexicon.Languages.Contains(source.LanguageCode.Split('-', '_')[0].ToLowerInvariant())) return null;
        var cache = new Dictionary<string, WordEvidence?>();
        var current = Read(source, physicalKeys, cache);
        // Avoid identifiers, addresses, paths and numbers. A punctuation key can still be a letter
        // in the intended layout (e.g. htdm.dftim), so punctuation alone is not a rejection.
        if (current.Text.Any(c => char.IsDigit(c) || c is '@' or '_' or '\\' or ':' or '=')) return null;
        var candidates = allowed.Where(l => l.SupportsConversion).DistinctBy(l => l.Id)
            .Select(l => Read(l, physicalKeys, cache)).Where(c => c.Score >= Reliable).ToArray();
        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        if (best is null || best.Layout.Id == source.Id) return null;
        if (current.Score >= Reliable && best.Text != current.Text) return null;
        if (best.Score - current.Score < Margin) return null;
        // Shared spellings can use the cycle's preferred member; competing different spellings
        // need a clear winner. Corpus weights are evidence, not calibrated probabilities.
        if (candidates.Any(c => c.Text != best.Text && best.Score - c.Score < AmbiguityMargin)) return null;
        return best.Layout;
    }

    private Candidate Read(KeyboardLayout layout, string keys, Dictionary<string, WordEvidence?> cache)
    {
        var rendered = LayoutConverter.Convert(keys, KeyboardLayouts.English, layout, forceSource: true);
        var text = LanguageLexicon.Normalize(rendered);
        var token = text.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '(', ')', '[', ']', '{', '}', '«', '»', '…');
        if (token.Count(char.IsLetter) < 2 || token.Any(c => !char.IsLetter(c) && c is not '\'' and not '-')) return new(layout, text, 0);
        var code = layout.LanguageCode.Split('-', '_')[0].ToLowerInvariant();
        var languages = lexicon.Languages;
        var index = -1;
        for (var i = 0; i < languages.Count; i++) if (languages[i] == code) { index = i; break; }
        if (index < 0) return new(layout, text, 0);
        if (!cache.TryGetValue(token, out var found)) cache[token] = found = lexicon.Lookup(token);
        if (found is not { } evidence) return new(layout, text, 0);
        var manual = (evidence.SupplementMask & (1 << index)) != 0;
        return new(layout, text, Math.Max(evidence.CorpusWeights[index], manual ? 160 : 0));
    }
}
