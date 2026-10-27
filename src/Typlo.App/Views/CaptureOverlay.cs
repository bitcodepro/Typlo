using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core.Imaging;

namespace Typlo.App.Views;

/// <summary>One window per monitor keeps the preview and pointer coordinates correct at mixed DPI.</summary>
internal sealed class CaptureOverlay : Window
{
    internal sealed class Session : IDisposable
    {
        internal readonly DesktopCapture.Frame Frame;
        internal readonly Bitmap Bitmap;
        internal CaptureShape Shape;
        internal readonly List<CapturePoint> Points = [];
        internal readonly List<CaptureOverlay> Windows = [];
        internal bool Drawing, Finished;
        internal readonly bool Diagnostic;
        private readonly TaskCompletionSource<CapturedPixels?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Session(DesktopCapture.Frame frame, CaptureShape shape, bool diagnostic = false)
        {
            Frame = frame; Shape = shape; Diagnostic = diagnostic;
            Bitmap = new WriteableBitmap(new PixelSize(frame.Bounds.Width, frame.Bounds.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using var fb = ((WriteableBitmap)Bitmap).Lock();
            for (var y = 0; y < frame.Bounds.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Bgra, y * frame.Bounds.Width * 4, fb.Address + y * fb.RowBytes, frame.Bounds.Width * 4);
        }
        internal Task<CapturedPixels?> ShowAsync(IReadOnlyList<Screen> screens)
        {
            if (screens.Count == 0) throw new InvalidOperationException("No displays found.");
            foreach (var screen in screens)
            {
                var window = new CaptureOverlay(this, screen.Bounds, screen.Scaling);
                Windows.Add(window);
                window.Show();
            }
            Windows.First().Activate();
            return _result.Task;
        }
        internal void ChangeShape(CaptureShape shape)
        {
            if (Drawing) return;
            Shape = shape; Points.Clear(); Refresh();
        }
        internal CapturePoint Point(Control control, Point point)
        {
            var p = control.PointToScreen(point);
            return new(Math.Clamp(p.X - Frame.Bounds.X, 0, Frame.Bounds.Width), Math.Clamp(p.Y - Frame.Bounds.Y, 0, Frame.Bounds.Height));
        }
        internal void Refresh() { foreach (var window in Windows) window.Refresh(); }
        internal void Complete()
        {
            var pixels = CaptureRegion.Crop(Frame.Bgra, Frame.Bounds.Width, Frame.Bounds.Height, Shape, Points);
            if (pixels is null) { Points.Clear(); Refresh(); return; }
            Finish(pixels);
        }
        internal void Finish(CapturedPixels? pixels = null)
        {
            if (Finished) return;
            Finished = true; Drawing = false;
            foreach (var window in Windows) window.Close();
            _result.TrySetResult(pixels);
        }
        public void Dispose() { Finish(); Bitmap.Dispose(); }
    }

    private readonly Session _session;
    private readonly Surface _surface;
    private readonly Button _rectangle, _freehand;
    private readonly TextBlock _hint;
    private readonly Border _toolbar;
    private CaptureOverlay(Session session, PixelRect monitor, double scaling)
    {
        _session = session;
        Title = "Typlo — " + Loc.T("capture.title");
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = session.Diagnostic; CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = monitor.Position;
        Width = monitor.Width / scaling; Height = monitor.Height / scaling;
        Background = Brushes.Black;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        _surface = new Surface(session, monitor);
        _rectangle = new Button { Content = Loc.T("capture.rectangle") + " · R" };
        _freehand = new Button { Content = Loc.T("capture.freehand") + " · F" };
        var cancel = new Button { Content = Loc.T("settings.cancel") + " · Esc" };
        _rectangle.Click += (_, _) => session.ChangeShape(CaptureShape.Rectangle);
        _freehand.Click += (_, _) => session.ChangeShape(CaptureShape.Freehand);
        cancel.Click += (_, _) => session.Finish();
        _hint = new TextBlock { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F0222938")), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14), Margin = new Thickness(16, 22), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Spacing = 9, Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _rectangle, _freehand, cancel } }, _hint,
            } },
        };
        Content = new Grid { Children = { _surface, _toolbar } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { session.Finish(); e.Handled = true; }
            else if (e.Key == Key.R) { session.ChangeShape(CaptureShape.Rectangle); e.Handled = true; }
            else if (e.Key == Key.F) { session.ChangeShape(CaptureShape.Freehand); e.Handled = true; }
        };
        Opened += (_, _) => { if (TryGetPlatformHandle() is { } handle) DesktopCapture.FitWindow(handle.Handle, monitor); };
        Closed += (_, _) => session.Finish();
        // Alt+Tab / opening another application cancels; switching between our monitor windows does not.
        Deactivated += async (_, _) =>
        {
            await Task.Delay(100);
            if (!session.Finished && !session.Windows.Any(w => w.IsActive)) session.Finish();
        };
        Refresh();
    }
    private void Refresh()
    {
        _rectangle.Classes.Set("accent", _session.Shape == CaptureShape.Rectangle);
        _freehand.Classes.Set("accent", _session.Shape == CaptureShape.Freehand);
        _hint.Text = Loc.T(_session.Shape == CaptureShape.Rectangle ? "capture.rectangleHint" : "capture.freehandHint");
        _toolbar.IsVisible = !_session.Drawing;
        _surface.InvalidateVisual();
    }

    private sealed class Surface(Session session, PixelRect monitor) : Control
    {
        private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
        private static readonly Pen Outline = new(Brushes.DeepSkyBlue, 2);
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e); Cursor = new Cursor(StandardCursorType.Cross);
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var src = new Rect(monitor.X - session.Frame.Bounds.X, monitor.Y - session.Frame.Bounds.Y, monitor.Width, monitor.Height);
            var dest = new Rect(Bounds.Size);
            context.DrawImage(session.Bitmap, src, dest);
            context.FillRectangle(Shade, dest);
            if (session.Points.Count < 2) return;
            Point Local(CapturePoint p) => new((p.X + session.Frame.Bounds.X - monitor.X) * Bounds.Width / monitor.Width,
                (p.Y + session.Frame.Bounds.Y - monitor.Y) * Bounds.Height / monitor.Height);
            Geometry geometry;
            if (session.Shape == CaptureShape.Rectangle)
            {
                var a = Local(session.Points[0]); var b = Local(session.Points[^1]);
                geometry = new RectangleGeometry(new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
            }
            else
            {
                var path = new StreamGeometry();
                using (var ctx = path.Open())
                {
                    ctx.BeginFigure(Local(session.Points[0]), true);
                    foreach (var p in session.Points.Skip(1)) ctx.LineTo(Local(p));
                    ctx.EndFigure(true);
                }
                geometry = path;
            }
            using (context.PushGeometryClip(geometry)) context.DrawImage(session.Bitmap, src, dest);
            context.DrawGeometry(null, Outline, geometry);
            var w = Math.Abs(session.Points.Max(p => p.X) - session.Points.Min(p => p.X));
            var h = Math.Abs(session.Points.Max(p => p.Y) - session.Points.Min(p => p.Y));
            var label = new FormattedText($"{Math.Ceiling(w)} × {Math.Ceiling(h)} px", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Inter"), 13, Brushes.White);
            context.DrawText(label, new Point(20, Bounds.Height - 40));
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { session.Finish(); e.Handled = true; return; }
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || session.Drawing) return;
            session.Points.Clear(); session.Drawing = true;
            var p = session.Point(this, e.GetPosition(this));
            session.Points.Add(p);
            if (session.Shape == CaptureShape.Rectangle) session.Points.Add(p);
            e.Pointer.Capture(this); e.Handled = true; session.Refresh();
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!session.Drawing) return;
            AddPoint(session.Point(this, e.GetPosition(this))); session.Refresh();
        }
        private void AddPoint(CapturePoint p)
        {
            if (session.Shape == CaptureShape.Rectangle) session.Points[^1] = p;
            else if (session.Points.Count < CaptureRegion.MaxPoints &&
                Math.Abs(p.X - session.Points[^1].X) + Math.Abs(p.Y - session.Points[^1].Y) >= 2) session.Points.Add(p);
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!session.Drawing || e.InitialPressMouseButton != MouseButton.Left) return;
            AddPoint(session.Point(this, e.GetPosition(this)));
            session.Drawing = false; e.Pointer.Capture(null); e.Handled = true; session.Complete();
        }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (session.Drawing) session.Finish();
        }
    }
}
