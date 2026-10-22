using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Platform.Shared;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Storage;

namespace Typlo.App.Services;

/// <summary>
/// End-to-end checks against the real clipboard, hotkeys and keyboard input of the current OS:
/// <c>Typlo --selftest report.txt</c>. Uses a temporary data folder and restores the clipboard afterwards.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var lines = new List<string>();
        var failures = 0;
        var dir = Path.Combine(Path.GetTempPath(), "typlo-selftest-" + Guid.NewGuid().ToString("N"));
        using var platform = PlatformFactory.Create();
        if (platform is HookPlatformBase hook) hook.AcceptSimulatedHotkeys = true;
        lines.Add($"Platform: {platform.Name}");
        var caps = platform.Capabilities;
        var userClipboard = platform.Snapshot();
        AppController? controller = null;

        async Task Check(string name, Func<Task<string?>> test, bool when = true)
        {
            if (!when) { lines.Add($"SKIP {name}"); return; }
            try
            {
                var error = await test();
                lines.Add(error is null ? $"PASS {name}" : $"FAIL {name}: {error}");
                if (error is not null) failures++;
            }
            catch (Exception e)
            {
                lines.Add($"FAIL {name}: {e.GetType().Name}: {e.Message}");
                failures++;
            }
        }

        try
        {
            controller = new AppController(platform, new AppPaths(dir), PlatformFactory.CreateKeyProtector());
            controller.ApplySettings(Tweak(controller.Settings));

            await Check("clipboard text round-trip", () =>
            {
                platform.SetText("Привет, clipboard ✓");
                var back = platform.ReadText();
                return Task.FromResult(back == "Привет, clipboard ✓" ? null : $"got '{back}'");
            });

            await Check("own writes are not captured", async () =>
            {
                platform.SetText("own-write-should-not-be-stored");
                await Task.Delay(1500);
                return controller.Store.List().Any(e => e.PreviewText.Contains("own-write")) ? "was stored" : null;
            });

            await Check("snapshot/restore", () =>
            {
                platform.SetText("original");
                var snap = platform.Snapshot();
                platform.SetText("temporary");
                platform.Restore(snap);
                var back = platform.ReadText();
                return Task.FromResult(back == "original" ? null : $"got '{back}'");
            });

            await Check("external text copy is captured (encrypted store)", async () =>
            {
                var marker = "selftest-" + Guid.NewGuid().ToString("N")[..8];
                External.SetText(marker);
                return await WaitFor(() => controller.Store.List().Any(e => e.PreviewText == marker), 5000)
                    ? null : "not captured within 5 s";
            });

            await Check("external image copy is captured with thumbnail", async () =>
            {
                External.SetImage(await MakePng(200, 120));
                var ok = await WaitFor(() => controller.Store.List().Any(e => e is { Kind: ClipKind.Image, Width: 200, Height: 120, Thumbnail.Length: > 0 }), 5000);
                return ok ? null : "image not captured";
            });

            await Check("image from history goes back to clipboard", async () =>
            {
                var img = controller.Store.List().FirstOrDefault(e => e.Kind == ClipKind.Image);
                if (img is null) return "no image in history";
                platform.SetText("placeholder");
                await controller.PasteAsync(img.Id, 0, paste: false);
                await Task.Delay(300);
                var size = External.ImageSize();
                return size == "200x120" ? null : $"clipboard image is '{size}'";
            });

            await Check("excluded app is not stored", async () =>
            {
                var s = controller.Settings.Clone();
                s.ExcludedApps = ["powershell"];
                controller.ApplySettings(s);
                // Keep the process alive: once a copying app exits, Windows no longer reports it as the owner.
                var ps = External.RunPowerShellAsync("Set-Clipboard -Value 'excluded-secret-123'; Start-Sleep -Seconds 2");
                await Task.Delay(800);
                var stored = controller.Store.List().FirstOrDefault(e => e.PreviewText == "excluded-secret-123");
                s.ExcludedApps = [];
                controller.ApplySettings(s);
                await ps;
                return stored is null ? null : $"was stored (source app '{stored.SourceApp}')";
            }, when: OperatingSystem.IsWindows());

            await Check("global hotkey fires on key press", async () =>
            {
                var fired = new TaskCompletionSource();
                void OnPressed(int id) { if (id == 4242) fired.TrySetResult(); }
                platform.HotkeyPressed += OnPressed;
                try
                {
                    if (!platform.RegisterHotkey(4242, new Hotkey(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "F11")))
                        return "could not register Ctrl+Alt+Shift+F11";
                    await Task.Delay(300);
                    platform.SendChord(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "F11");
                    var done = await Task.WhenAny(fired.Task, Task.Delay(3000));
                    return done == fired.Task ? null : "hotkey did not fire";
                }
                finally
                {
                    platform.HotkeyPressed -= OnPressed;
                    controller.RegisterHotkeys();
                }
            }, when: caps.GlobalHotkeys);

            await Check("convert selected text in a real text box (ghbdtn → привет)", () => ConvertInTextBox(controller, platform,
                "ghbdtn vbh", KeyboardLayouts.Russian, "привет мир"), when: caps.SimulateInput);
            await Check("convert back (руддщ → hello)", () => ConvertInTextBox(controller, platform,
                "руддщ", KeyboardLayouts.English, "hello"), when: caps.SimulateInput);
            await Check("conversion restores the user's clipboard", async () =>
            {
                platform.SetText("keep-me");
                var err = await ConvertInTextBox(controller, platform, "ntcn", KeyboardLayouts.Russian, "тест");
                var back = platform.ReadText();
                return err ?? (back == "keep-me" ? null : $"clipboard is '{back}'");
            }, when: caps.SimulateInput);

            await Check("keep-awake request is accepted by the OS", () =>
                Task.FromResult(platform.SetKeepAwake(true) && platform.SetKeepAwake(false) ? null : "keep-awake failed"));

            await Check("activity signal resets the system idle timer (Teams presence)", async () =>
            {
                if (!OperatingSystem.IsWindows()) { platform.SignalUserActivity(); return null; }
                await Task.Delay(1500);
                var before = IdleMs();
                if (before < 1000) return null; // the user is typing; nothing to measure
                Platform.Windows.WinPlatform.SendZeroMouseMove();
                await Task.Delay(100);
                var after = IdleMs();
                return after < before ? null : $"idle {before} ms -> {after} ms";
            });

            await Check("history file does not contain plaintext", () =>
            {
                var all = string.Concat(Directory.GetFiles(dir).Where(f => f.Contains("history.db")).Select(f =>
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return new StreamReader(fs).ReadToEnd();
                }));
                return Task.FromResult(all.Contains("selftest-") ? "plaintext marker found in db" : null);
            });
        }
        catch (Exception e)
        {
            lines.Add($"FAIL setup: {e}");
            failures++;
        }
        finally
        {
            controller?.Dispose();
            try { platform.Restore(userClipboard); } catch (Exception) { }
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        lines.Add(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        await File.WriteAllLinesAsync(reportPath, lines);
        return failures == 0 ? 0 : 1;
    }

    private static uint IdleMs()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var info = new Platform.Windows.Native.LASTINPUTINFO { cbSize = 8 };
        Platform.Windows.Native.GetLastInputInfo(ref info);
        return (uint)Platform.Windows.Native.GetTickCount64() - info.dwTime;
    }

    private static Core.Settings.AppSettings Tweak(Core.Settings.AppSettings s)
    {
        // Self-test must not grab the user's real shortcuts or touch autostart.
        s.ToggleHotkey = "";
        s.ClipboardHotkey = "";
        s.LayoutHotkeys = [];
        s.SwitchSystemLayout = false;
        s.StartWithSystem = false;
        s.ExcludedApps = [];
        return s;
    }

    private static async Task<byte[]> MakePng(int w, int h) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(w, h));
        using (var ctx = bmp.CreateDrawingContext())
            ctx.FillRectangle(Avalonia.Media.Brushes.OrangeRed, new Rect(0, 0, w, h));
        using var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    });

    private static async Task<string?> ConvertInTextBox(AppController controller, IPlatform platform,
        string input, KeyboardLayout target, string expected)
    {
        Window? window = null;
        TextBox? box = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            box = new TextBox { Text = input, AcceptsReturn = false };
            window = new Window { Width = 400, Height = 120, Content = box, Title = "Typlo self-test", Topmost = true };
            window.Show();
            window.Activate();
        });
        try
        {
            await Task.Delay(500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (OperatingSystem.IsWindows() && window!.TryGetPlatformHandle()?.Handle is { } h) platform.ActivateWindow(h);
                window!.Activate();
                box!.Focus();
                box.SelectAll();
            });
            await Task.Delay(300);
            await controller.ConvertSelectionAsync(target);
            await Task.Delay(300);
            var text = await Dispatcher.UIThread.InvokeAsync(() => box!.Text);
            return text == expected ? null : $"text box has '{text}'";
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close());
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Puts things on the clipboard from another process, the way a real app would.</summary>
    private static class External
    {
        public static void SetText(string text)
        {
            if (OperatingSystem.IsWindows()) _ = RunPowerShellAsync($"Set-Clipboard -Value '{text}'; Start-Sleep -Seconds 1");
            else if (OperatingSystem.IsMacOS()) Proc.Run("/usr/bin/pbcopy", [], System.Text.Encoding.UTF8.GetBytes(text));
            else Proc.Run("xclip", ["-selection", "clipboard", "-t", "UTF8_STRING", "-i"], System.Text.Encoding.UTF8.GetBytes(text), captureOutput: false);
        }

        public static void SetImage(byte[] png)
        {
            var file = Path.Combine(Path.GetTempPath(), "typlo-selftest.png");
            File.WriteAllBytes(file, png);
            if (OperatingSystem.IsWindows())
                _ = RunPowerShellAsync($"""
                    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                    [System.Windows.Forms.Clipboard]::SetImage([System.Drawing.Image]::FromFile('{file}')); Start-Sleep -Seconds 1
                    """);
            else if (OperatingSystem.IsMacOS())
                Proc.Run("/usr/bin/osascript", ["-e", $"set the clipboard to (read (POSIX file \"{file}\") as «class PNGf»)"]);
            else Proc.Run("xclip", ["-selection", "clipboard", "-t", "image/png", "-i"], png, captureOutput: false);
        }

        public static string ImageSize()
        {
            if (OperatingSystem.IsWindows())
                return RunPowerShellAsync("""
                    Add-Type -AssemblyName System.Windows.Forms
                    $i = [System.Windows.Forms.Clipboard]::GetImage(); if ($i) { "$($i.Width)x$($i.Height)" } else { "none" }
                    """).Result.Trim();
            var png = OperatingSystem.IsMacOS()
                ? PngFromMac()
                : Proc.Run("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]).Output;
            if (png is not { Length: > 0 }) return "none";
            using var bmp = new Bitmap(new MemoryStream(png));
            return $"{bmp.PixelSize.Width}x{bmp.PixelSize.Height}";
        }

        private static byte[]? PngFromMac()
        {
            var file = Path.Combine(Path.GetTempPath(), "typlo-selftest-out.png");
            var script = $"set f to open for access POSIX file \"{file}\" with write permission\nset eof f to 0\n" +
                         "write (the clipboard as «class PNGf») to f\nclose access f";
            return Proc.Run("/usr/bin/osascript", ["-e", script]).Ok ? File.ReadAllBytes(file) : null;
        }

        public static async Task<string> RunPowerShellAsync(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-STA", "-NonInteractive", "-Command", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
    }
}
