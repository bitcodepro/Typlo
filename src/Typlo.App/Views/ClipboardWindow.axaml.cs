using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typlo.App.Services;

namespace Typlo.App.Views;

/// <summary>Popup with the clipboard history; opens near the cursor and pastes into the previous window.</summary>
public partial class ClipboardWindow : Window
{
    private readonly AppController _controller;
    private readonly ObservableCollection<ClipItemViewModel> _items = [];
    private List<ClipItemViewModel> _all = [];
    private nint _targetWindow;
    private volatile bool _dirty = true;

    public ClipboardWindow() : this(null!) { } // designer

    public ClipboardWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Icon = AppIcon.Create();
        List.ItemsSource = _items;
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        List.DoubleTapped += (_, _) => PasteSelected();
        List.ContextRequested += OnContextRequested;
        // Tunnel so arrows/Enter work while the search box has focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Hide();
        if (controller is not null) controller.Store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _searchCts?.Cancel();
            if (controller is not null) controller.Store.Changed -= OnStoreChanged;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible)
            {
                _searchCts?.Cancel(); _items.Clear(); _all.Clear(); _dirty = true;
            }
        };
    }

    private void OnStoreChanged() => _dirty = true;

    public void ShowFor(nint targetWindow)
    {
        if (IsVisible)
        {
            // Second press of the history shortcut: paste the previous item right away.
            Hide();
            var target = _targetWindow;
            _ = Task.Run(() => _controller.PastePreviousAsync(target));
            return;
        }
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
        _all = _controller.Store.List(includeThumbnails: false).Select(e => new ClipItemViewModel(e, _controller.Store.GetThumbnail)).ToList();
        _dirty = false;
    }

    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// Filters at once by the stored preview (start of each text), then searches the full content of every
    /// text/file item in the background and adds the further matches.
    /// </summary>
    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        ShowItems(_all.Where(i => i.Matches(q)).ToList(), q);

        _searchCts?.Cancel();
        // One character is enough: a single Chinese or Japanese character is a whole word.
        if (q.Length == 0 || _controller is null) return;
        var cts = _searchCts = new CancellationTokenSource();
        CountText.Text = $"{_items.Count} / {_all.Count} …"; // full-text search running
        _ = SearchAsync(q, cts);
    }

    private async Task SearchAsync(string query, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await Task.Delay(150, token);
            var ids = await Task.Run(() => _controller.Store.SearchFullText(query, token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_searchCts, cts)) return;
            var matches = _all.Where(i => i.Matches(query) || ids.Contains(i.Entry.Id)).ToList();
            ShowItems(matches, query, keepSelection: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            Log.Error("Full-text search failed", e);
            if (!token.IsCancellationRequested && ReferenceEquals(_searchCts, cts))
                CountText.Text = $"{_items.Count} / {_all.Count}";
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            cts.Dispose();
        }
    }

    private void ShowItems(List<ClipItemViewModel> items, string query, bool keepSelection = false)
    {
        var selected = keepSelection ? Selected : null;
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        for (var i = 0; i < _items.Count; i++) _items[i].Badge = i < 9 ? (i + 1).ToString() : "";
        List.SelectedIndex = selected is not null && _items.Contains(selected) ? _items.IndexOf(selected) : _items.Count > 0 ? 0 : -1;
        EmptyText.IsVisible = _items.Count == 0;
        EmptyText.Text = Loc.T(_all.Count == 0 ? "history.empty" : "history.notFound");
        CountText.Text = query.Length == 0 ? $"{_all.Count}" : $"{_items.Count} / {_all.Count}";
    }

    /// <summary>Opens the selected item in full in its own window.</summary>
    private void ViewSelected()
    {
        if (Selected is not { } item) return;
        var target = _targetWindow;
        Hide();
        var viewer = new EntryViewWindow(_controller, item.Entry, target);
        viewer.Show();
        viewer.Activate();
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
                PasteSelected(plainText: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
            case Key.T when ctrl:
                _ = CopyTextFromImageAsync();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                ViewSelected();
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

    private void PasteSelected(bool plainText = false)
    {
        if (Selected is not { } item) return;
        Hide();
        var target = _targetWindow;
        _ = Task.Run(() => _controller.PasteAsync(item.Entry.Id, target, plainText: plainText));
    }

    /// <summary>Right click: select the item under the pointer and offer its actions.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ClipItemViewModel vm })
            List.SelectedItem = vm;
        if (Selected is not { } item) return;
        var menu = new ContextMenu();
        void Add(string key, string gesture, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = Loc.T(key), InputGesture = string.IsNullOrEmpty(gesture) ? null : KeyGesture.Parse(gesture), IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("history.ctx.paste", "Enter", () => PasteSelected());
        Add("history.ctx.view", "Ctrl+O", ViewSelected);
        if (item.IsText) Add("history.ctx.plain", "Shift+Enter", () => PasteSelected(plainText: true), item.Entry.HasFormatting);
        if (item.IsImage) Add("history.ctx.ocr", "Ctrl+T", () => _ = CopyTextFromImageAsync(), OcrService.Available);
        menu.Items.Add(new Separator());
        Add(item.Pinned ? "history.ctx.unpin" : "history.ctx.pin", "Ctrl+P", TogglePin);
        Add("history.ctx.delete", "Delete", DeleteSelected);
        menu.Open(List);
        e.Handled = true;
    }

    /// <summary>OCR on the selected image: the text goes to the clipboard and to the top of the history.</summary>
    private async Task CopyTextFromImageAsync()
    {
        if (Selected is not { IsImage: true } item || !OcrService.Available) return;
        CountText.Text = Loc.T("history.ocrBusy");
        var png = _controller.Store.Get(item.Entry.Id)?.ImagePng;
        var text = png is null ? null : await Task.Run(() => OcrService.RecognizeAsync(png));
        if (text is null)
        {
            CountText.Text = Loc.T("history.ocrNone");
            return;
        }
        _controller.Platform.SetText(text);
        _controller.Store.Add(Core.Storage.ClipContent.FromText(text, "Typlo OCR"));
        Reload();
        ApplyFilter();
        CountText.Text = Loc.T("history.ocrDone");
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
