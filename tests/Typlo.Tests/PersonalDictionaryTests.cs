using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed class PersonalDictionaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-personal-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ExactFormsPersistNormalizeAndCanBeRemovedWithoutChangingOtherLanguages()
    {
        var path = Path.Combine(_dir, "personal.sqlite3");
        using (var personal = new PersonalLexicon(path))
        {
            Assert.Equal(0, personal.Lookup("ревьюкать")); Assert.False(File.Exists(path));
            personal.Set("РЕВЬЮКАТЬ", "ru"); personal.Set("ревьюкать", "ru"); personal.Set("ревьюкать", "uk");
            Assert.Equal(6, personal.Lookup("ревьюкать"));
            Assert.Equal(0, personal.Lookup("ревьюкаешь")); // no inferred conjugations
            personal.Set("ревьюкать", "ru", remove: true);
        }
        using var reopened = new PersonalLexicon(path);
        Assert.Equal(4, reopened.Lookup("ревьюкать"));
        reopened.Set("ревьюкать", "uk", remove: true);
        Assert.Equal(0, reopened.Lookup("ревьюкать"));
    }
    [Theory]
    [InlineData("two words", "en")]
    [InlineData("sql123", "en")]
    [InlineData("word", "xx")]
    [InlineData("", "ru")]
    [InlineData("x", "en")]
    public void InvalidEntriesAreNotStored(string word, string language)
    {
        using var personal = new PersonalLexicon(Path.Combine(_dir, "personal.sqlite3"));
        Assert.Throws<ArgumentException>(() => personal.Set(word, language));
    }
    [Fact]
    public void PersonalFormsGuideDetectionWithinAllowedLanguagesAndProtectCorrectInput()
    {
        using var pack = new LanguageLexicon(Path.Combine(AppContext.BaseDirectory, "language-data", "words.sqlite3"));
        using var personal = new PersonalLexicon(Path.Combine(_dir, "personal.sqlite3"));
        var detector = new LayoutLanguageDetector(pack, personal);
        var all = new[] { KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian };
        const string word = "ревьюкать";
        var keys = LayoutConverter.Convert(word, KeyboardLayouts.Russian, KeyboardLayouts.English, forceSource: true);
        Assert.Null(detector.Suggest(keys, KeyboardLayouts.English, all));
        personal.Set(word, "ru");
        Assert.Equal("ru", detector.Suggest(keys, KeyboardLayouts.English, all)?.Id);
        Assert.Null(detector.Suggest(keys, KeyboardLayouts.Russian, all));
        Assert.Null(detector.Suggest(keys, KeyboardLayouts.English, [KeyboardLayouts.English, KeyboardLayouts.Ukrainian]));
        personal.Set(word, "ru", remove: true);
        Assert.Null(detector.Suggest(keys, KeyboardLayouts.English, all));
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

public sealed partial class AppControllerTests
{
    [Fact]
    public async Task PersonalWordImmediatelyEnablesAutomaticCorrectionAndRemovalDisablesIt()
    {
        EnableAutomaticCorrection(native: true);
        const string word = "ревьюкать";
        var keys = LayoutConverter.Convert(word, KeyboardLayouts.Russian, KeyboardLayouts.English, forceSource: true);
        await _controller.SetPersonalWordAsync(word, "ru");
        _platform.Type(keys + " "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(word + " ", _platform.Document.Text);
        await _controller.SetPersonalWordAsync(word, "ru", remove: true);
        _platform.ActiveLayoutId = "en";
        _platform.Type(keys + " "); await _controller.AutoCorrectionIdleAsync();
        Assert.EndsWith(keys + " ", _platform.Document.Text);
    }
    [Fact]
    public async Task AddWordShortcutOnlyReadsTheSelectionAndRestoresTheClipboard()
    {
        _platform.Document.Type("ревьюкать");
        _platform.Document.Anchor = 0;
        (string Word, string? Language)? captured = null;
        _controller.PersonalWordRequested += (word, language) => captured = (word, language);
        await _controller.CapturePersonalWordAsync();
        Assert.Equal("ревьюкать", captured?.Word);
        Assert.Equal("ревьюкать", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }
}
