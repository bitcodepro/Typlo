using Typlo.Core.Layouts;

namespace Typlo.Core.Languages;

public enum LanguageDetectionReason { Suggested, InvalidInput, UnsupportedLanguage, ProtectedToken, UnknownWord, AlreadyCorrect, LanguageRestricted, Ambiguous, LowConfidence, ExcludedWord }
public sealed record LanguageDetection(KeyboardLayout? Layout, LanguageDetectionReason Reason);
public sealed record LayoutOption(string LayoutId, string Text, int Evidence);

/// <summary>Conservative keyboard correction from exact disk vocabulary evidence, not translation.</summary>
public sealed class LayoutLanguageDetector(LanguageLexicon lexicon, PersonalLexicon? personal = null)
{
    private const int Reliable = 120, Margin = 32, AmbiguityMargin = 24;
    private sealed record Candidate(KeyboardLayout Layout, string Text, int Score);

    public LayoutOption[] Options(string keys, IReadOnlyList<KeyboardLayout> allowed)
    {
        if (keys.Length is < 2 or > 128 || keys.Any(char.IsWhiteSpace)) return [];
        var cache = new Dictionary<string, (WordEvidence? Corpus, byte Personal)>();
        return allowed.Where(l => l.SupportsConversion).DistinctBy(l => l.Id).Select(l => Read(l, keys, cache))
            .Where(c => c.Score > 0).OrderByDescending(c => c.Score).Take(16)
            .Select(c => new LayoutOption(c.Layout.Id, LayoutConverter.Convert(keys, KeyboardLayouts.English, c.Layout, forceSource: true), c.Score)).ToArray();
    }

    public KeyboardLayout? Suggest(string physicalKeys, KeyboardLayout source, IReadOnlyList<KeyboardLayout> allowed)
        => Analyze(physicalKeys, source, allowed).Layout;

    public LanguageDetection Analyze(string physicalKeys, KeyboardLayout source, IReadOnlyList<KeyboardLayout> allowed, IReadOnlyList<KeyboardLayout>? installed = null, bool protectTechnicalInput = true)
    {
        if (physicalKeys.Length is < 2 or > 128 || physicalKeys.Any(char.IsWhiteSpace) || !source.SupportsConversion) return new(null, LanguageDetectionReason.InvalidInput);
        var typed = LayoutConverter.Convert(physicalKeys, KeyboardLayouts.English, source, forceSource: true);
        if (protectTechnicalInput && (TechnicalToken.IsProtected(typed) || TechnicalToken.IsProtected(physicalKeys))) return new(null, LanguageDetectionReason.ProtectedToken);
        if (personal?.IsExcluded(typed) == true) return new(null, LanguageDetectionReason.ExcludedWord);
        if (!lexicon.Languages.Contains(source.LanguageCode.Split('-', '_')[0].ToLowerInvariant())) return new(null, LanguageDetectionReason.UnsupportedLanguage);
        var cache = new Dictionary<string, (WordEvidence? Corpus, byte Personal)>();
        var current = Read(source, physicalKeys, cache);
        // Avoid identifiers, addresses, paths and numbers. A punctuation key can still be a letter
        // in the intended layout (e.g. htdm.dftim), so punctuation alone is not a rejection.
        if (current.Text.Any(c => char.IsDigit(c) || c is '@' or '_' or '\\' or ':' or '=')) return new(null, LanguageDetectionReason.ProtectedToken);
        bool ReliableCandidate(Candidate candidate) => candidate.Score >= Reliable ||
            // Exact Cyrillic words such as «напиши» fall just below the subtitle-frequency
            // cutoff. Accept that weaker evidence only for an unknown all-Latin source word.
            (candidate.Score >= 110 && current.Score == 0 && current.Text.Length >= 4 &&
             current.Text.All(char.IsAsciiLetter) && candidate.Text.All(c => c is >= '\u0400' and <= '\u04ff'));
        var candidates = allowed.Where(l => l.SupportsConversion).DistinctBy(l => l.Id)
            .Select(l => Read(l, physicalKeys, cache)).Where(ReliableCandidate).ToArray();
        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        if (best is null)
        {
            if (current.Score >= Reliable) return new(null, LanguageDetectionReason.AlreadyCorrect);
            if (installed is not null && installed.Where(l => l.SupportsConversion && !allowed.Any(a => a.Id == l.Id))
                .Select(l => Read(l, physicalKeys, cache)).Any(ReliableCandidate)) return new(null, LanguageDetectionReason.LanguageRestricted);
            return new(null, cache.Values.Any(v => v.Corpus is not null || v.Personal != 0) ? LanguageDetectionReason.LowConfidence : LanguageDetectionReason.UnknownWord);
        }
        if (best.Layout.Id == source.Id || (current.Score >= Reliable && best.Text != current.Text)) return new(null, LanguageDetectionReason.AlreadyCorrect);
        if (best.Score - current.Score < Margin) return new(null, LanguageDetectionReason.LowConfidence);
        // Shared spellings can use the cycle's preferred member; competing different spellings
        // need a clear winner. Corpus weights are evidence, not calibrated probabilities.
        if (candidates.Any(c => c.Text != best.Text && best.Score - c.Score < AmbiguityMargin)) return new(null, LanguageDetectionReason.Ambiguous);
        return new(best.Layout, LanguageDetectionReason.Suggested);
    }

    private Candidate Read(KeyboardLayout layout, string keys, Dictionary<string, (WordEvidence? Corpus, byte Personal)> cache)
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
        if (!cache.TryGetValue(token, out var found)) cache[token] = found = (lexicon.Lookup(token), personal?.Lookup(token) ?? 0);
        if ((found.Personal & (1 << index)) != 0) return new(layout, text, 250);
        if (found.Corpus is not { } evidence) return new(layout, text, 0);
        var manual = (evidence.SupplementMask & (1 << index)) != 0;
        return new(layout, text, Math.Max(evidence.CorpusWeights[index], manual ? 160 : 0));
    }
}
