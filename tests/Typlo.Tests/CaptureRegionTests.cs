using Typlo.Core.Imaging;

namespace Typlo.Tests;

public sealed class CaptureRegionTests
{
    private static byte[] Frame(int w = 8, int h = 6) => Enumerable.Range(0, w * h)
        .SelectMany(i => new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2), 0 }).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rectangle_PreservesExactSourcePixelsInEitherDragDirection(bool reverse)
    {
        CapturePoint[] points = [new(2, 1), new(6, 4)];
        if (reverse) Array.Reverse(points);
        var crop = CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Rectangle, points)!;
        Assert.Equal((4, 3), (crop.Width, crop.Height));
        Assert.Equal(10, crop.Bgra[0]);
        Assert.Equal(29, crop.Bgra[^4]);
        Assert.All(crop.Bgra.Where((_, i) => i % 4 == 3), alpha => Assert.Equal(255, alpha));
    }

    [Fact]
    public void Rectangle_ClipsAtTheDesktopEdgeAndRoundsOutward()
    {
        var crop = CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Rectangle, [new(-10, 1.2), new(3.1, 40)])!;
        Assert.Equal((4, 5), (crop.Width, crop.Height));
        Assert.Equal(8, crop.Bgra[0]);
        Assert.Equal(43, crop.Bgra[^4]);
    }

    [Fact]
    public void Freehand_ClosesTheContourAndClearsEveryPixelOutsideIt()
    {
        CapturePoint[] points = [new(0, 0), new(6, 0), new(0, 6)];
        var crop = CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, points)!;
        Assert.Equal((6, 6), (crop.Width, crop.Height));
        Assert.Equal(255, crop.Bgra[3]);
        Assert.Equal(255, crop.Bgra[(2 * 6 + 1) * 4 + 3]);
        Assert.All(crop.Bgra[^4..], b => Assert.Equal(0, b));
        Array.Reverse(points);
        Assert.Equal(crop.Bgra, CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, points)!.Bgra);
    }

    [Fact]
    public void Freehand_ConcaveShapeDoesNotIncludeItsNotch()
    {
        CapturePoint[] points = [new(0, 0), new(6, 0), new(6, 2), new(2, 2), new(2, 6), new(0, 6)];
        var crop = CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, points)!;
        Assert.Equal(255, crop.Bgra[(4 * 6 + 1) * 4 + 3]);
        Assert.All(crop.Bgra[((4 * 6 + 4) * 4)..((4 * 6 + 5) * 4)], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Freehand_SelfIntersectionUsesEvenOddFill()
    {
        var crop = CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, [new(0, 0), new(6, 6), new(0, 6), new(6, 0)])!;
        Assert.Equal(255, crop.Bgra[2 * 4 + 3]);
        Assert.Equal(0, crop.Bgra[(2 * 6) * 4 + 3]);
    }

    [Fact]
    public void EmptyOrInvalidSelectionsAreNotImages()
    {
        Assert.Null(CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Rectangle, [new(2, 1), new(2, 5)]));
        Assert.Null(CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, [new(0, 0), new(3, 3), new(6, 6)]));
        Assert.Null(CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, [new(0, 0), new(3, 3)]));
        Assert.Null(CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Rectangle, [new(double.NaN, 0), new(4, 4)]));
        Assert.Null(CaptureRegion.Crop(Frame(), 8, 6, CaptureShape.Freehand, new CapturePoint[CaptureRegion.MaxPoints + 1]));
        Assert.Throws<ArgumentException>(() => CaptureRegion.Crop([], int.MaxValue, int.MaxValue, CaptureShape.Rectangle, []));
    }
}
