using System.Diagnostics;
using System.Text.Json;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Settings;
using Native = Typlo.App.Platform.Windows.Native;

namespace Typlo.WindowsIntegration;

// Separate process: cross-process marshalling and timeouts must not be tested against same-thread
// WinForms calls, where SendMessageTimeout does not enforce its timeout.
internal sealed class DirectEditorFixture : Form
{
    private readonly Dictionary<string, TextBoxBase> _fields = [];
    private readonly Form _popup = new() { Text = "Typlo notification fixture", Width = 300, Height = 100 };
    private readonly SlowTextBox _edit = new() { Multiline = true, Width = 450, Height = 80 };
    internal DirectEditorFixture()
    {
        Text = "Typlo native edit fixture"; Width = 550; Height = 500;
        _fields["edit"] = _edit;
        _fields["password"] = new TextBox { PasswordChar = '*' };
        _fields["readonly"] = new TextBox { ReadOnly = true };
        _fields["rich"] = new RichTextBox();
        _fields["plainrich"] = new RichTextBox();
        var y = 10;
        foreach (var field in _fields.Values) { field.Top = y; field.Left = 10; Controls.Add(field); y += 85; }
        Shown += (_, _) =>
        {
            _popup.Show(); Activate(); _edit.Focus();
            Native.SendMessageTimeoutW(_fields["plainrich"].Handle, 0x400 + 89, 1, 0, 0x23, 100, out _);
            Console.WriteLine("READY");
            _ = Task.Run(ReadCommands);
        };
        FormClosed += (_, _) => _popup.Dispose();
    }
    private void ReadCommands()
    {
        while (Console.ReadLine() is { } line)
        {
            var json = line;
            BeginInvoke(() => Execute(json));
        }
        BeginInvoke(Close);
    }
    private void Execute(string json)
    {
        using var command = JsonDocument.Parse(json);
        var root = command.RootElement;
        var op = root.GetProperty("op").GetString();
        if (op == "exit") { Close(); return; }
        var field = _fields[root.TryGetProperty("field", out var key) ? key.GetString()! : "edit"];
        if (op == "set")
        {
            field.Text = root.GetProperty("text").GetString();
            field.Select(root.GetProperty("start").GetInt32(), root.GetProperty("length").GetInt32());
            Activate(); field.Focus();
        }
        if (op == "delay") _edit.Delay = root.GetProperty("milliseconds").GetInt32();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            window = (long)Handle, element = (long)field.Handle, popup = (long)_popup.Handle,
            text = field.Text, start = field.SelectionStart, length = field.SelectionLength, writes = _edit.Writes,
        }));
    }
    private sealed class SlowTextBox : TextBox
    {
        internal int Delay, Writes;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xc2)
            {
                Writes++;
                var delay = Interlocked.Exchange(ref Delay, 0);
                if (delay > 0) Thread.Sleep(delay); // intentional unresponsive editor fixture
            }
            base.WndProc(ref m);
        }
    }
}

internal static class DirectEditCheck
{
    internal static async Task<int> RunAsync(string work)
    {
        Directory.CreateDirectory(work);
        Typlo.App.Log.FilePath = Path.Combine(work, "direct-edit.log");
        var report = new List<string>();
        var previous = Native.GetForegroundWindow();
        using var platform = new WinPlatform(monitorClipboard: false);
        var info = new ProcessStartInfo(Environment.ProcessPath!, "--direct-editor-fixture")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var child = Process.Start(info)!;
        try
        {
            Require(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "fixture readiness");
            async Task<JsonElement> Command(object command)
            {
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
                await child.StandardInput.FlushAsync();
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var doc = JsonDocument.Parse(line!);
                return doc.RootElement.Clone();
            }
            async Task<DirectTextCapture> Capture(JsonElement state) => await platform.CaptureDirectTextAsync(
                (nint)state.GetProperty("window").GetInt64(), (nint)state.GetProperty("element").GetInt64(), () => true);
            async Task Check(string name, Func<Task> check)
            {
                try { await check(); report.Add("PASS " + name); }
                catch (Exception e) { report.Add("FAIL " + name + ": " + e); }
            }

            await Check("cross-process edit, focus loss, repeat and Undo without clipboard writes", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var window = (nint)state.GetProperty("window").GetInt64();
                platform.ActivateWindow(window);
                Require(platform.FocusedInputElement == (nint)state.GetProperty("element").GetInt64(), "focused native element");
                var paths = new AppPaths(Path.Combine(work, "controller"));
                new SettingsStore(paths.SettingsFile).Save(new AppSettings
                {
                    LayoutsInitialized = true, ToggleHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "",
                    ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", ClipboardHotkey = "", HistoryEnabled = false,
                    SwitchSystemLayout = false, ConvertLastWord = true,
                });
                using var controller = new AppController(platform, paths, PlatformFactory.CreateKeyProtector(true), diagnostic: true);
                var sequence = platform.ClipboardSequence;
                var popup = (nint)state.GetProperty("popup").GetInt64();
                await controller.TransformSelectionAsync(text =>
                {
                    platform.ActivateWindow(popup);
                    Require(SpinWait.SpinUntil(() => platform.ForegroundWindow == popup, TimeSpan.FromSeconds(1)),
                        "popup took foreground before replacement");
                    return ("при", null);
                });
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "при", "direct background replacement");
                Require(platform.ForegroundWindow == popup, "conversion did not reactivate editor");
                await controller.ChangeCaseAsync();
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "При", "repeat retained bound range");
                await controller.UndoLastTransformAsync();
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "при", "direct Undo while popup foreground");
                Require(platform.ClipboardSequence == sequence, "clipboard never written");
            });
            await Check("multiline Edit UTF-16 offsets and native undo", async () =>
            {
                const string text = "a😀\r\nghb tail";
                var start = text.IndexOf("ghb", StringComparison.Ordinal);
                var state = await Command(new { op = "set", text, start, length = 3 });
                var capture = await Capture(state);
                Require(capture.Status == DirectCaptureStatus.Ready, "multiline capability");
                var edited = await capture.Snapshot!.ReplaceAsync(start, 3, "при", () => true);
                Require(edited.Status == DirectEditStatus.Applied && edited.After!.Text == "a😀\r\nпри tail", "multiline replacement");
                Native.SendMessageTimeoutW(capture.Snapshot.Element, 0x00c7, 0, 0, 0x23, 100, out _); // EM_UNDO
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == text, "native undo preserved");
            });
            await Check("password, read-only, rich content and oversized document decline", async () =>
            {
                foreach (var field in new[] { "password", "readonly", "rich" })
                {
                    var state = await Command(new { op = "set", field, text = "ghb", start = 0, length = 3 });
                    Require((await Capture(state)).Status == DirectCaptureStatus.Unsupported, field);
                }
                var big = await Command(new { op = "set", text = new string('a', NativeEditSnapshot.MaxText + 1), start = 0, length = 0 });
                Require((await Capture(big)).Status == DirectCaptureStatus.Unsupported, "bounded document");
            });
            await Check("plain-text RichEdit and conservative multiline decline", async () =>
            {
                var state = await Command(new { op = "set", field = "plainrich", text = "ghb", start = 0, length = 3 });
                var capture = await Capture(state);
                Require(capture.Status == DirectCaptureStatus.Ready, "plain-text RichEdit mode");
                Require((await capture.Snapshot!.ReplaceAsync(0, 3, "при", () => true)).Status == DirectEditStatus.Applied, "plain RichEdit write");
                state = await Command(new { op = "set", field = "plainrich", text = "a\r\nghb", start = 0, length = 0 });
                Require((await Capture(state)).Status == DirectCaptureStatus.Unsupported, "unmapped RichEdit offsets");
            });
            await Check("programmatic contents, caret changes and cancellation invalidate snapshot", async () =>
            {
                foreach (var changeText in new[] { true, false })
                {
                    var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                    var capture = await Capture(state);
                    await Command(new { op = "set", text = changeText ? "xyz" : "ghb", start = 0, length = 0 });
                    Require((await capture.Snapshot!.ReplaceAsync(0, 3, "при", () => true)).Status == DirectEditStatus.Cancelled, "stale snapshot");
                }
                var latest = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var current = await Capture(latest);
                Require((await current.Snapshot!.ReplaceAsync(0, 3, "при", () => false)).Status == DirectEditStatus.Cancelled, "caller cancellation");
            });
            await Check("timed-out write returns promptly and is never retried", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var before = state.GetProperty("writes").GetInt32();
                var capture = await Capture(state);
                await Command(new { op = "delay", milliseconds = 500 });
                var timer = Stopwatch.StartNew();
                var edited = await capture.Snapshot!.ReplaceAsync(0, 3, "при", () => true);
                Require(edited.Status == DirectEditStatus.Uncertain, "indeterminate timeout");
                Require(timer.Elapsed < TimeSpan.FromMilliseconds(400), "bounded unresponsive editor wait");
                state = await Command(new { op = "read" });
                Require(state.GetProperty("writes").GetInt32() == before + 1, "one write only");
                Require(state.GetProperty("text").GetString() == "при", "late completion remains one replacement");
            });
            await child.StandardInput.WriteLineAsync("{\"op\":\"exit\"}");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) { report.Add("FAIL fixture: " + e); }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (previous != 0) platform.ActivateWindow(previous);
            await File.WriteAllLinesAsync(Path.Combine(work, "direct-edit.txt"), report);
            foreach (var line in report) Console.WriteLine(line);
        }
        return report.Count == 6 && report.All(line => line.StartsWith("PASS ", StringComparison.Ordinal)) ? 0 : 1;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
