using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed record PersonalWordStatus(bool Personal, bool? Bundled);

public sealed partial class AppController
{
    private readonly PersonalLexicon _personal;
    public event Action<string, string?>? PersonalWordRequested;

    private PersonalWordStatus PersonalWordStatusCore(string word, string language, bool checkBundled = true)
    {
        word = LanguageLexicon.Normalize(word.Trim());
        var index = -1;
        for (var i = 0; i < PersonalLexicon.LanguageCodes.Count; i++) if (PersonalLexicon.LanguageCodes[i] == language) index = i;
        if (!PersonalLexicon.IsWord(word) || index < 0) throw new ArgumentException(Loc.T("dictionary.invalid"));
        var exists = (_personal.Lookup(word) & (1 << index)) != 0;
        if (!checkBundled) return new(exists, null);
        try { return new(exists, _lexicon.Lookup(word)?.Includes(index) == true); }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException or UnauthorizedAccessException)
        { Log.Error("Bundled dictionary duplicate check unavailable", e); return new(exists, null); }
    }

    public async Task<PersonalWordStatus> GetPersonalWordStatusAsync(string word, string language)
        => _remote is null ? await Task.Run(() => PersonalWordStatusCore(word, language)) :
            (await _remote.CallAsync(new("personal-word-status", Text: word, Args: [language]))).PersonalStatus ?? throw new IOException("Missing word status");

    public async Task<PersonalImportResult> AddMissingPersonalWordsAsync(PersonalWord[] words)
    {
        if (_remote is not null) return (await _remote.CallAsync(new("personal-add-missing", Words: words), 120_000)).ImportResult ?? throw new IOException("Missing import result");
        return await MutateDictionaryAsync(() =>
        {
            if (words.Length > PersonalLexicon.MaxImportWords) throw new InvalidDataException("Too many dictionary forms.");
            // Validate the complete batch before its first write. Lookup is indexed and bounded on disk.
            var checkBundled = true;
            var missing = words.Where(entry =>
            {
                if (entry is null || entry.Word is null || entry.Language is null) throw new ArgumentException(Loc.T("dictionary.invalid"));
                PersonalWordSpelling.RequireCorrectScript(entry.Word, entry.Language);
                var status = PersonalWordStatusCore(entry.Word, entry.Language, checkBundled);
                checkBundled &= status.Bundled.HasValue;
                return !status.Personal && status.Bundled != true;
            }).ToArray();
            if (missing.Length == 0) return new PersonalImportResult(0, words.Length);
            var added = _personal.AddMany(missing);
            return new PersonalImportResult(added.Added, words.Length - added.Added);
        });
    }

    public async Task SetPersonalWordAsync(string word, string language, bool remove = false)
    {
        if (_remote is not null) await _remote.CallAsync(new("personal-word", Text: word, Args: [language], Flag: remove));
        else await MutateDictionaryAsync(() => _personal.Set(word, language, remove));
    }

    internal async Task CapturePersonalWordAsync()
    {
        var window = _platform.ForegroundWindow;
        var input = Interlocked.Read(ref _typingVersion);
        var focus = _platform.ForegroundChangeVersion;
        bool Valid() => Volatile.Read(ref _disposed) == 0 && IsTargetActive(window) && !ForegroundChanged(focus) && input == Interlocked.Read(ref _typingVersion);
        await StopPendingAutomaticWords();
        if (!await EnterManualTextOperationAsync(window)) return;
        Platform.ClipboardSnapshot? snapshot = null;
        string? selected = null, language = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Valid()) return;
            using var capture = _platform.SuspendCapture();
            snapshot = CaptureClipboard();
            selected = await CopySelectionAsync(window, Valid, allowRevalidation: false);
            if (!Valid()) { selected = null; return; }
            language = FindLayout(_platform.GetCurrentLayoutId(window))?.LanguageCode.Split('-', '_')[0].ToLowerInvariant();
        }
        catch (SelectionChangedException) { return; }
        catch (Exception e) { Log.Error("Read personal dictionary selection failed", e); return; }
        finally
        {
            if (snapshot is not null) { using var capture = _platform.SuspendCapture(); RestoreIfUnchanged(snapshot); }
            ExitManualTextOperation();
        }
        // The source field and clipboard are untouched before another UI is activated.
        if (!Valid()) return;
        PersonalWordRequested?.Invoke(selected is { Length: <= 128 } ? selected.Trim() : "", language);
    }
}
