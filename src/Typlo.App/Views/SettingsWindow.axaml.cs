using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.Core.Text;

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
    private readonly List<(TextBox Trigger, TextBox Text, Control Row)> _snippetRows = [];
    private static readonly string[] Themes = ["system", "light", "dark"];

    /// <summary>Called after a successful import so the app can show a fresh settings window.</summary>
    public Action? Reopen { get; set; }

    /// <summary>Opens the "Check for updates" window.</summary>
    public Action? CheckForUpdates { get; set; }

    /// <summary>Opens the list of all keyboard shortcuts.</summary>
    public Action? ShowShortcuts { get; set; }
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
        foreach (var (box, _) in _hotkeyRows) box.HotkeyChanged += () => Validate();

        PairFirst.ItemsSource = KeyboardLayouts.All;
        PairSecond.ItemsSource = KeyboardLayouts.All;
        PairFirst.SelectedItem = KeyboardLayouts.Find(_draft.Layouts[0]);
        PairSecond.SelectedItem = KeyboardLayouts.Find(_draft.Layouts[1]);

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
        AddSnippetButton.Click += (_, _) => AddSnippetRow(new Snippet());
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
        foreach (var layout in KeyboardLayouts.All)
        {
            var box = new HotkeyBox
            {
                HotkeyText = _draft.LayoutHotkeys.FirstOrDefault(h => h.LayoutId == layout.Id)?.Hotkey ?? "",
            };
            _layoutBoxes[layout.Id] = box;
            AddRow(Loc.T("settings.convertTo", Loc.T("layout." + layout.Id)), box);
        }
        _caseBox = new HotkeyBox { HotkeyText = _draft.CaseHotkey };
        AddRow(Loc.T("hk.case"), _caseBox);
        _translitBox = new HotkeyBox { HotkeyText = _draft.TranslitHotkey };
        AddRow(Loc.T("hk.translit"), _translitBox);
    }

    private void AddSnippetRow(Snippet snippet)
    {
        var trigger = new TextBox { Text = snippet.Trigger, PlaceholderText = Loc.T("settings.snippetTrigger"), Width = 130 };
        var text = new TextBox
        {
            Text = snippet.Text, PlaceholderText = Loc.T("settings.snippetText"), AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 120,
        };
        var remove = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Top };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(trigger);
        row.Children.Add(text);
        row.Children.Add(remove);
        var entry = (trigger, text, (Control)row);
        remove.Click += (_, _) =>
        {
            SnippetRows.Children.Remove(row);
            _snippetRows.Remove(entry);
        };
        _snippetRows.Add(entry);
        SnippetRows.Children.Add(row);
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
        try { imported = new SettingsStore(path).Import(path); }
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
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
        box.Margin = margin;
        var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, margin.Top, 0, 0), Width = 110 };
        Grid.SetRow(text, row);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        Grid.SetRow(status, row); Grid.SetColumn(status, 2);
        HotkeyGrid.Children.Add(text);
        HotkeyGrid.Children.Add(box);
        HotkeyGrid.Children.Add(status);
        _hotkeyRows.Add((box, status));
    }

    /// <summary>Checks each shortcut: valid, not duplicated, and not taken by another app.</summary>
    private bool Validate()
    {
        var ok = true;
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

        _controller.ApplySettings(_draft);
        _saved = true;
        if (_controller.FailedHotkeys.Count > 0)
        {
            SaveStatus.Text = Loc.T("settings.failed", string.Join("; ", _controller.FailedHotkeys));
            return;
        }
        Close();
    }

    /// <summary>Copies the form into the draft settings.</summary>
    private void Collect()
    {
        _draft.ToggleHotkey = _toggleBox.HotkeyText;
        _draft.CaseHotkey = _caseBox.HotkeyText;
        _draft.TranslitHotkey = _translitBox.HotkeyText;
        _draft.UiTheme = Themes[Math.Max(0, ThemeBox.SelectedIndex)];
        _draft.Snippets = _snippetRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Trigger.Text) && !string.IsNullOrEmpty(r.Text.Text))
            .Select(r => new Snippet { Trigger = r.Trigger.Text!.Trim(), Text = r.Text.Text! }).ToList();
        _draft.LayoutHotkeys = _layoutBoxes
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.HotkeyText))
            .Select(kv => new LayoutHotkey { LayoutId = kv.Key, Hotkey = kv.Value.HotkeyText }).ToList();
        _draft.ClipboardHotkey = ClipboardHotkeyBox.HotkeyText;
        var first = (PairFirst.SelectedItem as KeyboardLayout)?.Id ?? "en";
        var second = (PairSecond.SelectedItem as KeyboardLayout)?.Id ?? "ru";
        if (first == second) second = first == "en" ? "ru" : "en";
        _draft.Layouts = [first, second];
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
