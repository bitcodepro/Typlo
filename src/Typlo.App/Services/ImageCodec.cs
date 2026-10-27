using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Typlo.App.Services;

/// <summary>Image helpers built on Avalonia's Skia codecs (cross-platform).</summary>
public static class ImageCodec
{
    public const int ThumbnailMaxSize = 320;

    public sealed record Encoded(byte[] Png, byte[] Thumbnail, int Width, int Height);

    /// <summary>Decodes PNG/BMP bytes and produces a normalized PNG plus a thumbnail PNG.</summary>
    public static Encoded Encode(byte[] imageBytes, bool alreadyPng)
    {
        using var bitmap = new Bitmap(new MemoryStream(imageBytes));
        var size = bitmap.PixelSize;
        byte[] png;
        if (alreadyPng) png = imageBytes;
        else
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, PngBitmapEncoderOptions.Default);
            png = ms.ToArray();
        }
        return new Encoded(png, Thumbnail(bitmap), size.Width, size.Height);
    }

    public static Encoded FromBgra(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > Core.Imaging.CaptureRegion.MaxPixels ||
            bgra.LongLength != (long)width * height * 4) throw new ArgumentException("Invalid image dimensions.");
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var frame = bitmap.Lock())
            for (var y = 0; y < height; y++) Marshal.Copy(bgra, y * width * 4, frame.Address + y * frame.RowBytes, width * 4);
        using var output = new MemoryStream();
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        // Skia's thumbnail resize accepts immutable decoded bitmaps, not WriteableBitmap.
        return Encode(output.ToArray(), alreadyPng: true);
    }

    public static byte[] ToJpeg(byte[] png)
    {
        var (pixels, width, height) = DecodeBgra(png);
        // JPEG has no alpha channel. Composite freehand pixels on white at full source resolution.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[i + 3] = 255;
        }
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
            for (var y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, frame.Address + y * frame.RowBytes, width * 4);
        using var output = new MemoryStream();
        bitmap.Save(output, new JpegBitmapEncoderOptions { Quality = 95 });
        return output.ToArray();
    }

    private static byte[] Thumbnail(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var scale = Math.Min(1.0, (double)ThumbnailMaxSize / Math.Max(size.Width, size.Height));
        var target = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        using var scaled = bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
        using var ms = new MemoryStream();
        scaled.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }

    /// <summary>Top-down BGRA pixels of a PNG, for building a Windows CF_DIB.</summary>
    public static (byte[] Bgra, int Width, int Height) DecodeBgra(byte[] png)
    {
        using var bitmap = new Bitmap(new MemoryStream(png));
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally { handle.Free(); }

        if (bitmap.Format is { } fmt && fmt == PixelFormat.Rgba8888)
        {
            for (var i = 0; i < buffer.Length; i += 4) (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
        }
        if (bitmap.AlphaFormat == AlphaFormat.Premul) Unpremultiply(buffer);
        return (buffer, size.Width, size.Height);
    }

    private static void Unpremultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a is 0 or 255) continue;
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
    }
}
