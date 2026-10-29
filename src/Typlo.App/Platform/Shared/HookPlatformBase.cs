using System.Collections.Concurrent;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;

namespace Typlo.App.Platform.Shared;

/// <summary>
/// Base for macOS and Linux: global hotkeys, modifier tracking, cursor position and synthetic
/// key presses via SharpHook (libuiohook), plus a polling clipboard monitor. Subclasses supply
/// the clipboard, window and system specifics.
/// </summary>
public abstract class HookPlatformBase : IPlatform
{
    private readonly SimpleGlobalHook? _hook;
    private readonly Lazy<EventSimulator> _simulatorLazy = new(() => EventSimulator.Create("Typlo"));
    private readonly ConcurrentDictionary<int, (KeyCode Key, KeyModifiers Mods)> _hotkeys = new();
    private readonly ConcurrentDictionary<KeyCode, bool> _down = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ulong> _ownTokens = [];
    private readonly Lock _tokenLock = new();
    private int _suspendCount;
    private int _cursorX, _cursorY;
    private ulong _lastToken;

    public event Action<int>? HotkeyPressed;
    public event Action<RawClip>? ClipboardCaptured;
    public event Action<TypedKey>? KeyTyped;

    private volatile bool _typingWatch;

    public void SetTypingWatch(bool enabled) => _typingWatch = enabled;

    public abstract string Name { get; }
    public abstract PlatformCapabilities Capabilities { get; }

    /// <summary>Modifier used by the OS for copy/paste (Ctrl, or Cmd on macOS).</summary>
    protected virtual KeyModifiers ShortcutModifier => KeyModifiers.Ctrl;

    /// <summary>How often the clipboard is polled when the subclass has no change notification.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromMilliseconds(500);

    protected HookPlatformBase(bool startHook = true)
    {
        if (startHook)
        {
            try
            {
                _hook = new SimpleGlobalHook();
                _hook.KeyPressed += OnKeyPressed;
                TypingEvents.Attach(_hook, key => { if (_typingWatch) KeyTyped?.Invoke(key); },
                    HookFlagsOwnInput ? null : (code, raw) => _ownInput.Consume(code, raw, Environment.TickCount64));
                _hook.KeyReleased += (_, e) => _down.TryRemove(e.Data.KeyCode, out bool _);
                _hook.MouseMoved += (_, e) =>
                {
                    _cursorX = e.Data.X;
                    _cursorY = e.Data.Y;
                    Interlocked.Exchange(ref _lastRealInputTicks, Environment.TickCount64);
                };
                _hook.MouseDragged += (_, e) => { _cursorX = e.Data.X; _cursorY = e.Data.Y; };
                _ = _hook.RunAsync().ContinueWith(t =>
                {
                    if (t.Exception is not null) Log.Error("Global hook stopped", t.Exception.GetBaseException());
                }, TaskScheduler.Default);
            }
            catch (Exception e)
            {
                Log.Error("Global hook unavailable", e);
                _hook = null;
            }
        }
    }

    /// <summary>Starts the clipboard watcher; call at the end of the subclass constructor.</summary>
    protected void StartClipboardMonitor()
    {
        _lastToken = SafeToken();
        var thread = new Thread(MonitorLoop) { IsBackground = true, Name = "Typlo.ClipboardMonitor" };
        thread.Start();
    }

    // ---------------------------------------------------------------- hotkeys

    /// <summary>Self-test only: lets synthetic key presses trigger hotkeys.</summary>
    internal bool AcceptSimulatedHotkeys { get; set; }

    private static readonly bool DebugKeys = Environment.GetEnvironmentVariable("TYPLO_DEBUG_KEYS") == "1";

    public virtual bool RegisterHotkey(int id, Hotkey hotkey)
    {
        if (_hook is null || KeyCodes.FromName(hotkey.Key) is not { } key) return false;
        foreach (var (otherId, other) in _hotkeys)
            if (otherId != id && other.Key == key && other.Mods == hotkey.Modifiers) return false;
        _hotkeys[id] = (key, hotkey.Modifiers);
        return true;
    }

    public virtual void UnregisterAllHotkeys() => _hotkeys.Clear();

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var code = e.Data.KeyCode;
        _down[code] = true;
        if (!e.IsEventSimulated) Interlocked.Exchange(ref _lastRealInputTicks, Environment.TickCount64);
        if (DebugKeys) Log.Info($"key {code} mask {e.RawEvent.Mask} simulated {e.IsEventSimulated}");
        if (e.IsEventSimulated && !AcceptSimulatedHotkeys) return;
        var mods = CurrentModifiers();
        foreach (var (id, hk) in _hotkeys)
        {
            if (hk.Key != code || hk.Mods != mods) continue;
            e.SuppressEvent = true; // honoured on Windows/macOS; X11 cannot swallow keys
            ThreadPool.QueueUserWorkItem(_ => HotkeyPressed?.Invoke(id));
            return;
        }
    }

    /// <summary>
    /// Modifiers from our own press/release tracking: the event mask is unreliable on X11
    /// (it can lag behind the key events).
    /// </summary>
    private KeyModifiers CurrentModifiers()
    {
        var m = KeyModifiers.None;
        if (_down.ContainsKey(KeyCode.VcLeftControl) || _down.ContainsKey(KeyCode.VcRightControl)) m |= KeyModifiers.Ctrl;
        if (_down.ContainsKey(KeyCode.VcLeftAlt) || _down.ContainsKey(KeyCode.VcRightAlt)) m |= KeyModifiers.Alt;
        if (_down.ContainsKey(KeyCode.VcLeftShift) || _down.ContainsKey(KeyCode.VcRightShift)) m |= KeyModifiers.Shift;
        if (_down.ContainsKey(KeyCode.VcLeftMeta) || _down.ContainsKey(KeyCode.VcRightMeta)) m |= KeyModifiers.Win;
        return m;
    }

    // ---------------------------------------------------------------- input

    private static readonly KeyCode[] ModifierKeys =
    [
        KeyCode.VcLeftControl, KeyCode.VcRightControl, KeyCode.VcLeftShift, KeyCode.VcRightShift,
        KeyCode.VcLeftAlt, KeyCode.VcRightAlt, KeyCode.VcLeftMeta, KeyCode.VcRightMeta,
    ];

    public async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ModifierKeys.Any(_down.ContainsKey)) return;
            await Task.Delay(10);
        }
        foreach (var key in ModifierKeys.Where(_down.ContainsKey))
        {
            Simulator.SimulateKeyRelease(key);
            _down.TryRemove(key, out _);
        }
    }

    private EventSimulator Simulator => _simulatorLazy.Value;

    /// <summary>
    /// True where the hook marks injected events as simulated, so the typing hook drops them without tracking.
    /// macOS: libuiohook flags every event that carries a source process ID (all posted events, none from hardware).
    /// </summary>
    protected virtual bool HookFlagsOwnInput => false;

    /// <summary>The raw key code text entry will be reported with, or null if it cannot be known.</summary>
    protected virtual ushort? TextEntryKeycode() => null;

    // X11: libuiohook does not flag our XTest input as simulated, so remember what we inject and drop exactly those
    // events when the hook reports them back; real key presses in between are still seen.
    private readonly OwnInputTracker _ownInput = new();

    public void SendChord(KeyModifiers modifiers, string key) => SendChord(modifiers, key, own: true);

    /// <summary>Self-test only: a key press the typing hook treats as coming from the user.</summary>
    internal void SendChordAsUser(KeyModifiers modifiers, string key) => SendChord(modifiers, key, own: false);

    public bool TypeText(string text, Func<bool>? keepGoing = null)
    {
        // One character at a time: X11 text entry is slow (libuiohook waits around each character), so the target
        // is re-checked before every character and a window switch stops the rest from going elsewhere.
        foreach (var element in System.Globalization.StringInfo.GetTextElementEnumerator(text).AsEnumerable())
        {
            if (keepGoing?.Invoke() == false) return false;
            if (!HookFlagsOwnInput)
            {
                // Without the exact key the hook will report, our text would look like user input (or be confused
                // with it), so nothing is typed.
                if (TextEntryKeycode() is not { } raw)
                {
                    Log.Info("Text entry skipped: its key code cannot be determined");
                    return false;
                }
                var now = Environment.TickCount64;
                for (var i = OwnInputTracker.X11KeyEvents(element); i > 0; i--) _ownInput.TextKey(raw, now);
            }
            Simulator.SimulateTextEntry(element);
        }
        return true;
    }

    private void SendChord(KeyModifiers modifiers, string key, bool own)
    {
        if (KeyCodes.FromName(key) is not { } code) return;
        var mods = new List<KeyCode>();
        if (modifiers.HasFlag(KeyModifiers.Ctrl)) mods.Add(KeyCode.VcLeftControl);
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods.Add(KeyCode.VcLeftAlt);
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods.Add(KeyCode.VcLeftShift);
        if (modifiers.HasFlag(KeyModifiers.Win)) mods.Add(KeyCode.VcLeftMeta);
        if (own && !HookFlagsOwnInput) _ownInput.Press(code, Environment.TickCount64); // modifiers are ignored by the typing hook anyway
        foreach (var m in mods) Simulator.SimulateKeyPress(m);
        Simulator.SimulateKeyPress(code);
        Simulator.SimulateKeyRelease(code);
        for (var i = mods.Count - 1; i >= 0; i--) Simulator.SimulateKeyRelease(mods[i]);
    }

    public void SendCopy() => SendChord(ShortcutModifier, "C");
    public void SendPaste() => SendChord(ShortcutModifier, "V");

    public virtual (int X, int Y) CursorPosition => (_cursorX, _cursorY);

    public abstract nint ForegroundWindow { get; }
    public virtual string? GetApplicationProcessName(nint window) => null;
    public abstract void ActivateWindow(nint handle);
    public abstract bool SwitchKeyboardLayout(nint window, int langId);
    public virtual IReadOnlyList<KeyboardLayout> GetKeyboardLayouts() => [];
    public virtual string? GetCurrentLayoutId(nint window) => null;
    public virtual bool SwitchKeyboardLayout(nint window, KeyboardLayout layout) => SwitchKeyboardLayout(window, layout.LangId);

    // ---------------------------------------------------------------- clipboard

    public ulong ClipboardSequence => SafeToken();

    /// <summary>A value that changes whenever the clipboard content changes.</summary>
    protected abstract ulong ReadChangeToken();

    /// <summary>Reads the current clipboard for the history (text first, then image).</summary>
    protected abstract RawClip? ReadClip();

    /// <summary>Blocks until the clipboard may have changed. Default: sleep for <see cref="PollInterval"/>.</summary>
    protected virtual void WaitForClipboardChange(CancellationToken ct) => ct.WaitHandle.WaitOne(PollInterval);

    public abstract string? ReadText();
    protected abstract void WriteText(string text);
    protected abstract void WriteImage(byte[] png, byte[] bgra, int width, int height);
    protected abstract void WriteRichText(string text, string? html, string? rtf);
    protected abstract void WriteFiles(IReadOnlyList<string> paths);
    protected abstract ClipboardSnapshot ReadSnapshot();
    protected abstract void WriteSnapshot(ClipboardSnapshot snapshot);

    public void SetText(string text) => OwnWrite(() => WriteText(text));
    public void SetImage(byte[] png, byte[] bgra, int width, int height) => OwnWrite(() => WriteImage(png, bgra, width, height));
    public void SetRichText(string text, string? html, string? rtf) => OwnWrite(() => WriteRichText(text, html, rtf));
    public void SetFiles(IReadOnlyList<string> paths) => OwnWrite(() => WriteFiles(paths));
    public ClipboardSnapshot Snapshot() => ReadSnapshot();

    /// <summary>VS Code's "vscode-editor-data" clipboard type, as raw text, or null.</summary>
    protected abstract string? ReadEditorData();

    public virtual bool? IsClipboardOwnedBy(nint window) => null;

    public bool IsEmptySelectionLineCopy() => ReadEditorData() is { } json && EditorClipboard.IsFromEmptySelection(json);
    public void Restore(ClipboardSnapshot snapshot) => OwnWrite(() => WriteSnapshot(snapshot));

    private void OwnWrite(Action write)
    {
        write();
        var token = SafeToken();
        lock (_tokenLock)
        {
            _ownTokens.Add(token);
            if (_ownTokens.Count > 64) _ownTokens.Clear();
        }
    }

    public IDisposable SuspendCapture()
    {
        Interlocked.Increment(ref _suspendCount);
        return new Scope(() =>
        {
            // Resume at once: the watcher only compares the current state, so the short-lived states during the
            // suspension (placeholder, copied selection) are gone, and the restored clipboard is our own write.
            // A delay here would also swallow whatever the user copies right after a conversion.
            Interlocked.Decrement(ref _suspendCount);
        });
    }

    private ulong SafeToken()
    {
        try { return ReadChangeToken(); }
        catch (Exception e) { Log.Error("Clipboard token failed", e); return _lastToken; }
    }

    private void MonitorLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                WaitForClipboardChange(_cts.Token);
                if (_cts.IsCancellationRequested) return;
                var token = SafeToken();
                if (token == _lastToken) continue;
                _lastToken = token;
                bool own;
                lock (_tokenLock) own = _ownTokens.Remove(token);
                if (own || Volatile.Read(ref _suspendCount) > 0 || ClipboardCaptured is null) continue;
                if (ReadClip() is { } clip) ClipboardCaptured?.Invoke(clip);
            }
            catch (Exception e)
            {
                Log.Error("Clipboard monitor", e);
                _cts.Token.WaitHandle.WaitOne(1000);
            }
        }
    }

    // ---------------------------------------------------------------- system

    public abstract bool SetKeepAwake(bool enabled);

    private long _lastRealInputTicks = Environment.TickCount64;

    /// <summary>After 3 idle minutes, presses F15 (absent on standard keyboards); never touches the mouse.</summary>
    public virtual void SignalUserActivity()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastRealInputTicks) < 3 * 60_000) return;
        try
        {
            Simulator.SimulateKeyPress(KeyCode.VcF15);
            Simulator.SimulateKeyRelease(KeyCode.VcF15);
            Log.Info("Activity signal sent");
        }
        catch (Exception e) { Log.Error("Activity signal failed", e); }
    }
    public abstract bool AutostartEnabled { get; set; }

    public virtual void Dispose()
    {
        _cts.Cancel();
        try { _hook?.Dispose(); } catch (Exception) { }
        if (_simulatorLazy.IsValueCreated) _simulatorLazy.Value.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }
}

/// <summary>Maps <see cref="Hotkey.Key"/> names to libuiohook key codes.</summary>
internal static class KeyCodes
{
    private static readonly Dictionary<string, KeyCode> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = KeyCode.VcSpace, ["Insert"] = KeyCode.VcInsert, ["Delete"] = KeyCode.VcDelete,
        ["Home"] = KeyCode.VcHome, ["End"] = KeyCode.VcEnd, ["PageUp"] = KeyCode.VcPageUp,
        ["PageDown"] = KeyCode.VcPageDown, ["Up"] = KeyCode.VcUp, ["Down"] = KeyCode.VcDown,
        ["Left"] = KeyCode.VcLeft, ["Right"] = KeyCode.VcRight, ["Pause"] = KeyCode.VcPause,
        ["Tilde"] = KeyCode.VcBackQuote, ["Minus"] = KeyCode.VcMinus, ["Plus"] = KeyCode.VcEquals,
        ["Comma"] = KeyCode.VcComma, ["Period"] = KeyCode.VcPeriod, ["Slash"] = KeyCode.VcSlash,
        ["Backslash"] = KeyCode.VcBackslash, ["Semicolon"] = KeyCode.VcSemicolon, ["Quote"] = KeyCode.VcQuote,
        ["OpenBracket"] = KeyCode.VcOpenBracket, ["CloseBracket"] = KeyCode.VcCloseBracket, ["Backspace"] = KeyCode.VcBackspace,
    };

    /// <summary>The <see cref="Hotkey.Key"/> name of a key code, or null if hotkeys cannot use it.</summary>
    public static string? ToName(KeyCode code)
    {
        foreach (var (name, value) in Named) if (value == code) return name;
        var text = code.ToString(); // VcA, Vc7, VcF12
        if (!text.StartsWith("Vc", StringComparison.Ordinal)) return null;
        var rest = text[2..];
        return Hotkey.IsValidKey(rest) ? rest : null;
    }

    public static KeyCode? FromName(string key)
    {
        if (Named.TryGetValue(key, out var named)) return named;
        // A-Z, 0-9, F1-F24 follow the "Vc" + name convention.
        return Enum.TryParse<KeyCode>("Vc" + key, ignoreCase: false, out var code) ? code : null;
    }
}

internal static class TextElementExtensions
{
    public static IEnumerable<string> AsEnumerable(this System.Globalization.TextElementEnumerator e)
    {
        while (e.MoveNext()) yield return e.GetTextElement();
    }
}
