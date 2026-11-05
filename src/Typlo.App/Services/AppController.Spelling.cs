using System.Diagnostics;
using Typlo.App.Platform;
using Typlo.Core.Languages;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private sealed record SpellingSession(SpellingSelection Selection, nint Window, nint Element, long Started)
    { internal int Canceled; }
    private SpellingSession? _spelling;
    private SpellingSession? _applyingSpelling;
    private long _nextSpelling;
    private nint _spellingForeground;
    public event Action<long>? SpellingRequested;
    private bool CanApplySpelling(nint element) => element != 0 && _platform.SupportsForegroundNotifications && _platform.ForegroundChangeVersion.HasValue;
    private void TrackSpellingForeground(nint window) => Volatile.Write(ref _spellingForeground, window);
    private void ObserveSpellingInput()
    {
        // Called on the input hook: cached handles and atomic flags only, no OS calls or waits.
        if (!_platform.SupportsForegroundNotifications) return;
        var source = Volatile.Read(ref _spellingForeground);
        var session = Volatile.Read(ref _spelling);
        if (session?.Window == source) { Volatile.Write(ref session.Canceled, 1); Interlocked.CompareExchange(ref _spelling, null, session); }
        var applying = Volatile.Read(ref _applyingSpelling);
        if (applying?.Window == source) Volatile.Write(ref applying.Canceled, 1);
    }

    internal async Task CaptureSpellingAsync() => await CaptureSpellingCoreAsync(preview: true);

    private async Task<SpellingSession?> CaptureSpellingCoreAsync(bool preview, Func<bool>? additionalValid = null)
    {
        var window = _platform.ForegroundWindow; var input = Interlocked.Read(ref _typingVersion);
        var focus = _platform.ForegroundChangeVersion; var element = _platform.FocusedInputElement;
        bool Valid() => Volatile.Read(ref _disposed) == 0 && !_editingSettings && Volatile.Read(ref _updating) == 0 &&
            IsTargetActive(window) && !ForegroundChanged(focus) && input == Interlocked.Read(ref _typingVersion) && _platform.FocusedInputElement == element && (additionalValid?.Invoke() ?? true);
        await StopPendingAutomaticWords();
        if (!Valid() || !await EnterManualTextOperationAsync(window, preserveQuickSpelling: !preview)) return null;
        ClipboardSnapshot? snapshot = null; SpellingSession? session = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Valid()) return null;
            using var capture = _platform.SuspendCapture(); snapshot = CaptureClipboard();
            var text = await CopySelectionAsync(window, Valid, allowRevalidation: false);
            if (!Valid()) return null;
            var notice = string.IsNullOrWhiteSpace(text) ? "spell.selection" : text.Length > SpellingText.MaxCharacters ? "spell.limit" : null;
            text = notice is null ? text! : "";
            var ignored = SpellingText.Tokens(text).Select(t => t.Word).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(word => _personal.Lookup(word) != 0 || _personal.IsExcluded(word) || _lexicon.Lookup(word)?.SupplementMask > 0).Take(SpellingText.MaxWords).ToArray();
            var excluded = ignored.Where(_personal.IsExcluded).ToArray();
            session = new(new(Interlocked.Increment(ref _nextSpelling), text, ignored, notice, CanApplySpelling(element), excluded), window, element, Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Log.Info("Spelling selection capture stopped: " + e.GetType().Name); }
        finally
        {
            try { if (snapshot is not null) RestoreIfUnchanged(snapshot); }
            finally { ExitManualTextOperation(); }
        }
        if (session is null || !Valid()) return null;
        Interlocked.Exchange(ref _spelling, session);
        if (!Valid()) { Interlocked.CompareExchange(ref _spelling, null, session); return null; }
        if (preview) SpellingRequested?.Invoke(session.Selection.Id);
        return session;
    }
    public async Task<SpellingSelection?> GetSpellingSelectionAsync(long id)
    {
        if (_remote is not null) return (await _remote.CallAsync(new("spelling-selection", Id: id))).Spelling;
        var session = Volatile.Read(ref _spelling);
        if (session?.Selection.Id != id) return null;
        if (Stopwatch.GetElapsedTime(session.Started) < TimeSpan.FromMinutes(5)) return session.Selection;
        Interlocked.CompareExchange(ref _spelling, null, session); return null;
    }
    public async Task CancelSpellingAsync(long id)
    {
        if (_remote is not null) { await _remote.CallAsync(new("spelling-cancel", Id: id)); return; }
        var session = Volatile.Read(ref _spelling);
        if (session?.Selection.Id == id) { Volatile.Write(ref session.Canceled, 1); Interlocked.CompareExchange(ref _spelling, null, session); }
        var applying = Volatile.Read(ref _applyingSpelling);
        if (applying?.Selection.Id == id) Volatile.Write(ref applying.Canceled, 1);
    }
    public async Task<bool> ApplySpellingAsync(long id, string replacement)
    {
        if (_remote is not null) return (await _remote.CallAsync(new("spelling-apply", Id: id, Text: replacement), 15_000, allowRejected: true)).Ok;
        return await ApplySpellingCoreAsync(id, replacement);
    }
    private async Task<bool> ApplySpellingCoreAsync(long id, string replacement, Func<bool>? additionalValid = null, Func<bool>? afterWriteValid = null, ulong? expectedClipboard = null)
    {
        var session = Volatile.Read(ref _spelling);
        if (session?.Selection.Id != id || !session.Selection.CanApply || !CanApplySpelling(session.Element) || session.Selection.Notice is not null || replacement.Length > SpellingText.MaxResultCharacters ||
            Stopwatch.GetElapsedTime(session.Started) >= TimeSpan.FromMinutes(5) || Volatile.Read(ref _disposed) != 0 ||
            _editingSettings || Volatile.Read(ref _updating) != 0 || _capturingScreen) return false;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _spelling, null, session), session)) return false;
        if (replacement == session.Selection.Text) return true;
        if (Interlocked.CompareExchange(ref _applyingSpelling, session, null) is not null) return false;
        var input = Interlocked.Read(ref _typingVersion);
        var sent = false;
        bool NoInput() => Volatile.Read(ref session.Canceled) == 0 && input == Interlocked.Read(ref _typingVersion) && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _updating) == 0 && ((sent ? afterWriteValid ?? additionalValid : additionalValid)?.Invoke() ?? true);
        ClipboardSnapshot? snapshot = null;
        var entered = false;
        try
        {
            await StopPendingAutomaticWords();
            if (!NoInput() || !await EnterManualTextOperationAsync(session.Window, preserveQuickSpelling: additionalValid is not null)) return false;
            entered = true;
            // Only this explicit Apply action may reactivate the captured source window.
            if (additionalValid is null) _platform.ActivateWindow(session.Window);
            for (var i = 0; i < 25 && NoInput() && !IsTargetActive(session.Window); i++) await Task.Delay(20);
            var focus = _platform.ForegroundChangeVersion;
            bool Valid() => NoInput() && CanApplySpelling(session.Element) && IsTargetActive(session.Window) && !ForegroundChanged(focus) &&
                _platform.FocusedInputElement == session.Element;
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Valid()) return false;
            if (expectedClipboard is { } expected && _platform.ClipboardSequence != expected) return false;
            using var capture = _platform.SuspendCapture(); snapshot = CaptureClipboard();
            if (expectedClipboard is { } afterSnapshot && _platform.ClipboardSequence != afterSnapshot) return false;
            var selected = await CopySelectionAsync(session.Window, Valid, allowRevalidation: false);
            if (!Valid() || selected is null || !string.Equals(selected, session.Selection.Text, StringComparison.Ordinal)) return false;
            if (!await PasteTextAsync(replacement, session.Window, Valid, selected, () => sent = true)) return false;
            if (!Valid()) { LastTransform = null; return false; }
            if (NeedsPasteVerification(session.Window) && await VerifyPastedRangeAsync(session.Window, selected, replacement, Valid,
                probeRemainingSelection: !PreferUnicodeReplacement(session.Window, replacement), maxCharacters: SpellingText.MaxResultCharacters) != ClipboardPasteState.Confirmed) { LastTransform = null; return false; }
            LastTransform = new(session.Window, selected, replacement, DateTime.UtcNow, null);
            _recentTyping.Reset(Interlocked.Read(ref _typingVersion));
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Log.Info("Spelling replacement stopped: " + e.GetType().Name); return false; }
        finally
        {
            try { if (snapshot is not null) RestoreIfUnchanged(snapshot); }
            finally { if (entered) ExitManualTextOperation(); Interlocked.CompareExchange(ref _applyingSpelling, null, session); }
        }
    }
}
