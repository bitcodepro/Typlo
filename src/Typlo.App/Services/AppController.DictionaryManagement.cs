using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly string _dictionaryDataDir;

    public async Task<PersonalWordPage> QueryPersonalWordsAsync(PersonalWordQuery query)
        => _remote is null ? await Task.Run(() => _personal.Query(query)) :
            (await _remote.CallAsync(new("personal-query", WordQuery: query))).WordPage ?? throw new IOException("Missing dictionary page");

    public async Task EditPersonalWordAsync(PersonalWord original, PersonalWord replacement)
    {
        if (_remote is null) await Task.Run(() => _personal.Edit(original, replacement));
        else await _remote.CallAsync(new("personal-edit", OriginalWord: original, ReplacementWord: replacement));
    }

    public async Task<PersonalImportResult> AddPersonalWordsAsync(PersonalWord[] words)
        => _remote is null ? await Task.Run(() => _personal.AddMany(words)) :
            (await _remote.CallAsync(new("personal-add", Words: words))).ImportResult ?? throw new IOException("Missing import result");

    public async Task<PersonalImportResult> ImportPersonalWordsAsync(string file)
        => _remote is null ? await Task.Run(() => _personal.Import(file)) :
            (await _remote.CallAsync(new("personal-import", Text: file), 120_000)).ImportResult ?? throw new IOException("Missing import result");

    public async Task ExportPersonalWordsAsync(string file)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(Path.Combine(_dictionaryDataDir, "settings.json")), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate file for the dictionary export.");
        if (_remote is null) await Task.Run(() => _personal.Export(file));
        else await _remote.CallAsync(new("personal-export", Text: file), 120_000);
    }
}
