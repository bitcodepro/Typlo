using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly PersonalLexicon _personal;
    public event Action<string, string?>? PersonalWordRequested;

    public async Task SetPersonalWordAsync(string word, string language, bool remove = false)
    {
        if (_remote is not null) await _remote.CallAsync(new("personal-word", Text: word, Args: [language], Flag: remove));
        else await Task.Run(() => _personal.Set(word, language, remove));
    }

    internal async Task CapturePersonalWordAsync()
    {
        var window = _platform.ForegroundWindow;
        var input = Interlocked.Read(ref _typingVersion);
        var focus = _platform.ForegroundChangeVersion;
        await StopPendingAutomaticWords();
        if (!await EnterManualTextOperationAsync(window)) return;
        Platform.ClipboardSnapshot? snapshot = null;
        string? selected = null, language = null;
        try
        {
            bool Valid() => Volatile.Read(ref _disposed) == 0 && IsTargetActive(window) && !ForegroundChanged(focus) && input == Interlocked.Read(ref _typingVersion);
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Valid()) return;
            using var capture = _platform.SuspendCapture();
            snapshot = CaptureClipboard();
            selected = await CopySelectionAsync(window, Valid, allowRevalidation: false);
            if (!Valid()) { selected = null; return; }
            language = FindLayout(_platform.GetCurrentLayoutId(window))?.LanguageCode.Split('-', '_')[0].ToLowerInvariant();
        }
        catch (Exception e) { Log.Error("Read personal dictionary selection failed", e); }
        finally
        {
            if (snapshot is not null) { using var capture = _platform.SuspendCapture(); RestoreIfUnchanged(snapshot); }
            ExitManualTextOperation();
        }
        // The source field and clipboard are untouched before another UI is activated.
        PersonalWordRequested?.Invoke(selected is { Length: <= 128 } ? selected.Trim() : "", language);
    }
}
