using SkiaSharp;
using Typlo.Core.Imaging;

namespace Typlo.App.Services;

/// <summary>Invoked by the on-demand editor only; exports original-size pixels, never the scaled UI.</summary>
internal static class ScreenshotAnnotationRenderer
{
    internal static ImageCodec.Encoded Render(ImageCodec.Encoded original, IReadOnlyList<AnnotationStroke> strokes)
    {
        if (strokes.Count == 0) return original;
        // Decode once into a writable export buffer. The original PNG and UI bitmap stay intact.
        using var result = SKBitmap.Decode(original.Png) ?? throw new InvalidDataException("Invalid screenshot");
        using var canvas = new SKCanvas(result);
        foreach (var stroke in strokes)
        {
            using var paint = new SKPaint { Color = new SKColor(stroke.DisplayColor), StrokeWidth = (float)stroke.Width,
                Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            foreach (var points in stroke.Paths())
            {
                if (points.Count == 1)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle((float)points[0].X, (float)points[0].Y, (float)stroke.Width / 2, paint);
                    paint.Style = SKPaintStyle.Stroke; continue;
                }
                using var path = new SKPath(); path.MoveTo((float)points[0].X, (float)points[0].Y);
                foreach (var point in points.Skip(1)) path.LineTo((float)point.X, (float)point.Y);
                canvas.DrawPath(path, paint);
            }
        }
        using var image = SKImage.FromBitmap(result); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return ImageCodec.Encode(png.ToArray(), alreadyPng: true);
    }
}
