using Typlo.App.Platform;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.Core.Storage;
using Typlo.Core.Text;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Services;

/// <summary>Wires hotkeys and clipboard events from the platform to conversion and history.</summary>
public sealed class AppController : IDisposable
{
    private const int ToggleId = 1;
    private const int ClipboardId = 2;
    private const int CaseId = 3;
    private const int TranslitId = 4;
    private const int UndoId = 5;
    private const int LayoutIdBase = 100;

    private readonly IPlatform _platform;
    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _convertGate = new(1, 1);
    private LayoutConverter _converter = null!;
    private readonly Dictionary<int, KeyboardLayout> _layoutHotkeys = [];

    public AppSettings Settings { get; private set; }
    public ClipStore Store { get; }
    public IPlatform Platform => _platform;

    /// <summary>Raised on a background thread; the argument is the window that was active before.</summary>
    public event Action<nint>? ShowHistoryRequested;

    /// <summary>Hotkeys that could not be registered (taken by another app), as text for the UI.</summary>
    public IReadOnlyList<string> FailedHotkeys { get; private set; } = [];

    /// <summary>Self-test/screenshot run: never touches system-wide settings such as autostart.</summary>
    private readonly bool _diagnostic;

    public AppController(IPlatform platform, AppPaths paths, IKeyProtector keyProtector, bool diagnostic = false)
    {
        _diagnostic = diagnostic;
        _platform = platform;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        Settings = _settingsStore.Load();
        Loc.SetLanguage(Settings.UiLanguage);
        Store = new ClipStore(paths.HistoryDb, ClipCrypto.LoadOrCreate(paths.KeyFile, keyProtector), Limits(Settings));
        BuildConverter();
        ApplyKeepAwake();
        _platform.HotkeyPressed += OnHotkey;
        _platform.KeyTyped += OnKeyTyped;
        ApplySnippets();
        _platform.ClipboardCaptured += OnClipboardCaptured;
        RegisterHotkeys();
    }

    private static StoreLimits Limits(AppSettings s) =>
        new(s.MaxItems, s.MaxTotalBytes, s.MaxImageBytes, s.MaxTextBytes);

    private void BuildConverter()
    {
        var ordered = Settings.Layouts.Select(KeyboardLayouts.Find).OfType<KeyboardLayout>()
            .Concat(KeyboardLayouts.All).Distinct().ToList();
        _converter = new LayoutConverter(ordered);
    }

    public void ApplySettings(AppSettings settings)
    {
        settings.Normalize();
        Settings = settings;
        Loc.SetLanguage(settings.UiLanguage);
        _settingsStore.Save(settings);
        Store.UpdateLimits(Limits(settings));
        BuildConverter();
        if (!_diagnostic)
        {
            try { _platform.AutostartEnabled = settings.StartWithSystem; }
            catch (Exception e) { Log.Error("Autostart update failed", e); }
        }
        ApplyKeepAwake();
        ApplySnippets();
        RegisterHotkeys();
    }

    /// <summary>Raised when settings change (e.g. to refresh the tray menu); may be on any thread.</summary>
    public event Action? SettingsChanged;

    private Timer? _activityTimer;

    private void ApplyKeepAwake()
    {
        if (!_platform.SetKeepAwake(Settings.PreventScreenLock)) Log.Error("Keep-awake request failed");
        // Keeping the display on does not stop Teams and similar apps from showing "Away": they look at the
        // input idle time, so the platform reports activity once the user has been idle for a few minutes.
        _activityTimer?.Dispose();
        _activityTimer = Settings.PreventScreenLock
            ? new Timer(_ =>
            {
                try { _platform.SignalUserActivity(); }
                catch (Exception e) { Log.Error("Activity signal failed", e); }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
            : null;
        SettingsChanged?.Invoke();
    }

    public void SetPreventScreenLock(bool enabled)
    {
        var s = Settings.Clone();
        s.PreventScreenLock = enabled;
        ApplySettings(s);
    }

    /// <summary>Settings window records shortcuts, so live hotkeys must not fire meanwhile.</summary>
    public void SuspendHotkeys() => _platform.UnregisterAllHotkeys();

    public void RegisterHotkeys()
    {
        _platform.UnregisterAllHotkeys();
        _layoutHotkeys.Clear();
        var failed = new List<string>();

        void Register(int id, string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var hk = Hotkey.TryParse(text);
            if (hk is not { IsUsable: true } || !_platform.RegisterHotkey(id, hk))
            {
                failed.Add($"{label}: {text}");
                Log.Error($"Hotkey not registered: {label} {text}");
            }
        }

        Register(ToggleId, Settings.ToggleHotkey, Loc.T("hk.toggle"));
        Register(ClipboardId, Settings.ClipboardHotkey, Loc.T("hk.history"));
        Register(CaseId, Settings.CaseHotkey, Loc.T("hk.case"));
        Register(TranslitId, Settings.TranslitHotkey, Loc.T("hk.translit"));
        Register(UndoId, Settings.UndoHotkey, Loc.T("hk.undo"));
        var next = LayoutIdBase;
        foreach (var lh in Settings.LayoutHotkeys)
        {
            if (KeyboardLayouts.Find(lh.LayoutId) is not { } layout) continue;
            _layoutHotkeys[next] = layout;
            Register(next++, lh.Hotkey, Loc.T("hk.to", Loc.T("layout." + layout.Id)));
        }
        FailedHotkeys = failed;
    }

    private void OnHotkey(int id)
    {
        try
        {
            if (id == ClipboardId) ShowHistoryRequested?.Invoke(_platform.ForegroundWindow);
            else if (id == ToggleId) _ = ConvertSelectionAsync(null);
            else if (id == CaseId) _ = ChangeCaseAsync();
            else if (id == TranslitId) _ = TransliterateAsync();
            else if (id == UndoId) _ = UndoLastTransformAsync();
            else if (_layoutHotkeys.TryGetValue(id, out var layout)) _ = ConvertSelectionAsync(layout);
        }
        catch (Exception e) { Log.Error("Hotkey handler failed", e); }
    }

    /// <summary>Copies the selection, converts it to <paramref name="target"/> (null = toggle) and pastes it back.</summary>
    public Task ConvertSelectionAsync(KeyboardLayout? target) => TransformSelectionAsync(text =>
    {
        var result = target is null ? _converter.Toggle(text) : _converter.ConvertTo(text, target);
        return (result.Changed ? result.Text : text, result.Target);
    }, switchToIfNothingSelected: target);

    /// <summary>Cycles the case of the selection (lower → Title → UPPER).</summary>
    public Task ChangeCaseAsync() => TransformSelectionAsync(text => (CaseConverter.Cycle(text), null));

    /// <summary>Transliterates the selection: «Привет» ⇄ "Privet".</summary>
    public Task TransliterateAsync() => TransformSelectionAsync(text => (Transliterator.Toggle(text), null));

    /// <summary>
    /// Copies the selection (or, with nothing selected, the word left of the caret), replaces it with
    /// <paramref name="transform"/>'s result and puts the user's clipboard back. Optionally switches the layout.
    /// </summary>
    internal async Task TransformSelectionAsync(Func<string, (string Text, KeyboardLayout? SwitchTo)> transform,
        KeyboardLayout? switchToIfNothingSelected = null)
    {
        if (!_platform.Capabilities.SimulateInput)
        {
            Log.Info("Text tools: synthetic input is not available on this platform/session");
            return;
        }
        if (!await _convertGate.WaitAsync(0)) return;
        ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1));
            var window = _platform.ForegroundWindow;
            using var _ = _platform.SuspendCapture();
            snapshot = _platform.Snapshot();
            var text = await CopySelectionAsync();
            if (text is null && Settings.ConvertLastWord)
            {
                // Nothing selected: select the word left of the caret and try again.
                _platform.SendChord(OperatingSystem.IsMacOS() ? KeyModifiers.Alt | KeyModifiers.Shift : KeyModifiers.Ctrl | KeyModifiers.Shift, "Left");
                await Task.Delay(40);
                text = await CopySelectionAsync();
            }
            if (string.IsNullOrEmpty(text))
            {
                Log.Info("Text tools: nothing selected");
                if (switchToIfNothingSelected is not null && Settings.SwitchSystemLayout)
                    _platform.SwitchKeyboardLayout(window, switchToIfNothingSelected.LangId);
                return;
            }

            var (result, switchTo) = transform(text);
            if (result != text && await PasteTextAsync(result, window))
                _lastTransform = new TransformRecord(window, text, result, DateTime.UtcNow);
            if (switchTo is not null && Settings.SwitchSystemLayout) _platform.SwitchKeyboardLayout(window, switchTo.LangId);
        }
        catch (Exception e)
        {
            Log.Error("Text tool failed", e);
        }
        finally
        {
            // Always put the user's clipboard back, even after an error.
            if (snapshot is not null) RestoreIfUnchanged(snapshot);
            _convertGate.Release();
        }
    }

    /// <summary>Clipboard sequence right after our last own write or triggered copy.</summary>
    private ulong _expectedSequence;

    /// <summary>
    /// Restores the user's clipboard, unless something else was copied since our last change
    /// (the user copied a new item meanwhile: keep it).
    /// </summary>
    private void RestoreIfUnchanged(ClipboardSnapshot snapshot)
    {
        try
        {
            if (_platform.ClipboardSequence != _expectedSequence)
            {
                Log.Info("Clipboard changed by someone else meanwhile; not restoring");
                return;
            }
            _platform.Restore(snapshot);
        }
        catch (Exception e) { Log.Error("Clipboard restore failed", e); }
    }

    private void SetOwnText(string text)
    {
        _platform.SetText(text);
        _expectedSequence = _platform.ClipboardSequence;
    }

    /// <summary>
    /// Pastes text into <paramref name="window"/> through the clipboard (the caller restores the clipboard).
    /// Nothing is pasted if the user switched to another window meanwhile.
    /// </summary>
    private async Task<bool> PasteTextAsync(string text, nint window)
    {
        SetOwnText(text);
        await Task.Delay(30);
        if (window != 0 && _platform.ForegroundWindow != window)
        {
            Log.Info("Paste skipped: the active window changed");
            return false;
        }
        _platform.SendPaste();
        // Give the target app time to read the clipboard before it changes again.
        await Task.Delay(300);
        return true;
    }

    // ---------------------------------------------------------------- undo

    private sealed record TransformRecord(nint Window, string Original, string Result, DateTime At);

    private TransformRecord? _lastTransform;

    /// <summary>
    /// Puts back the text replaced by the last layout fix, case change or transliteration: selects the inserted
    /// text left of the caret and pastes the original. Works within two minutes, in the same window.
    /// </summary>
    public async Task UndoLastTransformAsync()
    {
        if (_lastTransform is not { } last || DateTime.UtcNow - last.At > TimeSpan.FromMinutes(2)) return;
        if (!_platform.Capabilities.SimulateInput || !await _convertGate.WaitAsync(0)) return;
        ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1));
            var window = _platform.ForegroundWindow;
            if (last.Window != 0 && window != last.Window)
            {
                Log.Info("Undo skipped: another window is active");
                return;
            }
            using var _ = _platform.SuspendCapture();
            snapshot = _platform.Snapshot();
            // Editors move over a line break in one step, so count "\r\n" once.
            var length = last.Result.Replace("\r\n", "\n").Length;
            if (length > 2000) return;
            for (var i = 0; i < length; i++) _platform.SendChord(KeyModifiers.Shift, "Left");
            await Task.Delay(40);
            if (await PasteTextAsync(last.Original, window)) _lastTransform = null;
        }
        catch (Exception e) { Log.Error("Undo failed", e); }
        finally
        {
            if (snapshot is not null) RestoreIfUnchanged(snapshot);
            _convertGate.Release();
        }
    }

    // ---------------------------------------------------------------- snippets

    private readonly SnippetMatcher _snippets = new();

    private void ApplySnippets()
    {
        _snippets.SetSnippets(Settings.Snippets);
        _platform.SetTypingWatch(_snippets.HasSnippets && _platform.Capabilities.SimulateInput);
    }

    private void OnKeyTyped(TypedKey key)
    {
        Snippet? hit;
        lock (_snippets)
        {
            if (key.Reset) { _snippets.Reset(); return; }
            if (key.Backspace) { _snippets.Backspace(); return; }
            hit = _snippets.OnChar(key.Char);
        }
        // Never block the hook thread: expand on the thread pool.
        if (hit is not null) ThreadPool.QueueUserWorkItem(_ => _ = ExpandSnippetAsync(hit));
    }

    /// <summary>Deletes the typed trigger and pastes the snippet text in its place.</summary>
    public async Task ExpandSnippetAsync(Snippet snippet)
    {
        if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(2))) return;
        ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(500));
            var window = _platform.ForegroundWindow;
            using var _ = _platform.SuspendCapture();
            snapshot = _platform.Snapshot();
            _expectedSequence = _platform.ClipboardSequence;
            for (var i = 0; i < snippet.Trigger.Trim().Length; i++) _platform.SendChord(KeyModifiers.None, "Backspace");
            await Task.Delay(20);
            if (await PasteTextAsync(snippet.Text, window)) Log.Info("Snippet expanded");
        }
        catch (Exception e) { Log.Error("Snippet expansion failed", e); }
        finally
        {
            if (snapshot is not null) RestoreIfUnchanged(snapshot);
            _convertGate.Release();
        }
    }

    /// <summary>Sends Copy and returns the selected text, or null when nothing is selected.</summary>
    private async Task<string?> CopySelectionAsync()
    {
        // A unique placeholder guarantees the copy is visible as a change even when the selection
        // equals what was already on the clipboard (content-hashing platforms such as Linux).
        SetOwnText("\u2063typlo-" + Guid.NewGuid().ToString("N"));
        var before = _platform.ClipboardSequence;
        _platform.SendCopy();
        if (!await WaitForClipboardChange(before, TimeSpan.FromMilliseconds(600))) return null;
        _expectedSequence = _platform.ClipboardSequence; // the copy we triggered
        var text = _platform.ReadText();
        if (string.IsNullOrEmpty(text) || text.StartsWith('\u2063')) return null;
        // Editors such as VS Code copy the whole current line (with its line break) when nothing is selected.
        if (text.EndsWith('\n') && text.IndexOf('\n') == text.Length - 1) return null;
        return text;
    }

    private async Task<bool> WaitForClipboardChange(ulong before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_platform.ClipboardSequence != before) { await Task.Delay(20); return true; }
            await Task.Delay(10);
        }
        return false;
    }

    /// <summary>When set, nothing is stored until this moment (<see cref="DateTimeOffset.MaxValue"/> = until restart).</summary>
    public DateTimeOffset? PausedUntil { get; private set; }

    public bool IsPaused => PausedUntil is { } until && until > DateTimeOffset.Now;

    /// <summary>Stops storing clipboard history for <paramref name="duration"/>, or until the app restarts when null.</summary>
    public void PauseHistory(TimeSpan? duration)
    {
        PausedUntil = duration is { } d ? DateTimeOffset.Now + d : DateTimeOffset.MaxValue;
        _pauseTimer?.Dispose();
        // Refresh the tray when a timed pause ends by itself.
        _pauseTimer = duration is { } span ? new Timer(_ => ResumeHistory(), null, span, Timeout.InfiniteTimeSpan) : null;
        Log.Info(duration is null ? "History paused until restart" : $"History paused for {duration}");
        SettingsChanged?.Invoke();
    }

    public void ResumeHistory()
    {
        PausedUntil = null;
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        SettingsChanged?.Invoke();
    }

    private Timer? _pauseTimer;

    private void OnClipboardCaptured(RawClip raw)
    {
        if (!Settings.HistoryEnabled || IsPaused) return;
        if (raw.SourceApp is { } app &&
            Settings.ExcludedApps.Any(x => string.Equals(x.Trim(), app, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (raw.Files is { Count: > 0 } files) Store.Add(ClipContent.FromFiles(files, raw.SourceApp));
            else if (raw.Text is not null) Store.Add(ClipContent.FromText(raw.Text, raw.SourceApp, raw.Html, raw.Rtf));
            else if (raw.Image is not null)
            {
                if (raw.Image.LongLength > Settings.MaxImageBytes * 4) return; // BMPs are big; PNG is checked by the store
                var img = ImageCodec.Encode(raw.Image, raw.ImageIsPng);
                Store.Add(ClipContent.FromImage(img.Png, img.Thumbnail, img.Width, img.Height, raw.SourceApp));
            }
        }
        catch (Exception e) { Log.Error("Storing clip failed", e); }
    }

    /// <summary>
    /// Puts a history item on the clipboard, focuses the previous window and pastes it.
    /// <paramref name="plainText"/> drops the HTML/RTF formatting of text items.
    /// </summary>
    public async Task PasteAsync(long id, nint targetWindow, bool paste = true, bool plainText = false)
    {
        // One clipboard operation at a time: a paste must not interleave with a conversion or snippet.
        if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(3))) return;
        try
        {
            var content = Store.Get(id);
            if (content is null) return;
            switch (content.Kind)
            {
                case ClipKind.Text when content.HasFormatting && !plainText:
                    _platform.SetRichText(content.Text!, content.Html, content.Rtf);
                    break;
                case ClipKind.Text:
                    _platform.SetText(content.Text!);
                    break;
                case ClipKind.Files:
                    _platform.SetFiles(content.Files);
                    break;
                default:
                    var (bgra, w, h) = ImageCodec.DecodeBgra(content.ImagePng!);
                    _platform.SetImage(content.ImagePng!, bgra, w, h);
                    break;
            }
            Store.Touch(id);
            if (!paste || !_platform.Capabilities.SimulateInput) return;
            _platform.ActivateWindow(targetWindow);
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(60);
            if (targetWindow != 0 && _platform.ForegroundWindow != targetWindow)
            {
                Log.Info("Paste skipped: the target window is not active");
                return;
            }
            _platform.SendPaste();
        }
        catch (Exception e) { Log.Error("Paste failed", e); }
        finally { _convertGate.Release(); }
    }

    /// <summary>Pastes the item copied before the current one (second hotkey press in the history popup).</summary>
    public Task PastePreviousAsync(nint targetWindow, bool paste = true)
    {
        var previous = Store.List().OrderByDescending(e => e.LastUsed).Skip(1).FirstOrDefault();
        return previous is null ? Task.CompletedTask : PasteAsync(previous.Id, targetWindow, paste);
    }

    public void Dispose()
    {
        _platform.HotkeyPressed -= OnHotkey;
        _platform.KeyTyped -= OnKeyTyped;
        _platform.SetTypingWatch(false);
        _platform.ClipboardCaptured -= OnClipboardCaptured;
        _activityTimer?.Dispose();
        _pauseTimer?.Dispose();
        _platform.SetKeepAwake(false);
        Store.Dispose();
    }
}
