using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Typlo.App.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Native = Typlo.App.Platform.Windows.Native;

namespace Typlo.WindowsIntegration;

internal static class SystemShortcutCheck
{
    internal static async Task<int> RunAsync(string work)
    {
        Directory.CreateDirectory(work);
        var report = new List<string>();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Typlo.App.Log.FilePath = Path.Combine(work, "system-shortcuts.log");
        using var platform = new WinPlatform(monitorClipboard: false);
        platform.AcceptSimulatedSystemShortcuts = true;
        platform.SystemShortcutDiagnostic = (key, up, decision) => trace.Enqueue($"{key} up={up} {decision} foreground={platform.ForegroundWindow}");
        platform.HotkeyPressed += id => trace.Enqueue("HOTKEY " + id);
        var previous = platform.ForegroundWindow;
        var enabled = new nint[GetKeyboardLayoutList(0, null)]; GetKeyboardLayoutList(enabled.Length, enabled);
        var temporary = new List<nint>();
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--direct-editor-fixture");
        using var child = Process.Start(info)!;
        try
        {
            await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            async Task<JsonElement> Command(object message)
            {
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await child.StandardInput.FlushAsync();
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var json = JsonDocument.Parse(line!); return json.RootElement.Clone();
            }
            async Task Until(Func<bool> condition, string message)
            {
                var watch = Stopwatch.StartNew();
                while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(message); await Task.Delay(10); }
            }
            async Task Enter(nint target, AppController owner)
            {
                var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Focused(nint foreground) { if (foreground == target) seen.TrySetResult(); }
                platform.ForegroundChanged += Focused;
                try
                {
                    platform.ActivateWindow(target);
                    await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await owner.ApplicationLayoutsIdleAsync();
                }
                finally { platform.ForegroundChanged -= Focused; }
            }
            foreach (var id in new[] { "00000409", "00000419", "00000422" })
            {
                var handle = LoadKeyboardLayoutW(id, 0);
                if (handle == 0) throw new InvalidOperationException("Could not load temporary fixture layout " + id);
                if (!enabled.Contains(handle)) temporary.Add(handle);
            }
            var layouts = platform.GetKeyboardLayouts();
            KeyboardLayout Language(string code) => layouts.First(l => l.LanguageCode == code);
            var en = Language("en"); var ru = Language("ru"); var uk = Language("uk");
            var state = await Command(new { op = "set", text = "existing text", start = 13, length = 0 });
            var window = (nint)state.GetProperty("window").GetInt64(); var popup = (nint)state.GetProperty("popup").GetInt64();
            var popupProcess = "browser";
            platform.ApplicationProcessProbe = target => target == window ? "ms-teams" : target == popup ? popupProcess : null;
            var paths = new AppPaths(Path.Combine(work, "controller"));
            new SettingsStore(paths.SettingsFile).Save(new AppSettings
            {
                LayoutsInitialized = true, Layouts = [uk.Id, ru.Id, en.Id], WindowsShortcutsInitialized = true,
                ToggleHotkey = "", LayoutSwitchHotkey = "Ctrl+Shift", ScreenshotCopyHotkey = "Win+Shift+S", ScreenshotSaveHotkey = "",
                ClipboardHotkey = "", CaseHotkey = "", TranslitHotkey = "", UndoHotkey = "", AutoCorrectEnabled = false, HistoryEnabled = false,
                ApplicationLayouts = [new() { ProcessName = "ms-teams", Layouts = [en.Id, uk.Id] },
                    new() { ProcessName = "browser", Layouts = [] }, new() { ProcessName = "powershell", Layouts = [en.Id] }],
            });
            using var controller = new AppController(platform, paths, PlatformFactory.CreateKeyProtector(true), diagnostic: true);
            if (controller.FailedHotkeys.Count > 0) throw new InvalidOperationException(string.Join("; ", controller.FailedHotkeys));
            await platform.WaitForTypingWatchAsync();
            platform.ActivateWindow(window); await Until(() => platform.ForegroundWindow == window, "Initial Teams focus");
            platform.SwitchKeyboardLayout(window, en); await Until(() => platform.GetCurrentLayoutId(window) == en.Id, "Initial English layout");
            foreach (var target in new[] { uk, en, uk, en })
            {
                Keys((0xa2, false), (0xa0, false), (0xa0, true), (0xa2, true));
                await Until(() => platform.GetCurrentLayoutId(window) == target.Id, "Teams Ctrl+Shift failed to select " + target.Name);
                await Task.Delay(80);
                if (platform.GetCurrentLayoutId(window) != target.Id) throw new InvalidOperationException("Native switching fired a second time");
            }
            foreach (var target in new[] { uk, en })
            {
                Keys((0xa3, false), (0xa1, false), (0xa3, true), (0xa1, true));
                await Until(() => platform.GetCurrentLayoutId(window) == target.Id, "Right-side Ctrl+Shift failed");
            }
            state = await Command(new { op = "read" });
            if (state.GetProperty("text").GetString() != "existing text") throw new InvalidOperationException("Layout-only switching changed the editor text");
            report.Add("PASS Ctrl+Shift overrides native switching and cycles only EN/UK in the Teams rule without editing text");
            var otherShortcut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observed(int id) { if (id == 90) otherShortcut.TrySetResult(); }
            platform.HotkeyPressed += Observed;
            try
            {
                if (!platform.RegisterHotkey(90, Hotkey.TryParse("Ctrl+Shift+C")!)) throw new InvalidOperationException("Ctrl+Shift+C fixture registration failed");
                Keys((0xa2, false), (0xa0, false), (0x43, false), (0x43, true), (0xa0, true), (0xa2, true));
                await otherShortcut.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                if (platform.GetCurrentLayoutId(window) != en.Id) throw new InvalidOperationException("Ctrl+Shift+C also triggered a layout cycle");
            }
            finally { platform.HotkeyPressed -= Observed; }
            report.Add("PASS deferred modifiers are replayed and Ctrl+Shift+C remains an ordinary shortcut");
            Keys((0xa2, false), (0xa0, false), (0x25, false), (0x25, true), (0xa0, true), (0xa2, true));
            state = await Command(new { op = "read" });
            if (state.GetProperty("length").GetInt32() <= 0 || platform.GetCurrentLayoutId(window) != en.Id)
                throw new InvalidOperationException("Ctrl+Shift+Left lost its text-selection meaning");
            await Command(new { op = "set", text = "existing text", start = 13, length = 0 });
            report.Add("PASS Ctrl+Shift+Left selects text and does not cycle the layout");
            platform.ActivateWindow(popup); await Until(() => platform.ForegroundWindow == popup, "Browser focus");
            platform.SwitchKeyboardLayout(popup, uk); await Until(() => platform.GetCurrentLayoutId(popup) == uk.Id, "Browser initial layout");
            foreach (var target in new[] { ru, en, uk })
            {
                Keys((0xa0, false), (0xa2, false), (0xa2, true), (0xa0, true));
                await Until(() => platform.GetCurrentLayoutId(popup) == target.Id, "Global Ctrl+Shift order failed");
            }
            report.Add("PASS empty browser rule uses the configured UK/RU/EN global order with either modifier press order");
            platform.SwitchKeyboardLayout(popup, ru);
            await Until(() => platform.GetCurrentLayoutId(popup) == ru.Id, "Russian before entering restricted app");
            await Enter(window, controller);
            await Until(() => platform.GetCurrentLayoutId(window) == en.Id, "Restricted EN/UK entry did not replace Russian with English");
            platform.SwitchKeyboardLayout(window, uk);
            await Until(() => platform.GetCurrentLayoutId(window) == uk.Id, "Ukrainian before entering unrestricted app");
            await Enter(popup, controller);
            if (platform.GetCurrentLayoutId(popup) != uk.Id) throw new InvalidOperationException("Unrestricted app changed the active language");
            await Enter(window, controller);
            if (platform.GetCurrentLayoutId(window) != uk.Id) throw new InvalidOperationException("Restricted app replaced its allowed Ukrainian layout");
            var entrySettings = controller.Settings.Clone();
            entrySettings.ApplicationLayouts[0].Layouts = [uk.Id, ru.Id];
            entrySettings.ApplicationLayouts[1].Layouts = [uk.Id, ru.Id, en.Id];
            controller.ApplySettings(entrySettings); await controller.ApplicationLayoutsIdleAsync();
            platform.SwitchKeyboardLayout(window, en);
            await Until(() => platform.GetCurrentLayoutId(window) == en.Id, "English before no-English rule");
            await Enter(popup, controller);
            if (platform.GetCurrentLayoutId(popup) != en.Id) throw new InvalidOperationException("Wider three-language rule changed an allowed layout");
            await Enter(window, controller);
            await Until(() => platform.GetCurrentLayoutId(window) == uk.Id, "Rule without English did not choose its first layout");
            state = await Command(new { op = "read" });
            if (state.GetProperty("text").GetString() != "existing text") throw new InvalidOperationException("Automatic app entry edited existing text");
            entrySettings.ApplicationLayouts[0].Layouts = [en.Id, uk.Id];
            entrySettings.ApplicationLayouts[1].Layouts = [];
            controller.ApplySettings(entrySettings); await controller.ApplicationLayoutsIdleAsync();
            report.Add("PASS app entry prefers English for a disallowed layout, otherwise preserves allowed languages or chooses the first configured layout");
            popupProcess = "powershell"; controller.ApplySettings(controller.Settings.Clone());
            platform.ActivateWindow(window); await Until(() => platform.ForegroundWindow == window, "Teams focus");
            platform.SwitchKeyboardLayout(popup, ru); await Until(() => platform.GetCurrentLayoutId(popup) == ru.Id, "PowerShell initial layout");
            platform.ActivateWindow(popup);
            await Until(() => platform.GetCurrentLayoutId(popup) == en.Id, "Single-language entry did not select English");
            Keys((0xa2, false), (0xa0, false), (0xa0, true), (0xa2, true)); await Task.Delay(150);
            if (platform.GetCurrentLayoutId(popup) != en.Id) throw new InvalidOperationException("Single-language cycle escaped English");
            report.Add("PASS single-language rule activates English on entry and Ctrl+Shift stays on English");
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var captures = 0;
            controller.ScreenshotRequested += _ => { Interlocked.Increment(ref captures); captured.TrySetResult(); };
            Keys((0x5b, false), (0xa0, false), (0x53, false), (0x53, false), (0x53, true), (0xa0, true), (0x5b, true));
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(300);
            if (captures != 1 || platform.ForegroundWindow != popup) throw new InvalidOperationException("Win+Shift+S repeated or opened Windows UI");
            report.Add("PASS Win+Shift+S opens only Typlo's capture command once and does not activate the Windows snipper or Start menu");
            controller.SuspendHotkeys();
            if (!platform.RegisterHotkey(9000, Hotkey.TryParse("Ctrl+Shift")!)) throw new InvalidOperationException("Suspension retained a conflicting binding");
            platform.UnregisterAllHotkeys();
            report.Add("PASS shortcut interception can be suspended for editing settings");
            await child.StandardInput.WriteLineAsync("{\"op\":\"exit\"}"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) { report.Add("FAIL " + e); foreach (var line in trace) Console.WriteLine(line); }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            platform.UnregisterAllHotkeys();
            Keys((0xa0, true), (0xa2, true), (0x5b, true));
            if (previous != 0) platform.ActivateWindow(previous);
            foreach (var handle in temporary.Distinct()) if (!UnloadKeyboardLayout(handle)) report.Add("FAIL temporary keyboard layout was not unloaded");
            await File.WriteAllLinesAsync(Path.Combine(work, "system-shortcuts.txt"), report);
            foreach (var line in report) Console.WriteLine(line);
        }
        return report.Count == 8 && report.All(line => line.StartsWith("PASS ")) ? 0 : 1;
    }
    internal static unsafe void Keys(params (ushort Key, bool Up)[] keys)
    {
        var input = new Native.INPUT[keys.Length];
        for (var i = 0; i < keys.Length; i++) input[i] = new Native.INPUT { type = 1,
            ki = new Native.KEYBDINPUT { wVk = keys[i].Key,
                dwFlags = (keys[i].Up ? 2u : 0u) | (keys[i].Key is 0xa3 or 0xa5 or 0x25 ? 1u : 0u) } };
        fixed (Native.INPUT* pointer = input)
            if (Native.SendInput((uint)input.Length, pointer, sizeof(Native.INPUT)) != input.Length) throw new IOException("Fixture input failed");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayoutW(string id, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnloadKeyboardLayout(nint layout);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
}
