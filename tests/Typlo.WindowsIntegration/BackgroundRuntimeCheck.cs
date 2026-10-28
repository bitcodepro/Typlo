using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Settings;
using Typlo.Core.Storage;

namespace Typlo.WindowsIntegration;

internal static class BackgroundRuntimeCheck
{
    internal static async Task<int> RunAsync(string executable, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var lines = new List<string>();
        var paths = new AppPaths(Path.Combine(output, "data-" + Guid.NewGuid().ToString("N")));
        new SettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            ToggleHotkey = "", ClipboardHotkey = "", UndoHotkey = "", CaseHotkey = "", TranslitHotkey = "",
            ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", LayoutsInitialized = true, StartWithSystem = false, UiLanguage = "en",
        });
        var report = Path.Combine(output, "host-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, WindowStyle = ProcessWindowStyle.Hidden };
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
            var ui = new RuntimeClient("Typlo.Ui." + host.Id);
            var state = await runtime.CallAsync(new("state"));
            Check(state.UiProcessId == 0, "Cold start has no UI child");
            Check(!host.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("Skia", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("libgles", StringComparison.OrdinalIgnoreCase)), "Background does not load Skia or OpenGL");
            Sample("cold");

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
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).UiProcessId > 0, "capture UI launched");
            await ui.CallAsync(new("ui", Args: ["--dismiss"]));
            await UntilAsync(async () => (await runtime.CallAsync(new("state"))).UiProcessId == 0, "canceled capture UI exits");
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

            var feedTask = ServeFeedAsync();
            await runtime.CallAsync(new("update-fixture"));
            await feedTask;
            state = await runtime.CallAsync(new("state"));
            Check(state.AvailableVersion == "9.9.9" && state.UiProcessId == 0, "Automatic update helper exits and publishes the available version without UI");
            Sample("after-update-check");

            await OpenSettings();
            Check(child!.Id != firstPid, "UI is recreated on demand");
            host.Kill(); await host.WaitForExitAsync();
            await Until(() => child.HasExited, "UI exits when resident crashes");
            Check(true, "No orphan UI after resident exit");
            File.WriteAllLines(Path.Combine(output, "runtime.txt"), lines); return 0;

            async Task OpenSettings()
            {
                Check(IpcServer.Send(["--settings"], info["commands"]), "Settings command accepted");
                await UntilAsync(async () => (state = await runtime.CallAsync(new("state"))).UiProcessId > 0, "settings UI launched");
                child = Process.GetProcessById(state.UiProcessId);
                await ui.CallAsync(new("ui", Args: ["--settings"]));
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
