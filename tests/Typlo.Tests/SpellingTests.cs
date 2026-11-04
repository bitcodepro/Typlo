using Typlo.App.Services;

namespace Typlo.Tests;

public sealed class SpellingFixture
{
    internal SpellingEngine Engine { get; } = new(Path.Combine(AppContext.BaseDirectory, "spelling"));
}

public sealed class SpellingTests(SpellingFixture fixture) : IClassFixture<SpellingFixture>
{
    [Theory]
    [InlineData("en", "speling", "spelling")]
    [InlineData("ru", "превет", "привет")]
    [InlineData("uk", "привітт", "привіт")]
    public void ActualDictionariesSuggestCorrectSpelling(string language, string typo, string correct)
    {
        var issue = Assert.Single(fixture.Engine.Check(new(typo, language)).Issues);
        Assert.Contains(correct, issue.Suggestions);
        Assert.Equal(typo, issue.Word);
    }
    [Theory]
    [InlineData("en", "spelling words correctly")]
    [InlineData("ru", "пишу написали словами")]
    [InlineData("uk", "пишу написали словами привіт українська")]
    public void InflectedFormsAreAccepted(string language, string text) => Assert.Empty(fixture.Engine.Check(new(text, language)).Issues);
    [Fact]
    public void MixedLanguageParagraphPreservesCorrectWordsAndFindsTypos()
    {
        var result = fixture.Engine.Check(new("Hello привіт привет. speling превет привітт"));
        Assert.Equal(new[] { "speling", "превет", "привітт" }, result.Issues.Select(i => i.Word));
        Assert.Contains("spelling", result.Issues[0].Suggestions);
        Assert.Contains("привет", result.Issues[1].Suggestions);
        Assert.Contains("привіт", result.Issues[2].Suggestions);
    }
    [Fact]
    public void PersonalTermsAndExclusionsAreNotSuggestedForReplacement()
        => Assert.Empty(fixture.Engine.Check(new("Typloxyz таска SQL", Ignored: ["typloxyz", "таска", "sql"])).Issues);
    [Fact]
    public void UncertainCyrillicSuggestionsIncludeBothLanguages()
    {
        const string word = "робта";
        var auto = Assert.Single(fixture.Engine.Check(new(word)).Issues);
        foreach (var language in new[] { "ru", "uk" })
        {
            var explicitLanguage = Assert.Single(fixture.Engine.Check(new(word, language)).Issues);
            Assert.NotEmpty(explicitLanguage.Suggestions);
            Assert.Contains(explicitLanguage.Suggestions[0], auto.Suggestions);
        }
    }
    [Theory]
    [InlineData("https://speling.com user@speling.com C:\\speling\\file.txt speling_name $speling --speling")]
    [InlineData("`speling speling` ```\nspeling speling\n``` Console.WriteLine foo123")]
    public void TechnicalInputIsProtected(string text) => Assert.Empty(fixture.Engine.Check(new(text)).Issues);
    [Fact]
    public void CaseAndWhitespaceArePreservedAndNothingIsChangedWithoutAChoice()
    {
        const string text = "SPELING\r\n  speling!";
        var result = fixture.Engine.Check(new(text, "en"));
        Assert.Equal(2, result.Issues.Length);
        Assert.Contains("SPELLING", result.Issues[0].Suggestions);
        Assert.Equal(text, SpellingText.Apply(text, result.Issues, new string?[2]));
        Assert.Equal("SPELLING\r\n  spelling!", SpellingText.Apply(text, result.Issues, ["SPELLING", "spelling"]));
        Assert.Throws<ArgumentException>(() => SpellingText.Apply("different", result.Issues, ["SPELLING", "spelling"]));
        Assert.Throws<ArgumentException>(() => SpellingText.Apply(text, result.Issues, ["invented", null]));
    }
    [Fact]
    public void LimitsAndUnknownLanguagesFailBeforeLoading()
    {
        var empty = new SpellingEngine("not-a-directory");
        Assert.Throws<ArgumentException>(() => empty.Check(new(new string('x', SpellingText.MaxCharacters + 1))));
        Assert.Throws<ArgumentException>(() => empty.Check(new("test", "fr")));
        Assert.Throws<ArgumentException>(() => empty.Check(new(string.Concat(Enumerable.Repeat("a ", SpellingText.MaxWords + 1)).Replace("a ", "aa "))));
    }
    [Fact]
    public void PackMissingFailsWithoutReturningPartialSuggestions()
        => Assert.ThrowsAny<IOException>(() => new SpellingEngine("not-a-directory").Check(new("speling", "en")));
}

public sealed partial class AppControllerTests
{
    private async Task<long> CaptureSpellingSelection(string text)
    {
        _platform.Document.Type(text); _platform.Document.SelectAll();
        long id = 0; void Capture(long value) => id = value;
        _controller.SpellingRequested += Capture;
        try { await _controller.CaptureSpellingAsync(); }
        finally { _controller.SpellingRequested -= Capture; }
        Assert.NotEqual(0, id); return id;
    }
    [Fact]
    public async Task SpellingAppliesOnlyCapturedSelectionAndSupportsUndo()
    {
        var id = await CaptureSpellingSelection("speling\r\nпревет");
        Assert.Equal("original clipboard", _platform.ClipboardText);
        _platform.Window = 2; // preview window
        Assert.True(await _controller.ApplySpellingAsync(id, "spelling\r\nпривет"));
        Assert.Equal("spelling\r\nпривет", _platform.DocumentFor(1).Text);
        Assert.Empty(_platform.LayoutSwitches);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("speling\r\nпревет", _platform.DocumentFor(1).Text);
        Assert.False(await _controller.ApplySpellingAsync(id, "again"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpellingCancellationDuringCaptureNeverOpensPreview(bool focus)
    {
        _platform.Document.Type("speling"); _platform.Document.SelectAll();
        var opened = false; _controller.SpellingRequested += _ => opened = true;
        _platform.AfterCopy = () => { _platform.AfterCopy = null; if (focus) _platform.Window = 2; else _platform.Type("x"); };
        await _controller.CaptureSpellingAsync();
        Assert.False(opened); Assert.Equal(0, _platform.PasteCount);
    }
    [Fact]
    public async Task ChangedSelectionOrInputDuringApplyNeverWrites()
    {
        var id = await CaptureSpellingSelection("speling");
        _platform.Document.Type("different"); _platform.Document.SelectAll();
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal("different", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        id = await CaptureSpellingSelection(" speling");
        _platform.AfterCopy = () => { _platform.AfterCopy = null; _platform.Type("x"); };
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal(0, _platform.PasteCount);
    }
    [Fact]
    public async Task CancelAndStalePreviewCannotApplyAnotherSession()
    {
        var first = await CaptureSpellingSelection("speling");
        var second = await CaptureSpellingSelection("speling");
        await _controller.CancelSpellingAsync(first);
        Assert.False(await _controller.ApplySpellingAsync(first, "spelling"));
        Assert.NotNull(await _controller.GetSpellingSelectionAsync(second));
        await _controller.CancelSpellingAsync(second);
        Assert.Null(await _controller.GetSpellingSelectionAsync(second));
        Assert.False(await _controller.ApplySpellingAsync(second, "spelling"));
    }
    [Fact]
    public async Task SourceFieldChangeAndForeignClipboardBlockSpellingApply()
    {
        var id = await CaptureSpellingSelection("speling");
        _platform.FocusedInputElement++;
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal("speling", _platform.Document.Text);
        id = await CaptureSpellingSelection("speling");
        _platform.AfterCopy = () => { _platform.AfterCopy = null; _platform.ExternalCopy("foreign"); _platform.ClipboardOwner = 99; };
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal("foreign", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }
    [Fact]
    public async Task ClosingPreviewDuringApplyCopyCancelsBeforeWriting()
    {
        var id = await CaptureSpellingSelection("speling");
        _platform.AfterCopy = () => { _platform.AfterCopy = null; _controller.CancelSpellingAsync(id).GetAwaiter().GetResult(); };
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal("speling", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }
    [Fact]
    public async Task SelectionIncludesPersonalWordsExclusionsAndBuiltInTerms()
    {
        await _controller.SetPersonalWordAsync("typloxyz", "en");
        await _controller.SetExcludedWordAsync("someword");
        var id = await CaptureSpellingSelection("typloxyz someword таска SQL");
        var selection = await _controller.GetSpellingSelectionAsync(id);
        Assert.Equal(new[] { "typloxyz", "someword", "таска", "SQL" }, selection!.Ignored);
    }
    [Fact]
    public async Task InputInSourceRevokesPreviewEvenIfTheSameWordIsSelectedAgain()
    {
        var id = await CaptureSpellingSelection("speling");
        _platform.Type("speling"); _platform.Document.SelectAll();
        Assert.False(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal(0, _platform.PasteCount);
    }
    [Fact]
    public async Task InputInPreviewDoesNotRevokeUnchangedSource()
    {
        var id = await CaptureSpellingSelection("speling");
        _platform.Window = 2; _platform.Type("preview input");
        Assert.True(await _controller.ApplySpellingAsync(id, "spelling"));
        Assert.Equal("spelling", _platform.DocumentFor(1).Text);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpellingEmptyOrOversizedSelectionShowsInstructionsWithoutApplying(bool oversized)
    {
        var text = oversized ? new string('x', SpellingText.MaxCharacters + 1) : "";
        var id = await CaptureSpellingSelection(text);
        var selection = await _controller.GetSpellingSelectionAsync(id);
        Assert.Equal(oversized ? "spell.limit" : "spell.selection", selection!.Notice);
        Assert.Empty(selection.Text);
        Assert.False(await _controller.ApplySpellingAsync(id, "changed"));
        Assert.Equal(text, _platform.Document.Text);
    }
}
