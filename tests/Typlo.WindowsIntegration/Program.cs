using System.Diagnostics;
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

            await Check("three recently typed letters in a WebView2 contenteditable", async () =>
            {
                using var controller = Controller(platform, "recent", recent: true);
                TypingEvents.AcceptSimulated = true;
                await Task.Delay(500); // The native typing hook starts asynchronously.
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
                await Task.Delay(500);
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
                await Task.Delay(500);
                await Prepare("готовое ", select: false, formatted: true);
                await TypeFixture(platform);
                await controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
                await ExpectText("готовое при");
            });

            foreach (var takesFocus in new[] { false, true })
                await Check(takesFocus ? "transient notification activation during long selection" : "non-activating notification during long selection", async () =>
                {
                    using var controller = Controller(platform, "notification-" + takesFocus, recent: true);
                    TypingEvents.AcceptSimulated = true;
                    await Task.Delay(500);
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
            LayoutsInitialized = true, ToggleHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "", ClipboardHotkey = "",
            SwitchSystemLayout = false, ConvertLastWord = recent, HistoryEnabled = false,
        });
        return new AppController(platform, paths, PlatformFactory.CreateKeyProtector(diagnostic: true), diagnostic: true);
    }

    private async Task Check(string name, Func<Task> check)
    {
        try { await check(); _report.Add("PASS " + name); }
        catch (Exception e) { _report.Add("FAIL " + name + ": " + e.Message); }
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

    private async Task TypeFixture(WinPlatform platform)
    {
        foreach (var key in new[] { "G", "H", "B" })
        {
            if (platform.ForegroundWindow != Handle) throw new InvalidOperationException("The fixture window lost focus before typing");
            platform.SendChord(KeyModifiers.None, key);
            await Task.Delay(60);
        }
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
