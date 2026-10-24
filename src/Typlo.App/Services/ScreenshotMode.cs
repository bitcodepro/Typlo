using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Typlo.App.Platform;
using Typlo.App.Views;
using Typlo.Core;
using Typlo.Core.Storage;

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
        var controller = new AppController(platform, new AppPaths(dataDir), PlatformFactory.CreateKeyProtector());
        try
        {
            var s = controller.Settings.Clone();
            s.ToggleHotkey = ""; s.ClipboardHotkey = "Ctrl+Shift+V"; s.LayoutHotkeys = []; s.StartWithSystem = false;
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
                foreach (var (code, _) in Loc.Languages)
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
