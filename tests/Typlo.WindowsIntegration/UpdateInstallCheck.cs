using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Settings;

namespace Typlo.WindowsIntegration;

internal static class UpdateInstallCheck
{
    internal static async Task<int> RunAsync(string executable, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var lines = new List<string>();
        var install = Path.Combine(output, "installation with spaces");
        Directory.CreateDirectory(install);
        var source = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(install, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
        var archive = Path.Combine(output, "update.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
            using var writer = new StreamWriter(zip.CreateEntry("update-check.txt").Open()); writer.Write("new version");
        }
        var bytes = await File.ReadAllBytesAsync(archive);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var mode = 0; // 0 HTTP failure, 1 bad checksum, 2/3 valid archive
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Serve();
        var paths = new AppPaths(Path.Combine(install, "data"));
        new SettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            LayoutsInitialized = true, WindowsShortcutsInitialized = true,
            ToggleHotkey = "", LayoutSwitchHotkey = "", ClipboardHotkey = "", AddWordHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "",
            ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", StartWithSystem = false, UiLanguage = "en",
        });
        var settings = await File.ReadAllBytesAsync(paths.SettingsFile);
        var report = Path.Combine(output, "host.txt");
        var start = new ProcessStartInfo(Path.Combine(install, "Typlo.exe")) { UseShellExecute = false, WorkingDirectory = install, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--background-probe", "--profile", paths.DataDir, "--report", report, "--lifetime-seconds", "180", "--tray" }) start.ArgumentList.Add(arg);
        start.Environment["TYPLO_UPDATE_FEED"] = origin + "/latest";
        Process? host = null, restarted = null, child = null;
        try
        {
            host = Process.Start(start) ?? throw new IOException("Cannot start update fixture");
            var info = await HostInfo();
            var runtime = new RuntimeClient(info["channel"]);
            settings = await File.ReadAllBytesAsync(paths.SettingsFile);
            await runtime.CallAsync(new("personal-word", Text: "ревьюкать", Args: ["ru"]));
            var personal = await ReadPersonalFile();
            if (!IpcServer.Send(["--update"], info["commands"])) throw new IOException("Update dialog command failed");
            RuntimeClient ui = null!;
            await BindUi();
            await Until(async () => (await ui.CallAsync(new("update-dialog-fixture"), allowRejected: true)).AvailableVersion == "9.9.9", "initial release check");
            await ui.CallAsync(new("update-install-fixture"));
            await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var active = await runtime.CallAsync(new("update-state"));
            Check(!child!.HasExited && active.UpdateInstalling, "Update Now keeps its UI open while the download is pending");
            release.TrySetResult();
            await Until(async () => (await ui.CallAsync(new("update-dialog-fixture"), allowRejected: true)).Error is { Length: > 0 }, "visible HTTP error");
            Check(!host.HasExited && !File.Exists(Path.Combine(install, "update-check.txt")), "HTTP failure is visible and leaves the original installation running");
            Volatile.Write(ref mode, 1);
            await ui.CallAsync(new("update-install-fixture"));
            await Until(async () => (await runtime.CallAsync(new("update-state"))).UpdateError?.Contains("Checksum mismatch") == true, "checksum rejection");
            await Until(async () => (await ui.CallAsync(new("update-dialog-fixture"), allowRejected: true)).Error?.Contains("Checksum mismatch") == true, "visible checksum error");
            Check(!File.Exists(Path.Combine(install, "update-check.txt")), "Bad checksum is displayed and never installs the archive");
            Volatile.Write(ref mode, 2);
            await runtime.CallAsync(new("dictionary-fixture")); // The real resident holds the pack open before Update Now.
            var oldUi = child!.Id;
            using (var locked = new FileStream(Path.Combine(install, "Typlo.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ui.CallAsync(new("update-install-fixture"));
                await Until(async () => !(await runtime.CallAsync(new("update-state"))).UpdateInstalling && (await runtime.CallAsync(new("update-state"))).UpdateError is { Length: > 0 }, "locked-file rejection");
                await Until(async () => { var state = await runtime.CallAsync(new("state")); return state.UiProcessId > 0 && state.UiProcessId != oldUi; }, "replacement failure reopens UI");
                await BindUi();
                await Until(async () => (await ui.CallAsync(new("update-dialog-fixture"), allowRejected: true)).Error is { Length: > 0 }, "reopened error dialog");
                Check(!host.HasExited && !File.Exists(Path.Combine(install, "update-check.txt")), "Locked destination rolls back and reopens a visible error dialog");
            }
            await runtime.CallAsync(new("dictionary-fixture"));
            await ui.CallAsync(new("update-install-fixture"));
            await Until(() => Task.FromResult(host.HasExited), "old resident exits after installation");
            await Until(async () => (await HostInfo())["pid"] != host.Id.ToString(), "updated resident starts");
            var next = await HostInfo(); restarted = Process.GetProcessById(int.Parse(next["pid"]));
            var nextRuntime = new RuntimeClient(next["channel"]);
            await nextRuntime.CallAsync(new("state"));
            Check(!restarted.HasExited && File.ReadAllText(Path.Combine(install, "update-check.txt")) == "new version", "Download, checksum, extraction, replacement and native restart succeed with an opened dictionary");
            var newSettings = await File.ReadAllBytesAsync(paths.SettingsFile);
            var newPersonal = await ReadPersonalFile();
            Check(settings.SequenceEqual(newSettings) && personal.SequenceEqual(newPersonal), "Update preserves settings and the personal dictionary");
            await nextRuntime.CallAsync(new("dictionary-fixture"));
            Check(true, "Restarted resident opens the new dictionary pack");

            async Task BindUi()
            {
                RuntimeReply state = new(false);
                await Until(async () => (state = await runtime.CallAsync(new("state"))).UiProcessId > 0, "UI process published");
                child?.Dispose(); child = Process.GetProcessById(state.UiProcessId);
                ui = new RuntimeClient(state.UiChannel!);
            }
        }
        catch (Exception e) { lines.Add("FAIL " + e); }
        finally
        {
            release.TrySetResult(); stop.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            foreach (var process in new[] { host, restarted, child })
            { try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { } finally { process?.Dispose(); } }
            await File.WriteAllLinesAsync(Path.Combine(output, "update.txt"), lines);
        }
        return lines.Any(l => l.StartsWith("FAIL")) ? 1 : 0;

        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); lines.Add("PASS " + message); }
        async Task<byte[]> ReadPersonalFile()
        {
            await using var file = new FileStream(Path.Combine(paths.DataDir, "personal-words.sqlite3"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var data = new MemoryStream(); await file.CopyToAsync(data); return data.ToArray();
        }
        async Task<Dictionary<string, string>> HostInfo()
        {
            await Until(() => Task.FromResult(File.Exists(report) && File.ReadAllLines(report).Length >= 3), "resident report");
            return File.ReadAllLines(report).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        }
        async Task Until(Func<Task<bool>> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(30); }
        }
        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var path = request?.Split(' ')[1];
                byte[] body; var status = "200 OK";
                if (path == "/latest")
                    body = Encoding.UTF8.GetBytes($$"""{"tag_name":"v9.9.9","assets":[{"name":"Typlo-9.9.9-win-x64.zip","browser_download_url":"{{origin}}/archive"},{"name":"Typlo-9.9.9-win-x64.zip.sha256","browser_download_url":"{{origin}}/checksum"}]}""");
                else if (path == "/archive")
                {
                    if (Volatile.Read(ref mode) == 0)
                    {
                        downloading.TrySetResult(); await release.Task.WaitAsync(stop.Token);
                        status = "503 Service Unavailable"; body = [];
                    }
                    else body = bytes;
                }
                else body = Encoding.ASCII.GetBytes($"{(Volatile.Read(ref mode) == 1 ? new string('0', 64) : checksum)}  Typlo-9.9.9-win-x64.zip\n");
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
            }
        }
    }
}
