using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Languages;

namespace Typlo.App.Views;

internal sealed class PersonalWordDialog : Window
{
    internal readonly TextBox Word;
    private readonly ComboBox _language;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly (string Code, string Name)[] _languages = Loc.Languages.Where(l => PersonalLexicon.LanguageCodes.Contains(l.Code)).ToArray();

    internal PersonalWordDialog(AppController controller, string selected, string? source)
    {
        Title = Loc.T("dictionary.title"); Icon = AppIcon.Create();
        Width = 470; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Word = new TextBox { Text = selected, MaxLength = 128, PlaceholderText = Loc.T("dictionary.word") };
        _language = new ComboBox { ItemsSource = _languages.Select(l => l.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("dictionary.language") };
        // A copied Cyrillic word can be selected while English is active: do not silently label it English.
        var cyrillic = selected.Any(c => c is >= '\u0400' and <= '\u04ff');
        if (source is not null && cyrillic == (source is "ru" or "uk")) _language.SelectedIndex = Array.FindIndex(_languages, l => l.Code == source);
        var cancel = new Button { Content = Loc.T("settings.cancel") };
        var add = new Button { Content = Loc.T("dictionary.add"), Classes = { "accent" } };
        var remove = new Button { Content = Loc.T("dictionary.remove") };
        var never = new Button { Content = Loc.T("exclude.never") };
        never.Click += async (_, _) =>
        {
            add.IsEnabled = remove.IsEnabled = never.IsEnabled = false;
            try { await controller.SetExcludedWordAsync(Word.Text ?? ""); Close(); }
            catch (Exception e) { _error.Text = e.Message; }
            finally { add.IsEnabled = remove.IsEnabled = never.IsEnabled = true; }
        };
        cancel.Click += (_, _) => Close();
        add.Click += async (_, _) => await Save(false);
        remove.Click += async (_, _) => await Save(true);
        Content = new StackPanel { Margin = new Thickness(22), Spacing = 12, Children =
        {
            new TextBlock { Text = Loc.T("dictionary.hint"), TextWrapping = TextWrapping.Wrap },
            Word, _language, never, _error,
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { remove, cancel, add } },
        }};
        async Task Save(bool delete)
        {
            var word = LanguageLexicon.Normalize((Word.Text ?? "").Trim());
            if (!PersonalLexicon.IsWord(word) || _language.SelectedIndex < 0) { _error.Text = Loc.T("dictionary.invalid"); return; }
            add.IsEnabled = remove.IsEnabled = never.IsEnabled = false;
            try { await controller.SetPersonalWordAsync(word, _languages[_language.SelectedIndex].Code, delete); Close(); }
            catch (Exception e) { _error.Text = e.Message; }
            finally { add.IsEnabled = remove.IsEnabled = never.IsEnabled = true; }
        }
    }
}
