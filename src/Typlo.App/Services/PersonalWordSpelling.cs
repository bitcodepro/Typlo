using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.App.Services;

/// <summary>UI guidance for exact dictionary forms; never rewrites stored/imported user data.</summary>
internal static class PersonalWordSpelling
{
    internal static string[] Forms(string text) => text.Split(['\r', '\n', ',', ';'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool MatchesScript(string word, string language)
    {
        string normalized;
        try { normalized = LanguageLexicon.Normalize(word); } catch (ArgumentException) { return true; }
        var letters = normalized.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return true; // Normal word validation reports syntax errors.
        return language is "ru" or "uk"
            ? letters.All(c => c is >= '\u0400' and <= '\u052f')
            : letters.All(c => c is >= 'a' and <= 'z' or >= '\u00c0' and <= '\u024f' or >= '\u1e00' and <= '\u1eff');
    }

    internal static bool HasMismatch(string text, string language) =>
        Forms(text).Any(word => !MatchesScript(word, language));

    internal static void RequireCorrectScript(string text, string language)
    {
        if (HasMismatch(text, language)) throw new ArgumentException(Loc.T("dictionary.scriptMismatch"));
    }

    internal static string[] Suggestions(string text, string language, IReadOnlyList<KeyboardLayout> layouts)
    {
        var forms = Forms(text);
        if (forms.Length != 1 || !HasMismatch(text, language)) return [];
        var word = forms[0];
        var available = layouts.Where(l => l.SupportsConversion).ToArray();
        var targets = available.Where(l => l.LanguageCode.Split('-', '_')[0].Equals(language, StringComparison.OrdinalIgnoreCase));
        return available.Where(source => word.Where(char.IsLetter).All(source.Contains))
            .SelectMany(source => targets.Select(target => LayoutConverter.Convert(word, source, target, forceSource: true)))
            .Where(converted => converted != word && PersonalLexicon.IsWord(LanguageLexicon.Normalize(converted)) && MatchesScript(converted, language))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
    }
}
