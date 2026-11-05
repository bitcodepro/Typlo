using System.Diagnostics;
using System.Globalization;
using WeCantSpell.Hunspell;
using Typlo.Core.Languages;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

// Used only by --spelling-worker. No dictionary instances live in the resident/UI process.
internal sealed class SpellingEngine(string directory)
{
    private readonly Dictionary<string, WordList> _dictionaries = [];
    private WordList Dictionary(string code)
    {
        if (!_dictionaries.TryGetValue(code, out var value))
            _dictionaries[code] = value = WordList.CreateFromFiles(Path.Combine(directory, code switch
            { "en" => "en_US.dic", "ru" => "ru_RU.dic", "uk" => "uk_UA.dic", _ => throw new ArgumentException("Unsupported spelling language") }));
        return value;
    }
    internal SpellingResult Check(SpellingRequest request)
    {
        var timer = Stopwatch.StartNew();
        var frequenciesPath = Path.GetFullPath(Path.Combine(directory, "..", "language-data", "words.sqlite3"));
        using var frequencies = File.Exists(frequenciesPath) ? new LanguageLexicon(frequenciesPath) : null;
        var rankingAvailable = true;
        if (request.Text.Length > SpellingText.MaxCharacters || request.Language is not ("auto" or "en" or "ru" or "uk") || request.Ignored?.Length > SpellingText.MaxWords)
            throw new ArgumentException("Invalid spelling request");
        var tokens = SpellingText.Tokens(request.Text).Take(SpellingText.MaxWords + 1).ToArray();
        if (tokens.Length > SpellingText.MaxWords) throw new ArgumentException("Too many words");
        var ignored = new HashSet<string>(request.Ignored ?? [], StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>((request.Excluded ?? []).Select(SpellingCorrection.Key), StringComparer.Ordinal);
        var corrections = request.Corrections ?? [];
        if (corrections.Length > SpellingCorrection.MaxPairs || corrections.Any(p => p is null || !p.IsValid) ||
            corrections.DistinctBy(p => SpellingCorrection.Key(p.Error)).Count() != corrections.Length) throw new ArgumentException("Invalid personal corrections");
        var personal = corrections.ToDictionary(p => SpellingCorrection.Key(p.Error), p => p.Correction.Trim());
        var accepted = new Dictionary<(string, string), bool>();
        bool Known(string word, string code)
        {
            if (!accepted.TryGetValue((word, code), out var result)) accepted[(word, code)] = result = Dictionary(code).Check(word);
            return result;
        }
        static string[] Scripts(string word) => word.All(c => c <= 127 || c is '’' or 'ʼ') ? ["en"] :
            word.Any(c => c is >= '\u0400' and <= '\u04ff') && !word.Any(char.IsAsciiLetter) ? ["ru", "uk"] : [];
        // Use the surrounding paragraph, not the active keyboard layout, to resolve Cyrillic.
        var issues = new List<SpellingIssue>();
        var suggestionCache = new Dictionary<(string Word, string Languages), string[]>();
        foreach (var paragraph in tokens.GroupBy(t => request.Text.LastIndexOf('\n', Math.Max(0, t.Start - 1))))
        {
            var ru = 0; var uk = 0;
            foreach (var token in paragraph)
            {
                if (Scripts(token.Word).Length != 2 || ignored.Contains(token.Word) || excluded.Contains(SpellingCorrection.Key(token.Word)) || personal.ContainsKey(SpellingCorrection.Key(token.Word))) continue;
                var r = Known(token.Word, "ru"); var u = Known(token.Word, "uk");
                if (r && !u) ru++; if (u && !r) uk++;
            }
            foreach (var token in paragraph)
            {
                if (issues.Count == SpellingText.MaxIssues) break;
                if (excluded.Contains(SpellingCorrection.Key(token.Word))) continue;
                if (personal.TryGetValue(SpellingCorrection.Key(token.Word), out var correction))
                {
                    var corrected = CaseLike(token.Word, correction);
                    if (corrected != token.Word) issues.Add(new(token.Start, token.Word.Length, token.Word, "custom", [corrected]));
                    continue;
                }
                if (ignored.Contains(token.Word)) continue;
                var codes = request.Language == "auto" ? Scripts(token.Word) : [request.Language];
                if (codes.Length == 0 || codes.Any(code => Known(token.Word, code))) continue;
                if (codes.Length == 2 && Math.Abs(ru - uk) >= 2) codes = [ru > uk ? "ru" : "uk"];
                var cacheKey = (token.Word, string.Join('/', codes));
                if (!suggestionCache.TryGetValue(cacheKey, out var suggestions))
                {
                    var alternatives = codes.Select(code => Dictionary(code).Suggest(token.Word).Where(word => word.Length <= SpellingText.MaxSuggestionCharacters).Take(5).ToArray()).ToArray();
                    // An uncertain RU/UK word must not fill every slot from the first language.
                    suggestionCache[cacheKey] = suggestions = Enumerable.Range(0, 5)
                        .SelectMany(index => alternatives.Where(list => index < list.Length).Select(list => list[index]))
                        .Select(word => CaseLike(token.Word, word)).Distinct().Where(word => word != token.Word).Take(10).ToArray();
                    int Weight(string word)
                    {
                        if (frequencies is null || !rankingAvailable) return 0;
                        try
                        {
                            var evidence = frequencies.Lookup(word); if (evidence is null) return 0;
                            return codes.Max(code => evidence.Value.Score(code == "en" ? 0 : code == "ru" ? 1 : 2));
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { rankingAvailable = false; return 0; }
                    }
                    suggestionCache[cacheKey] = suggestions = suggestions.OrderBy(word => Distance(token.Word.ToLowerInvariant(), word.ToLowerInvariant()))
                        .ThenByDescending(Weight).ToArray();
                }
                issues.Add(new(token.Start, token.Word.Length, token.Word, codes.Length == 1 ? codes[0] : "ru/uk", suggestions));
            }
        }
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new(issues.OrderBy(i => i.Start).ToArray(), timer.ElapsedMilliseconds,
            Metric(() => process.WorkingSet64), Metric(() => process.PeakWorkingSet64), GC.GetTotalMemory(false));
    }
    internal static long Metric(Func<long> read)
    { try { return read(); } catch (Exception e) when (e is NotSupportedException or System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; } }
    private static string CaseLike(string source, string result) => source.Where(c => char.IsUpper(c) || char.IsLower(c)).All(char.IsUpper) ? result.ToUpperInvariant() :
        char.IsUpper(source[0]) ? char.ToUpperInvariant(result[0]) + result[1..] : result;

    private static int Distance(string source, string target)
    {
        var cells = new int[source.Length + 1, target.Length + 1];
        for (var i = 0; i <= source.Length; i++) cells[i, 0] = i;
        for (var j = 0; j <= target.Length; j++) cells[0, j] = j;
        for (var i = 1; i <= source.Length; i++)
            for (var j = 1; j <= target.Length; j++)
            {
                cells[i, j] = Math.Min(cells[i - 1, j] + 1, Math.Min(cells[i, j - 1] + 1, cells[i - 1, j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1)));
                if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1]) cells[i, j] = Math.Min(cells[i, j], cells[i - 2, j - 2] + 1);
            }
        return cells[source.Length, target.Length];
    }
}
