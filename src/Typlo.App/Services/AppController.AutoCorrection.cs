using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly LanguageLexicon _lexicon = new(Path.Combine(AppContext.BaseDirectory, "language-data", "words.sqlite3"));
    private readonly Lock _automaticWordLock = new();
    private sealed record AutomaticWord(RecentTyping.Run Run, long Version, long Generation, long Started);
    private AutomaticWord? _pendingAutomaticWord;
    private bool _automaticWordWorker;
    private Task _automaticWordTask = Task.CompletedTask;
    private long _automaticWordGeneration;
    private volatile bool _dictionaryFailed;

    public bool DictionaryAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "language-data", "words.sqlite3"));
    internal bool DictionaryIsOpen => _lexicon.IsOpen;
    internal void OpenDictionaryForDiagnostic() => _lexicon.Lookup("hello");
    private int _updating;

    internal async Task ReplaceUpdateFilesAsync(Action replace)
    {
        if (Interlocked.Exchange(ref _updating, 1) != 0) throw new InvalidOperationException("An update is already being installed");
        var editing = _editingSettings;
        try
        {
            _platform.UnregisterAllHotkeys();
            await StopPendingAutomaticWords();
            if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("A text operation is still running. Try the update again.");
            try { _lexicon.Close(); replace(); }
            finally { _convertGate.Release(); }
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
            if (Volatile.Read(ref _disposed) == 0) { if (editing) SuspendHotkeys(); else RegisterHotkeys(); }
        }
    }
    internal Task AutoCorrectionIdleAsync() { lock (_automaticWordLock) return _automaticWordTask; }

    private void QueueAutomaticWord(nint window, long version)
    {
        if (!Settings.AutoCorrectEnabled || !_platform.SupportsAutomaticWordCorrection || _editingSettings || Volatile.Read(ref _updating) != 0 || _capturingScreen ||
            _remote is not null || Volatile.Read(ref _disposed) != 0 || _dictionaryFailed) return;
        var run = _recentTyping.Capture(window);
        if (run is null || run.Keys.Length < 3 || run.Keys[^1] != ' ' || char.IsWhiteSpace(run.Keys[^2])) return;
        lock (_automaticWordLock)
        {
            _pendingAutomaticWord = new(run, version, _automaticWordGeneration, System.Diagnostics.Stopwatch.GetTimestamp());
            if (_automaticWordWorker) return;
            _automaticWordWorker = true;
            _automaticWordTask = Task.Run(DrainAutomaticWordsAsync);
        }
    }

    private Task StopPendingAutomaticWords()
    {
        lock (_automaticWordLock)
        {
            _automaticWordGeneration++; _pendingAutomaticWord = null;
            return _automaticWordTask;
        }
    }

    private bool AutomaticWordIsCurrent(AutomaticWord word) =>
        word.Generation == Interlocked.Read(ref _automaticWordGeneration) && AutomaticWordContextIsCurrent(word);

    private bool AutomaticWordContextIsCurrent(AutomaticWord word) => Volatile.Read(ref _disposed) == 0 &&
        Settings.AutoCorrectEnabled && !_editingSettings && Volatile.Read(ref _updating) == 0 && !_capturingScreen &&
        Interlocked.Read(ref _typingVersion) == word.Version && _platform.ForegroundWindow == word.Run.Window &&
        (word.Run.Element == 0 || _platform.FocusedInputElement == word.Run.Element);

    private async Task DrainAutomaticWordsAsync()
    {
        while (true)
        {
            AutomaticWord word;
            lock (_automaticWordLock)
            {
                if (_pendingAutomaticWord is not { } pending) { _automaticWordWorker = false; return; }
                word = pending; _pendingAutomaticWord = null;
            }
            try
            {
                // Let the target receive Space and skip words when the user continues typing.
                await Task.Delay(150);
                if (!AutomaticWordIsCurrent(word)) continue;
                // Own settings/history/search fields and unidentified targets are excluded.
                var process = _platform.GetApplicationProcessName(word.Run.Window);
                if (string.IsNullOrEmpty(process) || Settings.ExcludedApps.Any(p =>
                    string.Equals(p, process, StringComparison.OrdinalIgnoreCase))) continue;
                var source = FindLayout(_platform.GetCurrentLayoutId(word.Run.Window));
                if (source is null || !source.SupportsConversion) continue;
                var allowed = ApplicationCycleFor(word.Run.Window)?.Layouts ?? LayoutCatalog.Cycle(Settings).ToArray();
                var target = new LayoutLanguageDetector(_lexicon, _personal).Suggest(word.Run.Keys[..^1], source, allowed);
                if (target is null || !AutomaticWordIsCurrent(word)) continue;
                await TransformSelectionCoreAsync((text, matched) =>
                {
                    if (matched is null || word.Run.MatchSource(text, [source]) is null) return (text, null);
                    return (LayoutConverter.Convert(text, source, target, forceSource: true), target);
                }, automaticWord: word);
            }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException or UnauthorizedAccessException)
            {
                _dictionaryFailed = true;
                Log.Error("Automatic correction vocabulary unavailable; manual conversion remains available", e);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
            catch (Exception e) { Log.Error("Automatic correction failed", e); }
        }
    }
}
