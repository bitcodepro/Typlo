using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Typlo.App.Services;
using Typlo.Core.Imaging;

namespace Typlo.App.Views;

internal sealed class CapturePreviewWindow : Window
{
    internal bool IsSaving { get; private set; }
    internal ScreenshotAnnotations Annotations { get; }
    private readonly ScreenshotCanvas _canvas;
    private readonly TextBlock _status;
    private readonly Button _undo, _redo, _clear;
    private readonly Control _toolbar, _actions, _retake;
    private readonly ImageCodec.Encoded _original;
    private readonly Func<ImageCodec.Encoded, Task<bool>> _copy;
    private ImageCodec.Encoded? _rendered;
    private long _renderedState = -1;
    private bool _closed;

    public CapturePreviewWindow(ImageCodec.Encoded image, Action<CaptureShape> again,
        Func<ImageCodec.Encoded, Task<bool>> copy, bool forSave = false)
    {
        _original = image; _copy = copy;
        Title = "Typlo — " + Loc.T("paint.title"); Icon = AppIcon.Create();
        Width = 1100; Height = 760; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Annotations = new(image.Width, image.Height);
        var bitmap = new Bitmap(new MemoryStream(image.Png));
        _canvas = new(bitmap, Annotations) { Width = image.Width, Height = image.Height,
            Focusable = true, Cursor = new Cursor(StandardCursorType.Cross), ClipToBounds = true };
        Closed += (_, _) => { _closed = true; _canvas.CancelStroke(); bitmap.Dispose(); _rendered = null; };
        Closing += (_, e) => { if (IsSaving) e.Cancel = true; };

        var copyButton = MakeButton(Loc.T("capture.copy"), "Ctrl+C", async () => await CopyAsync());
        var save = MakeButton(Loc.T("capture.save"), "Ctrl+S", async () => await SaveAsync());
        save.Classes.Add("accent");
        _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyButton, save } };
        _undo = MakeButton("↶", Loc.T("paint.undo") + " · Ctrl+Z", () => Annotations.Undo());
        _redo = MakeButton("↷", Loc.T("paint.redo") + " · Ctrl+Y", () => Annotations.Redo());
        _clear = MakeButton(Loc.T("paint.clear"), Loc.T("paint.clear"), () => Annotations.Clear());
        var tools = new WrapPanel { ItemSpacing = 6, LineSpacing = 8 };
        var toolButtons = new Dictionary<AnnotationTool, Button>();
        foreach (var (tool, symbol, key) in new[] { (AnnotationTool.Pen, "✎", "pen"), (AnnotationTool.Marker, "▰", "marker"),
            (AnnotationTool.Arrow, "↗", "arrow"), (AnnotationTool.Rectangle, "□", "rectangle") })
        {
            var button = MakeButton(symbol + "  " + Loc.T("paint." + key), Loc.T("paint." + key), () =>
            {
                _canvas.Tool = tool;
                foreach (var (value, item) in toolButtons) item.Classes.Set("accent", value == tool);
            });
            toolButtons.Add(tool, button); tools.Children.Add(button);
        }
        toolButtons[AnnotationTool.Pen].Classes.Add("accent");
        var palette = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var swatches = new List<(uint Color, Button Button)>();
        foreach (var (color, name) in new (uint, string)[] { (0xffef4444, "red"), (0xfff97316, "orange"),
            (0xfffacc15, "yellow"), (0xff22c55e, "green"), (0xff38bdf8, "blue"), (0xffa78bfa, "purple"),
            (0xffffffff, "white"), (0xff151922, "black") })
        {
            var swatch = MakeButton("", Loc.T("paint." + name), () =>
            {
                _canvas.Color = color;
                foreach (var (value, item) in swatches)
                {
                    item.BorderThickness = new Thickness(value == color ? 3 : 1);
                    item.BorderBrush = value == color ? Brushes.DodgerBlue : Brushes.Gray;
                }
            });
            swatch.Width = 28; swatch.Height = 28; swatch.MinHeight = 28; swatch.Padding = new Thickness(0);
            var brush = new SolidColorBrush(Color.FromUInt32(color));
            swatch.Background = brush; swatch.BorderBrush = color == _canvas.Color ? Brushes.DodgerBlue : Brushes.Gray;
            swatch.BorderThickness = new Thickness(color == _canvas.Color ? 3 : 1);
            swatch.Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = brush };
            swatches.Add((color, swatch)); palette.Children.Add(swatch);
        }
        tools.Children.Add(palette);
        var widthLabel = new TextBlock { Width = 46, Text = "5 px", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var width = new Slider { Minimum = 1, Maximum = 32, Value = 5, Width = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
        ToolTip.SetTip(width, Loc.T("paint.width"));
        width.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { _canvas.StrokeWidth = width.Value; widthLabel.Text = $"{width.Value:0} px"; } };
        var adjustments = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        adjustments.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = Loc.T("paint.width"), VerticalAlignment = VerticalAlignment.Center }, width, widthLabel } });
        adjustments.Children.Add(_undo); adjustments.Children.Add(_redo); adjustments.Children.Add(_clear);
        _toolbar = new StackPanel { Spacing = 10, Children = { tools, adjustments } };
        _status = new TextBlock { Text = Loc.T(forSave ? "paint.ready" : "paint.clipboard"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _retake = new WrapPanel { ItemSpacing = 8, LineSpacing = 5, Children =
        {
            MakeButton(Loc.T("capture.rectangle"), Loc.T("paint.retake"), () => again(CaptureShape.Rectangle)),
            MakeButton(Loc.T("capture.freehand"), Loc.T("paint.retake"), () => again(CaptureShape.Freehand)),
        } };
        Content = new DockPanel { Margin = new Thickness(22), LastChildFill = true, Children =
        {
            new Grid { [DockPanel.DockProperty] = Dock.Top, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0,0,0,18), Children =
            {
                new StackPanel { Spacing = 4, Children =
                {
                    new TextBlock { Text = Loc.T("paint.title"), FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{image.Width} × {image.Height} px · PNG / JPEG 95%", FontSize = 12, Opacity = .65 },
                } },
                new Border { [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center, Child = _actions },
            } },
            new Border { [DockPanel.DockProperty] = Dock.Top, CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.FromArgb(20,128,128,128)), Margin = new Thickness(0,0,0,14), Child = _toolbar },
            new StackPanel { [DockPanel.DockProperty] = Dock.Bottom, Spacing = 10, Margin = new Thickness(0,14,0,0), Children =
            {
                _status,
                new DockPanel { Children =
                {
                    new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = Loc.T("paint.keys"), FontSize = 11, Opacity = .6, VerticalAlignment = VerticalAlignment.Center },
                    _retake,
                } },
            } },
            new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Padding = new Thickness(20),
                Background = new SolidColorBrush(Color.Parse("#181D27")), Child = new Viewbox { Stretch = Stretch.Uniform, Child = new Grid
                { Width = image.Width, Height = image.Height, Children = { new Checkerboard(), _canvas } } } },
        } };
        Annotations.Changed += () => { _rendered = null; _canvas.InvalidateVisual(); _status.Text = Loc.T("paint.changed"); Refresh(); };
        _canvas.DrawingChanged += Refresh;
        _canvas.StrokeRejected += () => _status.Text = Loc.T("paint.limit");
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && !IsSaving) { if (!_canvas.CancelStroke()) Close(); e.Handled = true; }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || IsSaving || _canvas.Drawing) return;
            if (e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Annotations.Redo(); else Annotations.Undo(); }
            else if (e.Key == Key.Y) Annotations.Redo();
            else if (e.Key == Key.C) await CopyAsync();
            else if (e.Key == Key.S) await SaveAsync();
            else return;
            e.Handled = true;
        };
        Refresh();
    }

    private static Button MakeButton(string label, string hint, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12,8), CornerRadius = new CornerRadius(7) };
        AutomationProperties.SetName(button, string.IsNullOrEmpty(label) || label is "↶" or "↷" ? hint : label);
        ToolTip.SetTip(button, hint); button.Click += (_, _) => action(); return button;
    }
    private void Refresh()
    {
        _toolbar.IsEnabled = !IsSaving && !_canvas.Drawing;
        _actions.IsEnabled = !IsSaving && !_canvas.Drawing; _retake.IsEnabled = !IsSaving && !_canvas.Drawing;
        _canvas.IsEnabled = !IsSaving;
        _undo.IsEnabled = Annotations.CanUndo; _redo.IsEnabled = Annotations.CanRedo; _clear.IsEnabled = Annotations.Strokes.Count > 0;
    }
    private async Task<ImageCodec.Encoded> RenderAsync()
    {
        var state = Annotations.StateId;
        if (_renderedState == state && _rendered is not null) return _rendered;
        var strokes = Annotations.Strokes;
        _rendered = await Task.Run(() => ScreenshotAnnotationRenderer.Render(_original, strokes));
        _renderedState = state; return _rendered;
    }
    private async Task CopyAsync()
    {
        if (IsSaving || _canvas.Drawing || _closed) return;
        using var operation = UiOperations.Shared.Begin(); if (operation is null) return;
        IsSaving = true; Refresh();
        try { _status.Text = Loc.T(await _copy(await RenderAsync()) ? "capture.copied" : "capture.copyFailed"); }
        catch (Exception e) { Log.Error("Copying screenshot failed", e); _status.Text = Loc.T("capture.copyFailed"); }
        finally { IsSaving = false; Refresh(); }
    }
    private async Task SaveAsync()
    {
        if (IsSaving || _canvas.Drawing || _closed) return;
        using var operation = UiOperations.Shared.Begin(); if (operation is null) return;
        IsSaving = true; Refresh();
        try
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "Typlo-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".jpg",
                DefaultExtension = "jpg", FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }],
            });
            if (file is null) return;
            var image = await RenderAsync();
            var jpeg = await Task.Run(() => ImageCodec.ToJpeg(image.Png));
            await using var output = await file.OpenWriteAsync();
            await output.WriteAsync(jpeg);
            if (output.CanSeek) output.SetLength(output.Position);
            _status.Text = Loc.T("capture.saved");
        }
        catch (Exception e) { Log.Error("Saving screenshot failed", e); _status.Text = Loc.T("capture.saveFailed"); }
        finally { IsSaving = false; Refresh(); }
    }
    private sealed class Checkerboard : Control
    {
        private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#E7EAF0"));
        private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#CCD3DE"));
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Light, new Rect(Bounds.Size));
            var cell = Math.Max(12, Math.Max(Bounds.Width, Bounds.Height) / 80);
            for (var y = 0d; y < Bounds.Height; y += cell)
                for (var x = 0d; x < Bounds.Width; x += cell)
                    if (((int)(x / cell) + (int)(y / cell)) % 2 == 0) context.FillRectangle(Dark, new Rect(x,y,cell,cell));
        }
    }
}
