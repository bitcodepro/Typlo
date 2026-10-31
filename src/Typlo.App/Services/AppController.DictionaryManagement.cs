using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly string _dictionaryDataDir;

    public async Task<PersonalWordPage> QueryPersonalWordsAsync(PersonalWordQuery query)
        => _remote is null ? await Task.Run(() => _personal.Query(query)) :
            (await _remote.CallAsync(new("personal-query", WordQuery: query))).WordPage ?? throw new IOException("Missing dictionary page");

    public async Task EditPersonalWordAsync(PersonalWord original, PersonalWord replacement, bool exclusions = false)
    {
        if (exclusions) await StopPendingAutomaticWords();
        if (_remote is null) await MutateDictionaryAsync(() => _personal.Edit(original, replacement, exclusions));
        else await _remote.CallAsync(new("personal-edit", OriginalWord: original, ReplacementWord: replacement, Flag: exclusions));
    }

    public async Task<PersonalImportResult> AddPersonalWordsAsync(PersonalWord[] words, bool exclusions = false)
    {
        if (exclusions) await StopPendingAutomaticWords();
        return _remote is null ? await MutateDictionaryAsync(() => _personal.AddMany(words, exclusions)) :
            (await _remote.CallAsync(new("personal-add", Words: words, Flag: exclusions))).ImportResult ?? throw new IOException("Missing import result");
    }

    public async Task<PersonalImportResult> ImportPersonalWordsAsync(string file, bool exclusions = false)
    {
        if (exclusions) await StopPendingAutomaticWords();
        return _remote is null ? await MutateDictionaryAsync(() => _personal.Import(file, exclusions)) :
            (await _remote.CallAsync(new("personal-import", Text: file, Flag: exclusions), 120_000)).ImportResult ?? throw new IOException("Missing import result");
    }

    public async Task SetExcludedWordAsync(string word, bool remove = false)
    {
        if (_remote is not null) await _remote.CallAsync(new("excluded-word", Text: word, Flag: remove));
        else { await StopPendingAutomaticWords(); await MutateDictionaryAsync(() => _personal.SetExclusion(word, remove)); }
    }

    public async Task ExportPersonalWordsAsync(string file, bool exclusions = false)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(Path.Combine(_dictionaryDataDir, "settings.json")), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate file for the dictionary export.");
        if (_remote is null) await Task.Run(() => _personal.Export(file, exclusions));
        else await _remote.CallAsync(new("personal-export", Text: file, Flag: exclusions), 120_000);
    }
}
