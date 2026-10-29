using System.Globalization;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private enum ClipboardPasteState { Confirmed, Unchanged, Unknown }
    private bool NeedsPasteVerification(nint window) => _platform.SupportsTextRangeBatch &&
        ApplicationLayoutRule.ProcessKey(_platform.GetApplicationProcessName(window)).ToLowerInvariant() is "ms-teams" or "teams";
    private static int RangeSteps(string text) => StringInfo.ParseCombiningCharacters(text.Replace("\r\n", "\n")).Length;

    private Task<string?> CopyCollapsedRangeAsync(nint window, int count, Func<bool> valid, Func<string, bool>? expected = null,
        Func<bool>? canRestoreCancelledCopy = null) =>
        CopySelectionAsync(window, valid, allowRevalidation: false,
            sendCopy: () => _platform.TrySendTextRangeBatch(window, count, paste: false, valid), expectedCopy: expected,
            canRestoreCancelledCopy: canRestoreCancelledCopy);

    private async Task<ClipboardPasteState> VerifyPastedRangeAsync(nint window, string original, string result, Func<bool> valid)
    {
        if (SelectionFocusChanged(window, original)) return ClipboardPasteState.Unknown;
        var focus = _platform.ForegroundChangeVersion;
        bool Current() => valid() && IsTargetActive(window) && !ForegroundChanged(focus);
        if (!Current()) return ClipboardPasteState.Unknown;
        if (NeedsPasteVerification(window))
        {
            // Teams can ignore a paste and leave the original selection intact. Re-selecting
            // from that selection would extend it into older text. Probe it before moving the caret.
            var selected = await CopySelectionAsync(window, Current, allowRevalidation: false,
                copyTimeout: TimeSpan.FromMilliseconds(120));
            if (selected is not null && Current() && (SameSelection(selected, result) || SameSelection(selected, original)))
            {
                Collapse(window);
                return SameSelection(selected, result) ? ClipboardPasteState.Confirmed : ClipboardPasteState.Unchanged;
            }
        }
        var copied = await CopyCollapsedRangeAsync(window, RangeSteps(result), Current);
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
            // Copy and collapse are one input sequence. No selection remains while waiting for
            // clipboard/OLE: continuing typing must append to the word, never overwrite it.
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
                SetOwnText(result);
                EnsureClipboardIsOurs();
                if (!Current() || !_platform.TrySendTextRangeBatch(window, word.Run.Keys.Length, paste: true, Current)) return;
                sent = true;
                await Task.Delay(300); // keep the payload available until the editor has read it
                var state = await VerifyPastedRangeAsync(window, text, result, Current);
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
