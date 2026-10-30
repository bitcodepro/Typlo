using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    // This strategy owns clipboard/selection state. It is entered only after a read-only native
    // decline; native cancellation or uncertain writes never run it for the same command.
    private async Task TransformWithSelectionAsync(nint window, nint directElement, long inputVersion,
        Func<bool> noInputSince, Func<string, KeyboardLayout?, (string Text, KeyboardLayout? SwitchTo)> transform,
        KeyboardLayout? switchToIfNothingSelected, TransformTiming timing)
    {
        // A native fallback stays bound to its original field. Opaque editors retain the existing
        // window/input/clipboard guards and are never bound to incidental browser focus HWNDs.
        bool StillValid() => noInputSince() && (directElement == 0 || !IsTargetActive(window) ||
            _platform.FocusedInputElement == directElement);
        if (!await _platform.WaitForTargetAsync(window, StillValid)) return;
        var run = _recentTyping.Capture(window);
        var last = CaptureLastTransform(window);
        using var _ = _platform.SuspendCapture();
        var snapshot = CaptureClipboard();
        string? ownedSelection = null;
        var pasteSent = false;
        var preserveRecentInput = false;
        IDisposable? protectedSelection = null;
        try
        {
            if (Settings.ConvertLastWord && (run is not null || last is not null) &&
                NeedsPasteVerification(window) && _platform.SupportsProtectedCopy)
                protectedSelection = ProtectOwnedSelection(window, StillValid);
            timing.Mark("clipboard snapshot");
            string? text;
            KeyboardLayout? activeLayout = FindLayout(_platform.GetCurrentLayoutId(window));
            KeyboardLayout? source = activeLayout;
            var automaticSelection = false;
            var reusingLast = false;
            if (Settings.ConvertLastWord && run is not null)
            {
                // A user-made selection resets the run through its click/navigation/shortcut event.
                // With a live run, select its exact length before Copy: some editors copy an entire
                // unselected line without setting any marker, which must never enlarge this range.
                text = await SelectRecentInputAsync(run, StillValid, activeLayout);
                if (text is null) return; // a failed range check must not become a layout-only switch
                if (text is not null)
                {
                    source = run.MatchSource(text, SourceLayouts(activeLayout));
                    automaticSelection = true;
                    ownedSelection = text;
                }
            }
            else if (Settings.ConvertLastWord && last is not null)
            {
                // The previous replacement is separate from newly typed input. Keep its exact range
                // available for another case/layout/transliteration command, but copy and verify it
                // again before every use. User-made selections invalidate this record through the hook.
                bool RangeStillValid() => StillValid() && ReferenceEquals(_lastTransform, last);
                text = await SelectTransformResultAsync(last, RangeStillValid);
                if (text is null)
                {
                    if (ReferenceEquals(_lastTransform, last)) LastTransform = null;
                    return; // failed verification of a known range must not become a layout-only switch
                }
                source = last.ResultLayout;
                automaticSelection = true;
                ownedSelection = text;
                reusingLast = true;
            }
            else text = await CopySelectionAsync(window, StillValid);
            timing.Mark("selection/copy");
            if (!StillValid()) return;
            if (string.IsNullOrEmpty(text))
            {
                Log.Info("Text tools: nothing selected");
                if (switchToIfNothingSelected is not null && Settings.SwitchSystemLayout)
                    if (StillValid() && IsTargetActive(window)) _platform.SwitchKeyboardLayout(window, switchToIfNothingSelected);
                return;
            }

            // From here on Typlo sends no keys of its own until Ctrl+V: any input means the user changed the selection
            // or moved the caret, so the result must not be pasted (or recorded for Undo).
            var (result, switchTo) = transform(text, source);
            timing.Mark("conversion");
            if (result == text && (_verifiedSelection?.FocusVersion is null || SelectionFocusChanged(window, text)))
            {
                var current = await CopySelectionAsync(window, StillValid, allowRevalidation: false);
                if (current is null || !SameSelection(current, text)) return;
            }
            if (result == text && automaticSelection && StillValid() && IsTargetActive(window))
            {
                Collapse(window);
                ownedSelection = null;
                if (switchTo is not null)
                {
                    // Two cycle members can produce identical text (e.g. a common RU/UA word).
                    // Advance the logical source even when no paste or system-layout switch is needed.
                    LastTransform = reusingLast && last is not null ? last with { ResultLayout = switchTo } :
                        new TransformRecord(window, text, text, DateTime.UtcNow, switchTo);
                    _recentTyping.Consume(inputVersion);
                }
            }
            if (result != text)
            {
                if (!await PasteTextAsync(result, window, StillValid, text, () =>
                {
                    pasteSent = true; ownedSelection = null;
                    if (!NeedsPasteVerification(window)) _recentTyping.Consume(inputVersion);
                    timing.Mark("replacement sent");
                })) return;
                if (NeedsPasteVerification(window))
                {
                    var state = await VerifyPastedRangeAsync(window, text, result, StillValid,
                        probeRemainingSelection: !PreferUnicodeReplacement(window, result));
                    if (state != ClipboardPasteState.Confirmed)
                    {
                        preserveRecentInput = state == ClipboardPasteState.Unchanged;
                        LastTransform = preserveRecentInput && reusingLast ? last : null;
                        Log.Info("Text replacement: paste=" + state);
                        return;
                    }
                    _recentTyping.Consume(inputVersion);
                }
                // Input during the paste's settling wait may have moved the caret: then the result cannot be located.
                LastTransform = StillValid() && IsTargetActive(window) ? new TransformRecord(window, text, result, DateTime.UtcNow,
                    switchTo ?? _converter.DetectLayout(result, source) ?? source) : null;
            }
            if (switchTo is not null && Settings.SwitchSystemLayout && StillValid() && IsTargetActive(window) &&
                (result != text || !SelectionFocusChanged(window, text)))
                _platform.SwitchKeyboardLayout(window, switchTo);
        }
        finally
        {
            try
            {
                // An aborted paste used to leave our verified selection highlighted. The next
                // command extended it into older text and failed again. Collapse only our still-
                // verified range, never a selection changed by input or a foreground transition.
                if (protectedSelection is null && ownedSelection is not null && StillValid() && IsTargetActive(window) &&
                    !SelectionFocusChanged(window, ownedSelection)) Collapse(window);
                if (pasteSent && !preserveRecentInput) _recentTyping.Consume(inputVersion);
            }
            finally
            {
                try { protectedSelection?.Dispose(); }
                finally { RestoreIfUnchanged(snapshot); }
            }
        }
    }
}
