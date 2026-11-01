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
    private bool _busy;
    private readonly TextBlock _error = new() { Name = "PersonalWordMessage", TextWrapping = TextWrapping.Wrap };
    private readonly (string Code, string Name)[] _languages = Loc.Languages.Where(l => PersonalLexicon.LanguageCodes.Contains(l.Code)).ToArray();

    internal PersonalWordDialog(AppController controller, string selected, string? source)
    {
        Title = Loc.T("dictionary.title"); Icon = AppIcon.Create();
        Width = 470; MaxHeight = 720; SizeToContent = SizeToContent.Height; CanResize = false;
        Opened += (_, _) => { if (Screens.ScreenFromWindow(this) is { } screen) MaxHeight = Math.Max(280, screen.WorkingArea.Height / screen.Scaling - 40); };
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Word = new TextBox { Name = "PersonalWord", Text = selected, MaxLength = 128, PlaceholderText = Loc.T("dictionary.word") };
        _language = new ComboBox { Name = "PersonalWordLanguage", ItemsSource = _languages.Select(l => l.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("dictionary.language") };
        // A copied Cyrillic word can be selected while English is active: do not silently label it English.
        var cyrillic = selected.Any(c => c is >= '\u0400' and <= '\u04ff');
        if (source is not null && cyrillic == (source is "ru" or "uk")) _language.SelectedIndex = Array.FindIndex(_languages, l => l.Code == source);
        var cancel = new Button { Content = Loc.T("settings.cancel") };
        var add = new Button { Name = "PersonalWordAdd", Content = Loc.T("dictionary.add"), Classes = { "accent" } };
        var remove = new Button { Content = Loc.T("dictionary.remove") };
        var never = new Button { Content = Loc.T("exclude.never") };
        var priority = new Button { Name = "PersonalWordPriority", Content = Loc.T("dictionary.addPriority"), IsVisible = false };
        PersonalWord? priorityFor = null;
        PersonalWordStatus? known = null;
        var spelling = new DictionarySpellingHint(controller.AvailableLayouts, value => Word.Text = value);
        void RefreshSpelling()
        {
            var language = _language.SelectedIndex < 0 ? null : _languages[_language.SelectedIndex].Code;
            spelling.Update(Word.Text ?? "", language);
            add.IsEnabled = !_busy && known is null && language is not null && !PersonalWordSpelling.HasMismatch(Word.Text ?? "", language);
            remove.IsEnabled = !_busy && known?.Personal != false;
            never.IsEnabled = !_busy;
            Word.IsEnabled = _language.IsEnabled = spelling.IsEnabled = !_busy;
            priority.IsVisible = priorityFor is not null;
            priority.IsEnabled = !_busy;
        }
        Word.TextChanged += (_, _) => { known = null; priorityFor = null; _error.Text = ""; RefreshSpelling(); };
        _language.SelectionChanged += (_, _) => { known = null; priorityFor = null; _error.Text = ""; RefreshSpelling(); };
        never.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; RefreshSpelling(); remove.IsEnabled = never.IsEnabled = false;
            try { await controller.SetExcludedWordAsync(Word.Text ?? ""); Close(); }
            catch (Exception e) { _error.Text = e.Message; }
            finally { _busy = false; remove.IsEnabled = never.IsEnabled = true; RefreshSpelling(); }
        };
        cancel.Click += (_, _) => Close();
        add.Click += async (_, _) => await Save(false);
        remove.Click += async (_, _) => await Save(true);
        priority.Click += async (_, _) => await Save(false, boost: true);
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(22), Spacing = 12, Children =
        {
            new TextBlock { Text = Loc.T("dictionary.hint"), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = Loc.T("dictionary.word") }, Word,
            new TextBlock { Text = Loc.T("dictionary.language") }, _language, spelling,
            new TextBlock { Text = Loc.T("dictionary.example"), TextWrapping = TextWrapping.Wrap }, never, _error,
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { remove, cancel, add, priority } },
        }}};
        RefreshSpelling();
        async Task Save(bool delete, bool boost = false)
        {
            if (_busy) return;
            if (!delete && !boost && known is not null) return;
            string word;
            try { word = LanguageLexicon.Normalize((Word.Text ?? "").Trim()); }
            catch (ArgumentException) { _error.Text = Loc.T("dictionary.invalid"); return; }
            if (!PersonalLexicon.IsWord(word) || _language.SelectedIndex < 0) { _error.Text = Loc.T("dictionary.invalid"); return; }
            if (!delete)
            {
                try { PersonalWordSpelling.RequireCorrectScript(word, _languages[_language.SelectedIndex].Code); }
                catch (ArgumentException e) { _error.Text = e.Message; return; }
            }
            var language = _languages[_language.SelectedIndex].Code;
            if (boost && priorityFor != new PersonalWord(word, language)) return;
            _busy = true; RefreshSpelling(); remove.IsEnabled = never.IsEnabled = false;
            try
            {
                if (delete || boost) await controller.SetPersonalWordAsync(word, language, remove: delete);
                else if ((await controller.AddMissingPersonalWordsAsync([new(word, language)])).Added == 0)
                {
                    var status = await controller.GetPersonalWordStatusAsync(word, language);
                    known = status.Personal || status.Bundled == true ? status : null;
                    priorityFor = !status.Personal && status.Bundled == true ? new(word, language) : null;
                    _error.Text = status.Personal ? Loc.T("dictionary.existsPersonal", word) :
                        status.Bundled == true ? Loc.T("dictionary.existsBundled", word) : Loc.T("manage.changed");
                    return;
                }
                Close();
            }
            catch (Exception e) { _error.Text = e.Message; }
            finally { _busy = false; remove.IsEnabled = never.IsEnabled = true; RefreshSpelling(); }
        }
    }
}
