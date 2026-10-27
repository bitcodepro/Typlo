using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Typlo.App.Services;
using Typlo.Core.Imaging;

namespace Typlo.App.Views;

internal sealed class CapturePreviewWindow : Window
{
    internal bool IsSaving { get; private set; }
    public CapturePreviewWindow(ImageCodec.Encoded image, Action<CaptureShape> again, Func<Task<bool>> copy, bool saveOnOpen = false)
    {
        Title = Loc.T(saveOnOpen ? "capture.ready" : "capture.copied"); Icon = AppIcon.Create();
        Width = 620; Height = 480; MinWidth = 450; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var bitmap = new Bitmap(new MemoryStream(image.Png));
        Closed += (_, _) => bitmap.Dispose();
        var save = new Button { Content = Loc.T("capture.save"), Classes = { "accent" } };
        var copyButton = new Button { Content = Loc.T("capture.copy") };
        var rectangle = new Button { Content = Loc.T("capture.rectangle") };
        var freehand = new Button { Content = Loc.T("capture.freehand") };
        var status = new TextBlock { Text = Loc.T("capture.pasteHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        rectangle.Click += (_, _) => again(CaptureShape.Rectangle);
        freehand.Click += (_, _) => again(CaptureShape.Freehand);
        copyButton.Click += async (_, _) =>
        {
            try { status.Text = Loc.T(await copy() ? "capture.copied" : "capture.copyFailed"); }
            catch (Exception e) { Log.Error("Copying screenshot failed", e); status.Text = Loc.T("capture.copyFailed"); }
        };
        async Task SaveAsync()
        {
            if (!save.IsEnabled) return;
            save.IsEnabled = false;
            IsSaving = true;
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = "Typlo-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".jpg",
                    DefaultExtension = "jpg", FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }],
                });
                if (file is null) return;
                var jpeg = await Task.Run(() => ImageCodec.ToJpeg(image.Png));
                await using var output = await file.OpenWriteAsync();
                await output.WriteAsync(jpeg);
                if (output.CanSeek) output.SetLength(output.Position);
                status.Text = Loc.T("capture.saved");
            }
            catch (Exception e) { Log.Error("Saving screenshot failed", e); status.Text = Loc.T("capture.saveFailed"); }
            finally { save.IsEnabled = true; IsSaving = false; }
        }
        save.Click += async (_, _) => await SaveAsync();
        if (saveOnOpen) Opened += async (_, _) => await SaveAsync();
        Content = new DockPanel { Margin = new Thickness(22), LastChildFill = true, Children =
        {
            new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 5, Margin = new Thickness(0, 0, 0, 14), Children =
            {
                new TextBlock { Text = Loc.T(saveOnOpen ? "capture.ready" : "capture.copied"), FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{image.Width} × {image.Height} px", FontSize = 12, Opacity = 0.7 },
            } },
            new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Spacing = 10, Margin = new Thickness(0, 14, 0, 0), Children =
            {
                status, new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { copyButton, save, rectangle, freehand } },
            } },
            new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Grid { Children =
            {
                new Checkerboard(), new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(10) },
            } } },
        } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private sealed class Checkerboard : Control
    {
        private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#E7EAF0"));
        private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#CCD3DE"));
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Light, new Rect(Bounds.Size));
            for (var y = 0; y < Bounds.Height; y += 12)
                for (var x = 0; x < Bounds.Width; x += 12)
                    if ((x / 12 + y / 12) % 2 == 0) context.FillRectangle(Dark, new Rect(x, y, 12, 12));
        }
    }
}
