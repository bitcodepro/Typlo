using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Settings;

namespace Typlo.App.Views;

internal sealed partial class SpellingWindow
{
    private readonly ListBox _pairs = new() { MinHeight = 200 };
    private readonly TextBox _pairError = new() { MaxLength = 64 };
    private readonly TextBox _pairCorrection = new() { MaxLength = 64 };
    private readonly TextBlock _pairStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _pairBusy;
    private string? _editingPair;
    private Control BuildPairs()
    {
        var body = new StackPanel { Margin = new(22), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Loc.T("spell.pairsHint"), TextWrapping = TextWrapping.Wrap });
        _pairs.ItemTemplate = new FuncDataTemplate<SpellingCorrection>((pair, _) => new TextBlock { Text = pair is null ? "" : $"{pair.Error} → {pair.Correction}", Margin = new(6) });
        _pairs.SelectionChanged += (_, _) =>
        {
            if (_pairBusy) return;
            if (_pairs.SelectedItem is SpellingCorrection pair) { _editingPair = pair.Error; _pairError.Text = pair.Error; _pairCorrection.Text = pair.Correction; }
        };
        body.Children.Add(_pairs);
        body.Children.Add(new TextBlock { Text = Loc.T("spell.pairError") }); body.Children.Add(_pairError);
        body.Children.Add(new TextBlock { Text = Loc.T("spell.pairCorrection") }); body.Children.Add(_pairCorrection);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        Button Action(string text, Func<Task> action)
        {
            var button = new Button { Content = Loc.T(text) };
            button.Click += async (_, _) =>
            {
                if (_pairBusy || _busy) return; _pairBusy = true; actions.IsEnabled = _pairs.IsEnabled = _pairError.IsEnabled = _pairCorrection.IsEnabled = false;
                try { await action(); } catch (Exception e) { if (!_closed) _pairStatus.Text = e.Message; }
                finally { _pairBusy = false; if (!_closed) actions.IsEnabled = _pairs.IsEnabled = _pairError.IsEnabled = _pairCorrection.IsEnabled = true; }
            };
            return button;
        }
        actions.Children.Add(Action("spell.pairNew", () => { _editingPair = null; _pairs.SelectedItem = null; _pairError.Text = _pairCorrection.Text = ""; return Task.CompletedTask; }));
        actions.Children.Add(Action("spell.pairSave", async () =>
        {
            await _controller.SetSpellingCorrectionAsync(_pairError.Text ?? "", _pairCorrection.Text ?? "", _editingPair);
            await RefreshPairsAsync(); _pairStatus.Text = Loc.T("spell.pairSaved");
        }));
        actions.Children.Add(Action("history.ctx.delete", async () =>
        {
            if (_editingPair is null) return;
            await _controller.SetSpellingCorrectionAsync(_editingPair, _pairCorrection.Text ?? "", _editingPair, remove: true);
            await RefreshPairsAsync(); _pairStatus.Text = Loc.T("manage.saved");
        }));
        actions.Children.Add(Action("dictionary.title", () => { DictionaryRequested?.Invoke(); return Task.CompletedTask; }));
        body.Children.Add(actions); body.Children.Add(_pairStatus);
        return new ScrollViewer { Content = body };
    }
    private async Task RefreshPairsAsync()
    {
        var busy = _pairBusy; _pairBusy = true;
        _pairs.IsEnabled = _pairError.IsEnabled = _pairCorrection.IsEnabled = false;
        try
        {
            var pairs = await _controller.GetSpellingCorrectionsAsync(); if (_closed) return;
            _editingPair = null; _pairs.ItemsSource = pairs.OrderBy(p => p.Error).ToArray();
            _pairError.Text = _pairCorrection.Text = "";
        }
        finally { _pairBusy = busy; if (!_closed && !busy) _pairs.IsEnabled = _pairError.IsEnabled = _pairCorrection.IsEnabled = true; }
    }
}
