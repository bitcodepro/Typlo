using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Typlo.Core.Imaging;

namespace Typlo.App.Views;

internal sealed class ScreenshotCanvas(Bitmap bitmap, ScreenshotAnnotations annotations) : Control
{
    internal AnnotationTool Tool { get; set; }
    internal uint Color { get; set; } = 0xffef4444;
    internal double StrokeWidth { get; set; } = 5;
    internal bool Drawing { get; private set; }
    internal event Action? DrawingChanged;
    internal event Action? StrokeRejected;
    private readonly List<CapturePoint> _draft = [];
    private AnnotationTool _activeTool;
    private uint _activeColor;
    private double _activeWidth;
    private readonly Dictionary<AnnotationStroke, DrawingGroup> _drawings = [];
    private long _drawnState = -1;

    internal bool CancelStroke()
    {
        if (!Drawing) return false;
        Drawing = false; _draft.Clear(); InvalidateVisual(); DrawingChanged?.Invoke(); return true;
    }
    public override void Render(DrawingContext context)
    {
        context.DrawImage(bitmap, new Rect(new Size(annotations.Width, annotations.Height)));
        if (_drawnState != annotations.StateId)
        {
            var current = annotations.Strokes.ToHashSet();
            foreach (var old in _drawings.Keys.Where(s => !current.Contains(s)).ToArray()) _drawings.Remove(old);
            foreach (var stroke in annotations.Strokes.Where(s => !_drawings.ContainsKey(s)))
            {
                var drawing = new DrawingGroup();
                using (var buffer = drawing.Open()) Draw(buffer, stroke);
                _drawings.Add(stroke, drawing);
            }
            _drawnState = annotations.StateId;
        }
        foreach (var stroke in annotations.Strokes) _drawings[stroke].Draw(context);
        if (Drawing) Draw(context, new(_activeTool, _activeColor, _activeWidth, _draft));
    }
    private static void Draw(DrawingContext context, AnnotationStroke stroke)
    {
        var brush = new SolidColorBrush(Avalonia.Media.Color.FromUInt32(stroke.DisplayColor));
        var pen = new Pen(brush, stroke.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var points in stroke.Paths())
        {
            if (points.Count == 1)
            { context.DrawEllipse(brush, null, new Point(points[0].X, points[0].Y), stroke.Width / 2, stroke.Width / 2); continue; }
            var path = new StreamGeometry();
            using (var drawing = path.Open())
            {
                drawing.BeginFigure(new Point(points[0].X, points[0].Y), false);
                foreach (var p in points.Skip(1)) drawing.LineTo(new(p.X, p.Y));
                drawing.EndFigure(false);
            }
            context.DrawGeometry(null, pen, path);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Drawing) return;
        Focus(); Drawing = true; _activeTool = Tool; _activeColor = Color; _activeWidth = StrokeWidth;
        _draft.Clear(); Add(e.GetPosition(this));
        e.Pointer.Capture(this); e.Handled = true; DrawingChanged?.Invoke(); InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (Drawing) { Add(e.GetPosition(this)); InvalidateVisual(); } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!Drawing || e.InitialPressMouseButton != MouseButton.Left) return;
        Add(e.GetPosition(this)); Drawing = false; e.Pointer.Capture(null);
        if (!annotations.Add(_activeTool, _activeColor, _activeWidth, _draft) && annotations.Strokes.Count >= ScreenshotAnnotations.MaxStrokes)
            StrokeRejected?.Invoke();
        _draft.Clear(); InvalidateVisual(); DrawingChanged?.Invoke(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); CancelStroke(); }
    private void Add(Point point)
    {
        var p = new CapturePoint(Math.Clamp(point.X, 0, annotations.Width - 1), Math.Clamp(point.Y, 0, annotations.Height - 1));
        // Keep long freehand strokes responsive and retain their latest endpoint within the point budget.
        if (_draft.Count == CaptureRegion.MaxPoints)
        {
            for (var i = 1; i < _draft.Count / 2; i++) _draft[i] = _draft[i * 2];
            _draft.RemoveRange(_draft.Count / 2, _draft.Count / 2);
        }
        if (_activeTool is AnnotationTool.Arrow or AnnotationTool.Rectangle && _draft.Count > 1) _draft[^1] = p;
        else if (_draft.Count < CaptureRegion.MaxPoints && (_draft.Count == 0 ||
            Math.Abs(p.X - _draft[^1].X) + Math.Abs(p.Y - _draft[^1].Y) >= 0.7)) _draft.Add(p);
    }
}
