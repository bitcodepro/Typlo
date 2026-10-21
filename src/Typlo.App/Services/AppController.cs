using Typlo.App.Platform;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.Core.Storage;

namespace Typlo.App.Services;

/// <summary>Wires hotkeys and clipboard events from the platform to conversion and history.</summary>
public sealed class AppController : IDisposable
{
    private const int ToggleId = 1;
    private const int ClipboardId = 2;
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

    public AppController(IPlatform platform, AppPaths paths, IKeyProtector keyProtector)
    {
        _platform = platform;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        Settings = _settingsStore.Load();
        Loc.SetLanguage(Settings.UiLanguage);
        Store = new ClipStore(paths.HistoryDb, ClipCrypto.LoadOrCreate(paths.KeyFile, keyProtector), Limits(Settings));
        BuildConverter();
        ApplyKeepAwake();
        _platform.HotkeyPressed += OnHotkey;
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
        try { _platform.AutostartEnabled = settings.StartWithSystem; }
        catch (Exception e) { Log.Error("Autostart update failed", e); }
        ApplyKeepAwake();
        RegisterHotkeys();
    }

    /// <summary>Raised when settings change (e.g. to refresh the tray menu); may be on any thread.</summary>
    public event Action? SettingsChanged;

    private void ApplyKeepAwake()
    {
        if (!_platform.SetKeepAwake(Settings.PreventScreenLock)) Log.Error("SetThreadExecutionState failed");
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
            else if (_layoutHotkeys.TryGetValue(id, out var layout)) _ = ConvertSelectionAsync(layout);
        }
        catch (Exception e) { Log.Error("Hotkey handler failed", e); }
    }

    /// <summary>Copies the selection, converts it to <paramref name="target"/> (null = toggle) and pastes it back.</summary>
    public async Task ConvertSelectionAsync(KeyboardLayout? target)
    {
        if (!_platform.Capabilities.SimulateInput)
        {
            Log.Info("Convert: synthetic input is not available on this platform/session");
            return;
        }
        if (!await _convertGate.WaitAsync(0)) return;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1));
            var window = _platform.ForegroundWindow;
            using var _ = _platform.SuspendCapture();
            var snapshot = _platform.Snapshot();
            // A unique placeholder guarantees the copy is visible as a change even when the selection
            // equals what was already on the clipboard (content-hashing platforms such as Linux).
            _platform.SetText("⁣typlo-" + Guid.NewGuid().ToString("N"));
            var before = _platform.ClipboardSequence;
            _platform.SendCopy();
            if (!await WaitForClipboardChange(before, TimeSpan.FromMilliseconds(600)))
            {
                Log.Info("Convert: nothing selected");
                _platform.Restore(snapshot);
                if (target is not null && Settings.SwitchSystemLayout) _platform.SwitchKeyboardLayout(window, target.LangId);
                return;
            }

            var text = _platform.ReadText();
            if (string.IsNullOrEmpty(text)) { _platform.Restore(snapshot); return; }

            var result = target is null ? _converter.Toggle(text) : _converter.ConvertTo(text, target);
            if (result.Changed && result.Text != text)
            {
                _platform.SetText(result.Text);
                await Task.Delay(30);
                _platform.SendPaste();
                // Give the target app time to read the clipboard before putting the user's content back.
                await Task.Delay(300);
            }
            _platform.Restore(snapshot);
            if (Settings.SwitchSystemLayout) _platform.SwitchKeyboardLayout(window, result.Target.LangId);
        }
        catch (Exception e)
        {
            Log.Error("Convert failed", e);
        }
        finally
        {
            _convertGate.Release();
        }
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

    private void OnClipboardCaptured(RawClip raw)
    {
        if (!Settings.HistoryEnabled) return;
        if (raw.SourceApp is { } app &&
            Settings.ExcludedApps.Any(x => string.Equals(x.Trim(), app, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (raw.Text is not null) Store.Add(ClipContent.FromText(raw.Text, raw.SourceApp));
            else if (raw.Image is not null)
            {
                if (raw.Image.LongLength > Settings.MaxImageBytes * 4) return; // BMPs are big; PNG is checked by the store
                var img = ImageCodec.Encode(raw.Image, raw.ImageIsPng);
                Store.Add(ClipContent.FromImage(img.Png, img.Thumbnail, img.Width, img.Height, raw.SourceApp));
            }
        }
        catch (Exception e) { Log.Error("Storing clip failed", e); }
    }

    /// <summary>Puts a history item on the clipboard, focuses the previous window and pastes it.</summary>
    public async Task PasteAsync(long id, nint targetWindow, bool paste = true)
    {
        try
        {
            var content = Store.Get(id);
            if (content is null) return;
            if (content.Kind == ClipKind.Text) _platform.SetText(content.Text!);
            else
            {
                var (bgra, w, h) = ImageCodec.DecodeBgra(content.ImagePng!);
                _platform.SetImage(content.ImagePng!, bgra, w, h);
            }
            Store.Touch(id);
            if (!paste || !_platform.Capabilities.SimulateInput) return;
            _platform.ActivateWindow(targetWindow);
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(60);
            _platform.SendPaste();
        }
        catch (Exception e) { Log.Error("Paste failed", e); }
    }

    public void Dispose()
    {
        _platform.HotkeyPressed -= OnHotkey;
        _platform.ClipboardCaptured -= OnClipboardCaptured;
        _platform.SetKeepAwake(false);
        Store.Dispose();
    }
}
