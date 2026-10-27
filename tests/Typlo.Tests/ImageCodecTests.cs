using Typlo.App.Services;
using Typlo.Core.Imaging;

namespace Typlo.Tests;

public sealed class ImageCodecTests
{
    static ImageCodecTests() => Avalonia.Skia.SkiaPlatform.Initialize();
    internal static void Initialize() { }

    [Fact]
    public void ClipboardPng_PreservesFreehandTransparencyAndSourcePixels()
    {
        var input = Enumerable.Range(0, 64).SelectMany(i => new byte[] { 20, 100, 180, 0 }).ToArray();
        var cropped = CaptureRegion.Crop(input, 8, 8, CaptureShape.Freehand, [new(0, 0), new(8, 0), new(0, 8)])!;
        var png = ImageCodec.FromBgra(cropped.Bgra, cropped.Width, cropped.Height).Png;
        var (pixels, width, height) = ImageCodec.DecodeBgra(png);
        Assert.Equal((8, 8), (width, height));
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, png[..4]);
        Assert.Equal(cropped.Bgra, pixels);
    }

    [Fact]
    public void SavedJpeg_IsAJpegAtOriginalResolutionWithWhiteOutsideTheContour()
    {
        const int width = 80, height = 60;
        var pixels = new byte[width * height * 4];
        for (var y = 16; y < 44; y++)
            for (var x = 16; x < 64; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = 30; pixels[i + 1] = 100; pixels[i + 2] = 180; pixels[i + 3] = 255;
            }
        var png = ImageCodec.FromBgra(pixels, width, height).Png;
        var jpeg = ImageCodec.ToJpeg(png);
        Assert.Equal(new byte[] { 255, 216 }, jpeg[..2]);
        var (decoded, w, h) = ImageCodec.DecodeBgra(jpeg);
        Assert.Equal((width, height), (w, h));
        Assert.All(decoded[..4], b => Assert.InRange(b, (byte)250, (byte)255));
        var center = (30 * width + 40) * 4;
        Assert.InRange(decoded[center], 25, 35);
        Assert.InRange(decoded[center + 1], 95, 105);
        Assert.InRange(decoded[center + 2], 175, 185);
        Assert.Equal(255, decoded[center + 3]);
    }
}
