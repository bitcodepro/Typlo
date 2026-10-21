using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Typlo.App.Services;

namespace Typlo.App.Views;

/// <summary>Popup with the clipboard history; opens near the cursor and pastes into the previous window.</summary>
public partial class ClipboardWindow : Window
{
    private readonly AppController _controller;
    private readonly ObservableCollection<ClipItemViewModel> _items = [];
    private List<ClipItemViewModel> _all = [];
    private nint _targetWindow;
    private bool _dirty = true;

    public ClipboardWindow() : this(null!) { } // designer

    public ClipboardWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Icon = AppIcon.Create();
        List.ItemsSource = _items;
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        List.DoubleTapped += (_, _) => PasteSelected();
        // Tunnel so arrows/Enter work while the search box has focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Hide();
        if (controller is not null) controller.Store.Changed += () => _dirty = true;
    }

    public void ShowFor(nint targetWindow)
    {
        _targetWindow = targetWindow;
        if (_dirty) Reload();
        SearchBox.Text = "";
        ApplyFilter();
        PlaceNearCursor();
        Show();
        Activate();
        if (TryGetPlatformHandle()?.Handle is { } h) _controller.Platform.ActivateWindow(h);
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    private void Reload()
    {
        _all = _controller.Store.List().Select(e => new ClipItemViewModel(e)).ToList();
        _dirty = false;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        _items.Clear();
        foreach (var item in _all.Where(i => i.Matches(q))) _items.Add(item);
        for (var i = 0; i < _items.Count; i++) _items[i].Badge = i < 9 ? (i + 1).ToString() : "";
        List.SelectedIndex = _items.Count > 0 ? 0 : -1;
        EmptyText.IsVisible = _items.Count == 0;
        EmptyText.Text = Loc.T(_all.Count == 0 ? "history.empty" : "history.notFound");
        CountText.Text = q.Length == 0 ? $"{_all.Count}" : $"{_items.Count} / {_all.Count}";
    }

    private void PlaceNearCursor()
    {
        var (x, y) = _controller.Platform.CursorPosition;
        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        var w = (int)(Width * scale);
        var h = (int)(Height * scale);
        var px = x + 12;
        var py = y + 12;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            px = Math.Clamp(px, area.X + 8, Math.Max(area.X + 8, area.Right - w - 8));
            if (py + h > area.Bottom - 8) py = y - h - 12;
            py = Math.Clamp(py, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - h - 8));
        }
        Position = new PixelPoint(px, py);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                PasteSelected();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(+8);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-8);
                e.Handled = true;
                break;
            case Key.Delete when ctrl || string.IsNullOrEmpty(SearchBox.Text) || !SearchBox.IsFocused:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.P when ctrl:
                TogglePin();
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl:
                var index = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                if (index < _items.Count)
                {
                    List.SelectedIndex = index;
                    PasteSelected();
                }
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        List.SelectedIndex = Math.Clamp(List.SelectedIndex + delta, 0, _items.Count - 1);
        List.ScrollIntoView(List.SelectedIndex);
    }

    private ClipItemViewModel? Selected => List.SelectedItem as ClipItemViewModel;

    private void PasteSelected()
    {
        if (Selected is not { } item) return;
        Hide();
        var target = _targetWindow;
        _ = Task.Run(() => _controller.PasteAsync(item.Entry.Id, target));
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;
        var index = List.SelectedIndex;
        _controller.Store.Delete(item.Entry.Id);
        _all.Remove(item);
        _items.Remove(item);
        for (var i = 0; i < _items.Count; i++) _items[i].Badge = i < 9 ? (i + 1).ToString() : "";
        _dirty = false;
        List.SelectedIndex = Math.Min(index, _items.Count - 1);
        EmptyText.IsVisible = _items.Count == 0;
        CountText.Text = $"{_all.Count}";
    }

    private void TogglePin()
    {
        if (Selected is not { } item) return;
        item.Pinned = !item.Pinned;
        item.Entry.Pinned = item.Pinned;
        _controller.Store.SetPinned(item.Entry.Id, item.Pinned);
        _dirty = true;
    }
}
