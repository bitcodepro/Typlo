using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Typlo.App.Services;

/// <summary>Draws the app icon at runtime (a rounded "T" badge with a swap arrow), so no binary assets are needed.</summary>
public static class AppIcon
{
    public static WindowIcon Create()
    {
        const int size = 64;
        using var bmp = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bmp.CreateDrawingContext())
        {
            var bg = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#3B82F6"), 0), new GradientStop(Color.Parse("#8B5CF6"), 1) },
            };
            ctx.DrawRectangle(bg, null, new RoundedRect(new Rect(0, 0, size, size), 14));
            var bold = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
            var letter = new FormattedText("T", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold, 40, Brushes.White);
            ctx.DrawText(letter, new Point((size - letter.Width) / 2 - 4, (size - letter.Height) / 2 - 2));
            var swap = new FormattedText("⇄", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold, 18, Brushes.White);
            ctx.DrawText(swap, new Point(size - swap.Width - 5, size - swap.Height - 3));
        }
        var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
