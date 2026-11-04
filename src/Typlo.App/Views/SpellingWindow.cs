using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;

namespace Typlo.App.Views;

internal sealed class SpellingWindow : Window
{
    private readonly AppController _controller;
    private readonly long _id;
    private readonly CancellationTokenSource _stop = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _issues = new() { Spacing = 8 };
    private readonly TextBox _preview = new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };
    private readonly Button _apply = new();
    private readonly ComboBox _language = new() { ItemsSource = new[] { "Auto · EN / RU / UK", "English", "Русский", "Українська" }, SelectedIndex = 0 };
    private SpellingSelection? _selection;
    private string? _replacement;
    private bool _busy, _closed;
    internal SpellingWindow(AppController controller, long id)
    {
        _controller = controller; _id = id;
        Title = Loc.T("spell.title"); Icon = AppIcon.Create(); Width = 760; Height = 700; MinWidth = 520; MinHeight = 420;
        var panel = new StackPanel { Spacing = 12, Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = Loc.T("spell.hint"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_language); panel.Children.Add(_status);
        panel.Children.Add(new ScrollViewer { Content = _issues, MaxHeight = 260 }); panel.Children.Add(_preview);
        var cancel = new Button { Content = Loc.T("spell.cancel") }; cancel.Click += (_, _) => Close();
        _apply.Content = Loc.T("spell.apply"); _apply.IsEnabled = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_apply); actions.Children.Add(cancel); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel };
        Opened += async (_, _) =>
        {
            try { _selection = await _controller.GetSpellingSelectionAsync(_id); await CheckAsync(); }
            catch { if (!_closed) _status.Text = Loc.T("spell.failed"); }
        };
        _language.SelectionChanged += async (_, _) => { if (_selection is not null && !_busy) await CheckAsync(); };
        _apply.Click += async (_, _) =>
        {
            if (_busy || _selection is null) return; _busy = true; _apply.IsEnabled = false;
            using var operation = UiOperations.Shared.Begin();
            if (operation is null) return;
            using var stopping = operation.Token.Register(() => _ = CancelAsync());
            try
            {
                var result = _replacement ?? _selection.Text;
                if (await _controller.ApplySpellingAsync(_id, result)) Close();
                else if (!_closed) _status.Text = Loc.T("spell.changed");
            }
            catch { if (!_closed) _status.Text = Loc.T("spell.failed"); }
            finally { _busy = false; }
        };
        Closed += (_, _) => { _closed = true; _stop.Cancel(); _selection = null; _replacement = null; _preview.Text = null; _issues.Children.Clear(); _ = CancelAsync(); };
    }
    private async Task CancelAsync()
    { try { await _controller.CancelSpellingAsync(_id); } catch (Exception) { /* Closing/disconnected UI never writes. */ } }
    private async Task CheckAsync()
    {
        if (_busy || _closed) return; _busy = true; _language.IsEnabled = _apply.IsEnabled = false;
        _status.Text = Loc.T("spell.busy"); _issues.Children.Clear();
        try
        {
            if (_selection is null) { _status.Text = Loc.T("spell.changed"); return; }
            if (_selection.Notice is { } notice) { _status.Text = Loc.T(notice); return; }
            using var operation = UiOperations.Shared.Begin();
            if (operation is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, operation.Token);
            var result = await SpellingWorker.CheckAsync(new(_selection.Text, new[] { "auto", "en", "ru", "uk" }[_language.SelectedIndex], _selection.Ignored), linked.Token);
            if (_closed) return;
            var choices = new string?[result.Issues.Length];
            void Preview() { _replacement = SpellingText.Apply(_selection.Text, result.Issues, choices); _preview.Text = _replacement; }
            for (var i = 0; i < result.Issues.Length; i++)
            {
                var index = i; var issue = result.Issues[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new TextBlock { Text = $"{issue.Word} · {issue.Language}", Width = 220, VerticalAlignment = VerticalAlignment.Center });
                var options = new[] { Loc.T("spell.keep") }.Concat(issue.Suggestions).ToArray();
                var box = new ComboBox { ItemsSource = options, SelectedIndex = 0, MinWidth = 200 };
                box.SelectionChanged += (_, _) => { choices[index] = box.SelectedIndex <= 0 ? null : issue.Suggestions[box.SelectedIndex - 1]; Preview(); };
                row.Children.Add(box); _issues.Children.Add(row);
            }
            Preview();
            _status.Text = result.Issues.Length == 0 ? Loc.T("spell.clean") : Loc.T("spell.found", result.Issues.Length);
            _apply.IsEnabled = true;
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Loc.T("spell.failed"); }
        catch { if (!_closed) _status.Text = Loc.T("spell.failed"); }
        finally { _busy = false; if (!_closed) _language.IsEnabled = true; }
    }
}
