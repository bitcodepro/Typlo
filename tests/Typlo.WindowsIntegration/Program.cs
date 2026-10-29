using System.Diagnostics;
using Typlo.Core.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Typlo.App.Platform;
using Typlo.App.Platform.Shared;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Native = Typlo.App.Platform.Windows.Native;

namespace Typlo.WindowsIntegration;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--direct-editor-fixture")
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new DirectEditorFixture());
            return 0;
        }
        if (args.FirstOrDefault() == "--direct-edit-check")
            return DirectEditCheck.RunAsync(args[1]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--typing-hook-lifecycle-check")
        {
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    Console.WriteLine("Native hook lifetime " + i);
                    using var platform = new WinPlatform(monitorClipboard: false);
                    _ = platform.ForegroundChangeVersion; // each recreated message window dispatches to its own platform
                    platform.SetTypingWatch(true);
                    // Alternate immediate disposal during startup with disposal after native readiness.
                    if (i % 2 != 0) platform.WaitForTypingWatchAsync().GetAwaiter().GetResult();
                    platform.Dispose(); // also exercise idempotent disposal by the using scope
                    if (!platform.TypingHookShutdownCompleted) throw new InvalidOperationException("Native hook did not start and finish cleanly");
                }
                Console.WriteLine("PASS native typing hook startup/disposal/recreation (10 platform lifetimes)");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        if (args.FirstOrDefault() == "--background-runtime-check")
            return BackgroundRuntimeCheck.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--ui-target-check")
            return BackgroundRuntimeCheck.RunAsync(args[1], args[2], int.Parse(args[3])).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--clipboard-helper")
        {
            using var platform = new WinPlatform();
            Console.WriteLine("READY");
            while (Console.ReadLine() is { } command && command != "exit")
            {
                if (command != "copy") continue;
                platform.SetText(EditorCheck.ExternalCopy);
                Console.WriteLine("COPIED");
            }
            return 0;
        }
        if (args.FirstOrDefault() == "--check-runtime")
        {
            try { Console.WriteLine(CoreWebView2Environment.GetAvailableBrowserVersionString()); return 0; }
            catch (WebView2RuntimeNotFoundException) { return 1; }
        }
        var work = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "typlo-webview-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        Typlo.App.Log.FilePath = Path.Combine(work, "typlo.log");
        ApplicationConfiguration.Initialize();
        using var form = new EditorCheck(work, Native.GetForegroundWindow());
        Application.Run(form);
        return form.Result;
    }
}

internal sealed class EditorCheck : Form
{
    internal const string ExternalCopy = "external-copy-fixture";
    private readonly string _work;
    private readonly nint _previousWindow;
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly List<string> _report = [];
    private Process? _clipboardHelper;
    internal int Result { get; private set; } = 1;

    internal EditorCheck(string work, nint previousWindow)
    {
        _work = work;
        _previousWindow = previousWindow;
        Text = "Typlo: isolated WebView2 editor check";
        Width = 700; Height = 400;
        Controls.Add(_browser);
        Shown += async (_, _) => await RunChecks();
    }

    private async Task RunChecks()
    {
        using var platform = new WinPlatform();
        ClipboardSnapshot? clipboard = null;
        try
        {
            clipboard = platform.Snapshot();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_work, "profile"));
            await _browser.EnsureCoreWebView2Async(environment);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _browser.NavigationCompleted += (_, e) => { if (e.IsSuccess) ready.TrySetResult(); };
            _browser.NavigateToString("""
                <!doctype html><meta charset="utf-8"><title>Local editor fixture</title>
                <style>body{font:20px sans-serif}#editor{border:1px solid #aaa;padding:20px;min-height:100px;white-space:pre-wrap}</style>
                <p>Local contenteditable fixture. No messages or network requests are sent.</p>
                <div id="editor" role="textbox" contenteditable="true" spellcheck="false"></div>
                <script>document.addEventListener('keydown', e => {if(e.key === 'Enter')e.preventDefault();});</script>
                """);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Activate();
            platform.ActivateWindow(Handle);
            var english = platform.GetKeyboardLayouts().FirstOrDefault(l => l.LanguageCode == "en");
            if (english is not null) platform.SwitchKeyboardLayout(Handle, english);
            await Task.Delay(200);

            // Reproduce the process boundary before testing conversion. The browser owns the copy,
            // while the visible top-level window belongs to this Windows Forms host.
            await Prepare("ghb", select: true);
            platform.SetText("clipboard-probe");
            platform.SendCopy();
            await Until(() => platform.ReadText() == "ghb", "The local editor did not copy its selection");
            Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var ownerPid);
            Native.GetWindowThreadProcessId(Handle, out var hostPid);
            var childPids = new HashSet<uint>();
            Native.EnumChildWindows(Handle, (window, _) => { Native.GetWindowThreadProcessId(window, out var pid); childPids.Add(pid); return true; }, 0);
            _report.Add($"Host PID={hostPid}; clipboard PID={ownerPid}; native child PIDs={string.Join(',', childPids)}; accepted={platform.IsClipboardOwnedBy(Handle)}");
            if (ownerPid == 0 || ownerPid == hostPid) throw new InvalidOperationException("Fixture did not produce a cross-process clipboard owner");

            await Check("explicit selection in a WebView2 contenteditable", async () =>
            {
                using var controller = Controller(platform, "selected", recent: false);
                await Prepare("ghb", select: true);
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await ExpectText("при");
            });

            await Check("typing hook survives rapid disable and controller replacement", async () =>
            {
                // The selected-text controller above has already disabled delivery. Replacing
                // controllers repeatedly must not overlap native hook start/stop callbacks.
                for (var i = 0; i < 5; i++)
                {
                    using var controller = Controller(platform, "hook-lifecycle-" + i, recent: true);
                    TypingEvents.AcceptSimulated = true;
                    await platform.WaitForTypingWatchAsync();
                    if (i == 0)
                    {
                        await Prepare("", select: false);
                        var disabledEvents = 0;
                        void ObserveDisabled(TypedKey _) => Interlocked.Increment(ref disabledEvents);
                        platform.SetTypingWatch(false);
                        platform.KeyTyped += ObserveDisabled;
                        try
                        {
                            platform.SendChord(KeyModifiers.None, "G");
                            await ExpectTextEventually("g");
                            if (Volatile.Read(ref disabledEvents) != 0) throw new InvalidOperationException("Disabled typing watch still delivered input");
                        }
                        finally { platform.KeyTyped -= ObserveDisabled; platform.SetTypingWatch(true); }
                    }
                    for (var toggle = 0; toggle < 20; toggle++)
                    { platform.SetTypingWatch(false); platform.SetTypingWatch(true); }
                    await Prepare("готовое ", select: false);
                    await TypeFixture(platform);
                    await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                    await ExpectText("готовое при");
                }
            });

            await Check("three recently typed letters in a WebView2 contenteditable", async () =>
            {
                using var controller = Controller(platform, "recent", recent: true);
                TypingEvents.AcceptSimulated = true;
                await platform.WaitForTypingWatchAsync();
                await Prepare("", select: false);
                await TypeFixture(platform);
                await ExpectText("ghb");
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await ExpectText("при");
            });

            await Check("only recently typed letters change beside existing text", async () =>
            {
                using var controller = Controller(platform, "suffix", recent: true);
                TypingEvents.AcceptSimulated = true;
                await platform.WaitForTypingWatchAsync();
                await Prepare("готовое ", select: false);
                await TypeFixture(platform);
                await ExpectText("готовое ghb");
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await ExpectText("готовое при");
                await controller.UndoLastTransformAsync();
                await ExpectText("готовое ghb");
            });

            await Check("recent input in a formatted paragraph", async () =>
            {
                using var controller = Controller(platform, "formatted", recent: true);
                TypingEvents.AcceptSimulated = true;
                await platform.WaitForTypingWatchAsync();
                await Prepare("готовое ", select: false, formatted: true);
                await TypeFixture(platform);
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await ExpectText("готовое при");
            });

            await Check("RU UK EN hotkeys repeatedly convert a WebView2 suffix without a native edit target", async () =>
            {
                using var controller = Controller(platform, "layout-hotkeys", recent: true);
                controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
                var settings = controller.Settings.Clone();
                settings.Layouts = ["en", "ru", "uk"];
                settings.LayoutHotkeys = [new() { LayoutId = "ru", Hotkey = "F13" },
                    new() { LayoutId = "uk", Hotkey = "F14" }, new() { LayoutId = "en", Hotkey = "F15" }];
                controller.ApplySettings(settings);
                if (controller.FailedHotkeys.Count != 0) throw new InvalidOperationException("Could not register layout fixture hotkeys");
                TypingEvents.AcceptSimulated = true;
                await platform.WaitForTypingWatchAsync();
                await Prepare("готовое ", select: false, formatted: true);
                await TypeFixture(platform, "SS");
                var input = platform.FocusedInputElement;
                if (platform.CanRetainDirectInputTarget(Handle, input)) throw new InvalidOperationException("WebView2 unexpectedly became a native edit control");
                const string marker = "layout-cycle-clipboard-fixture";
                platform.SetText(marker);
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    foreach (var (hotkey, expected) in new[] { ("F13", "готовое ыы"), ("F14", "готовое іі"), ("F15", "готовое ss") })
                    {
                        platform.SendChord(KeyModifiers.None, hotkey);
                        await ExpectTextEventually(expected);
                        await Until(() => platform.ReadText() == marker, "Layout conversion did not restore the clipboard");
                    }
                }
                await controller.UndoLastTransformAsync();
                await ExpectText("готовое іі");
            });

            foreach (var takesFocus in new[] { false, true })
                await Check(takesFocus ? "transient notification activation during long selection" : "non-activating notification during long selection", async () =>
                {
                    using var controller = Controller(platform, "notification-" + takesFocus, recent: true);
                    TypingEvents.AcceptSimulated = true;
                    await platform.WaitForTypingWatchAsync();
                    await Prepare("", select: false);
                    for (var i = 0; i < 300; i++)
                    {
                        if (platform.ForegroundWindow != Handle) throw new InvalidOperationException("Fixture lost focus before typing");
                        platform.SendChord(KeyModifiers.None, "G");
                    }
                    await ExpectTextEventually(new string('g', 300));
                    using var popup = new NotificationFixture(takesFocus);
                    using var show = new System.Windows.Forms.Timer { Interval = 10 };
                    using var hide = new System.Windows.Forms.Timer { Interval = 140 };
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var observedFocus = false;
                    show.Tick += (_, _) =>
                    {
                        show.Stop();
                        popup.ShowNotification(this);
                        observedFocus = Native.GetForegroundWindow() == popup.Handle;
                        hide.Start();
                    };
                    hide.Tick += (_, _) =>
                    {
                        hide.Stop();
                        var restore = takesFocus && Native.GetForegroundWindow() == popup.Handle;
                        popup.Close();
                        // Emulate the shell returning to the previous edit control after a transient activation.
                        if (restore) { Activate(); _browser.Focus(); }
                        closed.TrySetResult();
                    };
                    show.Start();
                    await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (observedFocus != takesFocus) throw new InvalidOperationException("Notification fixture did not produce the requested focus state");
                    await ExpectText(new string('п', 300));
                    if (popup.TextAtClose != "notification text") throw new InvalidOperationException("Input reached the notification window");
                });

            await Check("desktop capture has exact BGRA pixels and a transparent freehand mask", async () =>
            {
                using var swatch = new Form
                {
                    BackColor = Color.FromArgb(13, 97, 173), Width = 100, Height = 100,
                    FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                    StartPosition = FormStartPosition.Manual, Left = Left + 80, Top = Top + 80,
                };
                swatch.Show(this); swatch.Activate();
                try
                {
                    await Task.Delay(150);
                    var position = swatch.PointToScreen(new Point(20, 20));
                    var frame = DesktopCapture.Grab();
                    var x = position.X - frame.Bounds.X;
                    var y = position.Y - frame.Bounds.Y;
                    var offset = (y * frame.Bounds.Width + x) * 4;
                    if (!frame.Bgra.AsSpan(offset, 4).SequenceEqual(new byte[] { 173, 97, 13, 255 }))
                        throw new InvalidOperationException($"Screen copy color mismatch: {string.Join(',', frame.Bgra.AsSpan(offset, 4).ToArray())}; fixture DPI={swatch.DeviceDpi}, sample={x},{y}, frame={frame.Bounds.Width}x{frame.Bounds.Height}");
                    var crop = CaptureRegion.Crop(frame.Bgra, frame.Bounds.Width, frame.Bounds.Height, CaptureShape.Freehand,
                        [new(x, y), new(x + 20, y), new(x, y + 20)])!;
                    if (crop.Width != 20 || crop.Height != 20 || crop.Bgra[3] != 255 || crop.Bgra[^1] != 0)
                        throw new InvalidOperationException("The freehand crop did not retain its transparent background");
                }
                finally { swatch.Close(); Activate(); _browser.Focus(); }
            });

            await StartClipboardHelper();
            await Check("an unrelated child process is not an embedded editor", async () =>
            {
                await ExternalCopyAsync();
                if (platform.IsClipboardOwnedBy(Handle) != false)
                    throw new InvalidOperationException("Accepted clipboard from a child process without an embedded native window");
            });

            await Check("external copy during WebView2 conversion is preserved and aborts the paste", async () =>
            {
                using var controller = Controller(platform, "interrupted", recent: false);
                await Prepare("ghb", select: true);
                var interference = CopyWhenEditorCopies(platform);
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await interference;
                await ExpectText("ghb");
                if (platform.ReadText() != ExternalCopy) throw new InvalidOperationException("External copy was overwritten");
            });

            await Check("foreground events retain a complete activation cycle between samples", async () =>
            {
                await Prepare("ghb", select: true);
                var counter = typeof(IPlatform).GetProperty("ForegroundChangeVersion");
                long? Version() => (long?)counter?.GetValue(platform);
                var before = Version() ?? throw new InvalidOperationException("No durable foreground counter");
                using var popup = new NotificationFixture(true);
                popup.ShowNotification(this);
                var activated = Native.GetForegroundWindow() == popup.Handle;
                popup.Close(); Activate(); _browser.Focus();
                if (!activated || Native.GetForegroundWindow() != Handle)
                    throw new InvalidOperationException("Fixture could not perform the activation cycle");
                await Until(() => Version() >= before + 2, "A complete activation cycle was lost between samples");
            });

            await Check("copy snapshot followed by a completed blur and lost selection never duplicates text", async () =>
            {
                using var controller = Controller(platform, "copy-blur", recent: false);
                await Prepare("ghb", select: true);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CoreWebView2WebMessageReceivedEventArgs> handler = async (_, e) =>
                {
                    if (e.TryGetWebMessageAsString() != "focus-copy") return;
                    try
                    {
                        using var popup = new NotificationFixture(true);
                        popup.ShowNotification(this);
                        if (Native.GetForegroundWindow() != popup.Handle)
                            throw new InvalidOperationException("Fixture popup did not activate");
                        await _browser.ExecuteScriptAsync("window.getSelection().removeAllRanges()");
                        popup.Close(); Activate(); _browser.Focus();
                        await _browser.ExecuteScriptAsync("""
                            (()=>{const e=document.getElementById('editor');e.focus();
                            const r=document.createRange();r.selectNodeContents(e);r.collapse(false);
                            const s=window.getSelection();s.removeAllRanges();s.addRange(r);})()
                            """);
                        // Publish the pre-blur snapshot only after the editor has completed its return.
                        // No sleep decides whether the controller sees the popup while waiting for Copy.
                        platform.SetText("ghb");
                        completed.TrySetResult();
                    }
                    catch (Exception error) { completed.TrySetException(error); }
                };
                _browser.CoreWebView2.WebMessageReceived += handler;
                try
                {
                    await _browser.ExecuteScriptAsync("""
                        document.addEventListener('copy', e=>{e.preventDefault();
                            chrome.webview.postMessage('focus-copy');}, {once:true});
                        """);
                    await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await ExpectText("ghb");
                }
                finally { _browser.CoreWebView2.WebMessageReceived -= handler; }
            });

            Result = _report.Any(line => line.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
            _report.Add(Result == 0 ? "ALL PASSED" : "FAILED");
        }
        catch (Exception e) { _report.Add("FAIL setup: " + e); }
        finally
        {
            TypingEvents.AcceptSimulated = false;
            // Preserve a copy made by the user in another app while this check was running.
            Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var ownerPid);
            if (clipboard is not null && (ownerPid == (uint)Environment.ProcessId || ownerPid == _browser.CoreWebView2?.BrowserProcessId ||
                (_clipboardHelper is not null && ownerPid == (uint)_clipboardHelper.Id)))
                platform.Restore(clipboard);
            if (_clipboardHelper is not null)
            {
                _clipboardHelper.StandardInput.WriteLine("exit");
                _clipboardHelper.StandardInput.Flush();
                if (!_clipboardHelper.WaitForExit(3000)) _clipboardHelper.Kill();
                _clipboardHelper.Dispose();
            }
            if (platform.ForegroundWindow == Handle) platform.ActivateWindow(_previousWindow);
            File.WriteAllLines(Path.Combine(_work, "webview2.txt"), _report);
            foreach (var line in _report) Console.WriteLine(line);
            _browser.Dispose();
            Close();
        }
    }

    private AppController Controller(WinPlatform platform, string name, bool recent)
    {
        var paths = new AppPaths(Path.Combine(_work, name));
        new SettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            LayoutsInitialized = true, ToggleHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "", ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", ClipboardHotkey = "",
            SwitchSystemLayout = false, ConvertLastWord = recent, HistoryEnabled = false,
        });
        return new AppController(platform, paths, PlatformFactory.CreateKeyProtector(diagnostic: true), diagnostic: true);
    }

    private async Task Check(string name, Func<Task> check)
    {
        try { await check(); _report.Add("PASS " + name); }
        catch (Exception e) { _report.Add("FAIL " + name + ": " + e); }
    }

    private async Task Prepare(string text, bool select, bool formatted = false)
    {
        Activate();
        _browser.Focus();
        var value = JsonSerializer.Serialize(text);
        await _browser.ExecuteScriptAsync($$"""
            (() => {const e=document.getElementById('editor');e.textContent='';let content=e;
            if({{(formatted ? "true" : "false")}}){const p=document.createElement('p');content=document.createElement('strong');p.append(content);e.append(p);}
            content.textContent={{value}};e.focus();
            const r=document.createRange();r.selectNodeContents(content);{{(select ? "" : "r.collapse(false);")}}
            const s=window.getSelection();s.removeAllRanges();s.addRange(r);})()
            """);
        await Task.Delay(80);
        if (Native.GetForegroundWindow() != Handle) throw new InvalidOperationException("The fixture window lost focus");
    }

    private async Task StartClipboardHelper()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("--clipboard-helper");
        _clipboardHelper = Process.Start(info) ?? throw new InvalidOperationException("Could not start clipboard fixture");
        if (await _clipboardHelper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "READY")
            throw new InvalidOperationException("Clipboard fixture did not start");
    }

    private async Task ExternalCopyAsync()
    {
        await _clipboardHelper!.StandardInput.WriteLineAsync("copy");
        await _clipboardHelper.StandardInput.FlushAsync();
        if (await _clipboardHelper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "COPIED")
            throw new InvalidOperationException("Clipboard fixture did not copy");
    }

    private async Task CopyWhenEditorCopies(WinPlatform platform)
    {
        await Until(() =>
        {
            Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var pid);
            return pid == _browser.CoreWebView2.BrowserProcessId && platform.ReadText() == "ghb";
        }, "The editor did not publish the selected text");
        await ExternalCopyAsync();
    }

    private async Task TypeFixture(WinPlatform platform, string physicalKeys = "GHB")
    {
        await platform.WaitForTypingWatchAsync();
        var before = JsonSerializer.Deserialize<string>(await _browser.ExecuteScriptAsync("document.getElementById('editor').innerText")) ?? "";
        var observed = new System.Collections.Concurrent.ConcurrentQueue<char>();
        void Observe(TypedKey key) { if (!key.Reset && !key.Backspace) observed.Enqueue(key.Char); }
        platform.KeyTyped += Observe;
        try
        {
            var count = 0;
            foreach (var key in physicalKeys.Select(c => c.ToString()))
            {
                if (platform.ForegroundWindow != Handle) throw new InvalidOperationException("The fixture window lost focus before typing");
                platform.SendChord(KeyModifiers.None, key);
                var expectedCount = ++count;
                await Until(() => observed.Count >= expectedCount, "Native hook did not deliver fixture key " + key);
            }
            var expected = physicalKeys.ToLowerInvariant();
            if (new string(observed.ToArray()) != expected) throw new InvalidOperationException("Native hook delivered unexpected/duplicate fixture keys");
            await ExpectTextEventually(before + expected); // Browser input delivery is asynchronous too.
        }
        finally { platform.KeyTyped -= Observe; }
    }

    private async Task ExpectText(string expected)
    {
        var actual = JsonSerializer.Deserialize<string>(await _browser.ExecuteScriptAsync("document.getElementById('editor').innerText"));
        if (actual != expected) throw new InvalidOperationException($"Expected fixture text '{expected}', got '{actual}'");
    }

    private async Task ExpectTextEventually(string expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var actual = JsonSerializer.Deserialize<string>(await _browser.ExecuteScriptAsync("document.getElementById('editor').innerText"));
            if (actual == expected) return;
            await Task.Delay(20);
        }
        await ExpectText(expected);
    }

    private static async Task Until(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3)) { if (condition()) return; await Task.Delay(5); }
        throw new TimeoutException(failure);
    }

}

internal sealed class NotificationFixture(bool takesFocus) : IDisposable
{
    private Form? _active;
    private TextBox? _editor;
    private nint _passive;
    internal nint Handle => _active?.Handle ?? _passive;
    internal string TextAtClose { get; private set; } = "notification text";
    internal void ShowNotification(Form owner)
    {
        if (takesFocus)
        {
            _editor = new TextBox { Text = "notification text", Dock = DockStyle.Fill };
            _active = new Form { ShowInTaskbar = false, TopMost = true, Text = "Typlo notification fixture", Width = 250, Height = 100 };
            _active.Controls.Add(_editor);
            _active.Show(owner);
            _active.Activate();
        }
        else
        {
            // A real non-activating native popup, without Windows Forms focus bookkeeping.
            _passive = Native.CreateWindowEx(0x08000088 /* NOACTIVATE | TOOLWINDOW | TOPMOST */, "STATIC", "notification text",
                0x80000000 /* WS_POPUP */, 100, 100, 250, 100, owner.Handle, 0, 0, 0);
            if (_passive == 0) throw new InvalidOperationException("Could not create passive popup");
            ShowWindow(_passive, 4 /* SW_SHOWNOACTIVATE */);
        }
    }
    internal void Close()
    {
        if (_active is not null)
        {
            TextAtClose = _editor!.Text;
            _active.Close();
            _active.Dispose();
            _active = null;
        }
        if (_passive != 0) { Native.DestroyWindow(_passive); _passive = 0; }
    }
    public void Dispose() => Close();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
}
