using System.Runtime.InteropServices;
using Typlo.App.Platform;
using Typlo.App.Platform.Shared;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Settings;

namespace Typlo.WindowsIntegration;

// Runs the real resident controller against a separate, temporary profile. This runner does
// not drive any UI: a human or Computer Use types in the actual editor. Simulated physical
// keys are accepted only in this explicitly started test process, never in the shipped app.
internal static class InteractiveInputCheck
{
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Typlo.App.Log.FilePath = Path.Combine(directory, "live-input.log");
        var prior = new nint[GetKeyboardLayoutList(0, null)]; GetKeyboardLayoutList(prior.Length, prior);
        var loaded = new List<nint>();
        using var platform = new WinPlatform(monitorClipboard: false) { AcceptSimulatedSystemShortcuts = true };
        var window = platform.ForegroundWindow;
        var original = platform.GetKeyboardLayouts().FirstOrDefault(l => l.Id == platform.GetCurrentLayoutId(window));
        try
        {
            foreach (var id in new[] { "00000409", "00000419", "00000422" })
            {
                var handle = LoadKeyboardLayoutW(id, 0);
                if (handle == 0) throw new InvalidOperationException("Cannot load test input source " + id);
                if (!prior.Contains(handle)) loaded.Add(handle);
            }
            var layouts = platform.GetKeyboardLayouts();
            var en = layouts.First(l => l.LanguageCode == "en");
            var uk = layouts.First(l => l.LanguageCode == "uk");
            var ru = layouts.First(l => l.LanguageCode == "ru");
            var paths = new AppPaths(directory);
            new SettingsStore(paths.SettingsFile).Save(new AppSettings
            {
                WindowsShortcutsInitialized = true, LayoutsInitialized = true, Layouts = [uk.Id, ru.Id, en.Id],
                ToggleHotkey = "Ctrl+Alt+Shift+Space", LayoutSwitchHotkey = "Ctrl+Shift",
                LayoutHotkeys = [new() { LayoutId = en.Id, Hotkey = "Ctrl+Shift+E" },
                    new() { LayoutId = uk.Id, Hotkey = "Ctrl+Shift+U" }, new() { LayoutId = ru.Id, Hotkey = "Ctrl+Shift+R" }],
                ClipboardHotkey = "", ScreenshotCopyHotkey = "", ScreenshotSaveHotkey = "", CaseHotkey = "", TranslitHotkey = "",
                UndoHotkey = "Ctrl+Alt+Shift+Z", AutoCorrectEnabled = true, HistoryEnabled = false,
                ApplicationLayouts = [new() { ProcessName = "ms-teams", Layouts = [en.Id, uk.Id] }],
            });
            TypingEvents.AcceptSimulated = true;
            using var controller = new AppController(platform, paths, PlatformFactory.CreateKeyProtector(true), diagnostic: true);
            await platform.WaitForTypingWatchAsync();
            var characters = 0;
            platform.KeyTyped += key => { if (!key.Reset && !key.Backspace) Interlocked.Increment(ref characters); };
            Console.WriteLine("READY " + string.Join(',', controller.FailedHotkeys));
            while (await Console.In.ReadLineAsync() is { } command && command != "stop")
                if (command == "state") Console.WriteLine($"characters={characters}; layout={platform.GetCurrentLayoutId(platform.ForegroundWindow)}; dictionary={controller.DictionaryIsOpen}");
            await controller.AutoCorrectionIdleAsync();
            if (original is not null) { platform.SwitchKeyboardLayout(window, original); await Task.Delay(150); }
            return 0;
        }
        finally
        {
            TypingEvents.AcceptSimulated = false;
            platform.Dispose();
            foreach (var handle in loaded.Distinct()) UnloadKeyboardLayout(handle);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayoutW(string id, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnloadKeyboardLayout(nint layout);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
}
