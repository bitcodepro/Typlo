using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Layouts;
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
    private nint _plainRichInitialHandle;
    internal DirectEditorFixture()
    {
        Text = "Typlo native edit fixture"; Width = 550; Height = 500;
        _fields["edit"] = _edit;
        _fields["password"] = new TextBox { PasswordChar = '*' };
        _fields["readonly"] = new TextBox { ReadOnly = true };
        _fields["rich"] = new RichTextBox();
        _fields["plainrich"] = new PlainTextRichTextBox();
        var y = 10;
        foreach (var field in _fields.Values) { field.Top = y; field.Left = 10; Controls.Add(field); y += 85; }
        Shown += (_, _) =>
        {
            _popup.Show(); Activate(); _edit.Focus();
            _plainRichInitialHandle = _fields["plainrich"].Handle;
            Console.WriteLine(((PlainTextRichTextBox)_fields["plainrich"]).Setup);
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
        if (op == "delay-after-write") _edit.AfterWriteReadDelay = root.GetProperty("milliseconds").GetInt32();
        if (op == "recreate") ((PlainTextRichTextBox)field).RecreateForTest();
        var modeReadSent = Native.SendMessageTimeoutW(_fields["plainrich"].Handle, 0x400 + 90, 0, 0, 0x23, 100, out var mode) != 0;
        var nativeLengthSent = Native.SendMessageTimeoutW(field.Handle, 0x000e, 0, 0, 0x23, 100, out var nativeLength) != 0;
        var nativeLengthError = nativeLengthSent ? 0 : Marshal.GetLastPInvokeError();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            window = (long)Handle, element = (long)field.Handle, popup = (long)_popup.Handle,
            text = field.Text, start = field.SelectionStart, length = field.SelectionLength, writes = _edit.Writes,
            modeReadSent, plainRichMode = (ulong)mode, plainRichHandleChanged = _fields["plainrich"].Handle != _plainRichInitialHandle,
            plainRichSetup = ((PlainTextRichTextBox)_fields["plainrich"]).Setup,
            nativeLengthSent, nativeLengthError, nativeLength = (ulong)nativeLength, keyboardMessages = _edit.KeyboardMessages,
        }));
        if (op == "delay-read") _edit.ReadDelay = root.GetProperty("milliseconds").GetInt32();
    }
    private sealed class PlainTextRichTextBox : RichTextBox
    {
        internal string Setup { get; private set; } = "";
        internal void RecreateForTest() => RecreateHandle();
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WinForms restores rich text when recreating HWND. EM_SETTEXTMODE only works on an
            // empty control, so preserve the fixture's plain content/selection around setup.
            // RichTextBox.Text deliberately returns an empty string during RecreatingHandle.
            // Read the native contents that base.OnHandleCreated has just restored instead.
            var text = ReadNativeText();
            var selectionReadSent = Native.SendMessageTimeoutW(Handle, 0x00b0, 0, 0, 0x23, 100, out var selection) != 0;
            var start = (int)(selection & 0xffff);
            var end = (int)((selection >> 16) & 0xffff);
            var cleared = SetNativeText("");
            var timer = Stopwatch.StartNew();
            var modeSetSent = Native.SendMessageTimeoutW(Handle, 0x400 + 89, 1, 0, 0x23, 100, out var modeSetResult) != 0;
            var modeSetError = modeSetSent ? 0 : Marshal.GetLastPInvokeError();
            var textRestored = SetNativeText(text);
            var selectionRestored = Native.SendMessageTimeoutW(Handle, 0x00b1, (nuint)start, end, 0x23, 100, out _) != 0;
            var modeReadSent = Native.SendMessageTimeoutW(Handle, 0x400 + 90, 0, 0, 0x23, 100, out var mode) != 0;
            var modeReadError = modeReadSent ? 0 : Marshal.GetLastPInvokeError();
            Setup = JsonSerializer.Serialize(new
            {
                ready = cleared && modeSetSent && modeSetResult == 0 && modeReadSent && (mode & 1) != 0 &&
                    textRestored && selectionReadSent && selectionRestored,
                modeSetSent, modeSetResult = (ulong)modeSetResult, modeSetError, modeReadSent, modeReadError,
                mode = (ulong)mode, cleared, textRestored, selectionReadSent, selectionRestored,
                preservedLength = text.Length, preservedStart = start, elapsedMs = timer.Elapsed.TotalMilliseconds,
            });
        }
        private string ReadNativeText()
        {
            if (Native.SendMessageTimeoutW(Handle, 0x000e, 0, 0, 0x23, 100, out var length) == 0 || length > NativeEditSnapshot.MaxText)
                throw new InvalidOperationException("Cannot read fixture RichEdit length");
            var chars = Marshal.AllocHGlobal(checked(((int)length + 1) * sizeof(char)));
            try
            {
                if (Native.SendMessageTimeoutW(Handle, 0x000d, length + 1, chars, 0x23, 100, out var copied) == 0 || copied != length)
                    throw new InvalidOperationException("Cannot read fixture RichEdit contents");
                return Marshal.PtrToStringUni(chars, (int)copied)!;
            }
            finally { Marshal.FreeHGlobal(chars); }
        }
        private bool SetNativeText(string text)
        {
            var chars = Marshal.StringToHGlobalUni(text);
            try { return Native.SendMessageTimeoutW(Handle, 0x000c, 0, chars, 0x23, 100, out var result) != 0 && result != 0; }
            finally { Marshal.FreeHGlobal(chars); }
        }
    }
    private sealed class SlowTextBox : TextBox
    {
        internal int Delay, ReadDelay, AfterWriteReadDelay, Writes, KeyboardMessages;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg is >= 0x100 and <= 0x109) KeyboardMessages++;
            if (m.Msg == 0x0d)
            {
                var delay = Interlocked.Exchange(ref ReadDelay, 0);
                if (delay > 0) Thread.Sleep(delay); // intentional unresponsive read fixture
            }
            if (m.Msg == 0xc2)
            {
                Writes++;
                var delay = Interlocked.Exchange(ref Delay, 0);
                if (delay > 0) Thread.Sleep(delay); // intentional unresponsive editor fixture
            }
            base.WndProc(ref m);
            if (m.Msg == 0xc2) ReadDelay = Interlocked.Exchange(ref AfterWriteReadDelay, 0);
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
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var nativeTrace = new System.Collections.Concurrent.ConcurrentQueue<NativeEditDiagnostic>();
        var previous = Native.GetForegroundWindow();
        using var platform = new WinPlatform(monitorClipboard: false);
        platform.DirectTextDiagnostic = entry => { nativeTrace.Enqueue(entry); diagnostics.Enqueue(entry.Summary); };
        var info = new ProcessStartInfo(Environment.ProcessPath!, "--direct-editor-fixture")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var child = Process.Start(info)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            diagnostics.Enqueue("fixture-setup " + ready);
            using (var setup = JsonDocument.Parse(ready!)) Require(setup.RootElement.GetProperty("ready").GetBoolean(), "fixture plain-text mode setup: " + ready);
            async Task<JsonElement> Command(object command)
            {
                var request = JsonSerializer.SerializeToElement(command);
                await child.StandardInput.WriteLineAsync(request.GetRawText());
                await child.StandardInput.FlushAsync();
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var doc = JsonDocument.Parse(line!);
                diagnostics.Enqueue($"fixture-state op={request.GetProperty("op")} text_length={doc.RootElement.GetProperty("text").GetString()!.Length} native_length={doc.RootElement.GetProperty("nativeLength")} length_sent={doc.RootElement.GetProperty("nativeLengthSent")} length_error={doc.RootElement.GetProperty("nativeLengthError")} selection_start={doc.RootElement.GetProperty("start")} selection_length={doc.RootElement.GetProperty("length")} writes={doc.RootElement.GetProperty("writes")} keyboard_messages={doc.RootElement.GetProperty("keyboardMessages")} mode_read={doc.RootElement.GetProperty("modeReadSent")} mode={doc.RootElement.GetProperty("plainRichMode")} handle_changed={doc.RootElement.GetProperty("plainRichHandleChanged")} setup={doc.RootElement.GetProperty("plainRichSetup").GetString()}");
                return doc.RootElement.Clone();
            }
            async Task<DirectTextCapture> Capture(JsonElement state)
            {
                var captured = await platform.CaptureDirectTextAsync((nint)state.GetProperty("window").GetInt64(),
                    (nint)state.GetProperty("element").GetInt64(), () => true, entry =>
                    { nativeTrace.Enqueue(entry); diagnostics.Enqueue(entry.Summary); });
                diagnostics.Enqueue("capture-status=" + captured.Status);
                return captured;
            }
            async Task Check(string name, Func<Task> check)
            {
                diagnostics.Enqueue("check=" + name);
                try { await check(); report.Add("PASS " + name); }
                catch (Exception e) { report.Add("FAIL " + name + ": " + e); }
            }

            await Check("cross-process edit, focus loss, repeat and Undo without clipboard writes", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var window = (nint)state.GetProperty("window").GetInt64();
                Require(string.Equals(platform.GetApplicationProcessName(window), child.ProcessName, StringComparison.OrdinalIgnoreCase),
                    "foreground app identity uses the external window owner process");
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
            foreach (var phase in new[] { "write", "verification" })
            await Check("slow " + phase + " retains repeat and Undo within the original edit budget", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var window = (nint)state.GetProperty("window").GetInt64();
                platform.ActivateWindow(window);
                var paths = new AppPaths(Path.Combine(work, "slow-" + phase + "-controller"));
                new SettingsStore(paths.SettingsFile).Save(new AppSettings
                {
                    LayoutsInitialized = true, ToggleHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "",
                    ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", ClipboardHotkey = "", HistoryEnabled = false,
                    SwitchSystemLayout = false, ConvertLastWord = true,
                });
                using var controller = new AppController(platform, paths, PlatformFactory.CreateKeyProtector(true), diagnostic: true);
                await platform.WaitForTypingWatchAsync();
                var writes = state.GetProperty("writes").GetInt32();
                var keys = state.GetProperty("keyboardMessages").GetInt32();
                var sequence = platform.ClipboardSequence;
                var traceStart = nativeTrace.Count;
                await Command(new { op = phase == "write" ? "delay" : "delay-after-write", milliseconds = 100 });
                var popup = (nint)state.GetProperty("popup").GetInt64();
                await controller.TransformSelectionAsync(text =>
                {
                    platform.ActivateWindow(popup);
                    Require(SpinWait.SpinUntil(() => platform.ForegroundWindow == popup, TimeSpan.FromSeconds(1)), "slow-verification popup foreground");
                    return ("при", null);
                });
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "при", "completed write changed text");
                await controller.ChangeCaseAsync();
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "При", "slow verification retains repeat range");
                await controller.UndoLastTransformAsync();
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "при", "slow verification retains Undo range");
                Require(state.GetProperty("writes").GetInt32() == writes + 3, "one write per explicit command");
                Require(state.GetProperty("keyboardMessages").GetInt32() == keys && platform.ClipboardSequence == sequence,
                    "slow verification never selects or pastes with keyboard/clipboard");
                Require(platform.ForegroundWindow == popup, "slow verification never reactivates editor");
                var phaseTrace = nativeTrace.Skip(traceStart).ToArray();
                var verification = Array.FindIndex(phaseTrace, e => e.Operation == "verify");
                var delayed = phase == "write" ? phaseTrace : phaseTrace.Skip(verification + 1);
                // WinForms can perform its own GETTEXT before the external verification begins;
                // in that case the first external GETLENGTH waits for it on the same target thread.
                Require(delayed.Any(e => e.Operation == "send" && e.Outcome == "ok" && e.CallElapsedMs >= 60 &&
                    (phase == "write" ? e.Message == 0xc2 : e.Message is 0x0e or 0x0d or 0xb0)),
                    "the controller adapter exercised the delayed " + phase);
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
                Require(capture.Status == DirectCaptureStatus.Ready, "plain-text RichEdit capture status=" + capture.Status);
                Require((await capture.Snapshot!.ReplaceAsync(0, 3, "при", () => true)).Status == DirectEditStatus.Applied, "plain RichEdit write");
                state = await Command(new { op = "recreate", field = "plainrich" });
                Require(state.GetProperty("plainRichHandleChanged").GetBoolean(), "RichEdit handle was recreated");
                capture = await Capture(state);
                Require(capture.Status == DirectCaptureStatus.Ready, "recreated plain-text RichEdit capture status=" + capture.Status);
                Require(capture.Snapshot!.Text == "при" && capture.Snapshot.SelectionStart == 3 && capture.Snapshot.SelectionEnd == 3,
                    "recreated RichEdit retains plain text and selection");
                Require((await capture.Snapshot.ReplaceAsync(0, 3, "ghb", () => true)).Status == DirectEditStatus.Applied,
                    "recreated plain RichEdit write");
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
            await Check("post-write verification remains bounded and never retries a completed write", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var writes = state.GetProperty("writes").GetInt32();
                var capture = await Capture(state);
                await Command(new { op = "delay-after-write", milliseconds = 500 });
                var before = nativeTrace.Count;
                var timer = Stopwatch.StartNew();
                var edited = await capture.Snapshot!.ReplaceAsync(0, 3, "при", () => true);
                Require(edited.Status == DirectEditStatus.Uncertain && edited.After is null, "verification beyond budget is not reported as Applied");
                Require(timer.Elapsed < TimeSpan.FromMilliseconds(400), "post-write verification keeps original operation deadline");
                var trace = nativeTrace.Skip(before).ToArray();
                Require(trace.Any(e => e.Operation == "verify" && e.Outcome == "after-confirmed-write"), "confirmed write entered verification phase");
                Require(trace.Count(e => e.Operation == "send" && e.Message == 0xc2) == 1, "replacement was sent only once");
                var verification = Array.FindIndex(trace, e => e.Operation == "verify");
                Require(trace.Skip(verification + 1).Any(e => e.Operation == "send" && e.Message is 0x0e or 0x0d or 0xb0 &&
                    e.Outcome is "failed" or "budget-exhausted"), "verification timeout is diagnosed");
                state = await Command(new { op = "read" });
                Require(state.GetProperty("writes").GetInt32() == writes + 1 && state.GetProperty("text").GetString() == "при",
                    "late verification does not duplicate or undo the completed replacement");
            });
            await Check("timed-out read reports native failure without editing", async () =>
            {
                var state = await Command(new { op = "set", text = "ghb", start = 0, length = 3 });
                var writes = state.GetProperty("writes").GetInt32();
                await Command(new { op = "delay-read", milliseconds = 500 });
                var before = nativeTrace.Count;
                var timer = Stopwatch.StartNew();
                var capture = await Capture(state);
                Require(capture.Status == DirectCaptureStatus.Cancelled, "read timeout capture status=" + capture.Status);
                Require(timer.Elapsed < TimeSpan.FromMilliseconds(400), "bounded unresponsive read wait");
                Require(nativeTrace.Skip(before).Any(entry => entry.Operation == "send" && entry.Outcome == "failed" && entry.Message == 0x0d),
                    "diagnostic identifies the failed WM_GETTEXT read");
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "ghb" && state.GetProperty("writes").GetInt32() == writes,
                    "timed-out capture does not edit the document");
            });
            await Check("external process uses its application cycle instead of the global cycle", async () =>
            {
                var state = await Command(new { op = "set", text = "ss", start = 0, length = 2 });
                var paths = new AppPaths(Path.Combine(work, "application-cycle"));
                new SettingsStore(paths.SettingsFile).Save(new AppSettings
                {
                    LayoutsInitialized = true, ToggleHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "",
                    ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", ClipboardHotkey = "", HistoryEnabled = false,
                    SwitchSystemLayout = false,
                });
                using var controller = new AppController(platform, paths, PlatformFactory.CreateKeyProtector(true), diagnostic: true);
                controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
                var settings = controller.Settings.Clone(); settings.Layouts = ["en", "ru"];
                settings.ApplicationLayouts = [new() { ProcessName = child.ProcessName, Layouts = ["en", "uk"] }];
                controller.ApplySettings(settings);
                var sequence = platform.ClipboardSequence;
                await controller.ConvertSelectionAsync(null);
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "іі", "app cycle selects Ukrainian, not the global Russian target");
                await controller.ConvertSelectionAsync(null);
                state = await Command(new { op = "read" });
                Require(state.GetProperty("text").GetString() == "ss", "app cycle returns to English");
                Require(platform.ClipboardSequence == sequence, "native app cycle does not change clipboard");
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
            await File.WriteAllLinesAsync(Path.Combine(work, "direct-edit-native.log"), diagnostics);
            foreach (var line in report) Console.WriteLine(line);
        }
        return report.Count == 11 && report.All(line => line.StartsWith("PASS ", StringComparison.Ordinal)) ? 0 : 1;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
