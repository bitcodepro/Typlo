using Typlo.App.Platform;
using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private int _disposed;

    // A notification can already be foreground when the hotkey arrives. Only retained input or a
    // verified previous replacement may bind us to a background editor; never search other windows.
    private nint PinDirectTarget(ref nint window)
    {
        if (!_platform.SupportsDirectTextEditing) return 0;
        var element = _platform.FocusedInputElement;
        if (!Settings.ConvertLastWord) return element;
        var run = _recentTyping.Capture(_recentTyping.Window);
        if (run is { Element: not 0 })
        {
            if (run.Window != window) { window = run.Window; return run.Element; }
            if (run.Element != element) _recentTyping.Consume(Interlocked.Read(ref _typingVersion));
        }
        if (_lastTransform is { Direct: { } direct } last)
        {
            if (last.DirectVersion != Interlocked.Read(ref _typingVersion)) { LastTransform = null; return element; }
            if (window != last.Window) { window = last.Window; return direct.Element; }
            if (element != direct.Element) LastTransform = null;
        }
        return element;
    }

    // False is a capability decline before any mutation. All other outcomes are terminal for this
    // command: especially a timed-out write must never be followed by a clipboard paste.
    private async Task<bool> TryDirectTransformAsync(nint window, nint element, long version, Func<bool> valid,
        Func<string, KeyboardLayout?, (string Text, KeyboardLayout? SwitchTo)> transform, KeyboardLayout? emptyLayout)
    {
        if (!_platform.SupportsDirectTextEditing) return false;
        var last = CaptureLastTransform(window);
        if (Settings.ConvertLastWord && last is { Direct: null }) return false; // retained legacy range
        var capture = await _platform.CaptureDirectTextAsync(window, element, valid);
        if (capture.Status == DirectCaptureStatus.Unsupported) return false;
        if (!valid() || capture.Status != DirectCaptureStatus.Ready || capture.Snapshot is not { } snapshot) return true;
        if (snapshot.Window != window || snapshot.Element != element) return true;
        var active = FindLayout(_platform.GetCurrentLayoutId(window));
        KeyboardLayout? source = active;
        var start = snapshot.SelectionStart;
        var length = snapshot.SelectionEnd - start;
        var run = _recentTyping.Capture(window);
        var automatic = false;
        var reusing = false;
        if (Settings.ConvertLastWord && run is not null)
        {
            automatic = true;
            length = run.Keys.Length;
            if (run.Element != element || snapshot.SelectionStart != snapshot.SelectionEnd || start < length) return true;
            start -= length;
            source = run.MatchSource(snapshot.Text.Substring(start, length), SourceLayouts(active));
            if (source is null) return true;
        }
        else if (Settings.ConvertLastWord && last?.Direct is { } previous)
        {
            if (!previous.SameContext(snapshot)) { LastTransform = null; return true; }
            start = last.DirectStart;
            length = last.Result.Length;
            source = last.ResultLayout;
            automatic = reusing = true;
        }
        if (start < 0 || length < 0 || start > snapshot.Text.Length - length) return true;
        if (length == 0)
        {
            if (emptyLayout is not null && Settings.SwitchSystemLayout && valid() && IsTargetActive(window))
                _platform.SwitchKeyboardLayout(window, emptyLayout);
            return true;
        }
        var text = snapshot.Text.Substring(start, length);
        var (result, switchTo) = transform(text, source);
        if (!valid()) return true;
        if (result == text)
        {
            // No selection mutation for an identical result. Preserve the exact range for cycles.
            var refreshed = await snapshot.RefreshAsync(valid);
            if (refreshed is null || !snapshot.SameContext(refreshed) || !valid()) return true;
            if (automatic && switchTo is not null)
            {
                LastTransform = reusing && last is not null ? last with { ResultLayout = switchTo } :
                    new(window, text, text, DateTime.UtcNow, switchTo, refreshed, start, version);
                _recentTyping.Consume(version);
            }
            if (switchTo is not null && Settings.SwitchSystemLayout && valid())
                _platform.SwitchKeyboardLayout(window, switchTo);
            return true;
        }
        DirectTextEditResult edited;
        try { edited = await snapshot.ReplaceAsync(start, length, result, valid); }
        catch (Exception e)
        {
            // A provider throwing during a write cannot establish that nothing changed.
            Log.Error("Direct text write could not be verified", e);
            edited = new(DirectEditStatus.Uncertain);
        }
        if (edited.Status == DirectEditStatus.Unsupported) return false;
        _recentTyping.Consume(version);
        LastTransform = null;
        if (edited.Status != DirectEditStatus.Applied || edited.After is not { } after || !valid()) return true;
        var expected = snapshot.Text[..start] + result + snapshot.Text[(start + length)..];
        if (after.Window != window || after.Element != element || after.Text != expected ||
            after.SelectionStart != start + result.Length || after.SelectionEnd != after.SelectionStart) return true;
        LastTransform = new(window, text, result, DateTime.UtcNow,
            switchTo ?? _converter.DetectLayout(result, source) ?? source, after, start, version);
        if (switchTo is not null && Settings.SwitchSystemLayout && valid())
            _platform.SwitchKeyboardLayout(window, switchTo);
        return true;
    }

    private async Task UndoDirectAsync(TransformRecord last, Func<bool> valid)
    {
        try
        {
            var snapshot = last.Direct!;
            var current = await snapshot.RefreshAsync(valid);
            if (current is null || !snapshot.SameContext(current) || !valid()) return;
            await current.ReplaceAsync(last.DirectStart, last.Result.Length, last.Original, valid);
        }
        finally
        {
            if (ReferenceEquals(_lastTransform, last)) LastTransform = null;
        }
    }
}
