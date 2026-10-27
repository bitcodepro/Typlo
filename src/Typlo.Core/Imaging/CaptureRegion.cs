namespace Typlo.Core.Imaging;

public enum CaptureShape { Rectangle, Freehand }
public enum CaptureDestination { Clipboard, File }
public readonly record struct CapturePoint(double X, double Y);
public sealed record CapturedPixels(byte[] Bgra, int Width, int Height);

/// <summary>Pixel coordinates relative to a frozen desktop frame, independent of monitor DPI.</summary>
public static class CaptureRegion
{
    public const int MaxPoints = 8192;
    public const long MaxPixels = 64_000_000;

    public static CapturedPixels? Crop(byte[] bgra, int width, int height, CaptureShape shape, IReadOnlyList<CapturePoint> points)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPixels || bgra.LongLength != (long)width * height * 4)
            throw new ArgumentException("Invalid screen frame.");
        if (points.Count < (shape == CaptureShape.Rectangle ? 2 : 3) || points.Count > MaxPoints ||
            points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > int.MaxValue || Math.Abs(p.Y) > int.MaxValue)) return null;
        var left = (int)Math.Floor(Math.Clamp(points.Min(p => p.X), 0, width));
        var top = (int)Math.Floor(Math.Clamp(points.Min(p => p.Y), 0, height));
        var right = (int)Math.Ceiling(Math.Clamp(points.Max(p => p.X), 0, width));
        var bottom = (int)Math.Ceiling(Math.Clamp(points.Max(p => p.Y), 0, height));
        var w = right - left;
        var h = bottom - top;
        if (w < 2 || h < 2) return null;
        var result = new byte[checked(w * h * 4)];
        var intersections = new List<double>(points.Count);
        var hasPixels = false;
        for (var row = 0; row < h; row++)
        {
            if (shape == CaptureShape.Rectangle) Copy(left, right, row);
            else
            {
                intersections.Clear();
                var y = top + row + 0.5;
                for (var i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count]; // releasing the mouse closes the contour
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                        intersections.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                intersections.Sort();
                for (var i = 0; i + 1 < intersections.Count; i += 2)
                {
                    var start = (int)Math.Ceiling(Math.Clamp(intersections[i] - 0.5, left, right));
                    var end = (int)Math.Ceiling(Math.Clamp(intersections[i + 1] - 0.5, left, right));
                    Copy(start, end, row);
                }
            }
        }
        return hasPixels ? new(result, w, h) : null;

        void Copy(int start, int end, int row)
        {
            if (end <= start) return;
            hasPixels = true;
            var dest = (row * w + start - left) * 4;
            Buffer.BlockCopy(bgra, ((top + row) * width + start) * 4, result, dest, (end - start) * 4);
            // Desktop GDI images do not carry reliable alpha; captured pixels are opaque.
            for (var i = dest + 3; i < dest + (end - start) * 4; i += 4) result[i] = 255;
        }
    }
}
