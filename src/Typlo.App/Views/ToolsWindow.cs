using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Typlo.App.Services;

namespace Typlo.App.Views;

internal sealed class ToolsWindow : Window
{
    private readonly AppController _controller;
    private readonly bool _choicesOnly;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _field = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _memory = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _choices = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _timings = new() { Height = 190 };
    private readonly TabControl _tabs = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private bool _closed, _refreshing, _busy;
    public event Action? Restored;
    internal ToolsWindow(AppController controller, bool choicesOnly)
    {
        _controller = controller; _choicesOnly = choicesOnly;
        _timer.Interval = TimeSpan.FromMilliseconds(choicesOnly ? 300 : 1000);
        Title = Loc.T(choicesOnly ? "tools.choices" : "tools.title"); Icon = AppIcon.Create();
        Width = choicesOnly ? 510 : 850; Height = choicesOnly ? 330 : 670; MinWidth = 450; MinHeight = 280;
        ShowActivated = !choicesOnly; ShowInTaskbar = !choicesOnly; Topmost = choicesOnly;
        var body = new StackPanel { Margin = new(22), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = Loc.T(choicesOnly ? "tools.choiceHint" : "tools.probeHint", controller.Settings.ChoicesHotkey, controller.Settings.AcceptChoiceHotkey, controller.Settings.CancelChoiceHotkey, controller.Settings.ToolsHotkey), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_choices);
        if (!choicesOnly)
        {
            body.Children.Add(_field); body.Children.Add(_memory);
            body.Children.Add(new TextBlock { Text = Loc.T("tools.performanceHint"), TextWrapping = TextWrapping.Wrap });
            body.Children.Add(_timings);
            var backup = new StackPanel { Margin = new(22), Spacing = 14 };
            backup.Children.Add(new TextBlock { Text = Loc.T("tools.backupHint"), TextWrapping = TextWrapping.Wrap });
            AddButton("tools.backup", ExportAsync); AddButton("tools.restore", RestoreAsync); AddButton("tools.report", ReportAsync);
            backup.Children.Add(_actions); backup.Children.Add(_status);
            _tabs.ItemsSource = new[] { new TabItem { Header = Loc.T("tools.measurements"), Content = new ScrollViewer { Content = body } },
                new TabItem { Header = Loc.T("tools.backupGroup"), Content = new ScrollViewer { Content = backup } } };
        }
        Content = choicesOnly ? new ScrollViewer { Content = body } : _tabs;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) => { await RefreshAsync(); if (!_closed) _timer.Start(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
    }
    internal void ShowBackup() => _tabs.SelectedIndex = 1;
    private void AddButton(string label, Func<Task> action)
    {
        var button = new Button { Content = Loc.T(label) };
        button.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; _actions.IsEnabled = false;
            try { await action(); } catch (Exception e) { if (!_closed) _status.Text = e.Message; }
            finally { _busy = false; if (!_closed) _actions.IsEnabled = true; }
        };
        _actions.Children.Add(button);
    }
    private async Task RefreshAsync()
    {
        if (_closed || _refreshing) return; _refreshing = true;
        try
        {
            var status = await _controller.GetToolsStatusAsync(); if (_closed) return;
            if (_choicesOnly && status.Choices.Length == 0) { Close(); return; }
            _choices.Text = string.Join("\n", status.Choices.Select((c, i) => (i == status.ChoiceIndex ? "➜ " : "   ") + c));
            if (_choicesOnly) return;
            _field.Text = status.Field ?? Loc.T("tools.noProbe");
            var values = status.Recent.Where(s => s.Operation is "text-replacement" or "manual-choice").Select(s => s.Milliseconds).Order().ToArray();
            static string Metric(long value) => value < 0 ? "—" : (value / 1048576d).ToString("F1");
            _memory.Text = Loc.T("tools.metrics", Metric(status.WorkingSet), Metric(status.PrivateBytes), Metric(status.ManagedBytes),
                values.Length == 0 ? 0 : values[(values.Length - 1) / 2], values.Length == 0 ? 0 : values[(int)Math.Ceiling(values.Length * .95) - 1]);
            _timings.ItemsSource = status.Recent.Select(s => $"{s.At.ToLocalTime():HH:mm:ss} · {s.Operation} · {s.Milliseconds} ms · {string.Join(", ", s.Phases)}").ToArray();
        }
        catch (Exception e) { if (!_closed) _status.Text = e.Message; }
        finally { _refreshing = false; }
    }
    private static readonly FilePickerFileType BackupType = new("Typlo") { Patterns = ["*.typlo"] };
    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.T("tools.backup"), SuggestedFileName = "Typlo-backup.typlo", DefaultExtension = "typlo", FileTypeChoices = [BackupType] });
        if (file?.TryGetLocalPath() is not { } path) return;
        await _controller.ExportBackupAsync(path); _status.Text = Loc.T("manage.saved");
    }
    private async Task RestoreAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("tools.restore"), FileTypeFilter = [BackupType] });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var info = await _controller.PreviewBackupAsync(path); if (_closed) return;
        var dialog = new Window { Title = Loc.T("tools.restore"), Width = 510, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var confirm = new Button { Content = Loc.T("tools.restore") }; var cancel = new Button { Content = Loc.T("settings.cancel") };
        confirm.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(20), Spacing = 12, Children =
        { new TextBlock { Text = Loc.T("tools.preview", info.Words, info.Exclusions, info.Layouts, info.Snippets, info.Profiles), TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } } } };
        if (!await dialog.ShowDialog<bool>(this) || _closed) return;
        await _controller.RestoreBackupAsync(path, info.Hash); _status.Text = Loc.T("manage.saved"); Restored?.Invoke();
    }
    private async Task ReportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "Typlo-performance.txt", DefaultExtension = "txt" });
        if (file?.TryGetLocalPath() is not { } path || !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return;
        var status = await _controller.GetToolsStatusAsync();
        // Excludes field/app names and manual candidate texts. Only bounded numeric/phase telemetry.
        await File.WriteAllTextAsync(path, $"Typlo {UpdateService.CurrentVersion}\nResident working set: {status.WorkingSet}\nPrivate: {status.PrivateBytes}\nManaged: {status.ManagedBytes}\n" +
            string.Join("\n", status.Recent.Select(s => $"{s.At:O} {s.Operation} {s.Milliseconds}ms {string.Join(", ", s.Phases)}")));
        _status.Text = Loc.T("manage.saved");
    }
}
