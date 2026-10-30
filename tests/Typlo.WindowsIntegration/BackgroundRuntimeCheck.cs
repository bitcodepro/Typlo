using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core.Languages;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Settings;
using Typlo.Core.Storage;

namespace Typlo.WindowsIntegration;

internal static class BackgroundRuntimeCheck
{
    private sealed class TargetEditor : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<(Form, nint)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Form _form;
        internal nint Handle { get; }
        internal TargetEditor()
        {
            _thread = new Thread(() =>
            {
                var form = new Form { Text = "Typlo isolated target editor", Width = 400, Height = 160 };
                form.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true });
                form.Shown += (_, _) => _ready.SetResult((form, form.Handle));
                Application.Run(form); form.Dispose();
            });
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
            (_form, Handle) = _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        public void Dispose() { _form.BeginInvoke(() => _form.Close()); _thread.Join(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CheckOcrAsync()
    {
        using var image = new System.Drawing.Bitmap(800, 180);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        using (var font = new System.Drawing.Font("Arial", 48))
        { graphics.Clear(System.Drawing.Color.White); graphics.DrawString("HELLO TYPLO", font, System.Drawing.Brushes.Black, 20, 30); }
        using var stream = new MemoryStream(); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        var text = await OcrService.RecognizeAsync(stream.ToArray());
        if (text is null || !text.Contains("HELLO", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Windows in-memory OCR failed: " + text);

        var pidFile = Path.Combine(Path.GetTempPath(), "typlo-ocr-helper-" + Guid.NewGuid().ToString("N") + ".pid");
        using var cancel = new CancellationTokenSource();
        var start = new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
            "[IO.File]::WriteAllText($env:TYPLO_TEST_PID, [string]$PID); Start-Sleep -Seconds 60" } };
        start.Environment["TYPLO_TEST_PID"] = pidFile;
        try
        {
            var pending = OcrService.RunAsync(start, [], cancel.Token);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(pidFile)) { if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("OCR helper startup"); await Task.Delay(20); }
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            cancel.Cancel();
            try { await pending; throw new InvalidOperationException("OCR cancellation ignored"); }
            catch (OperationCanceledException) { }
            if (!process.HasExited) throw new InvalidOperationException("OCR helper outlived cancellation");
        }
        finally { cancel.Cancel(); File.Delete(pidFile); }
    }

    internal static async Task<int> RunAsync(string executable, string output, int targetCycles = 0)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Typlo.App.Log.FilePath = Path.Combine(output, "harness.log");
        var lines = new List<string>();
        var uiTrace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var paths = new AppPaths(Path.Combine(output, "data-" + Guid.NewGuid().ToString("N")));
        new SettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            ToggleHotkey = "", ClipboardHotkey = "", UndoHotkey = "", CaseHotkey = "", TranslitHotkey = "",
            ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", LayoutsInitialized = true, StartWithSystem = false, UiLanguage = "en",
            AddWordHotkey = "", LayoutSwitchHotkey = "", AutoCorrectEnabled = false,
        });
        var report = Path.Combine(output, "host-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, WindowStyle = ProcessWindowStyle.Hidden };
        var ocrTemp = Path.Combine(paths.DataDir, "ocr-temp"); Directory.CreateDirectory(ocrTemp);
        start.Environment["TEMP"] = start.Environment["TMP"] = ocrTemp;
        using var feed = new TcpListener(IPAddress.Loopback, 0);
        feed.Start();
        start.Environment["TYPLO_UPDATE_FEED"] = $"http://127.0.0.1:{((IPEndPoint)feed.LocalEndpoint).Port}/latest";
        foreach (var arg in new[] { "--background-probe", "--profile", paths.DataDir, "--report", report, "--lifetime-seconds", "180", "--tray" }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start) ?? throw new IOException("Cannot start background fixture");
        Process? child = null;
        using var platform = new WinPlatform();
        var originalSequence = platform.ClipboardSequence;
        try
        {
            await Until(() => File.Exists(report) && File.ReadAllLines(report).Length >= 3, "host ready");
            var info = File.ReadAllLines(report).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            var runtime = new RuntimeClient(info["channel"]);
            var expectedUiPid = 0;
            RuntimeClient ui = null!;
            var uiChannels = new HashSet<string>();
            var state = await runtime.CallAsync(new("state"));
            Hotkey? targetFixtureKey = null;
            Check(state.UiProcessId == 0, "Cold start has no UI child");
            Check(!host.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("Skia", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("libgles", StringComparison.OrdinalIgnoreCase)), "Background does not load Skia or OpenGL");
            Sample("cold");
            if (targetCycles > 0)
            {
                targetFixtureKey = Enumerable.Range(13, 12).Select(n => Hotkey.TryParse("Ctrl+Alt+Shift+F" + n)!).First(h => platform.RegisterHotkey(9000, h));
                platform.UnregisterAllHotkeys();
                var fixtureSettings = new SettingsStore(paths.SettingsFile).Load();
                fixtureSettings.ToggleHotkey = targetFixtureKey.ToString();
                await runtime.CallAsync(new("settings", Settings: fixtureSettings));
                for (var i = 0; i < targetCycles; i++)
                {
                    await CheckHistoryTarget(gateRestore: i == 0);
                    Check(true, $"UI history target cycle {i + 1}/{targetCycles}");
                }
                File.WriteAllLines(Path.Combine(output, "runtime.txt"), lines);
                return 0;
            }

            var key = Enumerable.Range(13, 12).Select(n => Hotkey.TryParse("Ctrl+Alt+Shift+F" + n)!).First(h => platform.RegisterHotkey(9000, h));
            platform.UnregisterAllHotkeys();
            var settings = new SettingsStore(paths.SettingsFile).Load(); settings.ToggleHotkey = key.ToString();
            await runtime.CallAsync(new("settings", Settings: settings));
            Check(!platform.RegisterHotkey(9000, key), "Resident owns the shortcut");
            await OpenSettings();
            Check(platform.RegisterHotkey(9000, key), "Settings suspends resident shortcuts");
            platform.UnregisterAllHotkeys();
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await Until(() => child!.HasExited, "UI exits after last window closes");
            child!.Dispose(); child = null;
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).UiProcessId == 0, "resident observes UI exit");
            Check(!platform.RegisterHotkey(9000, key), "Normal UI exit restores the shortcut");
            Sample("after-settings");

            await OpenSettings();
            var firstPid = child!.Id;
            var oldOwner = new RuntimeClient(info["channel"], firstPid);
            await oldOwner.CallAsync(new("capture-begin"));
            await oldOwner.CallAsync(new("image-write-begin"));
            child.Kill(); await child.WaitForExitAsync(); child.Dispose(); child = null;
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).UiProcessId == 0, "crashed UI is reaped");
            Check(!platform.RegisterHotkey(9000, key), "Crashed UI restores the shortcut");
            await OpenSettings();
            var nextOwner = new RuntimeClient(info["channel"], child!.Id);
            Check((await nextOwner.CallAsync(new("capture-begin"))).Ok, "Crashed UI releases capture and clipboard leases");
            try { await oldOwner.CallAsync(new("capture-end")); throw new InvalidOperationException("Stale UI command was accepted"); }
            catch (IOException) { Check(true, "Stale UI cannot release a newer UI capture lease"); }
            await nextOwner.CallAsync(new("capture-end"));
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await Until(() => child.HasExited, "replacement UI exits");
            child.Dispose(); child = null;

            Check(IpcServer.Send(["--capture", "rectangle"], info["commands"]), "Capture command accepted");
            await BindUiAsync("capture UI launched");
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).UiProcessId == 0, "canceled capture UI exits");
            child!.Dispose(); child = null;
            Check(platform.ClipboardSequence == originalSequence, "Canceled capture preserves clipboard");

            var pixels = new byte[2048 * 1024 * 4]; new Random(42).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var image = WindowsImageCodec.FromBgra(pixels, 2048, 1024);
            var crypto = ClipCrypto.LoadOrCreate(paths.KeyFile, PlatformFactory.CreateKeyProtector(diagnostic: true));
            var fixture = Path.Combine(paths.DataDir, "fixture.image");
            File.WriteAllBytes(fixture, crypto.Encrypt(image.Png));
            await runtime.CallAsync(new("image-fixture"));
            File.Delete(fixture);
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).StoredItems > 0, "image stored on disk");
            Check((await runtime.CallAsync(new("state"))).UiProcessId == 0, "Image processing does not start UI");
            Check(!host.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("Skia", StringComparison.OrdinalIgnoreCase)), "Image processing does not initialize Skia in resident");
            Sample("after-image");
            await Task.Delay(3000); Sample("image-idle");

            // History hides on Escape/deactivation. Delayed local OCR must outlive the idle timeout.
            Check(IpcServer.Send(["--history"], info["commands"]), "History command accepted");
            await BindUiAsync("history UI launched");
            await ui.CallAsync(new("ui", Args: ["--ocr-fixture"]));
            await Until(() => File.Exists(Path.Combine(paths.DataDir, "ocr-started")), "delayed OCR starts");
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await Task.Delay(1500);
            Check(!child!.HasExited, "Hidden history keeps UI alive during delayed OCR");
            await Until(() => File.Exists(Path.Combine(paths.DataDir, "ocr-completed")), "hidden OCR completes");
            await Until(() => child.HasExited, "UI exits after OCR cleanup");
            child.Dispose(); child = null;
            Check(!Directory.EnumerateFiles(ocrTemp, "typlo-ocr-*.png").Any(), "Delayed OCR leaves no plaintext temporary image");
            File.Delete(Path.Combine(paths.DataDir, "ocr-started"));
            File.Delete(Path.Combine(paths.DataDir, "ocr-completed"));
            Check(IpcServer.Send(["--history"], info["commands"]), "Cancellation history command accepted");
            await BindUiAsync("cancellation UI launched");
            await ui.CallAsync(new("ui", Args: ["--ocr-fixture"]));
            await Until(() => File.Exists(Path.Combine(paths.DataDir, "ocr-started")), "cancelable OCR starts");
            await ui.CallAsync(new("ui", Args: ["--shutdown"]));
            await Until(() => child!.HasExited, "shutdown awaits OCR cancellation and cleanup");
            Check(File.Exists(Path.Combine(paths.DataDir, "ocr-canceled")) && File.Exists(Path.Combine(paths.DataDir, "ocr-completed")), "Explicit UI shutdown cancels and drains OCR before exit");
            Check(!Directory.EnumerateFiles(ocrTemp, "typlo-ocr-*.png").Any(), "Canceled OCR leaves no plaintext temporary image");
            child!.Dispose(); child = null;

            await CheckHistoryTarget();

            await CheckOcrAsync();
            Check(true, "Windows OCR reads PNG from memory; cancellation reaps the helper");
            var feedTask = ServeFeedAsync();
            await runtime.CallAsync(new("update-fixture"));
            await feedTask;
            state = await runtime.CallAsync(new("state"));
            Check(state.AvailableVersion == "9.9.9" && state.UiProcessId == 0, "Automatic update helper exits and publishes the available version without UI");
            Sample("after-update-check");

            var addedWords = await runtime.CallAsync(new("personal-add", Words: [new("словарик", "ru"), new("словарик", "uk"), new("проверка", "ru")]));
            Check(addedWords.ImportResult?.Added == 3, "Resident adds a batch of personal forms over IPC");
            var wordPage = await runtime.CallAsync(new("personal-query", WordQuery: new("сло", "ru")));
            Check(wordPage.WordPage is { Total: 1 } && wordPage.WordPage.Items[0].Word == "словарик", "Native AOT serializes a filtered dictionary page");
            await runtime.CallAsync(new("personal-edit", OriginalWord: new("словарик", "ru"), ReplacementWord: new("словарики", "ru")));
            await runtime.CallAsync(new("personal-word", Text: "проверка", Args: ["ru"], Flag: true));
            var wordExport = Path.Combine(output, "personal-export.json");
            await runtime.CallAsync(new("personal-export", Text: wordExport));
            var imported = await runtime.CallAsync(new("personal-import", Text: wordExport));
            Check(imported.ImportResult is { Added: 0, Skipped: 2 }, "Native AOT exports, validates and imports a personal dictionary without duplicates");
            await runtime.CallAsync(new("excluded-word", Text: "GhB"));
            var excludedPage = await runtime.CallAsync(new("personal-query", WordQuery: new("GH", Exclusions: true)));
            Check(excludedPage.WordPage is { Total: 1 } && excludedPage.WordPage.Items[0].Word == "ghb", "Native AOT adds and queries a normalized global exclusion");
            await runtime.CallAsync(new("personal-edit", OriginalWord: new("ghb", ""), ReplacementWord: new("sql", ""), Flag: true));
            var excludedExport = Path.Combine(output, "excluded-export.json");
            await runtime.CallAsync(new("personal-export", Text: excludedExport, Flag: true));
            await runtime.CallAsync(new("excluded-word", Text: "sql", Flag: true));
            var importedExcluded = await runtime.CallAsync(new("personal-import", Text: excludedExport, Flag: true));
            Check(importedExcluded.ImportResult is { Added: 1 }, "Native AOT edits, deletes, exports and imports exclusions independently of personal forms");
            var checkedDictionary = await runtime.CallAsync(new("dictionary-check"));
            Check(checkedDictionary.AutoCorrection?.Health == DictionaryHealth.Ready, "Resident verifies dictionary readers without restarting");
            Check(IpcServer.Send(["--dictionary"], info["commands"]), "Dictionary manager UI command accepted");
            await BindUiAsync("dictionary manager launched");
            await UntilAsync(async () =>
            {
                var manager = await ui.CallAsync(new("dictionary-manager-fixture"), allowRejected: true);
                return manager.Ok && manager.WordPage is { Total: 2 } && manager.AutoCorrection?.Health == DictionaryHealth.Ready;
            }, "manager displays resident words and dictionary health");
            Check(true, "On-demand manager displays resident pages and verified diagnostic health through real IPC");
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await Until(() => child!.HasExited, "dictionary manager exits after closing"); child!.Dispose(); child = null;

            await OpenSettings();
            Check(child!.Id != firstPid, "UI is recreated on demand");
            host.Kill(); await host.WaitForExitAsync();
            await Until(() => child.HasExited, "UI exits when resident crashes");
            Check(true, "No orphan UI after resident exit");
            File.WriteAllLines(Path.Combine(output, "runtime.txt"), lines); return 0;

            async Task BindUiAsync(string launched)
            {
                await UntilAsync(async () => (state = await runtime.CallAsync(new("state"))).UiProcessId > 0, launched);
                child = Process.GetProcessById(state.UiProcessId);
                var channel = state.UiChannel ?? throw new InvalidOperationException("Resident did not publish the UI generation channel");
                if (state.UiProcessId != Volatile.Read(ref expectedUiPid) && !uiChannels.Add(channel))
                    throw new InvalidOperationException("A new UI process reused a previous generation's channel");
                Volatile.Write(ref expectedUiPid, state.UiProcessId);
                ui = new RuntimeClient(channel, diagnostic: entry =>
                    uiTrace.Enqueue($"{DateTime.UtcNow:O} expected_pid={Volatile.Read(ref expectedUiPid)} {entry.Summary}"));
            }
            async Task OpenSettings(bool userInitiated = false)
            {
                Check(IpcServer.Send(["--settings"], info["commands"]), "Settings command accepted");
                await BindUiAsync("settings UI launched");
                // The editor fixture is the foreground process. Model the user granting focus to
                // the newly spawned UI, rather than relying on Windows' foreground-lock timing.
                if (userInitiated) Native.AllowSetForegroundWindow((uint)state.UiProcessId);
                await ui.CallAsync(new("ui", Args: ["--settings"]));
            }
            async Task CheckHistoryTarget(bool gateRestore = false)
            {
                // The editor has its own STA/message pump, external to the resident and UI child.
                using var editor = new TargetEditor();
                platform.ActivateWindow(editor.Handle);
                await Until(() => platform.ForegroundWindow == editor.Handle, "external editor foreground");
                await OpenSettings(userInitiated: true);
                await Until(() => { var w = platform.ForegroundWindow; Native.GetWindowThreadProcessId(w, out var pid); return pid == child!.Id; }, "UI child foreground");
                var target = await runtime.CallAsync(new("history-target-fixture"));
                Check(target.Window == (long)editor.Handle, "Tray history remembers external editor while Settings is foreground");
                await UntilAsync(async () => (await ui.CallAsync(new("history-target-fixture"))).Window == (long)editor.Handle, "history receives external editor target");
                if (gateRestore)
                {
                    var hold = Path.Combine(paths.DataDir, "hold-hotkey-restore");
                    File.WriteAllText(hold, "hold");
                    try
                    {
                        var dismiss = ui.CallAsync(new("ui", Args: ["--dismiss"]));
                        await Until(() => File.Exists(Path.Combine(paths.DataDir, "hotkey-restore-waiting")), "resident waits at hotkey restore barrier");
                        await dismiss.WaitAsync(TimeSpan.FromSeconds(5));
                        Check((await ui.CallAsync(new("history-target-fixture"))).Window == (long)editor.Handle,
                            "UI dispatcher responds while resident hotkey restore is pending");
                        await Task.Delay(1500); // Exceeds the existing one-second UI idle lifetime.
                        Check(!child!.HasExited, "Pending hotkey restoration keeps the UI alive");
                        await ui.CallAsync(new("ui", Args: ["--settings"]));
                        File.Delete(hold);
                        await Until(() => File.Exists(Path.Combine(paths.DataDir, "hotkey-restore-complete")), "resident completes hotkey restoration");
                        Check(platform.RegisterHotkey(9000, targetFixtureKey!), "Delayed close cannot reactivate shortcuts in reopened Settings");
                        platform.UnregisterAllHotkeys();
                        await ui.CallAsync(new("ui", Args: ["--dismiss"]));
                    }
                    finally { File.Delete(hold); }
                }
                else await ui.CallAsync(new("ui", Args: ["--dismiss"]));
                await Until(() => child!.HasExited, "target-check UI exits");
                if (gateRestore) Check(!platform.RegisterHotkey(9000, targetFixtureKey!), "Closing reopened Settings restores live shortcuts");
                child!.Dispose(); child = null;
            }
            void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); lines.Add("PASS " + message); }
            void Sample(string stage)
            { host.Refresh(); lines.Add($"MEMORY {stage}: working-set={host.WorkingSet64} private-commit={host.PrivateMemorySize64}"); }

            async Task ServeFeedAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var connection = await feed.AcceptTcpClientAsync(timeout.Token);
                using var stream = connection.GetStream();
                var request = new List<byte>();
                var one = new byte[1];
                while (request.Count < 8192)
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("Incomplete feed request");
                    request.Add(one[0]);
                    if (request.Count >= 4 && request.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                const string body = "{\"tag_name\":\"v9.9.9\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://github.com/bitcodepro/Typlo/releases/tag/v9.9.9\",\"assets\":[{\"name\":\"Typlo-9.9.9-win-x64.zip\",\"browser_download_url\":\"https://github.com/bitcodepro/Typlo/releases/download/v9.9.9/Typlo-9.9.9-win-x64.zip\"},{\"name\":\"Typlo-9.9.9-win-x64.zip.sha256\",\"browser_download_url\":\"https://github.com/bitcodepro/Typlo/releases/download/v9.9.9/Typlo-9.9.9-win-x64.zip.sha256\"}]}";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, timeout.Token);
            }
        }
        catch (Exception e)
        { lines.Add("FAIL " + e); File.WriteAllLines(Path.Combine(output, "runtime.txt"), lines); return 1; }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "ui-transport.log"), uiTrace);
            try { if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            child?.Dispose();
        }
        async Task Until(Func<bool> condition, string message)
        { await UntilAsync(async () => { await Task.CompletedTask; return condition(); }, message); }
        async Task UntilAsync(Func<Task<bool>> condition, string message)
        {
            var deadline = Stopwatch.StartNew();
            while (!await condition())
            { if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(50); }
        }
    }
}
