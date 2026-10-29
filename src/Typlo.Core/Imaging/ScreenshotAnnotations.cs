namespace Typlo.Core.Imaging;

public enum AnnotationTool { Pen, Marker, Arrow, Rectangle }

public sealed record AnnotationStroke(AnnotationTool Tool, uint Color, double Width, IReadOnlyList<CapturePoint> Points)
{
    public uint DisplayColor => Tool == AnnotationTool.Marker ? (Color & 0x00ffffff) | 0x60000000 : Color;

    public IEnumerable<IReadOnlyList<CapturePoint>> Paths()
    {
        if (Points.Count == 0) yield break;
        if (Tool is AnnotationTool.Pen or AnnotationTool.Marker) { yield return Points; yield break; }
        var a = Points[0]; var b = Points[^1];
        if (Tool == AnnotationTool.Rectangle)
        { yield return new[] { a, new CapturePoint(b.X, a.Y), b, new CapturePoint(a.X, b.Y), a }; yield break; }
        yield return new[] { a, b };
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        var head = Math.Min(Math.Max(12, Width * 3.5), Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) * 0.6);
        yield return new[] { new CapturePoint(b.X - head * Math.Cos(angle - 0.5), b.Y - head * Math.Sin(angle - 0.5)), b,
            new CapturePoint(b.X - head * Math.Cos(angle + 0.5), b.Y - head * Math.Sin(angle + 0.5)) };
    }
}

/// <summary>Vector-only bounded history: undo never duplicates the screenshot bitmap.</summary>
public sealed class ScreenshotAnnotations(int width, int height)
{
    public const int MaxStrokes = 512;
    public const int MaxHistory = 100;
    private sealed record State(long Id, IReadOnlyList<AnnotationStroke> Strokes);
    private readonly List<State> _history = [new(0, Array.Empty<AnnotationStroke>())];
    private int _position;
    private long _nextId;
    public int Width { get; } = width > 0 ? width : throw new ArgumentOutOfRangeException(nameof(width));
    public int Height { get; } = height > 0 && (long)width * height <= CaptureRegion.MaxPixels ? height : throw new ArgumentOutOfRangeException(nameof(height));
    public long StateId => _history[_position].Id;
    public IReadOnlyList<AnnotationStroke> Strokes => _history[_position].Strokes;
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _history.Count - 1;
    public event Action? Changed;

    public bool Add(AnnotationTool tool, uint color, double strokeWidth, IReadOnlyList<CapturePoint> points)
    {
        if (!Enum.IsDefined(tool) || !double.IsFinite(strokeWidth) || strokeWidth < 1 || strokeWidth > 64 ||
            points.Count is < 1 or > CaptureRegion.MaxPoints || Strokes.Count >= MaxStrokes ||
            points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return false;
        var copy = points.Select(p => new CapturePoint(Math.Clamp(p.X, 0, Width - 1), Math.Clamp(p.Y, 0, Height - 1))).ToArray();
        if (tool is AnnotationTool.Arrow or AnnotationTool.Rectangle && (copy.Length < 2 || copy[0] == copy[^1])) return false;
        var stroke = new AnnotationStroke(tool, color | 0xff000000, strokeWidth, Array.AsReadOnly(copy));
        Commit(Strokes.Append(stroke).ToArray()); return true;
    }
    public void Clear() { if (Strokes.Count > 0) Commit([]); }
    public void Undo() { if (CanUndo) { _position--; Changed?.Invoke(); } }
    public void Redo() { if (CanRedo) { _position++; Changed?.Invoke(); } }
    private void Commit(AnnotationStroke[] strokes)
    {
        _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(new(++_nextId, Array.AsReadOnly(strokes)));
        if (_history.Count > MaxHistory + 1) _history.RemoveAt(0);
        _position = _history.Count - 1; Changed?.Invoke();
    }
}
