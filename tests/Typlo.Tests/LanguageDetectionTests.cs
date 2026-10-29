using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed class LanguageDetectionTests : IDisposable
{
    private readonly LanguageLexicon _lexicon = new(Path.Combine(AppContext.BaseDirectory, "language-data", "words.sqlite3"));
    private static KeyboardLayout Latin(string code) => new(code, code, 0, KeyboardLayouts.English.Normal, KeyboardLayouts.English.Shifted, code);
    private static readonly KeyboardLayout[] All = [KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian,
        Latin("fr"), Latin("es"), Latin("it"), Latin("de"), Latin("pt")];
    [Theory]
    [InlineData("ghb", "en", "ru")]
    [InlineData("ghbdtn", "en", "ru")]
    [InlineData("lzre.", "en", "uk")]
    [InlineData("hello", "ru", "en")]
    [InlineData("bonjour", "en", "fr")]
    [InlineData("gracias", "en", "es")]
    [InlineData("buongiorno", "en", "it")]
    [InlineData("danke", "en", "de")]
    [InlineData("obrigado", "en", "pt")]
    [InlineData("htdm.dftim", "en", "ru")]
    [InlineData("rjvsnbnb", "en", "uk")]
    public void ExactVocabularyChoosesTheIntendedLayout(string keys, string source, string expected)
    {
        Assert.Equal(expected, new LayoutLanguageDetector(_lexicon).Suggest(keys, All.Single(l => l.Id == source), All)?.Id);
    }
    [Theory]
    [InlineData("hello")]
    [InlineData("chat")]
    [InlineData("xyzabcxqz")]
    [InlineData("user@example.com")]
    [InlineData("ghb_123")]
    public void ValidAmbiguousUnknownAndIdentifierInputsStayUnchanged(string keys) =>
        Assert.Null(new LayoutLanguageDetector(_lexicon).Suggest(keys, KeyboardLayouts.English, All));
    [Fact]
    public void ApplicationLanguagesRestrictSuggestions()
    {
        var detector = new LayoutLanguageDetector(_lexicon);
        Assert.Equal("uk", detector.Suggest("ghb", KeyboardLayouts.English, [KeyboardLayouts.English, KeyboardLayouts.Ukrainian])?.Id);
        Assert.Null(detector.Suggest("ghb", KeyboardLayouts.English, [KeyboardLayouts.English]));
        Assert.Null(detector.Suggest("ghb", Latin("el"), All)); // no evidence to judge a current Greek word
    }
    [Fact]
    public void DiskReaderOpensLazilyNormalizesAndRetainsManualMembership()
    {
        Assert.False(_lexicon.IsOpen);
        var a = _lexicon.Lookup("CAFÉ"); var b = _lexicon.Lookup("cafe\u0301");
        Assert.Equal(a!.Value.CorpusWeights, b!.Value.CorpusWeights);
        Assert.True(_lexicon.IsOpen);
        Assert.Equal(2, _lexicon.Lookup("ревьюваешь")!.Value.SupplementMask);
        Assert.Equal(4, _lexicon.Lookup("комітити")!.Value.SupplementMask);
        Assert.Null(_lexicon.Lookup("xqzvnotaword987"));
        Assert.Null(_lexicon.Lookup("\ud800"));
    }
    public void Dispose() => _lexicon.Dispose();
}
