using System.Globalization;
using Typlo.App.Platform;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private enum ClipboardPasteState { Confirmed, Unchanged, Unknown }
    private bool NeedsPasteVerification(nint window) => _platform.SupportsTextRangeBatch &&
        ApplicationLayoutRule.ProcessKey(_platform.GetApplicationProcessName(window)).ToLowerInvariant() is "ms-teams" or "teams";
    private bool PreferUnicodeReplacement(nint window, string text) => _platform.SupportsUnicodeTextReplacement &&
        text.Length <= 4096 && text.IndexOfAny(['\n', '\r', '\t']) < 0 && NeedsPasteVerification(window);

    // Some rich editors append a paragraph terminator to a copied word. Strip only one
    // virtual terminator, only in Teams, and only when the entire remaining range matches
    // the known input/result. Explicit multiline selections keep their actual line breaks.
    private string? MatchKnownEditorCopy(nint window, string text, Func<string, bool> expected)
    {
        if (expected(text)) return text;
        if (!NeedsPasteVerification(window)) return null;
        var trimmed = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] :
            text.EndsWith('\n') ? text[..^1] : null;
        if (trimmed is not null && expected(trimmed)) return trimmed;
        Log.Info($"Teams copy range mismatch: chars={text.Length}, whitespace={text.All(char.IsWhiteSpace)}, paragraph={trimmed is not null}");
        return null;
    }
    private void CanonicalizeVerifiedCopy(nint window, string raw, string canonical)
    {
        if (_verifiedSelection is { } verified && verified.Window == window && SameSelection(verified.Text, raw))
            _verifiedSelection = (window, canonical, verified.FocusVersion);
    }
    private static int RangeSteps(string text) => StringInfo.ParseCombiningCharacters(text.Replace("\r\n", "\n")).Length;

    private async Task<string?> CopyCollapsedRangeAsync(nint window, int count, Func<bool> valid, Func<string, bool>? expected = null,
        Func<bool>? canRestoreCancelledCopy = null)
    {
        if (NeedsPasteVerification(window) && _platform.SupportsProtectedCopy)
        {
            if (count is < 1 or > 2000) return null;
            using var guard = _platform.ProtectCopySelection(window, () => valid() || canRestoreCancelledCopy?.Invoke() == true);
            if (await _platform.SelectPreviousCharactersAsync(window, count, valid) != SelectionStatus.Completed)
                return null;
            return await CopySelectionAsync(window, valid, allowRevalidation: false, expectedCopy: expected,
                canRestoreCancelledCopy: canRestoreCancelledCopy);
        }
        return await CopySelectionAsync(window, valid, allowRevalidation: false,
            sendCopy: () => _platform.TrySendTextRangeBatch(window, count, paste: false, valid), expectedCopy: expected,
            canRestoreCancelledCopy: canRestoreCancelledCopy);
    }

    private async Task<ClipboardPasteState> VerifyPastedRangeAsync(nint window, string original, string result, Func<bool> valid,
        bool probeRemainingSelection = true)
    {
        if (SelectionFocusChanged(window, original)) return ClipboardPasteState.Unknown;
        var focus = _platform.ForegroundChangeVersion;
        bool Current() => valid() && IsTargetActive(window) && !ForegroundChanged(focus);
        if (!Current()) return ClipboardPasteState.Unknown;
        if (probeRemainingSelection && NeedsPasteVerification(window))
        {
            // Teams can ignore a paste and leave the original selection intact. Re-selecting
            // from that selection would extend it into older text. Probe it before moving the caret.
            var selected = await CopySelectionAsync(window, Current, allowRevalidation: false,
                copyTimeout: TimeSpan.FromMilliseconds(120));
            if (selected is not null && MatchKnownEditorCopy(window, selected,
                candidate => SameSelection(candidate, result) || SameSelection(candidate, original)) is { } canonical)
            { CanonicalizeVerifiedCopy(window, selected, canonical); selected = canonical; }
            if (selected is not null && Current() && (SameSelection(selected, result) || SameSelection(selected, original)))
            {
                Collapse(window);
                return SameSelection(selected, result) ? ClipboardPasteState.Confirmed : ClipboardPasteState.Unchanged;
            }
        }
        var copied = await CopyCollapsedRangeAsync(window, RangeSteps(result), Current,
            candidate => SameSelection(candidate, result) || RangeSteps(result) == RangeSteps(original) && SameSelection(candidate, original));
        if (copied is not null && Current() && SameSelection(copied, result)) return ClipboardPasteState.Confirmed;
        if (copied is not null && Current() && RangeSteps(result) == RangeSteps(original) && SameSelection(copied, original))
            return ClipboardPasteState.Unchanged;
        return ClipboardPasteState.Unknown;
    }

    private async Task TransformAutomaticWordWithBatchesAsync(AutomaticWord word,
        Func<string, KeyboardLayout?, (string Text, KeyboardLayout? SwitchTo)> transform, Func<bool> noInput)
    {
        if (!_platform.SupportsTextRangeBatch) return;
        var window = word.Run.Window;
        var focus = _platform.ForegroundChangeVersion;
        var sent = false;
        // Cancellation wins until Paste is sent. After that, only finish verification/cleanup:
        // the waiting manual command must receive a verified result, not lose its range record.
        bool ContextCurrent() => noInput() && AutomaticWordContextIsCurrent(word) && !ForegroundChanged(focus);
        bool Current() => ContextCurrent() && (sent || word.Generation == Interlocked.Read(ref _automaticWordGeneration));
        bool CanRestoreCancelledCopy() => ContextCurrent() && word.Generation != Interlocked.Read(ref _automaticWordGeneration);
        if (!Current()) return;
        using var suspended = _platform.SuspendCapture();
        var snapshot = CaptureClipboard();
        var unchanged = false;
        try
        {
            // Opaque asynchronous editors retain a protected selection until Copy finishes;
            // other editors copy and collapse in one native input sequence.
            var active = FindLayout(_platform.GetCurrentLayoutId(window));
            bool Expected(string copied) => word.Run.MatchSource(copied, SourceLayouts(active)) is not null;
            var text = await CopyCollapsedRangeAsync(window, word.Run.Keys.Length, Current, Expected, CanRestoreCancelledCopy);
            var source = text is null ? null : word.Run.MatchSource(text, SourceLayouts(active));
            if (text is null || source is null || !Current()) return;
            var (result, target) = transform(text, source);
            if (target is null) return;
            if (result != text)
            {
                if (focus is null)
                {
                    var fresh = await CopyCollapsedRangeAsync(window, word.Run.Keys.Length, Current, Expected, CanRestoreCancelledCopy);
                    if (fresh is null || !SameSelection(fresh, text)) return;
                }
                var unicode = PreferUnicodeReplacement(window, result);
                if (!unicode) SetOwnText(result);
                EnsureClipboardIsOurs();
                if (!Current()) return;
                var accepted = unicode ? _platform.TryReplacePreviousText(window, word.Run.Keys.Length, result, Current) :
                    _platform.TrySendTextRangeBatch(window, word.Run.Keys.Length, paste: true, Current);
                if (!accepted) return;
                sent = true;
                Log.Info($"Automatic correction: replacement={(unicode ? "unicode" : "clipboard")}; since-space={System.Diagnostics.Stopwatch.GetElapsedTime(word.Started).TotalMilliseconds:F0}ms");
                await Task.Delay(unicode ? 40 : 300);
                var state = await VerifyPastedRangeAsync(window, text, result, Current, probeRemainingSelection: !unicode);
                unchanged = state == ClipboardPasteState.Unchanged;
                if (state != ClipboardPasteState.Confirmed)
                {
                    LastTransform = null;
                    Log.Info("Automatic correction: paste=" + state);
                    return;
                }
            }
            if (!Current()) return;
            _recentTyping.Consume(word.Version);
            LastTransform = new(window, text, result, DateTime.UtcNow, target);
            if (Settings.SwitchSystemLayout && Current()) _platform.SwitchKeyboardLayout(window, target);
        }
        finally
        {
            if (sent && !unchanged) _recentTyping.Consume(word.Version);
            RestoreIfUnchanged(snapshot);
        }
    }
}
