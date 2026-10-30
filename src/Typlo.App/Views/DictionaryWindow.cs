using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Typlo.App.Services;
using Typlo.Core.Languages;

namespace Typlo.App.Views;

internal sealed class DictionaryWindow : Window
{
    private readonly AppController _controller;
    private readonly TextBox _search = new() { PlaceholderText = Loc.T("manage.search"), MinWidth = 180 };
    private readonly ComboBox _filter = new() { MinWidth = 170 };
    private readonly ListBox _words = new();
    private readonly TextBox _forms = new() { AcceptsReturn = true, MinHeight = 120, MaxLength = 64_000, TextWrapping = TextWrapping.Wrap, PlaceholderText = Loc.T("manage.forms") };
    private readonly ComboBox _language = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _health = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _diagnostics = new();
    private readonly TextBlock _emptyDiagnostics = new() { Text = Loc.T("manage.empty"), TextWrapping = TextWrapping.Wrap, MaxWidth = 500,
        Margin = new Thickness(20), Opacity = .75, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = "←" }, _next = new() { Content = "→" };
    private readonly StackPanel _actions = new() { Spacing = 10 };
    private readonly TabControl _tabs = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly (string Code, string Name)[] _languages = Loc.Languages.Where(l => PersonalLexicon.LanguageCodes.Contains(l.Code)).ToArray();
    private PersonalWord? _selected;
    private int _page, _queryVersion;
    private bool _closed, _refreshingStatus, _mutating;
    internal PersonalWordPage? DisplayedPage { get; private set; }
    internal AutoCorrectionStatus? DisplayedStatus { get; private set; }

    internal DictionaryWindow(AppController controller, bool diagnostics = false)
    {
        _controller = controller;
        Title = Loc.T("manage.title"); Icon = AppIcon.Create(); Width = 900; Height = 670; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _filter.ItemsSource = new[] { Loc.T("manage.all") }.Concat(_languages.Select(l => l.Name)).ToArray(); _filter.SelectedIndex = 0;
        _language.ItemsSource = _languages.Select(l => l.Name).ToArray();
        _language.SelectedIndex = Math.Max(0, Array.FindIndex(_languages, l => l.Code == Loc.Current));
        _words.ItemTemplate = new FuncDataTemplate<PersonalWord>((entry, _) => new TextBlock
        { Text = entry is null ? "" : $"{entry.Word}  ·  {LanguageName(entry.Language)}", Margin = new Thickness(8, 6) });
        _words.SelectionChanged += (_, _) =>
        {
            _selected = _words.SelectedItem as PersonalWord;
            if (_selected is not { } selected) return;
            _forms.Text = selected.Word; _language.SelectedIndex = Array.FindIndex(_languages, l => l.Code == selected.Language);
        };
        var filters = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        filters.Children.Add(_search); Grid.SetColumn(_filter, 1); filters.Children.Add(_filter);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _previous, _pageLabel, _next, ActionButton("manage.refresh", LoadWordsAsync) } };
        _actions.Children.Add(new TextBlock { Text = Loc.T("manage.formsHint"), TextWrapping = TextWrapping.Wrap });
        _actions.Children.Add(_forms); _actions.Children.Add(_language);
        _actions.Children.Add(ActionButton("dictionary.add", () => Mutate(async () =>
        {
            var language = SelectedLanguage();
            var entries = (_forms.Text ?? "").Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => new PersonalWord(word, language)).ToArray();
            if (entries.Length == 0) throw new ArgumentException(Loc.T("dictionary.invalid"));
            ShowImportResult(await _controller.AddPersonalWordsAsync(entries));
        })));
        _actions.Children.Add(ActionButton("manage.edit", () => Mutate(async () =>
        {
            if (_selected is null) throw new InvalidOperationException(Loc.T("manage.select"));
            await _controller.EditPersonalWordAsync(_selected, new(_forms.Text ?? "", SelectedLanguage()));
            _message.Text = Loc.T("manage.saved");
        })));
        _actions.Children.Add(ActionButton("history.ctx.delete", () => Mutate(async () =>
        {
            if (_selected is null) throw new InvalidOperationException(Loc.T("manage.select"));
            await _controller.SetPersonalWordAsync(_selected.Word, _selected.Language, remove: true); _message.Text = Loc.T("manage.saved");
        })));
        _actions.Children.Add(ActionButton("manage.import", ImportAsync));
        _actions.Children.Add(ActionButton("manage.export", ExportAsync));
        var body = new Grid { ColumnDefinitions = new("*,280"), ColumnSpacing = 20, Margin = new Thickness(0, 12) };
        body.Children.Add(_words);
        var editorScroll = new ScrollViewer { Content = _actions, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(editorScroll, 1); body.Children.Add(editorScroll);
        var personal = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(filters, Dock.Top); personal.Children.Add(filters); DockPanel.SetDock(footer, Dock.Bottom); personal.Children.Add(footer); personal.Children.Add(body);
        var check = ActionButton("manage.check", async () =>
        {
            try { DisplayStatus(await _controller.CheckDictionaryAsync()); }
            catch (Exception e) { _message.Text = e.Message; }
        });
        var diagnosticHeader = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 12), Children =
        {
            _health, new TextBlock { Text = Loc.T("manage.privacy"), TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { check, ActionButton("manage.report", SaveReportAsync) } },
        }};
        _diagnostics.ItemTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6) });
        var diagnosticList = new Grid { Children = { _diagnostics, _emptyDiagnostics } };
        var diagnosticPanel = new DockPanel(); DockPanel.SetDock(diagnosticHeader, Dock.Top); diagnosticPanel.Children.Add(diagnosticHeader); diagnosticPanel.Children.Add(diagnosticList);
        _tabs.ItemsSource = new[] { new TabItem { Header = Loc.T("dictionary.title"), Content = personal }, new TabItem { Header = Loc.T("manage.diagnostics"), Content = diagnosticPanel } };
        _tabs.SelectedIndex = diagnostics ? 1 : 0;
        var root = new DockPanel { Margin = new Thickness(22) }; DockPanel.SetDock(_message, Dock.Bottom); root.Children.Add(_message); root.Children.Add(_tabs); Content = root;
        _search.TextChanged += async (_, _) => { var version = ++_queryVersion; await Task.Delay(200); if (!_closed && version == _queryVersion) { _page = 0; await LoadWordsAsync(); } };
        _filter.SelectionChanged += async (_, _) => { _page = 0; await LoadWordsAsync(); };
        _previous.Click += async (_, _) => { _page--; await LoadWordsAsync(); };
        _next.Click += async (_, _) => { _page++; await LoadWordsAsync(); };
        _timer.Tick += async (_, _) => { if (_tabs.SelectedIndex == 1) await RefreshStatusAsync(); };
        Opened += async (_, _) => { await LoadWordsAsync(); await RefreshStatusAsync(); if (!_closed) _timer.Start(); };
        Closed += (_, _) => { _closed = true; _queryVersion++; _timer.Stop(); };
    }

    private string LanguageName(string code) => _languages.FirstOrDefault(l => l.Code == code).Name ?? code;
    private string SelectedLanguage() => _language.SelectedIndex >= 0 ? _languages[_language.SelectedIndex].Code : throw new ArgumentException(Loc.T("dictionary.invalid"));
    private Button ActionButton(string key, Func<Task> action)
    {
        var button = new Button { Content = Loc.T(key) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception e) { if (!_closed) _message.Text = e.Message; } };
        return button;
    }
    private async Task LoadWordsAsync()
    {
        if (_closed) return;
        var version = ++_queryVersion;
        var language = _filter.SelectedIndex > 0 ? _languages[_filter.SelectedIndex - 1].Code : null;
        try
        {
            var page = await _controller.QueryPersonalWordsAsync(new(_search.Text ?? "", language, Math.Max(0, _page)));
            if (_closed || version != _queryVersion) return;
            DisplayedPage = page;
            _selected = null; _words.ItemsSource = page.Items; _page = page.Page;
            _previous.IsEnabled = page.Page > 0; _next.IsEnabled = (page.Page + 1) * page.PageSize < page.Total;
            _pageLabel.Text = Loc.T("manage.page", page.Page + 1, Math.Max(1, (page.Total + page.PageSize - 1) / page.PageSize), page.Total);
        }
        catch (Exception e) { if (!_closed && version == _queryVersion) _message.Text = e.Message; }
    }
    private async Task Mutate(Func<Task> action)
    {
        if (_mutating) return; _mutating = true; _actions.IsEnabled = false;
        try { await action(); if (!_closed) await LoadWordsAsync(); }
        catch (Exception e) { if (!_closed) _message.Text = e.Message; }
        finally { _mutating = false; if (!_closed) _actions.IsEnabled = true; }
    }
    private void ShowImportResult(PersonalImportResult result) => _message.Text = Loc.T("manage.imported", result.Added, result.Skipped);
    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("manage.import"), FileTypeFilter = [JsonType] });
        if (files.Count == 0) return;
        await Mutate(async () => ShowImportResult(await _controller.ImportPersonalWordsAsync(LocalPath(files[0]))));
    }
    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("manage.export"), SuggestedFileName = "Typlo-personal-dictionary.json", DefaultExtension = "json", FileTypeChoices = [JsonType] });
        if (file is null) return;
        await Mutate(async () => { await _controller.ExportPersonalWordsAsync(LocalPath(file)); _message.Text = Loc.T("manage.saved"); });
    }
    private static readonly FilePickerFileType JsonType = new("JSON") { Patterns = ["*.json"] };
    private static string LocalPath(IStorageItem file) => file.TryGetLocalPath() ?? throw new IOException("Choose a local file.");
    private async Task RefreshStatusAsync()
    {
        if (_closed || _refreshingStatus) return; _refreshingStatus = true;
        try { var status = await _controller.GetAutomaticStatusAsync(); if (!_closed) DisplayStatus(status); }
        catch (Exception e) { if (!_closed) _message.Text = e.Message; }
        finally { _refreshingStatus = false; }
    }
    internal void DisplayStatus(AutoCorrectionStatus status)
    {
        DisplayedStatus = status;
        _health.Text = Loc.T("manage.status", Loc.T(status.Enabled ? "manage.on" : "manage.off"), Loc.T("health." + status.Health)) +
            (status.Supported ? "" : " · " + Loc.T("auto.unavailable")) +
            (status.PauseReason is { } pause ? " · " + Loc.T("reason." + pause) : "");
        _emptyDiagnostics.IsVisible = status.Recent.Length == 0;
        _diagnostics.ItemsSource = status.Recent.Select(row => $"{row.At.ToLocalTime():HH:mm:ss}  ·  {Loc.T("reason." + row.Reason)}  ·  {row.Source ?? "—"} → {row.Target ?? "—"}  ·  {row.Milliseconds} ms").ToArray();
    }
    private async Task SaveReportAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("manage.report"), SuggestedFileName = "Typlo-diagnostics.txt", DefaultExtension = "txt" });
            if (file is null) return;
            var status = await _controller.GetAutomaticStatusAsync();
            var path = LocalPath(file);
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) throw new IOException(Loc.T("manage.fileError"));
            await File.WriteAllTextAsync(path, AutoCorrectionReport.Format(status)); _message.Text = Loc.T("manage.saved");
        }
        catch (Exception e) { if (!_closed) _message.Text = e.Message; }
    }
}
