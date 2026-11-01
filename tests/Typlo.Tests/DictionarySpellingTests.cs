using Typlo.App.Services;
using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed class DictionarySpellingTests
{
    [Theory]
    [InlineData("ыйд", "en", false)] [InlineData("ійд", "en", false)]
    [InlineData("sql", "en", true)] [InlineData("SQL", "en", true)]
    [InlineData("привет", "ru", true)] [InlineData("привіт", "uk", true)]
    [InlineData("café", "fr", true)] [InlineData("niño", "es", true)]
    [InlineData("città", "it", true)] [InlineData("straße", "de", true)]
    [InlineData("coração", "pt", true)] [InlineData("hello", "ru", false)]
    public void CorrectSpellingUsesTheSelectedScript(string word, string language, bool valid)
        => Assert.Equal(valid, PersonalWordSpelling.MatchesScript(word, language));

    [Theory]
    [InlineData("ыйд", "sql")] [InlineData("ійд", "sql")] [InlineData("ЫЙД", "SQL")]
    public void ConversionPreviewUsesInstalledPhysicalMapsAndPreservesCase(string input, string expected)
        => Assert.Equal([expected], PersonalWordSpelling.Suggestions(input, "en", KeyboardLayouts.All));

    [Theory]
    [InlineData(",jkm", "боль")] [InlineData(";bnm", "жить")]
    public void SingleWordPreviewRetainsPunctuationKeysAndBatchPreviewNeverDropsForms(string input, string expected)
    {
        Assert.Equal([expected], PersonalWordSpelling.Suggestions(input, "ru", KeyboardLayouts.All));
        Assert.Empty(PersonalWordSpelling.Suggestions(input, "ru", KeyboardLayouts.All, multiple: true));
        Assert.Empty(PersonalWordSpelling.Suggestions(input + ",hello", "ru", KeyboardLayouts.All, multiple: true));
    }

    [Fact]
    public void MultiFormAndMissingLayoutSuggestionsNeverDiscardOrGuessInput()
    {
        Assert.True(PersonalWordSpelling.HasMismatch("hello\nыйд", "en"));
        Assert.Empty(PersonalWordSpelling.Suggestions("hello\nыйд", "en", KeyboardLayouts.All));
        Assert.Empty(PersonalWordSpelling.Suggestions("ыйд", "en", [KeyboardLayouts.English]));
        Assert.Empty(PersonalWordSpelling.Suggestions("ыйд", "en", [KeyboardLayouts.Russian]));
        Assert.False(PersonalWordSpelling.HasMismatch("cafe\u0301", "fr"));
        Assert.False(PersonalWordSpelling.HasMismatch("\ud800", "en"));
    }
}

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData(false, "ru", false)] [InlineData(false, "ru", true)]
    [InlineData(true, "ru", false)] [InlineData(true, "ru", true)]
    [InlineData(false, "uk", false)] [InlineData(false, "uk", true)]
    [InlineData(true, "uk", false)] [InlineData(true, "uk", true)]
    public async Task SqlWorksFromBothCyrillicLayoutsWithCapsLockAndBothReplacementStrategies(bool native, string source, bool capitals)
    {
        EnableAutomaticCorrection(native);
        const string keys = "sql";
        var typed = LayoutConverter.Convert(keys, KeyboardLayouts.English, KeyboardLayouts.Find(source)!, forceSource: true);
        if (capitals) typed = typed.ToUpperInvariant();
        _platform.ActiveLayoutId = source; _platform.TypeAs(keys + " ", typed + " ");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal((capitals ? "SQL " : "sql "), _platform.Document.Text);
    }

    private static KeyboardLayout VocabularyLayout(string language)
    {
        if (KeyboardLayouts.Find(language) is { } known) return known;
        // Representative physical differences for the words exercised below, independent of
        // which native input sources happen to be installed on the test runner.
        var normal = KeyboardLayouts.English.Normal.ToCharArray(); var shifted = KeyboardLayouts.English.Shifted.ToCharArray();
        void Map(char key, char value, char? upper = null)
        {
            var index = KeyboardLayouts.English.Normal.IndexOf(key); normal[index] = value;
            shifted[index] = upper ?? char.ToUpperInvariant(value);
        }
        switch (language)
        {
            case "fr": Map('a', 'q'); Map('q', 'a'); Map('w', 'z'); Map('z', 'w'); Map(';', 'm'); Map('m', ',', '?'); Map('\'', 'ù', '%'); break;
            case "de": Map('y', 'z'); Map('z', 'y'); Map('[', 'ü'); break;
            case "es": Map(';', 'ñ'); break;
            case "it": Map('\'', 'à', '°'); break;
            case "pt": Map(';', 'ç'); break;
        }
        return new(language, language, 0, new(normal), new(shifted), language);
    }

    [Theory]
    [InlineData("fr", "où", false)] [InlineData("fr", "où", true)]
    [InlineData("es", "niño", false)] [InlineData("es", "niño", true)]
    [InlineData("it", "città", false)] [InlineData("it", "città", true)]
    [InlineData("de", "für", false)] [InlineData("de", "für", true)]
    [InlineData("pt", "força", false)] [InlineData("pt", "força", true)]
    public async Task AccentedWordsUseTheirPhysicalLayoutPositions(string language, string expected, bool native)
        => await BundledVocabularyCorrectsWordsInEverySupportedLanguage(language, expected, native);

    [Theory]
    [InlineData("en", "hello", false)] [InlineData("en", "hello", true)] [InlineData("ru", "привет", false)] [InlineData("ru", "привет", true)] [InlineData("uk", "дякую", false)] [InlineData("uk", "дякую", true)]
    [InlineData("fr", "bonjour", false)] [InlineData("fr", "bonjour", true)] [InlineData("es", "gracias", false)] [InlineData("es", "gracias", true)] [InlineData("it", "buongiorno", false)] [InlineData("it", "buongiorno", true)]
    [InlineData("de", "danke", false)] [InlineData("de", "danke", true)] [InlineData("pt", "obrigado", false)] [InlineData("pt", "obrigado", true)]
    public async Task BundledVocabularyCorrectsWordsInEverySupportedLanguage(string language, string expected, bool native)
    {
        EnableAutomaticCorrection(native);
        var target = VocabularyLayout(language); var source = language is "ru" or "uk" ? KeyboardLayouts.English : KeyboardLayouts.Russian;
        _controller.ConfigureDiagnosticLayouts([source, target]);
        var settings = _controller.Settings.Clone(); settings.Layouts = [source.Id, target.Id]; _controller.ApplySettings(settings);
        var keys = LayoutConverter.Convert(expected, target, KeyboardLayouts.English, forceSource: true);
        var typed = LayoutConverter.Convert(keys, KeyboardLayouts.English, source, forceSource: true);
        _platform.ActiveLayoutId = source.Id; _platform.TypeAs(keys + " ", typed + " ");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(expected + " ", _platform.Document.Text);
        Assert.Equal(target.Id, _platform.ActiveLayoutId);
        var status = await _controller.GetPersonalWordStatusAsync(expected, language);
        Assert.True(status.Bundled); Assert.False(status.Personal);
    }

    [Theory]
    [InlineData("en", false)] [InlineData("en", true)] [InlineData("ru", false)] [InlineData("ru", true)] [InlineData("uk", false)] [InlineData("uk", true)] [InlineData("fr", false)] [InlineData("fr", true)]
    [InlineData("es", false)] [InlineData("es", true)] [InlineData("it", false)] [InlineData("it", true)] [InlineData("de", false)] [InlineData("de", true)] [InlineData("pt", false)] [InlineData("pt", true)]
    public async Task ANewPersonalFormImmediatelyEnablesCorrectionForEachSupportedLanguage(string language, bool native)
    {
        EnableAutomaticCorrection(native);
        var target = VocabularyLayout(language); var source = language is "ru" or "uk" ? KeyboardLayouts.English : KeyboardLayouts.Russian;
        _controller.ConfigureDiagnosticLayouts([source, target]);
        var settings = _controller.Settings.Clone(); settings.Layouts = [source.Id, target.Id]; _controller.ApplySettings(settings);
        const string keys = "typloxyz";
        var expected = LayoutConverter.Convert(keys, KeyboardLayouts.English, target, forceSource: true);
        var typed = LayoutConverter.Convert(keys, KeyboardLayouts.English, source, forceSource: true);
        Assert.Equal(new PersonalImportResult(1, 0), await _controller.AddMissingPersonalWordsAsync([new(expected, language)]));
        Assert.Equal(new PersonalImportResult(0, 1), await _controller.AddMissingPersonalWordsAsync([new(expected.ToUpperInvariant(), language)]));
        _platform.ActiveLayoutId = source.Id; _platform.TypeAs(keys + " ", typed + " "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(expected + " ", _platform.Document.Text); Assert.Equal(target.Id, _platform.ActiveLayoutId);
    }

    [Fact]
    public async Task ExistingBundledFormsDoNotCreateAPersonalDatabaseAndMixedBatchesReportDuplicates()
    {
        Assert.Equal(new PersonalWordStatus(false, true), await _controller.GetPersonalWordStatusAsync(" SQL ", "en"));
        Assert.Equal(new PersonalImportResult(0, 3), await _controller.AddMissingPersonalWordsAsync([new("SQL", "en"), new("sql", "en"), new("cafe\u0301", "fr")]));
        Assert.False(File.Exists(Path.Combine(_dir, "personal-words.sqlite3")));
        var added = await _controller.AddMissingPersonalWordsAsync([new("typloxyz", "en"), new("TYPLOXYZ", "en"), new("sql", "en")]);
        Assert.Equal(new PersonalImportResult(1, 2), added);
        Assert.Equal(new PersonalWordStatus(true, false), await _controller.GetPersonalWordStatusAsync("typloxyz", "en"));
        Assert.Single((await _controller.QueryPersonalWordsAsync(new())).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.AddMissingPersonalWordsAsync([new("anothercustomform", "en"), new("two words", "en")]));
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.AddMissingPersonalWordsAsync([new("anothercustomform", "en"), new("ыйд", "en")]));
        Assert.Single((await _controller.QueryPersonalWordsAsync(new())).Items);
    }

    [Fact]
    public async Task ConcurrentAddsStayUniqueAndDifferentLanguagesKeepIndependentMembership()
    {
        var added = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _controller.AddMissingPersonalWordsAsync([new("typloxyz", "en")])));
        Assert.Equal(1, added.Sum(r => r.Added)); Assert.Equal(7, added.Sum(r => r.Skipped));
        Assert.False((await _controller.GetPersonalWordStatusAsync("typloxyz", "fr")).Personal);
        Assert.Equal(new PersonalImportResult(1, 0), await _controller.AddMissingPersonalWordsAsync([new("typloxyz", "fr")]));
        Assert.Equal(2, (await _controller.QueryPersonalWordsAsync(new())).Total);
    }
}
