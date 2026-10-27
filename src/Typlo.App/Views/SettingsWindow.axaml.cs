using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.Core.Text;
using Typlo.Core.Imaging;

namespace Typlo.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly AppSettings _draft;
    private readonly List<(HotkeyBox Box, TextBlock Status)> _hotkeyRows = [];
    private HotkeyBox _toggleBox = null!;
    private readonly Dictionary<string, HotkeyBox> _layoutBoxes = [];
    private HotkeyBox _caseBox = null!;
    private HotkeyBox _translitBox = null!;
    private HotkeyBox _undoBox = null!;
    private readonly List<(TextBox Trigger, TextBox Text, Control Row)> _snippetRows = [];
    private readonly List<KeyboardLayout> _cycleOrder = [];
    private readonly HashSet<string> _enabledCycle = [];
    private static readonly string[] Themes = ["system", "light", "dark"];

    /// <summary>Called after a successful import so the app can show a fresh settings window.</summary>
    public Action? Reopen { get; set; }

    /// <summary>Opens the "Check for updates" window.</summary>
    public Action? CheckForUpdates { get; set; }

    /// <summary>Opens the list of all keyboard shortcuts.</summary>
    public Action? ShowShortcuts { get; set; }
    public Action<CaptureShape, CaptureDestination>? CaptureScreenshot { get; set; }
    private bool _saved;

    public SettingsWindow() : this(null!) { } // designer

    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Icon = AppIcon.Create();
        if (controller is null) { _draft = new AppSettings(); return; }
        _draft = controller.Settings.Clone();

        // Live hotkeys would fire while the user records new ones.
        controller.SuspendHotkeys();
        Closed += (_, _) => { if (!_saved) controller.RegisterHotkeys(); };

        BuildHotkeyRows();
        ClipboardHotkeyBox.HotkeyText = _draft.ClipboardHotkey;
        _hotkeyRows.Add((ClipboardHotkeyBox, ClipboardHotkeyStatus));
        ClipboardHotkeyBox.HotkeyChanged += () => Validate();
        CaptureCopyHotkeyBox.HotkeyText = _draft.ScreenshotCopyHotkey;
        CaptureSaveHotkeyBox.HotkeyText = _draft.ScreenshotSaveHotkey;
        CaptureCopyButton.IsEnabled = CaptureSaveButton.IsEnabled = controller.Platform.SupportsScreenCapture;
        CaptureCopyHotkeyBox.IsEnabled = CaptureSaveHotkeyBox.IsEnabled = controller.Platform.SupportsScreenCapture;
        if (controller.Platform.SupportsScreenCapture)
        {
            _hotkeyRows.Add((CaptureCopyHotkeyBox, CaptureCopyStatus));
            _hotkeyRows.Add((CaptureSaveHotkeyBox, CaptureSaveStatus));
            CaptureCopyHotkeyBox.HotkeyChanged += () => Validate();
            CaptureSaveHotkeyBox.HotkeyChanged += () => Validate();
        }
        else CaptureHint.Text = Loc.T("capture.windowsOnly");
        CaptureShapeBox.IsEnabled = controller.Platform.SupportsScreenCapture;
        CaptureShapeBox.ItemsSource = new[] { Loc.T("capture.rectangle"), Loc.T("capture.freehand") };
        CaptureShapeBox.SelectedIndex = (int)_draft.ScreenshotShape;
        CaptureCopyButton.Click += (_, _) => CaptureScreenshot?.Invoke((CaptureShape)Math.Max(0, CaptureShapeBox.SelectedIndex), CaptureDestination.Clipboard);
        CaptureSaveButton.Click += (_, _) => CaptureScreenshot?.Invoke((CaptureShape)Math.Max(0, CaptureShapeBox.SelectedIndex), CaptureDestination.File);
        InitializeCycle();
        RefreshLayoutsButton.Click += (_, _) => RefreshLayouts();

        SwitchLayoutBox.IsChecked = _draft.SwitchSystemLayout;
        LastWordBox.IsChecked = _draft.ConvertLastWord;
        HistoryEnabledBox.IsChecked = _draft.HistoryEnabled;
        MaxItemsBox.Value = _draft.MaxItems;
        MaxTotalBox.Value = _draft.MaxTotalMegabytes;
        MaxImageBox.Value = _draft.MaxImageMegabytes;
        ExcludedBox.Text = string.Join(", ", _draft.ExcludedApps);
        AutostartBox.IsChecked = SafeAutostart();
        KeepAwakeBox.IsChecked = _draft.PreventScreenLock;
        LanguageBox.ItemsSource = Loc.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == _draft.UiLanguage));
        ThemeBox.ItemsSource = Themes.Select(t => Loc.T("theme." + t)).ToList();
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, _draft.UiTheme));
        foreach (var snippet in _draft.Snippets) AddSnippetRow(snippet);
        AddSnippetButton.Click += (_, _) => AddSnippetRow(new Snippet { Text = Loc.T("settings.snippetExample") });
        ExportButton.Click += async (_, _) => await ExportAsync();
        CheckUpdatesButton.Click += (_, _) => CheckForUpdates?.Invoke();
        ShortcutsButton.Click += (_, _) => ShowShortcuts?.Invoke();
        ImportButton.Click += async (_, _) => await ImportAsync();
        StorageHint.Text = Loc.T("settings.storage", Platform.PlatformFactory.KeyStorageDescription);
        if (controller.Platform.Capabilities.Notes is { } notes)
        {
            PlatformNote.Text = notes;
            PlatformNote.IsVisible = true;
        }
        UpdateStats();

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
        ClearButton.Click += async (_, _) => await ClearHistory();
        Validate();
        SaveStatus.Text = Loc.T("settings.version", Services.UpdateService.CurrentVersion);

        if (controller.FailedHotkeys.Count > 0)
            SaveStatus.Text = Loc.T("settings.failed", string.Join("; ", controller.FailedHotkeys));
    }

    private bool SafeAutostart()
    {
        try { return _controller.Platform.AutostartEnabled; }
        catch (Exception) { return _draft.StartWithSystem; }
    }

    private void BuildHotkeyRows()
    {
        _toggleBox = new HotkeyBox { HotkeyText = _draft.ToggleHotkey };
        AddRow(Loc.T("settings.toggle"), _toggleBox);
        foreach (var layout in _controller.AvailableLayouts)
        {
            var box = new HotkeyBox
            {
                HotkeyText = _draft.LayoutHotkeys.FirstOrDefault(h => h.LayoutId == layout.Id)?.Hotkey ?? "",
            };
            _layoutBoxes[layout.Id] = box;
            AddRow(Loc.T("settings.convertTo", layout.Name) + (layout.SupportsConversion ? "" : " · " + Loc.T("settings.inputMethod")), box);
        }
        _caseBox = new HotkeyBox { HotkeyText = _draft.CaseHotkey };
        AddRow(Loc.T("hk.case"), _caseBox);
        _translitBox = new HotkeyBox { HotkeyText = _draft.TranslitHotkey };
        AddRow(Loc.T("hk.translit"), _translitBox);
        _undoBox = new HotkeyBox { HotkeyText = _draft.UndoHotkey };
        AddRow(Loc.T("hk.undo"), _undoBox);
    }

    private void AddSnippetRow(Snippet snippet)
    {
        var trigger = new TextBox { Text = snippet.Trigger, PlaceholderText = Loc.T("settings.snippetTrigger") };
        var text = new TextBox
        {
            Text = snippet.Text, PlaceholderText = Loc.T("settings.snippetText"), AcceptsReturn = true,
            AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, Height = 140,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 13,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var remove = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Top };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(new TextBlock { Text = Loc.T("settings.snippetKeyword"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(trigger, 1);
        Grid.SetColumn(remove, 2);
        header.Children.Add(trigger);
        header.Children.Add(remove);
        var insert = new Button { Content = Loc.T("settings.snippetCursor") };
        var previewButton = new Button { Content = Loc.T("settings.snippetPreview") };
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, IsVisible = false, MinHeight = 70,
            MaxHeight = 180, TextWrapping = TextWrapping.Wrap, FontFamily = text.FontFamily };
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        void UpdatePreview()
        {
            var template = SnippetTemplate.Parse(text.Text ?? "");
            insert.IsEnabled = template.CursorCount == 0;
            preview.Text = template.CursorOffset is { } offset ? template.Text.Insert(offset, "│") : template.Text;
            hint.Text = template.CursorCount > 1 ? Loc.T("snippet.multipleCursors", trigger.Text ?? "") : Loc.T("settings.snippetCursorHint");
            hint.Foreground = template.CursorCount > 1 ? Brushes.IndianRed : Brushes.Gray;
        }
        text.TextChanged += (_, _) => UpdatePreview();
        insert.Click += (_, _) =>
        {
            var start = Math.Min(text.SelectionStart, text.SelectionEnd);
            text.SelectedText = SnippetTemplate.Marker;
            text.CaretIndex = start + SnippetTemplate.Marker.Length;
            text.ClearSelection();
            text.Focus();
        };
        previewButton.Click += (_, _) => { UpdatePreview(); preview.IsVisible = !preview.IsVisible; };
        var body = new StackPanel { Spacing = 8, Children = { header, text,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { insert, previewButton } }, hint, preview } };
        var row = new Border { Child = body, Padding = new(12), CornerRadius = new(8), BorderThickness = new(1),
            [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBorderBrush") };
        var entry = (trigger, text, (Control)row);
        remove.Click += (_, _) =>
        {
            SnippetRows.Children.Remove(row);
            _snippetRows.Remove(entry);
        };
        _snippetRows.Add(entry);
        SnippetRows.Children.Add(row);
        UpdatePreview();
    }

    private void InitializeCycle()
    {
        _cycleOrder.Clear();
        _enabledCycle.Clear();
        foreach (var id in _draft.Layouts)
            if (_controller.FindLayout(id) is { SupportsConversion: true } layout)
            { _cycleOrder.Add(layout); _enabledCycle.Add(layout.Id); }
        _cycleOrder.AddRange(_controller.AvailableLayouts.Where(l => _cycleOrder.All(item => item.Id != l.Id)));
        BuildCycleRows();
    }

    private void BuildCycleRows()
    {
        CycleRows.Children.Clear();
        foreach (var layout in _cycleOrder)
        {
            var check = new CheckBox { Content = layout.Name, IsChecked = _enabledCycle.Contains(layout.Id), IsEnabled = layout.SupportsConversion };
            if (!layout.SupportsConversion) ToolTip.SetTip(check, Loc.T("settings.inputMethod"));
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _enabledCycle.Add(layout.Id); else _enabledCycle.Remove(layout.Id);
                UpdateCyclePreview();
                Validate();
            };
            var index = _cycleOrder.IndexOf(layout);
            var up = new Button { Content = "↑", IsEnabled = index > 0 };
            var down = new Button { Content = "↓", IsEnabled = index < _cycleOrder.Count - 1 };
            ToolTip.SetTip(up, Loc.T("settings.moveUp"));
            ToolTip.SetTip(down, Loc.T("settings.moveDown"));
            void Move(int delta)
            {
                var at = _cycleOrder.IndexOf(layout);
                (_cycleOrder[at], _cycleOrder[at + delta]) = (_cycleOrder[at + delta], _cycleOrder[at]);
                BuildCycleRows();
            }
            up.Click += (_, _) => Move(-1);
            down.Click += (_, _) => Move(1);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5 };
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2);
            row.Children.Add(check); row.Children.Add(up); row.Children.Add(down);
            CycleRows.Children.Add(row);
        }
        UpdateCyclePreview();
    }

    private void UpdateCyclePreview()
    {
        var selected = _cycleOrder.Where(l => _enabledCycle.Contains(l.Id)).ToArray();
        string Label(KeyboardLayout layout) => selected.Count(l => l.LanguageCode == layout.LanguageCode) > 1
            ? layout.Name : layout.LanguageCode.ToUpperInvariant();
        var labels = selected.Select(Label).ToList();
        if (labels.Count > 1) labels.Add(labels[0]);
        CyclePreview.Text = labels.Count > 0 ? string.Join(" → ", labels) : Loc.T("settings.cycleEmpty");
        ToolTip.SetTip(CyclePreview, string.Join(" → ", selected.Select(l => l.Name)));
        CycleStatus.Text = _controller.AvailableLayouts.Count == 0 ? Loc.T("settings.noSystemLayouts") :
            selected.Length < Math.Min(2, _cycleOrder.Count(l => l.SupportsConversion)) ? Loc.T("settings.cycleMinimum") : "";
    }

    private void RefreshLayouts()
    {
        Collect();
        _controller.RefreshKeyboardLayouts();
        _controller.LayoutCatalog.Synchronize(_draft);
        _hotkeyRows.Clear(); _layoutBoxes.Clear(); HotkeyGrid.Children.Clear(); HotkeyGrid.RowDefinitions.Clear();
        HotkeyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        BuildHotkeyRows();
        _hotkeyRows.Add((ClipboardHotkeyBox, ClipboardHotkeyStatus));
        InitializeCycle();
        Validate();
    }

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            SuggestedFileName = "typlo-settings.json",
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Collect();
        new SettingsStore(path).Export(_draft, path);
        ExportStatus.Text = Loc.T("settings.exported");
    }

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        AppSettings imported;
        try { imported = new SettingsStore(path).Import(path, _controller.AvailableLayouts); }
        catch (SettingsImportException e) when (e.Problems.Count > 0)
        {
            ExportStatus.Text = Loc.T("settings.snippetProblem", Describe(e.Problems));
            return;
        }
        catch (Exception)
        {
            ExportStatus.Text = Loc.T("settings.importFailed");
            return;
        }
        _controller.ApplySettings(imported);
        _saved = true;
        Close();
        Reopen?.Invoke();
    }

    private void AddRow(string label, HotkeyBox box)
    {
        var row = HotkeyGrid.RowDefinitions.Count - 1;
        if (_hotkeyRows.Count > 0) { HotkeyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); row++; }
        var margin = new Avalonia.Thickness(0, _hotkeyRows.Count > 0 ? 8 : 0, 0, 0);
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = margin, TextWrapping = TextWrapping.Wrap, MaxWidth = 182 };
        box.Margin = margin;
        var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, margin.Top, 0, 0), Width = 110 };
        Grid.SetRow(text, row);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        Grid.SetRow(status, row); Grid.SetColumn(status, 2);
        HotkeyGrid.Children.Add(text);
        HotkeyGrid.Children.Add(box);
        HotkeyGrid.Children.Add(status);
        _hotkeyRows.Add((box, status));
        box.HotkeyChanged += () => Validate();
    }

    /// <summary>Checks each shortcut: valid, not duplicated, and not taken by another app.</summary>
    private bool Validate()
    {
        var ok = _enabledCycle.Count >= Math.Min(2, _controller.AvailableLayouts.Count(l => l.SupportsConversion));
        var seen = new Dictionary<Hotkey, HotkeyBox>();
        foreach (var (box, status) in _hotkeyRows)
        {
            var text = box.HotkeyText;
            if (string.IsNullOrWhiteSpace(text)) { Show(status, Loc.T("hotkey.off"), false, muted: true); continue; }
            var hk = Hotkey.TryParse(text);
            if (hk is null || !hk.IsUsable) { Show(status, Loc.T("hotkey.needMod"), true); ok = false; continue; }
            if (!seen.TryAdd(hk, box)) { Show(status, Loc.T("hotkey.dup"), true); ok = false; continue; }
            // Probe: hotkeys are suspended while this window is open, so a failure means another app owns it.
            if (_controller.Platform.RegisterHotkey(9000, hk))
            {
                _controller.Platform.UnregisterAllHotkeys();
                Show(status, _controller.Platform.Capabilities.DetectHotkeyConflicts ? Loc.T("hotkey.free") : "✓", false);
            }
            else { Show(status, Loc.T("hotkey.taken"), true); ok = false; }
        }
        SaveButton.IsEnabled = ok;
        return ok;
    }

    private static void Show(TextBlock status, string text, bool error, bool muted = false)
    {
        status.Text = text;
        status.Foreground = error ? Brushes.IndianRed : muted ? Brushes.Gray : Brushes.SeaGreen;
    }

    private void UpdateStats()
    {
        var (count, bytes) = _controller.Store.Stats();
        StatsText.Text = Loc.T("settings.stats", count, bytes / 1024.0 / 1024.0);
    }

    private async Task ClearHistory()
    {
        var dialog = new Window
        {
            Title = Loc.T("clear.title"),
            Width = 380, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = false;
        var yes = new Button { Content = Loc.T("clear.yes"), Classes = { "accent" } };
        var no = new Button { Content = Loc.T("settings.cancel") };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = Loc.T("clear.question"), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
            },
        };
        await dialog.ShowDialog(this);
        if (!result) return;
        _controller.Store.Clear();
        UpdateStats();
    }

    private void Save()
    {
        if (!Validate()) return;
        Collect();
        if (_draft.Validate(_controller.AvailableLayouts) is { Count: > 0 } problems)
        {
            SaveStatus.Text = Loc.T("settings.snippetProblem", Describe(problems));
            return;
        }

        _controller.ApplySettings(_draft);
        _saved = true;
        if (_controller.FailedHotkeys.Count > 0)
        {
            SaveStatus.Text = Loc.T("settings.failed", string.Join("; ", _controller.FailedHotkeys));
            return;
        }
        Close();
    }

    private static string Describe(IReadOnlyList<SettingsProblem> problems) => string.Join("; ", problems.Take(4).Select(p => p.Kind switch
    {
        SettingsProblemKind.TriggerTooShort => Loc.T("snippet.tooShort", p.Trigger),
        SettingsProblemKind.TriggerTooLong => Loc.T("snippet.tooLong", p.Trigger[..Math.Min(20, p.Trigger.Length)] + "…", AppSettings.MaxTriggerLength),
        SettingsProblemKind.DuplicateTrigger => Loc.T("snippet.duplicate", p.Trigger),
        SettingsProblemKind.MultipleCursors => Loc.T("snippet.multipleCursors", p.Trigger),
        _ => Loc.T("snippet.emptyText", p.Trigger),
    }));

    /// <summary>Copies the form into the draft settings.</summary>
    private void Collect()
    {
        _draft.ToggleHotkey = _toggleBox.HotkeyText;
        _draft.CaseHotkey = _caseBox.HotkeyText;
        _draft.TranslitHotkey = _translitBox.HotkeyText;
        _draft.UndoHotkey = _undoBox.HotkeyText;
        _draft.ScreenshotShape = (CaptureShape)Math.Max(0, CaptureShapeBox.SelectedIndex);
        _draft.ScreenshotCopyHotkey = CaptureCopyHotkeyBox.HotkeyText;
        _draft.ScreenshotSaveHotkey = CaptureSaveHotkeyBox.HotkeyText;
        _draft.UiTheme = Themes[Math.Max(0, ThemeBox.SelectedIndex)];
        _draft.Snippets = _snippetRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Trigger.Text) || !string.IsNullOrEmpty(r.Text.Text))
            .Select(r => new Snippet { Trigger = (r.Trigger.Text ?? "").Trim(), Text = r.Text.Text ?? "" }).ToList();
        _draft.LayoutHotkeys = _layoutBoxes
            .Select(kv => new LayoutHotkey { LayoutId = kv.Key, Hotkey = kv.Value.HotkeyText })
            .Concat(_draft.LayoutHotkeys.Where(h => _controller.FindLayout(h.LayoutId) is null)).ToList();
        _draft.ClipboardHotkey = ClipboardHotkeyBox.HotkeyText;
        _draft.Layouts = _cycleOrder.Where(l => _enabledCycle.Contains(l.Id)).Select(l => l.Id)
            .Concat(_draft.Layouts.Where(id => _controller.FindLayout(id) is null)).ToList();
        _draft.SwitchSystemLayout = SwitchLayoutBox.IsChecked == true;
        _draft.ConvertLastWord = LastWordBox.IsChecked == true;
        _draft.HistoryEnabled = HistoryEnabledBox.IsChecked == true;
        _draft.MaxItems = (int)(MaxItemsBox.Value ?? 500);
        _draft.MaxTotalMegabytes = (int)(MaxTotalBox.Value ?? 200);
        _draft.MaxImageMegabytes = (int)(MaxImageBox.Value ?? 20);
        _draft.ExcludedApps = (ExcludedBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s).ToList();
        _draft.StartWithSystem = AutostartBox.IsChecked == true;
        _draft.PreventScreenLock = KeepAwakeBox.IsChecked == true;
        _draft.UiLanguage = Loc.Languages[Math.Max(0, LanguageBox.SelectedIndex)].Code;
    }
}
