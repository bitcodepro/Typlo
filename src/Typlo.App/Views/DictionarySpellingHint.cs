using Avalonia.Controls;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Layouts;

namespace Typlo.App.Views;

internal sealed class DictionarySpellingHint : StackPanel
{
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _options = new();
    private readonly IReadOnlyList<KeyboardLayout> _layouts;
    private readonly Action<string> _use;
    internal DictionarySpellingHint(IReadOnlyList<KeyboardLayout> layouts, Action<string> use)
    {
        _layouts = layouts; _use = use; Spacing = 8;
        Children.Add(_hint); Children.Add(_options);
    }
    internal void Update(string text, string? language)
    {
        _options.Children.Clear();
        var mismatch = language is not null && PersonalWordSpelling.HasMismatch(text, language);
        IsVisible = mismatch;
        if (!mismatch) return;
        _hint.Text = Loc.T("dictionary.scriptMismatch");
        foreach (var spelling in PersonalWordSpelling.Suggestions(text, language!, _layouts))
        {
            var button = new Button { Name = "DictionaryUseSpelling", MaxWidth = 260,
                Content = new TextBlock { Text = Loc.T("dictionary.useSpelling", spelling), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } };
            button.Click += (_, _) => _use(spelling); _options.Children.Add(button);
        }
    }
}
