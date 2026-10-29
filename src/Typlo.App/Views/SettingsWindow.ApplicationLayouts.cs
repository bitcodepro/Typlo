using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Settings;

namespace Typlo.App.Views;

public partial class SettingsWindow
{
    private sealed record ApplicationRow(TextBox Process, CheckBox Enabled, List<string> Order,
        HashSet<string> Selected, TextBlock Status);
    private sealed record RunningApplication(string Process, string Title)
    {
        public override string ToString() => Title.Length == 0 ? Process : $"{Title} · {Process}";
    }
    private readonly List<ApplicationRow> _applicationRows = [];
    private bool _hotkeysValid = true;

    private void RebuildApplicationLayoutRows()
    {
        _applicationRows.Clear(); ApplicationLayoutRows.Children.Clear();
        foreach (var rule in _draft.ApplicationLayouts) AddApplicationLayoutRow(rule);
    }

    private void AddApplicationLayoutRow(ApplicationLayoutRule rule)
    {
        var process = new TextBox { Text = rule.ProcessName, PlaceholderText = Loc.T("appLayouts.process"), MinWidth = 160 };
        var enabled = new CheckBox { Content = Loc.T("appLayouts.enabled"), IsChecked = rule.Enabled };
        var choose = new Button { Content = Loc.T("appLayouts.choose") };
        var remove = new Button { Content = "✕" };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        header.Children.Add(process); Grid.SetColumn(choose, 1); header.Children.Add(choose);
        Grid.SetColumn(remove, 2); header.Children.Add(remove);
        var order = rule.Layouts.Concat(_controller.AvailableLayouts.Where(l => l.SupportsConversion).Select(l => l.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selected = rule.Layouts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var choices = new StackPanel { Spacing = 3 };
        var editor = new ApplicationRow(process, enabled, order, selected, status);
        var content = new StackPanel { Spacing = 7, Children = { header, enabled, choices, status } };
        var border = new Border { Padding = new(10), BorderThickness = new(1), CornerRadius = new(6),
            BorderBrush = Brushes.Gray, Child = content };
        _applicationRows.Add(editor); ApplicationLayoutRows.Children.Add(border);
        void Update() => SaveButton.IsEnabled = _hotkeysValid && ValidateApplicationLayouts();
        void BuildChoices()
        {
            choices.Children.Clear();
            foreach (var id in order.ToArray())
            {
                var layout = _controller.FindLayout(id);
                var check = new CheckBox { Content = layout?.Name ?? id, IsChecked = selected.Contains(id) };
                check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(id); else selected.Remove(id); Update(); };
                var up = new Button { Content = "↑", IsEnabled = order.IndexOf(id) > 0, Padding = new(8, 2) };
                var down = new Button { Content = "↓", IsEnabled = order.IndexOf(id) < order.Count - 1, Padding = new(8, 2) };
                void Move(int offset)
                { var at = order.IndexOf(id); order.RemoveAt(at); order.Insert(at + offset, id); BuildChoices(); Update(); }
                up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
                row.Children.Add(check); Grid.SetColumn(up, 1); row.Children.Add(up);
                Grid.SetColumn(down, 2); row.Children.Add(down); choices.Children.Add(row);
            }
        }
        process.TextChanged += (_, _) => Update(); enabled.IsCheckedChanged += (_, _) => Update();
        choose.Click += async (_, _) => { if (await ChooseProcessAsync() is { } name) process.Text = name; };
        remove.Click += (_, _) => { _applicationRows.Remove(editor); ApplicationLayoutRows.Children.Remove(border); Update(); };
        BuildChoices(); Update();
    }

    private bool ValidateApplicationLayouts()
    {
        var valid = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _applicationRows)
        {
            var process = ApplicationLayoutRule.ProcessKey(row.Process.Text);
            var layouts = row.Order.Where(row.Selected.Contains).ToArray();
            var error = row.Enabled.IsChecked == true && process.Length == 0;
            var duplicate = row.Enabled.IsChecked == true && process.Length > 0 && !seen.Add(process);
            error |= duplicate;
            valid &= !error;
            row.Status.Foreground = error ? Brushes.IndianRed : Brushes.Gray;
            row.Status.Text = row.Enabled.IsChecked != true ? Loc.T("hotkey.off") : duplicate ? Loc.T("appLayouts.duplicate", process) :
                error ? Loc.T("appLayouts.invalid") : layouts.Length == 0 ? Loc.T("appLayouts.global") : layouts.Any(id => _controller.FindLayout(id)?.SupportsConversion != true) ?
                Loc.T("appLayouts.missing") : layouts.Length == 1 ? Loc.T("appLayouts.single", _controller.FindLayout(layouts[0])!.Name) :
                string.Join(" → ", layouts.Select(id => _controller.FindLayout(id)?.LanguageCode.ToUpperInvariant() ?? id));
        }
        return valid;
    }

    private List<ApplicationLayoutRule> CollectApplicationLayouts() => _applicationRows.Select(row => new ApplicationLayoutRule
    {
        ProcessName = ApplicationLayoutRule.ProcessKey(row.Process.Text), Enabled = row.Enabled.IsChecked == true,
        Layouts = row.Order.Where(row.Selected.Contains).ToList(),
    }).ToList();

    private async Task<string?> ChooseProcessAsync()
    {
        var dialog = new Window { Title = Loc.T("appLayouts.choose"), Width = 430, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var search = new TextBox { PlaceholderText = Loc.T("appLayouts.search") };
        var list = new ListBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var use = new Button { Content = Loc.T("appLayouts.choose"), IsEnabled = false };
        var cancel = new Button { Content = Loc.T("settings.cancel") };
        var refresh = new Button { Content = "↻" }; ToolTip.SetTip(refresh, Loc.T("appLayouts.refresh"));
        RunningApplication[] names = [];
        void Filter() => list.ItemsSource = names.Where(n => n.ToString().Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        async Task Refresh()
        {
            refresh.IsEnabled = false;
            names = await Task.Run(() =>
            {
                var result = new Dictionary<string, RunningApplication>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Process.GetProcesses())
                {
                    using (item)
                    {
                        try
                        {
                            if (item.Id == Environment.ProcessId) continue;
                            var name = item.ProcessName;
                            var title = OperatingSystem.IsWindows() ? item.MainWindowTitle : "";
                            if (!result.TryGetValue(name, out var old) || old.Title.Length == 0)
                                result[name] = new(name, title);
                        }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                }
                return result.Values.OrderBy(n => n.Title.Length == 0).ThenBy(n => n.Process, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            refresh.IsEnabled = true; Filter();
        }
        search.TextChanged += (_, _) => Filter(); refresh.Click += async (_, _) => await Refresh();
        list.SelectionChanged += (_, _) => use.IsEnabled = list.SelectedItem is RunningApplication;
        use.Click += (_, _) => dialog.Close((list.SelectedItem as RunningApplication)?.Process); cancel.Click += (_, _) => dialog.Close(null);
        var dock = new DockPanel { Margin = new(14) };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Margin = new(0, 0, 0, 10) };
        top.Children.Add(search); Grid.SetColumn(refresh, 1); top.Children.Add(refresh); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new(0, 10, 0, 0), Children = { cancel, use } };
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        if (OperatingSystem.IsWindows())
        {
            var hostHint = new TextBlock { Text = Loc.T("appLayouts.host"), TextWrapping = TextWrapping.Wrap,
                FontSize = 12, Opacity = .75, Margin = new(0,0,0,10) };
            DockPanel.SetDock(hostHint, Dock.Top); dock.Children.Add(hostHint);
        }
        dock.Children.Add(list); dialog.Content = dock;
        dialog.Opened += async (_, _) => await Refresh();
        return await dialog.ShowDialog<string?>(this);
    }
}
