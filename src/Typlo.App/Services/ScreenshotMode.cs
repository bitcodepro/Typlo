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
            s.AutoCorrectEnabled = false;
            s.AddWordHotkey = ""; s.LayoutSwitchHotkey = "";
            s.ToolsHotkey = s.ChoicesHotkey = s.AcceptChoiceHotkey = s.CancelChoiceHotkey = "";
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
                    new() { ProcessName = "sample-editor", Layouts = ["en"] }, new() { ProcessName = "sample-browser", Layouts = [] }];
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
                darkSettings.FindControl<TextBlock>("DictionaryStatus")?.BringIntoView();
                await Task.Delay(300);
                Save(darkSettings, Path.Combine(outDir, "settings_auto_correction.png"));
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

                var sample = new Border { Width = 960, Height = 520, Padding = new Thickness(40),
                    Background = Avalonia.Media.Brushes.White, Child = new StackPanel { Spacing = 24, Children =
                    {
                        new TextBlock { Text = "Project overview", FontSize = 30, Foreground = Avalonia.Media.Brushes.Black },
                        new TextBlock { Text = "Release checklist", FontSize = 22, Foreground = Avalonia.Media.Brushes.SlateGray },
                        new Border { Padding = new Thickness(24), Background = Avalonia.Media.Brushes.AliceBlue, CornerRadius = new CornerRadius(12),
                            Child = new TextBlock { Text = "Design review       Ready\n\nWindows build     Passed\n\nDocumentation    In progress", FontSize = 21,
                                Foreground = Avalonia.Media.Brushes.DarkSlateGray } },
                        new TextBlock { Text = "Next milestone: publish the release", FontSize = 18, Foreground = Avalonia.Media.Brushes.SlateGray },
                    } } };
                sample.Measure(new Size(960,520)); sample.Arrange(new Rect(new Size(960,520)));
                using var sampleBitmap = new RenderTargetBitmap(new PixelSize(960,520));
                sampleBitmap.Render(sample);
                using var samplePng = new MemoryStream(); sampleBitmap.Save(samplePng, PngBitmapEncoderOptions.Default);
                var editor = new CapturePreviewWindow(ImageCodec.Encode(samplePng.ToArray(), true), _ => { }, _ => Task.FromResult(true), forSave: true);
                editor.Annotations.Add(Typlo.Core.Imaging.AnnotationTool.Marker, 0xfffacc15, 24, [new(65,194),new(370,194)]);
                editor.Annotations.Add(Typlo.Core.Imaging.AnnotationTool.Rectangle, 0xffef4444, 4, [new(52,159),new(430,280)]);
                editor.Annotations.Add(Typlo.Core.Imaging.AnnotationTool.Arrow, 0xff38bdf8, 5, [new(725,332),new(450,261)]);
                editor.Show();
                // Optional pause for manual pointer/keyboard QA; isolated --screenshots mode only.
                var editorPause = int.TryParse(Environment.GetEnvironmentVariable("TYPLO_SCREENSHOT_EDITOR_PAUSE_MS"), out var pause)
                    ? Math.Clamp(pause, 400, 300_000) : 400;
                var editorClosed = new TaskCompletionSource();
                editor.Closed += (_, _) => editorClosed.TrySetResult();
                await Task.WhenAny(Task.Delay(editorPause), editorClosed.Task);
                if (editor.IsVisible)
                {
                    Save(editor, Path.Combine(outDir, "screenshot_editor.png"));
                    editor.Width = 760; editor.Height = 520; await Task.Delay(250);
                    Save(editor, Path.Combine(outDir, "screenshot_editor_compact.png"));
                    editor.Close();
                }

                var personalWord = new PersonalWordDialog(controller, "напиши", "ru");
                personalWord.Show(); await Task.Delay(250);
                Save(personalWord, Path.Combine(outDir, "personal_dictionary.png")); personalWord.Close();

                Loc.SetLanguage("ru");
                await controller.AddPersonalWordsAsync([new("напиши", "ru"), new("писать", "ru"), new("написал", "ru"), new("рев'ювати", "uk"), new("sql", "en")]);
                await controller.SetExcludedWordAsync("ghb"); await controller.SetExcludedWordAsync("sql");
                var manager = new DictionaryWindow(controller); manager.Show(); await Task.Delay(500);
                Save(manager, Path.Combine(outDir, "dictionary_manager.png"));
                manager.ShowExclusions(); await Task.Delay(500);
                Save(manager, Path.Combine(outDir, "dictionary_exclusions.png")); manager.Close();
                var toolWindow = new ToolsWindow(controller, false); toolWindow.Show(); await Task.Delay(400);
                Save(toolWindow, Path.Combine(outDir, "tools.png"));
                toolWindow.ShowBackup(); await Task.Delay(100); Save(toolWindow, Path.Combine(outDir, "tools_backup.png")); toolWindow.Close();
                var diagnosis = new DictionaryWindow(controller, diagnostics: true); diagnosis.Show(); await Task.Delay(500);
                diagnosis.DisplayStatus(new(true, true, DictionaryHealth.Ready,
                    [new(DateTimeOffset.UtcNow, AutoCorrectionReason.Corrected, 194, "en", "ru"),
                     new(DateTimeOffset.UtcNow, AutoCorrectionReason.UnknownWord, 153, "en"),
                     new(DateTimeOffset.UtcNow, AutoCorrectionReason.Canceled, 210, "en", "uk"),
                     new(DateTimeOffset.UtcNow, AutoCorrectionReason.ClipboardChanged, 224, "en", "ru")]));
                await Task.Delay(50); Save(diagnosis, Path.Combine(outDir, "dictionary_diagnostics.png")); diagnosis.Close();

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
