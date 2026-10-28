using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Typlo.App.Services;

namespace Typlo.App.Platform.Windows;

/// <summary>Use the OS GDI+ codecs in the background process; no Avalonia/Skia initialization.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsImageCodec
{
    internal static ImageCodec.Encoded Encode(byte[] bytes, bool alreadyPng)
    {
        using var input = new MemoryStream(bytes);
        using var source = Image.FromStream(input, false, true);
        return EncodeImage(source, alreadyPng ? bytes : null);
    }
    private static ImageCodec.Encoded EncodeImage(Image source, byte[]? png)
    {
        CheckSize(source.Width, source.Height);
        var scale = Math.Min(1d, (double)ImageCodec.ThumbnailMaxSize / Math.Max(source.Width, source.Height));
        using var thumbnail = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumbnail))
        {
            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, thumbnail.Width, thumbnail.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        return new(png ?? Png(source), Png(thumbnail), source.Width, source.Height);
    }
    internal static ImageCodec.Encoded FromBgra(byte[] pixels, int width, int height)
    {
        CheckSize(width, height);
        if (pixels.LongLength != (long)width * height * 4) throw new ArgumentException("Invalid pixel buffer");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4); }
        finally { bitmap.UnlockBits(data); }
        return EncodeImage(bitmap, null);
    }
    internal static (byte[], int, int) DecodeBgra(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var source = Image.FromStream(input, false, true);
        CheckSize(source.Width, source.Height);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(source, 0, 0, source.Width, source.Height); }
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4); }
        finally { bitmap.UnlockBits(data); }
        return (pixels, bitmap.Width, bitmap.Height);
    }
    internal static byte[] ToJpeg(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var source = Image.FromStream(input, false, true);
        CheckSize(source.Width, source.Height);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.White); g.DrawImage(source, 0, 0, source.Width, source.Height); }
        using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
        return output.ToArray();
    }
    private static byte[] Png(Image bitmap) { using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    private static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > Core.Imaging.CaptureRegion.MaxPixels) throw new ArgumentException("Image dimensions exceed the limit");
    }
}
