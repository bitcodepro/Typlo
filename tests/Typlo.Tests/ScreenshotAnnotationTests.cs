using Typlo.App.Services;
using Typlo.Core.Imaging;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed class ScreenshotAnnotationTests
{
    [Fact]
    public void UndoRedoAndBranchingKeepOriginalSnapshotsImmutable()
    {
        var document = new ScreenshotAnnotations(100, 80);
        var points = new[] { new CapturePoint(10, 10), new CapturePoint(20, 20) };
        Assert.True(document.Add(AnnotationTool.Pen, 0xff0000, 5, points));
        var first = document.Strokes; var firstId = document.StateId;
        points[0] = new(99, 79);
        Assert.Equal(new CapturePoint(10, 10), first[0].Points[0]);
        document.Clear(); var clearId = document.StateId;
        document.Undo(); Assert.Equal(firstId, document.StateId); Assert.Single(document.Strokes);
        document.Redo(); Assert.Equal(clearId, document.StateId); Assert.Empty(document.Strokes);
        document.Undo(); document.Add(AnnotationTool.Pen, 0, 1, [new(40, 40)]);
        Assert.False(document.CanRedo); Assert.Single(first); Assert.Equal(2, document.Strokes.Count);
        Assert.NotEqual(clearId, document.StateId);
    }

    [Fact]
    public void HistoryAndStrokeCountsAreBounded()
    {
        var document = new ScreenshotAnnotations(20, 20);
        for (var i = 0; i < ScreenshotAnnotations.MaxStrokes; i++)
            Assert.True(document.Add(AnnotationTool.Pen, 0, 1, [new(5, 5)]));
        var state = document.StateId;
        Assert.False(document.Add(AnnotationTool.Pen, 0, 1, [new(5, 5)]));
        Assert.Equal(state, document.StateId);
        var undos = 0;
        while (document.CanUndo) { document.Undo(); undos++; }
        Assert.Equal(ScreenshotAnnotations.MaxHistory, undos);
        Assert.Equal(ScreenshotAnnotations.MaxStrokes - ScreenshotAnnotations.MaxHistory, document.Strokes.Count);
    }

    [Fact]
    public void InvalidInputCannotChangeTheDocumentAndCoordinatesAreClamped()
    {
        var document = new ScreenshotAnnotations(100, 80);
        Assert.False(document.Add((AnnotationTool)100, 0, 1, [new(0, 0)]));
        Assert.False(document.Add(AnnotationTool.Pen, 0, double.NaN, [new(0, 0)]));
        Assert.False(document.Add(AnnotationTool.Pen, 0, 1, [new(double.PositiveInfinity, 0)]));
        Assert.False(document.Add(AnnotationTool.Rectangle, 0, 1, [new(0, 0), new(0, 0)]));
        Assert.False(document.Add(AnnotationTool.Arrow, 0, 1, [new(0, 0)]));
        Assert.False(document.CanUndo);
        Assert.True(document.Add(AnnotationTool.Pen, 0, 1, [new(-10, -10), new(200, 200)]));
        Assert.Equal(new CapturePoint(0, 0), document.Strokes[0].Points[0]);
        Assert.Equal(new CapturePoint(99, 79), document.Strokes[0].Points[1]);
    }

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Marker)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Rectangle)]
    public void ExportDrawsAtSourceResolutionAndUndoRestoresOriginal(AnnotationTool tool)
    {
        ImageCodecTests.Initialize();
        const int width = 180, height = 100;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var original = ImageCodec.FromBgra(pixels, width, height);
        var document = new ScreenshotAnnotations(width, height);
        Assert.Same(original, ScreenshotAnnotationRenderer.Render(original, document.Strokes));
        document.Add(tool, 0xffff0000, 10, [new(20, 40), new(150, 40)]);
        var edited = ScreenshotAnnotationRenderer.Render(original, document.Strokes);
        var (result, w, h) = ImageCodec.DecodeBgra(edited.Png);
        Assert.Equal((width, height), (w, h));
        Assert.Equal(pixels[..4], result[..4]);
        var center = (40 * width + 70) * 4;
        Assert.Equal(255, result[center + 2]);
        Assert.Equal(255, result[center + 3]);
        if (tool == AnnotationTool.Marker) Assert.InRange(result[center], (byte)150, (byte)165);
        else Assert.Equal(0, result[center]);
        document.Undo();
        Assert.Same(original, ScreenshotAnnotationRenderer.Render(original, document.Strokes));
        Assert.Equal(pixels, ImageCodec.DecodeBgra(original.Png).Bgra);
    }

    [Fact]
    public void EditedFreehandPngRetainsAlphaAndJpegCompositesAgainstWhite()
    {
        ImageCodecTests.Initialize();
        var original = ImageCodec.FromBgra(new byte[80 * 60 * 4], 80, 60);
        var document = new ScreenshotAnnotations(80, 60);
        document.Add(AnnotationTool.Pen, 0xff0000ff, 10, [new(40, 30)]);
        var edited = ScreenshotAnnotationRenderer.Render(original, document.Strokes);
        var (png, _, _) = ImageCodec.DecodeBgra(edited.Png);
        Assert.Equal(0, png[3]);
        var center = (30 * 80 + 40) * 4;
        Assert.Equal(new byte[] {255, 0, 0, 255}, png[center..(center + 4)]);
        var (jpeg, width, height) = ImageCodec.DecodeBgra(ImageCodec.ToJpeg(edited.Png));
        Assert.Equal((80, 60), (width, height));
        Assert.All(jpeg[..4], b => Assert.InRange(b, (byte)250, (byte)255));
        Assert.InRange(jpeg[center], (byte)240, (byte)255);
        Assert.InRange(jpeg[center + 1], (byte)0, (byte)15);
    }

    [Fact]
    public void QuickCopyPreferenceDefaultsOnAndSurvivesSettingsClone()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(settings.ScreenshotEditorAfterCopy);
        settings.ScreenshotEditorAfterCopy = false;
        Assert.False(settings.Clone().ScreenshotEditorAfterCopy);
    }
}
