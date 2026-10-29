using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typlo.App.Platform;
using Typlo.App.Views;
using Typlo.Core;
using Typlo.Core.Storage;
using Typlo.Core.Text;

namespace Typlo.App.Services;

/// <summary>
/// <c>Typlo.exe --screenshots dir</c>: fills a temporary history with sample clips and renders
/// both windows to PNG, for checking the UI without touching the user's data.
/// </summary>
internal static class ScreenshotMode
{
    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var dataDir = Path.Combine(Path.GetTempPath(), "typlo-shots-" + Guid.NewGuid().ToString("N"));
        using var platform = PlatformFactory.Create();
        var controller = new AppController(platform, new AppPaths(dataDir), PlatformFactory.CreateKeyProtector(diagnostic: true), diagnostic: true);
        try
        {
            var s = controller.Settings.Clone();
            s.ToggleHotkey = ""; s.ClipboardHotkey = ""; s.CaseHotkey = ""; s.TranslitHotkey = ""; s.UndoHotkey = ""; s.ScreenshotCopyHotkey = ""; s.ScreenshotSaveHotkey = ""; s.StartWithSystem = false;
            controller.ApplySettings(s);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var (w, h, color) in new[] { (800, 450, "#F97316"), (300, 300, "#22C55E") })
                {
                    using var bmp = new RenderTargetBitmap(new PixelSize(w, h));
                    using (var ctx = bmp.CreateDrawingContext())
                        ctx.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color)), new Rect(0, 0, w, h));
                    using var ms = new MemoryStream();
                    bmp.Save(ms, PngBitmapEncoderOptions.Default);
                    var img = ImageCodec.Encode(ms.ToArray(), true);
                    controller.Store.Add(ClipContent.FromImage(img.Png, img.Thumbnail, img.Width, img.Height, "mspaint"));
                }
            });
            controller.Store.Add(ClipContent.FromText("SELECT id, name FROM users\nWHERE created_at > now() - interval '7 days'\nORDER BY name;", "Code"));
            controller.Store.Add(ClipContent.FromText("https://github.com/AvaloniaUI/Avalonia", "chrome"));
            var pin = controller.Store.Add(ClipContent.FromText("Привет! Это закреплённая запись — она не удаляется при очистке.", "Telegram"));
            controller.Store.SetPinned(pin!.Value, true);
            controller.Store.Add(ClipContent.FromText("ghbdtn vbh", "notepad"));
            controller.Store.Add(ClipContent.FromFiles([@"C:\Users\me\Documents\Quarterly report.pdf", @"C:\Users\me\Pictures\team.jpg",
                @"C:\Users\me\Desktop\notes.txt"], "explorer"));
            controller.Store.Add(ClipContent.FromText("Q3 revenue\t1 240 000\nQ4 revenue\t1 410 000", "EXCEL",
                "<table><tr><td>Q3 revenue</td><td>1 240 000</td></tr></table>"));

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                using (var icon = File.Create(Path.Combine(outDir, "icon.png"))) AppIcon.Create().Save(icon);
                var language = Environment.GetEnvironmentVariable("TYPLO_SCREENSHOT_LANGUAGE");
                foreach (var (code, _) in Loc.Languages.Where(l => string.IsNullOrEmpty(language) || l.Code == language))
                {
                    var ls = controller.Settings.Clone();
                    ls.UiLanguage = code;
                    controller.ApplySettings(ls);

                    var history = new ClipboardWindow(controller);
                    history.ShowFor(0);
                    await Task.Delay(600);
                    Save(history, Path.Combine(outDir, $"history_{code}.png"));
                    history.Close();

                    var settings = new SettingsWindow(controller);
                    settings.Show();
                    await Task.Delay(600);
                    Save(settings, Path.Combine(outDir, $"settings_{code}.png"));
                    settings.Close();
                }

                // Dark theme, with a snippet, scrolled to the new sections.
                var dark = controller.Settings.Clone();
                controller.ConfigureDiagnosticLayouts([Typlo.Core.Layouts.KeyboardLayouts.English, Typlo.Core.Layouts.KeyboardLayouts.Russian, Typlo.Core.Layouts.KeyboardLayouts.Ukrainian]);
                dark.Layouts = ["en", "ru", "uk"];
                dark.ApplicationLayouts = [new() { ProcessName = "sample-chat", Layouts = ["en", "ru", "uk"] },
                    new() { ProcessName = "sample-editor", Layouts = ["en"] }];
                dark.UiTheme = "dark";
                dark.Snippets = [new Snippet { Trigger = ";hello", Text = "Здравствуйте, {cursor}!\n\n    Спасибо за сообщение.\n\nС уважением,\n" }];
                controller.ApplySettings(dark);
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                var darkHistory = new ClipboardWindow(controller);
                darkHistory.ShowFor(0);
                await Task.Delay(600);
                Save(darkHistory, Path.Combine(outDir, "history_dark.png"));
                darkHistory.Close();
                var darkSettings = new SettingsWindow(controller);
                darkSettings.Show();
                await Task.Delay(600);
                Save(darkSettings, Path.Combine(outDir, "settings_dark.png"));
                darkSettings.FindControl<TextBlock>("CaptureSaveStatus")?.BringIntoView();
                await Task.Delay(300);
                Save(darkSettings, Path.Combine(outDir, "settings_screenshots.png"));
                darkSettings.FindControl<StackPanel>("CycleRows")?.BringIntoView();
                await Task.Delay(300);
                Save(darkSettings, Path.Combine(outDir, "settings_layout_cycle.png"));
                darkSettings.FindControl<StackPanel>("ApplicationLayoutRows")?.BringIntoView();
                await Task.Delay(300);
                Save(darkSettings, Path.Combine(outDir, "settings_application_layouts.png"));
                if (darkSettings.FindControl<ScrollViewer>("Scroller") is { } scroller)
                {
                    scroller.ScrollToEnd();
                    await Task.Delay(400);
                    Save(darkSettings, Path.Combine(outDir, "settings_dark_bottom.png"));
                    var preview = darkSettings.GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(b => Equals(b.Content, Loc.T("settings.snippetPreview")));
                    preview?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(200);
                    scroller.ScrollToEnd();
                    await Task.Delay(200);
                    Save(darkSettings, Path.Combine(outDir, "settings_snippet_preview.png"));
                }
                darkSettings.Close();

                var sql = controller.Store.List().First(e => e.PreviewText.StartsWith("SELECT"));
                var viewer = new EntryViewWindow(controller, sql, 0);
                viewer.Show();
                await Task.Delay(500);
                Save(viewer, Path.Combine(outDir, "viewer_dark.png"));
                viewer.Close();

                var shortcuts = new ShortcutsWindow(controller.Settings, null, controller.AvailableLayouts);
                shortcuts.Show();
                await Task.Delay(500);
                Save(shortcuts, Path.Combine(outDir, "shortcuts_dark.png"));
                shortcuts.Close();

                // The update dialog checks the feed when it opens (TYPLO_UPDATE_FEED points it at a test server).
                using var updates = new UpdateService(dataDir);
                var dialog = new UpdateDialog(updates, () => Task.CompletedTask);
                dialog.Show();
                await Task.Delay(3000);
                Save(dialog, Path.Combine(outDir, "update_dialog.png"));
                dialog.Close();
            });
        }
        finally
        {
            controller.Dispose();
            try { Directory.Delete(dataDir, true); } catch (Exception) { }
        }
    }

    private static void Save(Window window, string path)
    {
        var size = new PixelSize((int)window.Bounds.Width * 2, (int)window.Bounds.Height * 2);
        using var bmp = new RenderTargetBitmap(size, new Vector(192, 192));
        bmp.Render(window);
        bmp.Save(path, PngBitmapEncoderOptions.Default);
    }
}
