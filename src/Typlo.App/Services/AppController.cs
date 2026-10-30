using System.Diagnostics;
using Typlo.Core.Imaging;
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
public sealed partial class AppController : IDisposable
{
    private const int ToggleId = 1;
    private const int ClipboardId = 2;
    private const int CaseId = 3;
    private const int TranslitId = 4;
    private const int UndoId = 5;
    private const int ScreenshotCopyId = 6;
    private const int ScreenshotSaveId = 7;
    private const int LayoutSwitchId = 8;
    private const int AddWordId = 9;
    private const int LayoutIdBase = 100;
    // A hotkey is delivered on key-down. Waiting a whole second for its modifiers penalises a held
    // chord; give a normal release a short grace period, then use the platform's safe release path.
    private static readonly TimeSpan ModifierReleaseGrace = TimeSpan.FromMilliseconds(80);

    private readonly IPlatform _platform;
    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _convertGate = new(1, 1);
    private LayoutConverter _converter = null!;
    private readonly Dictionary<int, KeyboardLayout> _layoutHotkeys = [];

    public AppSettings Settings { get; private set; }
    public ClipStore Store { get; }
    public IPlatform Platform => _platform;
    public LayoutCatalog LayoutCatalog { get; private set; } = new([]);
    public IReadOnlyList<KeyboardLayout> AvailableLayouts => LayoutCatalog.All;
    public KeyboardLayout? FindLayout(string? id) => LayoutCatalog.Find(id);

    /// <summary>Raised on a background thread; the argument is the window that was active before.</summary>
    public event Action<nint>? ShowHistoryRequested;
    public event Action<CaptureDestination>? ScreenshotRequested;
    private volatile bool _capturingScreen;
    public bool IsCapturingScreen => _capturingScreen;

    /// <summary>Hotkeys that could not be registered (taken by another app), as text for the UI.</summary>
    public IReadOnlyList<string> FailedHotkeys { get; private set; } = [];

    /// <summary>Self-test/screenshot run: never touches system-wide settings such as autostart.</summary>
    private readonly bool _diagnostic;
    private readonly RuntimeClient? _remote;
    private readonly LatestImageWorker _images;
    private readonly IdleMemoryReclaimer _memory;
    private long _lastInputAt = Environment.TickCount64;

    public AppController(IPlatform platform, AppPaths paths, IKeyProtector keyProtector, bool diagnostic = false)
        : this(platform, paths, keyProtector, diagnostic, null) { }

    internal static AppController CreateRemote(IPlatform platform, AppPaths paths, IKeyProtector protector, RuntimeClient remote)
        => new(platform, paths, protector, true, remote);

    private AppController(IPlatform platform, AppPaths paths, IKeyProtector keyProtector, bool diagnostic, RuntimeClient? remote)
    {
        _remote = remote;
        _dictionaryDataDir = paths.DataDir;
        _personal = new(Path.Combine(paths.DataDir, "personal-words.sqlite3"));
        _images = new LatestImageWorker(StoreCapturedImage);
        _memory = new IdleMemoryReclaimer(() => _convertGate.CurrentCount > 0 && !_capturingScreen && !_editingSettings && !_images.IsBusy && Environment.TickCount64 - Interlocked.Read(ref _lastInputAt) > 1500);
        _diagnostic = diagnostic;
        _platform = platform;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        Settings = _settingsStore.Load();
        if (_remote is null && !_diagnostic && _platform.Name == "Windows") Settings.InitializeWindowsShortcuts();
        DiscoverLayouts();
        LayoutCatalog.Synchronize(Settings);
        if (_remote is null) _settingsStore.Save(Settings);
        Loc.SetLanguage(Settings.UiLanguage);
        Store = new ClipStore(paths.HistoryDb, ClipCrypto.LoadOrCreate(paths.KeyFile, keyProtector), Limits(Settings));
        BuildConverter();
        if (_remote is not null) { UpdateRemoteState(_remote.Call(new("state"))); return; }
        ApplyKeepAwake();
        _platform.HotkeyPressed += OnHotkey;
        _platform.KeyTyped += OnKeyTyped;
        ApplySnippets();
        _platform.ClipboardCaptured += OnClipboardCaptured;
        RegisterHotkeys();
        ConfigureApplicationLayouts();
    }

    private static StoreLimits Limits(AppSettings s) =>
        new(s.MaxItems, s.MaxTotalBytes, s.MaxImageBytes, s.MaxTextBytes);

    private void BuildConverter()
    {
        var cycle = LayoutCatalog.Cycle(Settings);
        var ordered = cycle.Concat(AvailableLayouts).Append(KeyboardLayouts.English).DistinctBy(l => l.Id).ToList();
        _converter = new LayoutConverter(ordered, cycle);
        BuildApplicationCycles();
    }

    private void DiscoverLayouts()
    {
        try { LayoutCatalog = new(_platform.GetKeyboardLayouts()); }
        catch (Exception e) { Log.Error("System keyboard layout discovery failed", e); }
    }

    /// <summary>Text fixtures can exercise both directions without installing input sources on the test machine.</summary>
    internal void ConfigureDiagnosticLayouts(IEnumerable<KeyboardLayout> layouts)
    {
        if (!_diagnostic) throw new InvalidOperationException("Diagnostic layouts are disabled");
        LayoutCatalog = new(layouts);
        BuildConverter();
    }

    public void RefreshKeyboardLayouts()
    {
        if (_remote is not null)
        {
            UpdateRemoteState(_remote.Call(new("refresh-layouts")));
            DiscoverLayouts(); LayoutCatalog.Synchronize(Settings); BuildConverter(); return;
        }
        DiscoverLayouts();
        LayoutCatalog.Synchronize(Settings);
        _settingsStore.Save(Settings);
        BuildConverter();
        ApplySnippets();
        ConfigureApplicationLayouts();
    }

    private IEnumerable<KeyboardLayout> SourceLayouts(KeyboardLayout? preferred) => preferred is { SupportsConversion: true }
        ? new[] { preferred }.Concat(_converter.Layouts).DistinctBy(l => l.Id) : _converter.Layouts;

    public void ApplySettings(AppSettings settings)
    {
        if (_remote is not null)
        {
            UpdateRemoteState(_remote.Call(new("settings", Settings: settings)));
            SettingsChanged?.Invoke(); return;
        }
        settings.Normalize();
        LayoutCatalog.Synchronize(settings);
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
        ConfigureApplicationLayouts();
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
    private volatile bool _editingSettings;
    private long _settingsSession;
    public void SuspendHotkeys()
    {
        if (_remote is not null) { _remote.Call(new("suspend-hotkeys", Id: Interlocked.Increment(ref _settingsSession))); return; }
        _editingSettings = true; _platform.UnregisterAllHotkeys();
    }

    /// <summary>Typlo's active hotkeys: pressing one is a command, not input that moves the caret.</summary>
    private readonly HashSet<Hotkey> _activeHotkeys = [];

    /// <summary>Closing a settings window must not block its dispatcher on a resident RPC.</summary>
    internal async Task RestoreHotkeysAsync()
    {
        if (_remote is null) { RegisterHotkeys(); return; }
        try
        {
            var reply = await _remote.CallAsync(new("register-hotkeys", Id: Interlocked.Read(ref _settingsSession)));
            if (Volatile.Read(ref _disposed) == 0) UpdateRemoteState(reply);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { Log.Error("Background runtime unavailable", e); }
    }

    public void RegisterHotkeys()
    {
        if (Volatile.Read(ref _updating) != 0) return;
        if (_remote is not null)
        {
            try { UpdateRemoteState(_remote.Call(new("register-hotkeys", Id: Interlocked.Read(ref _settingsSession)))); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { Log.Error("Background runtime unavailable", e); }
            return;
        }
        _editingSettings = false;
        _platform.UnregisterAllHotkeys();
        lock (_activeHotkeys) _activeHotkeys.Clear();
        _layoutHotkeys.Clear();
        var failed = new List<string>();

        void Register(int id, string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var hk = Hotkey.TryParse(text);
            if (hk is { IsUsable: true } && _platform.RegisterHotkey(id, hk))
            {
                lock (_activeHotkeys) _activeHotkeys.Add(hk);
            }
            else
            {
                failed.Add($"{label}: {text}");
                Log.Error($"Hotkey not registered: {label} {text}");
            }
        }

        Register(ToggleId, Settings.ToggleHotkey, Loc.T("hk.toggle"));
        Register(LayoutSwitchId, Settings.LayoutSwitchHotkey, Loc.T("hk.inputCycle"));
        Register(AddWordId, Settings.AddWordHotkey, Loc.T("dictionary.title"));
        Register(ClipboardId, Settings.ClipboardHotkey, Loc.T("hk.history"));
        Register(CaseId, Settings.CaseHotkey, Loc.T("hk.case"));
        Register(TranslitId, Settings.TranslitHotkey, Loc.T("hk.translit"));
        Register(UndoId, Settings.UndoHotkey, Loc.T("hk.undo"));
        if (_platform.SupportsScreenCapture)
        {
            Register(ScreenshotCopyId, Settings.ScreenshotCopyHotkey, Loc.T("capture.copy"));
            Register(ScreenshotSaveId, Settings.ScreenshotSaveHotkey, Loc.T("capture.save"));
        }
        var next = LayoutIdBase;
        foreach (var lh in Settings.LayoutHotkeys)
        {
            if (FindLayout(lh.LayoutId) is not { } layout) continue;
            _layoutHotkeys[next] = layout;
            Register(next++, lh.Hotkey, Loc.T("hk.to", layout.Name));
        }
        FailedHotkeys = failed;
    }

    private void OnHotkey(int id)
    {
        if (Volatile.Read(ref _updating) != 0) return;
        try
        {
            if (_capturingScreen) return;
            if (id == ScreenshotCopyId) ScreenshotRequested?.Invoke(CaptureDestination.Clipboard);
            else if (id == ScreenshotSaveId) ScreenshotRequested?.Invoke(CaptureDestination.File);
            else if (id == ClipboardId) ShowHistoryRequested?.Invoke(_platform.ForegroundWindow);
            else if (id == ToggleId) _ = ConvertSelectionAsync(null);
            else if (id == LayoutSwitchId) _ = CycleInputLayoutAsync();
            else if (id == AddWordId) _ = CapturePersonalWordAsync();
            else if (id == CaseId) _ = ChangeCaseAsync();
            else if (id == TranslitId) _ = TransliterateAsync();
            else if (id == UndoId) _ = UndoLastTransformAsync();
            else if (_layoutHotkeys.TryGetValue(id, out var layout)) _ = ConvertSelectionAsync(layout);
        }
        catch (Exception e) { Log.Error("Hotkey handler failed", e); }
    }

    /// <summary>Serializes a user-requested capture with all other clipboard tools. Cancellation never writes.</summary>
    internal async Task<ImageCodec.Encoded?> CaptureScreenshotAsync(Func<Task<CapturedPixels?>> capture, bool copyToClipboard = true)
    {
        if (!_platform.SupportsScreenCapture || !await _convertGate.WaitAsync(0)) return null;
        if (_remote is not null)
        {
            try
            {
                var reply = await _remote.CallAsync(new("capture-begin"), allowRejected: true);
                if (!reply.Ok) { _convertGate.Release(); return null; }
                UpdateRemoteState(reply);
            }
            catch { _convertGate.Release(); throw; }
        }
        _capturingScreen = true;
        try
        {
            LastTransform = null;
            _recentTyping.Reset(Interlocked.Read(ref _typingVersion));
            var pixels = await capture();
            if (pixels is null) return null;
            var image = ImageCodec.FromBgra(pixels.Bgra, pixels.Width, pixels.Height);
            if (copyToClipboard) PublishScreenshot(image, pixels.Bgra);
            return image;
        }
        finally
        {
            _capturingScreen = false; _convertGate.Release();
            if (_remote is not null) await _remote.CallAsync(new("capture-end"));
        }
    }

    internal async Task<bool> CopyScreenshotAsync(ImageCodec.Encoded image)
    {
        if (_remote is not null)
        {
            var reply = await _remote.CallAsync(new("capture-begin"), allowRejected: true);
            if (!reply.Ok) return false;
            UpdateRemoteState(reply);
            try { var (pixels, _, _) = ImageCodec.DecodeBgra(image.Png); PublishScreenshot(image, pixels); return true; }
            finally { await _remote.CallAsync(new("capture-end")); }
        }
        if (!await _convertGate.WaitAsync(0)) return false;
        try
        {
            var (pixels, _, _) = ImageCodec.DecodeBgra(image.Png);
            PublishScreenshot(image, pixels);
            return true;
        }
        finally { _convertGate.Release(); }
    }

    private void PublishScreenshot(ImageCodec.Encoded image, byte[] bgra)
    {
        if (_remote is not null) _remote.Call(new("image-write-begin"));
        try
        {
            using (_platform.SuspendCapture())
            {
                _adoptedCopy = null;
                _platform.SetImage(image.Png, bgra, image.Width, image.Height);
            }
            if (ShouldRecord(new RawClip(null, null, true, "Typlo")))
                Store.Add(ClipContent.FromImage(image.Png, image.Thumbnail, image.Width, image.Height, "Typlo"));
        }
        finally { if (_remote is not null) _remote.Call(new("image-write-end")); }
    }

    /// <summary>Copies the selection, converts it to <paramref name="target"/> (null = toggle) and pastes it back.</summary>
    public Task ConvertSelectionAsync(KeyboardLayout? target)
    {
        if (target is { SupportsConversion: false }) return SwitchInputSourceAsync(target);
        if (target is null && _applicationCycles.Count == 0 && LayoutCatalog.Cycle(Settings).Count < 2) return Task.CompletedTask;
        return TransformSelectionCoreAsync((text, source) =>
        {
            var result = target is null ? _converter.Toggle(text, source) : _converter.ConvertTo(text, target, source);
            return (result.Changed ? result.Text : text, result.Target);
        }, switchToIfNothingSelected: target, cycleForApplication: target is null);
    }

    private async Task SwitchInputSourceAsync(KeyboardLayout layout)
    {
        await StopPendingAutomaticWords();
        var origin = _platform.ForegroundWindow;
        if (!await EnterManualTextOperationAsync(origin)) return;
        try
        {
            var window = origin;
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!IsTargetActive(window)) return;
            LastTransform = null;
            _recentTyping.Reset(Interlocked.Read(ref _typingVersion));
            _platform.SwitchKeyboardLayout(window, layout);
        }
        catch (Exception e) { Log.Error("Input source switch failed", e); }
        finally { ExitManualTextOperation(); }
    }

    /// <summary>Cycles the case of the selection (lower → Title → UPPER).</summary>
    public Task ChangeCaseAsync() => TransformSelectionAsync(text => (CaseConverter.Cycle(text), null));

    /// <summary>Transliterates the selection: «Привет» ⇄ "Privet".</summary>
    public Task TransliterateAsync() => TransformSelectionAsync(text => (Transliterator.Toggle(text), null));

    /// <summary>
    /// Copies an explicit selection or selects only the tracked recent input, replacing it with
    /// <paramref name="transform"/>'s result and puts the user's clipboard back. Optionally switches the layout.
    /// </summary>
    internal async Task TransformSelectionAsync(Func<string, (string Text, KeyboardLayout? SwitchTo)> transform,
        KeyboardLayout? switchToIfNothingSelected = null)
        => await TransformSelectionCoreAsync((text, _) => transform(text), switchToIfNothingSelected);

    private async Task TransformSelectionCoreAsync(Func<string, KeyboardLayout?, (string Text, KeyboardLayout? SwitchTo)> transform,
        KeyboardLayout? switchToIfNothingSelected = null, bool cycleForApplication = false, AutomaticWord? automaticWord = null)
    {
        if (!_platform.Capabilities.SimulateInput && !_platform.SupportsDirectTextEditing)
        {
            if (automaticWord is not null) automaticWord.Outcome = AutoCorrectionReason.Unsupported;
            Log.Info("Text tools: synthetic input is not available on this platform/session");
            return;
        }
        var origin = _platform.ForegroundWindow;
        var originInput = Interlocked.Read(ref _typingVersion);
        if (automaticWord is null) await StopPendingAutomaticWords();
        else if (!AutomaticWordIsCurrent(automaticWord)) return;
        if (!await EnterManualTextOperationAsync(origin)) { if (automaticWord is not null) automaticWord.Outcome = AutoCorrectionReason.Busy; return; }
        var timing = new TransformTiming();
        try
        {
            if (_platform.ForegroundWindow != origin || Interlocked.Read(ref _typingVersion) != originInput ||
                (automaticWord is not null && !AutomaticWordIsCurrent(automaticWord))) return;
            var window = origin;
            var inputVersion = originInput;
            var element = PinDirectTarget(ref window);
            lock (_applicationFocusLock) _manualTextWindow = window;
            var application = cycleForApplication ? ApplicationCycleFor(window) : null;
            if (cycleForApplication && application is null && LayoutCatalog.Cycle(Settings).Count < 2) return;
            if (application is not null)
            {
                transform = (text, source) =>
                {
                    var result = application.Layouts.Length == 1
                        ? application.Converter.ConvertTo(text, application.Layouts[0], source)
                        : application.Converter.Toggle(text, source);
                    return (result.Changed ? result.Text : text, result.Target);
                };
                if (application.Layouts.Length == 1) switchToIfNothingSelected = application.Layouts[0];
            }
            bool NoInputSince() => Volatile.Read(ref _disposed) == 0 && Interlocked.Read(ref _typingVersion) == inputVersion;
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            timing.Mark("modifiers");
            if (!NoInputSince()) return;
            if (automaticWord is not null)
            {
                await TransformAutomaticWordWithBatchesAsync(automaticWord, transform, NoInputSince);
                return;
            }
            var directHandled = await TryDirectTransformAsync(window, element, inputVersion, NoInputSince, transform, switchToIfNothingSelected);
            timing.Mark("direct adapter");
            if (directHandled) return;
            if (!_platform.Capabilities.SimulateInput || !NoInputSince()) return;
            await TransformWithSelectionAsync(window, element, inputVersion, NoInputSince, transform,
                switchToIfNothingSelected, timing);
        }
        catch (SelectionChangedException)
        {
            if (automaticWord is not null) automaticWord.Outcome = AutoCorrectionReason.Canceled;
            Log.Info("Text tool stopped: the copied selection is no longer verified");
        }
        catch (ClipboardTakenException)
        {
            if (automaticWord is not null) automaticWord.Outcome = AutoCorrectionReason.ClipboardChanged;
            Log.Info("Text tool stopped: another app changed the clipboard meanwhile");
        }
        catch (Exception e)
        {
            if (automaticWord is not null) automaticWord.Outcome = AutoCorrectionReason.Failed;
            Log.Error("Text tool failed", e);
        }
        finally
        {
            ExitManualTextOperation();
            timing.Finish();
        }
    }

    /// <summary>Only phase names and elapsed time, never text or key contents.</summary>
    private sealed class TransformTiming
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly List<string> _phases = [];
        private long _previous;

        public void Mark(string phase)
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _phases.Add($"{phase}={elapsed - _previous}ms");
            _previous = elapsed;
        }

        public void Finish()
        {
            Mark("settle/restore");
            Log.Info($"Text tool timing: {string.Join(", ", _phases)}; total={_previous}ms");
        }
    }

    /// <summary>Clipboard sequence right after our last own write or triggered copy.</summary>
    private ulong _expectedSequence;

    private bool IsTargetActive(nint window) => window != 0 && _platform.ForegroundWindow == window;

    private ClipboardSnapshot CaptureClipboard()
    {
        _verifiedSelection = null;
        var sequence = _platform.ClipboardSequence;
        var snapshot = _platform.Snapshot();
        if (_platform.ClipboardSequence != sequence) throw new ClipboardTakenException();
        _expectedSequence = sequence;
        return snapshot;
    }

    /// <summary>
    /// Restores the user's clipboard, unless something else was copied since our last change
    /// (the user copied a new item meanwhile: keep it).
    /// </summary>
    private void RestoreIfUnchanged(ClipboardSnapshot snapshot)
    {
        if (snapshot.Formats.Sum(f => (long)f.Data.Length) >= 1024 * 1024) _memory.Schedule();
        _verifiedSelection = null;
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

    /// <summary>Thrown to stop an operation when another app changed the clipboard while Typlo was using it.</summary>
    private sealed class ClipboardTakenException() : Exception("The clipboard was changed by another app.");
    private sealed class SelectionChangedException() : Exception("The copied selection can no longer be verified.");
    private (nint Window, string Text, long? FocusVersion)? _verifiedSelection;

    private static string ComparableSelection(string text) => text.Replace("\r\n", "\n").Replace('\u00a0', ' ').Replace('\u202f', ' ');
    private static bool SameSelection(string a, string b) => ComparableSelection(a) == ComparableSelection(b);
    private bool ForegroundChanged(long? before) => before is { } version && _platform.ForegroundChangeVersion != version;
    private bool SelectionFocusChanged(nint window, string text) => _verifiedSelection is not { } copy ||
        copy.Window != window || !SameSelection(copy.Text, text) || ForegroundChanged(copy.FocusVersion);

    /// <summary>The selection we copied and the window whose app wrote it (to recognise that app's late flush).</summary>
    private (string Text, nint Window)? _adoptedCopy;

    /// <summary>Stops the operation if someone else wrote to the clipboard since our last change.</summary>
    private void EnsureClipboardIsOurs()
    {
        var now = _platform.ClipboardSequence;
        if (now == _expectedSequence) return;
        // OLE apps may flush the copy we triggered a moment later: same owner, same text. That is not a new copy.
        if (_adoptedCopy is { } copy && _platform.IsClipboardOwnedBy(copy.Window) == true && _platform.ReadText() == copy.Text &&
            _platform.ClipboardSequence == now)
        {
            _expectedSequence = now;
            return;
        }
        throw new ClipboardTakenException();
    }

    /// <summary>Writes the clipboard only if it still holds what we expect, so a fresh copy is never overwritten.</summary>
    private void SetOwnText(string text)
        => WriteOwnClipboard(() => _platform.SetText(text), text);

    private void WriteOwnClipboard(Action write, string? expectedText = null)
    {
        EnsureClipboardIsOurs();
        _adoptedCopy = null; // our write replaces the adopted copy; a later change is no longer its flush
        write();
        var sequence = _platform.ClipboardSequence;
        // A write can be followed immediately by an external copy, before we read its sequence number.
        if ((expectedText is not null && _platform.ReadText() != expectedText) || _platform.ClipboardSequence != sequence)
            throw new ClipboardTakenException();
        _expectedSequence = sequence;
    }

    /// <summary>
    /// Pastes text into <paramref name="window"/> through the clipboard (the caller restores the clipboard).
    /// Nothing is pasted if the user switched to another window meanwhile, or if <paramref name="stillValid"/> (the
    /// operation's own guard, e.g. "no input since the selection was made") fails right before Ctrl+V.
    /// </summary>
    private async Task<bool> PasteTextAsync(string text, nint window, Func<bool> stillValid, string expectedSelection, Action? onSent = null)
    {
        // Without an event counter, make an independent second copy before replacement instead of
        // trusting the copy that crossed the clipboard settle wait. Windows also detects hidden
        // away/return cycles throughout conversion and the final clipboard checks.
        var resumed = _verifiedSelection?.FocusVersion is null;
        async Task<bool> VerifyAfterResume()
        {
            if (!resumed && !SelectionFocusChanged(window, expectedSelection)) return true;
            // A second interruption during this verification is a safe abort, not an unbounded retry.
            var current = await CopySelectionAsync(window, stillValid, allowRevalidation: false);
            resumed = false;
            return current is not null && SameSelection(current, expectedSelection);
        }
        if (!await _platform.WaitForTargetAsync(window, stillValid, () => resumed = true) || !await VerifyAfterResume()) return false;
        if (!stillValid())
        {
            Log.Info("Paste skipped: the user typed or moved the caret before it");
            return false;
        }
        var unicode = PreferUnicodeReplacement(window, text);
        if (!unicode) SetOwnText(text);
        // SetOwnText returns only after the write is readable and its sequence has been verified.
        // There is no pending clipboard write to wait for before sending Paste.
        // Clipboard, target, clipboard again, target again: the clipboard check can block (Linux runs xclip), so
        // neither a copy made during the target check nor a focus/caret change during the clipboard check gets through.
        for (var pass = 0; pass < 2; pass++)
        {
            // If another app copied something since our write, that content would be pasted.
            EnsureClipboardIsOurs();
            if (!await _platform.WaitForTargetAsync(window, stillValid, () => resumed = true))
            {
                Log.Info("Paste skipped: the active window changed");
                return false;
            }
            if (resumed || SelectionFocusChanged(window, expectedSelection))
            {
                if (!await VerifyAfterResume()) return false;
                if (!unicode) SetOwnText(text);
            }
            if (!stillValid())
            {
                Log.Info("Paste skipped: the user typed or moved the caret meanwhile");
                return false;
            }
        }
        EnsureClipboardIsOurs();
        if (!IsTargetActive(window) || !stillValid() || SelectionFocusChanged(window, expectedSelection)) return false;
        if (unicode)
        {
            try { if (!_platform.TypeText(text, stillValid)) return false; }
            catch
            {
                // A partial SendInput is an uncertain write. Discard range ownership/Undo
                // and never attempt clipboard replacement for this same command.
                LastTransform = null; onSent?.Invoke(); throw;
            }
        }
        else _platform.SendPaste();
        Log.Info("Text replacement: " + (unicode ? "unicode" : "clipboard"));
        onSent?.Invoke();
        // Give the target app time to read the clipboard before it changes again.
        await Task.Delay(unicode ? 40 : 300);
        return true;
    }

    // ---------------------------------------------------------------- undo

    private sealed record TransformRecord(nint Window, string Original, string Result, DateTime At, KeyboardLayout? ResultLayout,
        DirectTextSnapshot? Direct = null, int DirectStart = 0, long DirectVersion = -1, RecentTyping.Run? AutomaticRun = null);

    private TransformRecord? _lastTransform;

    /// <summary>
    /// The exact last replacement, used by Undo and consecutive conversion commands. Genuine input invalidates it.
    /// </summary>
    private TransformRecord? LastTransform
    {
        get => _lastTransform;
        set
        {
            _lastTransform = value;
            // Input can arrive between a successful write's final check and publishing its record.
            if (value?.Direct is not null && (value.DirectVersion != Interlocked.Read(ref _typingVersion) || Volatile.Read(ref _disposed) != 0))
            {
                Log.Info("Direct text: range discarded during publication (input changed or controller disposed)");
                Interlocked.CompareExchange(ref _lastTransform, null, value);
            }
            UpdateTypingWatch();
        }
    }

    private TransformRecord? CaptureLastTransform(nint window)
    {
        var last = _lastTransform;
        if (last is null) return null;
        // Repeat commands keep their verified range until genuine input or a context change.
        // The two-minute limit applies only to Undo, not to repeated conversion.
        if (last.Window == window) return last;
        LastTransform = null;
        return null;
    }

    /// <summary>
    /// Puts back the text replaced by the last layout fix, case change or transliteration: selects the inserted
    /// text left of the caret and pastes the original. Works within two minutes, in the same window.
    /// </summary>
    public async Task UndoLastTransformAsync()
    {
        await StopPendingAutomaticWords();
        if (_lastTransform is not { } last || DateTime.UtcNow - last.At > TimeSpan.FromMinutes(2)) return;
        if ((!_platform.Capabilities.SimulateInput && last.Direct is null) || !await _convertGate.WaitAsync(0)) return;
        ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            var window = last.Window;
            var undoVersion = Interlocked.Read(ref _typingVersion);
            var undoFocus = _platform.ForegroundChangeVersion;
            var undoElement = _platform.FocusedInputElement;
            // The record is cleared by any user input; re-check it after every wait and before every keystroke.
            bool StillValid() => Volatile.Read(ref _disposed) == 0 && ReferenceEquals(_lastTransform, last) &&
                (last.Direct is null || last.DirectVersion == Interlocked.Read(ref _typingVersion));
            if (last.Direct is not null)
            {
                await UndoDirectAsync(last, StillValid);
                return;
            }
            if (!await _platform.WaitForTargetAsync(window, StillValid))
            {
                Log.Info("Undo skipped: the conversion can no longer be located");
                return;
            }
            using var _ = _platform.SuspendCapture();
            snapshot = CaptureClipboard();
            using var protectedSelection = NeedsPasteVerification(window) && _platform.SupportsProtectedCopy
                ? ProtectOwnedSelection(window, StillValid) : null;
            // Only replace what Typlo inserted: if the user typed or moved since, the selection is different.
            var selected = await SelectTransformResultAsync(last, StillValid);
            if (selected is null || !StillValid())
            {
                LastTransform = null;
                Log.Info("Undo skipped: the text before the caret is no longer the converted text");
                return;
            }
            if (await PasteTextAsync(last.Original, window, StillValid, last.Result))
            {
                // Only a verified automatic replacement can seed this temporary, caret-local exemption.
                // Failed/canceled Undo must not protect an unrelated word or overwrite newer input.
                if (last.AutomaticRun is { } run && StillValid() &&
                    await VerifyPastedRangeAsync(window, last.Result, last.Original, StillValid,
                        probeRemainingSelection: !PreferUnicodeReplacement(window, last.Original)) == ClipboardPasteState.Confirmed &&
                    StillValid() && !ForegroundChanged(undoFocus) && _platform.FocusedInputElement == undoElement)
                    _recentTyping.RestoreAfterUndo(run, undoVersion, Environment.TickCount64, undoElement, undoFocus);
                LastTransform = null;
            }
        }
        catch (SelectionChangedException) { Log.Info("Undo stopped: the copied selection is no longer verified"); }
        catch (ClipboardTakenException) { Log.Info("Undo stopped: another app changed the clipboard meanwhile"); }
        catch (Exception e) { Log.Error("Undo failed", e); }
        finally
        {
            if (snapshot is not null) RestoreIfUnchanged(snapshot);
            _convertGate.Release();
        }
    }

    /// <summary>Drops a selection Typlo made (caret back to its end), if the target window is still active.</summary>
    private void Collapse(nint window)
    {
        if (IsTargetActive(window)) _platform.SendChord(KeyModifiers.None, "Right");
    }

    // ---------------------------------------------------------------- snippets

    private readonly SnippetMatcher _snippets = new();
    private readonly RecentTyping _recentTyping = new();
    private long _typingVersion;
    private long _caretVersion;

    private void ApplySnippets()
    {
        _snippets.SetSnippets(Settings.Snippets, AvailableLayouts);
        UpdateTypingWatch();
    }

    // Always on where Typlo can type: snippets need it, and every conversion and Undo needs to know about user input
    // (a conversion published after its paste, an Undo in progress) to never act at a caret the user moved.
    private void UpdateTypingWatch() => _platform.SetTypingWatch(Volatile.Read(ref _disposed) == 0 &&
        (_platform.Capabilities.SimulateInput || _platform.SupportsDirectTextEditing));

    private void OnKeyTyped(TypedKey key)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _lastInputAt, Environment.TickCount64);
        if (key.Shortcut is { } shortcut)
        {
            bool own;
            lock (_activeHotkeys) own = _activeHotkeys.Contains(shortcut);
            if (own) return; // e.g. the Undo hotkey must not invalidate the conversion it is about to undo
        }
        Volatile.Read(ref _pendingCopyInput)?.Observe(key);
        if (!key.LayoutGesture) Interlocked.Increment(ref _caretVersion);
        var version = Interlocked.Increment(ref _typingVersion);
        // Any user input (a key, Backspace, arrows, a click) may move the caret: an older conversion can no longer
        // be located reliably, so it cannot be undone.
        LastTransform = null;
        nint typedWindow = 0;
        var previousWindow = _recentTyping.Window;
        try
        {
            var now = Environment.TickCount64;
            if (_recentTyping.HasUndoSuppression)
                _recentTyping.ValidateUndoContext(_platform.ForegroundWindow, _platform.FocusedInputElement, _platform.ForegroundChangeVersion);
            if (key.Reset)
            {
                if (!key.LayoutGesture || !_recentTyping.HasUndoSuppression) _recentTyping.Reset(version);
            }
            else if (key.Backspace)
            {
                var window = _platform.ForegroundWindow;
                _recentTyping.Backspace(window, now, version, CaptureDirectInputElement(window));
            }
            else
            {
                typedWindow = _platform.ForegroundWindow;
                _recentTyping.Record(key.Char, typedWindow, now, version, CaptureDirectInputElement(typedWindow));
                if (key.Char == ' ') QueueAutomaticWord(typedWindow, version);
            }
        }
        catch (Exception e) { _recentTyping.Reset(version); Log.Error("Cannot track input context", e); }
        Snippet? hit;
        lock (_snippets)
        {
            if (_editingSettings || Volatile.Read(ref _updating) != 0) { _snippets.Reset(); return; }
            if (typedWindow != 0 && previousWindow != 0 && typedWindow != previousWindow) _snippets.Reset();
            if (key.Reset) { _snippets.Reset(); return; }
            if (key.Backspace) { _snippets.Backspace(); return; }
            hit = _snippets.OnChar(key.Char);
        }
        // Expand on the thread pool, but remember the source window before dispatching.
        if (hit is not null)
        {
            try
            {
                var window = typedWindow != 0 ? typedWindow : _platform.ForegroundWindow;
                if (window == 0) return;
                ThreadPool.QueueUserWorkItem(_ => _ = ExpandSnippetAsync(hit, window, version));
            }
            catch (Exception e) { Log.Error("Cannot capture snippet target", e); }
        }
    }

    /// <summary>Deletes the typed trigger and pastes the snippet text in its place.</summary>
    /// <param name="window">The window that was active when the trigger was typed; nothing happens in any other.</param>
    public Task ExpandSnippetAsync(Snippet snippet, nint window) =>
        ExpandSnippetAsync(snippet, window, Interlocked.Read(ref _typingVersion));

    private async Task ExpandSnippetAsync(Snippet snippet, nint window, long typingVersion)
    {
        if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(2))) return;
        ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(500));
            bool StillValid() => IsTargetActive(window) && Interlocked.Read(ref _typingVersion) == typingVersion;
            if (!StillValid())
            {
                Log.Info("Snippet skipped: another window is active or more text was typed");
                return;
            }
            LastTransform = null;
            _recentTyping.Consume(typingVersion);

            // Line breaks and tabs typed as keys would act as Enter/Tab (submit a chat message, move to another field),
            // so such snippets are pasted as text. The clipboard is prepared before anything is deleted.
            var template = SnippetTemplate.Parse(snippet.Text);
            if (template.CursorCount > 1 || template.Text.Length == 0) return;
            var paste = template.Text.IndexOfAny(['\n', '\r', '\t']) >= 0 || template.Suffix.Length > 0;
            // Disposed on every path, including a failed snapshot or write below.
            using var _ = paste ? _platform.SuspendCapture() : null;
            if (paste)
            {
                snapshot = CaptureClipboard();
                SetOwnText(template.Text);
            }

            var trigger = snippet.Trigger.Trim();
            // Every destructive key is preceded by the clipboard check (which can block: Linux runs xclip) and then by
            // the target/input check, so a focus or caret change during the clipboard check is still seen.
            for (var deleted = 0; deleted < trigger.Length; deleted++)
            {
                if (paste && !ClipboardStillOurs())
                {
                    RetypeTrigger(trigger, deleted, StillValid);
                    return;
                }
                if (!StillValid())
                {
                    // The user typed, clicked or switched windows: the caret position is no longer known, so nothing is
                    // typed back (that could overwrite whatever the user selected). Only part of the trigger is gone.
                    Log.Info("Snippet stopped half-way: the user moved on");
                    return;
                }
                _platform.SendChord(KeyModifiers.None, "Backspace");
            }
            // Never paste another app's copy. If one arrived while the trigger was deleted, keep it, and put the
            // trigger back while the caret is still where Typlo left it. Checked twice around the target check, as in
            // PasteTextAsync, so the last check before Ctrl+V is the target/input one.
            for (var pass = 0; pass < (paste ? 2 : 1); pass++)
            {
                if (paste && !ClipboardStillOurs())
                {
                    RetypeTrigger(trigger, trigger.Length, StillValid);
                    return;
                }
                if (!StillValid())
                {
                    Log.Info("Snippet stopped: the user moved on before the text was inserted");
                    return;
                }
            }

            if (paste)
            {
                _platform.SendPaste();
                await Task.Delay(300); // let the target read the clipboard before it is restored
            }
            else if (!_platform.TypeText(template.Text, StillValid))
            {
                Log.Info("Snippet stopped while typing: the target window changed or the user typed");
                return;
            }
            if (template.CursorOffset is not null && template.Suffix.Length > 0 && StillValid())
            {
                var expected = template.Suffix.Replace("\r\n", "\n");
                var selected = await SelectAndVerifyAsync(window, template.CursorSteps, StillValid,
                    text => text.Replace("\r\n", "\n") == expected, maxSteps: 100_000);
                // Left collapses our verified selection to its start: the exact marker position.
                if (selected is not null && StillValid()) _platform.SendChord(KeyModifiers.None, "Left");
                else Log.Info("Snippet cursor not placed: its suffix or input context changed");
            }
            Log.Info("Snippet expanded");
        }
        catch (SelectionChangedException) { Log.Info("Snippet stopped: the copied selection is no longer verified"); }
        catch (ClipboardTakenException) { Log.Info("Snippet stopped: another app changed the clipboard meanwhile"); }
        catch (Exception e) { Log.Error("Snippet expansion failed", e); }
        finally
        {
            if (snapshot is not null) RestoreIfUnchanged(snapshot);
            _convertGate.Release();
        }
    }

    private bool ClipboardStillOurs()
    {
        try
        {
            EnsureClipboardIsOurs();
            return true;
        }
        catch (ClipboardTakenException) { return false; }
    }

    /// <summary>
    /// Another app copied while a multi-line snippet was being inserted: its copy stays, so the snippet cannot be
    /// pasted. The deleted part of the trigger is typed back, only while no user input has moved the caret.
    /// </summary>
    private void RetypeTrigger(string trigger, int deleted, Func<bool> stillValid)
    {
        Log.Info("Snippet stopped: another app changed the clipboard meanwhile");
        if (deleted > 0 && stillValid()) _platform.TypeText(trigger[^deleted..], stillValid);
    }

    /// <summary>Sends Copy and returns the selected text, or null when nothing is selected.</summary>
    private async Task<string?> CopySelectionAsync(nint window, Func<bool> stillValid, bool allowRevalidation = true,
        Func<bool>? sendCopy = null, TimeSpan? copyTimeout = null, Func<string, bool>? expectedCopy = null,
        Func<bool>? canRestoreCancelledCopy = null)
    {
        _verifiedSelection = null;
        var focusVersion = _platform.ForegroundChangeVersion;
        var resumed = false;
        if (!await _platform.WaitForTargetAsync(window, stillValid, () => resumed = true)) throw new SelectionChangedException();
        // A unique placeholder makes even identical copies visible on content-hashing platforms.
        var placeholder = "\u2063typlo-" + Guid.NewGuid().ToString("N");
        SetOwnText(placeholder);
        var before = _platform.ClipboardSequence;
        if (!await _platform.WaitForTargetAsync(window, stillValid, () => resumed = true)) throw new SelectionChangedException();
        using var copyInput = new CopyInputObservation(this);
        if (sendCopy is not null) { if (!sendCopy()) throw new SelectionChangedException(); }
        else _platform.SendCopy();
        if (await WaitForClipboardChange(before, copyTimeout ?? TimeSpan.FromSeconds(2)) is not { } change)
        {
            if (resumed || ForegroundChanged(focusVersion) || !stillValid()) throw new SelectionChangedException();
            return null;
        }
        var observed = change.Sequence;
        if (!stillValid())
        {
            // The editor may have completed our Copy just before the user resumed typing.
            // Keep ownership for restoring the saved clipboard, even though text editing stops.
            // An unowned/unknown clipboard write is never adopted on this cancellation path.
            // A manual command can also cancel an automatic Copy without changing input/focus.
            // Its caller may authorize restoration in that unchanged context, but only for the
            // expected word, a confirmed target owner and a stable clipboard sequence.
            if (expectedCopy is not null && (copyInput.CopyCompletedBeforeTyping(observed) || canRestoreCancelledCopy?.Invoke() == true) &&
                _platform.IsClipboardOwnedBy(window) == true && _platform.ClipboardSequence == observed)
            {
                var copied = _platform.ReadText();
                if (copied is not null && MatchKnownEditorCopy(window, copied, expectedCopy) is not null &&
                    _platform.ClipboardSequence == observed && _platform.IsClipboardOwnedBy(window) == true)
                { _expectedSequence = observed; _adoptedCopy = (copied, window); }
            }
            throw new SelectionChangedException();
        }
        if (!await _platform.WaitForTargetAsync(window, stillValid, () => resumed = true)) throw new SelectionChangedException();
        // Never replace a foreign clipboard write with another verification copy.
        var owned = _platform.IsClipboardOwnedBy(window);
        if (owned == false || (owned is null && change.ChangedAgain) || _platform.ClipboardSequence != observed)
            throw new ClipboardTakenException();
        var text = _platform.ReadText();
        var rawText = text;
        if (text is not null && expectedCopy is not null)
            text = MatchKnownEditorCopy(window, text, expectedCopy) ?? throw new ClipboardTakenException();
        _expectedSequence = observed;
        if (rawText is not null && _platform.IsClipboardOwnedBy(window) == true) _adoptedCopy = (rawText, window);
        EnsureClipboardIsOurs();
        var lineCopy = _platform.IsEmptySelectionLineCopy();
        EnsureClipboardIsOurs();
        var interrupted = resumed || ForegroundChanged(focusVersion);
        if (!stillValid() || !IsTargetActive(window)) throw new SelectionChangedException();
        if (string.IsNullOrEmpty(text) || text == placeholder || lineCopy)
        {
            if (interrupted) throw new SelectionChangedException();
            return null;
        }
        if (interrupted)
        {
            // The clipboard still contains what was selected before blur. Only a new copy can
            // establish the current range. Another interruption during that copy aborts safely.
            if (!allowRevalidation) throw new SelectionChangedException();
            var current = await CopySelectionAsync(window, stillValid, allowRevalidation: false, sendCopy, copyTimeout, expectedCopy, canRestoreCancelledCopy);
            if (current is null || !SameSelection(current, text)) throw new SelectionChangedException();
            return current;
        }
        // Retain the version from BEFORE the copy, not a newer version sampled after verification.
        // A transition just after this check must still invalidate the range before Paste.
        _verifiedSelection = (window, text, focusVersion);
        return text;
    }

    /// <summary>
    /// Waits for the copy we triggered and until the clipboard stops changing (OLE apps write in two steps: set,
    /// then flush); returns the final sequence and whether it changed more than once, or null if nothing was copied.
    /// </summary>
    private async Task<(ulong Sequence, bool ChangedAgain)?> WaitForClipboardChange(ulong before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var now = _platform.ClipboardSequence;
            if (now != before)
            {
                var changedAgain = false;
                var settleDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
                while (DateTime.UtcNow < settleDeadline)
                {
                    await Task.Delay(40);
                    var later = _platform.ClipboardSequence;
                    if (later == now) break;
                    now = later;
                    changedAgain = true;
                }
                return (now, changedAgain);
            }
            await Task.Delay(10);
        }
        return null;
    }

    private Task<string?> SelectRecentInputAsync(RecentTyping.Run run, Func<bool> stillValid, KeyboardLayout? preferred = null) =>
        SelectAndVerifyAsync(run.Window, run.Keys.Length, stillValid,
            text => run.MatchSource(text, SourceLayouts(preferred)) is not null,
            remainingSteps: text => text.Length is > 0 && text.Length < run.Keys.Length &&
                new RecentTyping.Run(run.Window, run.Keys[^text.Length..]).MatchSource(text, SourceLayouts(preferred)) is not null
                    ? run.Keys.Length - text.Length : null);

    private Task<string?> SelectTransformResultAsync(TransformRecord last, Func<bool> stillValid)
    {
        // Editors move over CRLF in one step, and over a whole grapheme (including surrogate pairs).
        var expected = ComparableSelection(last.Result);
        var steps = System.Globalization.StringInfo.ParseCombiningCharacters(expected).Length;
        return SelectAndVerifyAsync(last.Window, steps, stillValid,
            text => ComparableSelection(text) == expected, remainingSteps: text =>
            {
                var suffix = ComparableSelection(text);
                if (suffix.Length == 0 || suffix.Length >= expected.Length || !expected.EndsWith(suffix, StringComparison.Ordinal)) return null;
                var remaining = steps - System.Globalization.StringInfo.ParseCombiningCharacters(suffix).Length;
                return remaining > 0 ? remaining : null;
            });
    }

    private async Task<string?> SelectAndVerifyAsync(nint window, int steps, Func<bool> stillValid, Func<string, bool> matches,
        int maxSteps = 2000, Func<string, int?>? remainingSteps = null)
    {
        if (steps < 1 || steps > maxSteps) return null;
        var selectedByUs = false;
        try
        {
            bool BeforeSelectionKey()
            {
                EnsureClipboardIsOurs();
                if (!stillValid()) return false;
                if (IsTargetActive(window)) selectedByUs = true;
                return true;
            }
            var remaining = steps;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var status = await _platform.SelectPreviousCharactersAsync(window, remaining, BeforeSelectionKey);
                if (status == SelectionStatus.Cancelled || !await _platform.WaitForTargetAsync(window, stillValid)) return null;
                var text = await CopySelectionAsync(window, stillValid, expectedCopy: NeedsPasteVerification(window)
                    ? candidate => matches(candidate) || status == SelectionStatus.Interrupted && remainingSteps?.Invoke(candidate) is > 0 : null);
                if (text is not null && stillValid() && matches(text))
                {
                    selectedByUs = false; // keep this verified selection for the replacement
                    return text;
                }
                // A notification may have interrupted an input batch. Extend only a nonempty selection
                // verified as the suffix of this exact run/result. A collapsed or unrelated range is refused.
                if (status != SelectionStatus.Interrupted || text is null || !stillValid() ||
                    remainingSteps?.Invoke(text) is not { } left || left < 1 || left >= steps)
                {
                    Log.Info($"Text selection could not be verified (steps={steps}, copied length={text?.Length ?? 0})");
                    return null;
                }
                remaining = left;
                Log.Info($"Text selection resumed after transient activation (remaining steps={remaining})");
            }
            return null;
        }
        finally
        {
            if (selectedByUs && stillValid()) Collapse(window);
        }
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

    private bool ShouldRecord(RawClip raw) => Settings.HistoryEnabled && !IsPaused &&
        !(raw.SourceApp is { } app && Settings.ExcludedApps.Any(x => string.Equals(x.Trim(), app, StringComparison.OrdinalIgnoreCase)));

    private void OnClipboardCaptured(RawClip raw)
    {
        if (!ShouldRecord(raw)) return;
        try
        {
            if (raw.Files is { Count: > 0 } files) Store.Add(ClipContent.FromFiles(files, raw.SourceApp));
            else if (raw.Text is not null) Store.Add(ClipContent.FromText(raw.Text, raw.SourceApp, raw.Html, raw.Rtf));
            else if (raw.Image is { } bytes && bytes.LongLength <= Settings.MaxImageBytes * 4) _images.Post(raw);
        }
        catch (Exception e) { Log.Error("Storing clip failed", e); }
    }

    private void StoreCapturedImage(RawClip raw, DateTimeOffset capturedAt)
    {
        if (!ShouldRecord(raw) || raw.Image is not { } bytes) return;
        try
        {
            var img = ImageCodec.Encode(bytes, raw.ImageIsPng);
            if (ShouldRecord(raw)) Store.Add(ClipContent.FromImage(img.Png, img.Thumbnail, img.Width, img.Height, raw.SourceApp), capturedAt);
        }
        finally { if (bytes.Length >= 1024 * 1024) _memory.Schedule(); }
    }

    internal void IngestDiagnosticImage(byte[] png)
    {
        if (!_diagnostic) throw new InvalidOperationException("Diagnostic image input is disabled");
        OnClipboardCaptured(new RawClip(null, png, true, "Typlo diagnostic"));
    }

    /// <summary>
    /// Puts a history item on the clipboard, focuses the previous window and pastes it.
    /// <paramref name="plainText"/> drops the HTML/RTF formatting of text items.
    /// </summary>
    public async Task PasteAsync(long id, nint targetWindow, bool paste = true, bool plainText = false)
    {
        if (_remote is not null) { await _remote.CallAsync(new("paste", id, (long)targetWindow, plainText, paste ? "paste" : "copy")); return; }
        var largeImage = false;
        // One clipboard operation at a time: a paste must not interleave with a conversion or snippet.
        if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(3))) return;
        try
        {
            var content = Store.Get(id);
            if (content is null) return;
            largeImage = content.ImagePng is { Length: >= 1024 * 1024 };
            LastTransform = null;
            _recentTyping.Consume(Interlocked.Read(ref _typingVersion));
            _expectedSequence = _platform.ClipboardSequence;
            switch (content.Kind)
            {
                case ClipKind.Text when content.HasFormatting && !plainText:
                    WriteOwnClipboard(() => _platform.SetRichText(content.Text!, content.Html, content.Rtf), content.Text);
                    break;
                case ClipKind.Text:
                    SetOwnText(content.Text!);
                    break;
                case ClipKind.Files:
                    WriteOwnClipboard(() => _platform.SetFiles(content.Files));
                    break;
                default:
                    var (bgra, w, h) = ImageCodec.DecodeBgra(content.ImagePng!);
                    WriteOwnClipboard(() => _platform.SetImage(content.ImagePng!, bgra, w, h));
                    break;
            }
            Store.Touch(id);
            if (!paste || targetWindow == 0 || !_platform.Capabilities.SimulateInput) return;
            _platform.ActivateWindow(targetWindow);
            await _platform.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(60);
            for (var pass = 0; pass < 2; pass++) // clipboard and target, twice: see PasteTextAsync
            {
                EnsureClipboardIsOurs();
                if (targetWindow != 0 && _platform.ForegroundWindow != targetWindow)
                {
                    Log.Info("Paste skipped: the target window is not active");
                    return;
                }
            }
            _platform.SendPaste();
        }
        catch (ClipboardTakenException) { Log.Info("Paste stopped: another app changed the clipboard meanwhile"); }
        catch (Exception e) { Log.Error("Paste failed", e); }
        finally { _convertGate.Release(); if (largeImage) _memory.Schedule(); }
    }

    /// <summary>Pastes the item copied before the current one (second hotkey press in the history popup).</summary>
    public Task PastePreviousAsync(nint targetWindow, bool paste = true)
    {
        if (_remote is not null) return _remote.CallAsync(new("paste-previous", Window: (long)targetWindow, Flag: paste));
        var previous = Store.PreviousId();
        return previous is null ? Task.CompletedTask : PasteAsync(previous.Value, targetWindow, paste);
    }

    private void UpdateRemoteState(RuntimeReply reply)
    {
        Settings = _settingsStore.Load(); Loc.SetLanguage(Settings.UiLanguage); LayoutCatalog.Synchronize(Settings); BuildConverter();
        Store.UpdateLimits(Limits(Settings));
        FailedHotkeys = reply.FailedHotkeys ?? [];
        PausedUntil = reply.PausedUntil;
    }

    private int _externalCapture;
    private IDisposable? _externalClipboardWrite;
    internal async Task<bool> BeginExternalCaptureAsync()
    {
        if (!await _convertGate.WaitAsync(0)) return false;
        _externalCapture = 1; _capturingScreen = true; LastTransform = null;
        _recentTyping.Reset(Interlocked.Read(ref _typingVersion)); return true;
    }
    internal void BeginExternalClipboardWrite()
    {
        if (Volatile.Read(ref _externalCapture) == 0) throw new InvalidOperationException("No active capture lease");
        var scope = _platform.SuspendCapture();
        if (Interlocked.CompareExchange(ref _externalClipboardWrite, scope, null) is not null) scope.Dispose();
    }
    internal void EndExternalClipboardWrite() => Interlocked.Exchange(ref _externalClipboardWrite, null)?.Dispose();
    internal void EndExternalCapture()
    {
        if (Interlocked.Exchange(ref _externalCapture, 0) == 0) return;
        try { EndExternalClipboardWrite(); } finally { _capturingScreen = false; _convertGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _recentTyping.Reset(Interlocked.Increment(ref _typingVersion));
        _lastTransform = null;
        StopApplicationLayouts();
        _ = StopPendingAutomaticWords();
        _lexicon.Dispose();
        _personal.Dispose();
        _memory.Dispose();
        _images.Dispose();
        if (_remote is not null) { Store.Dispose(); return; }
        EndExternalCapture();
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
