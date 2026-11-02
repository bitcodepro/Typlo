using Typlo.Core.Languages;
using Typlo.Core.Layouts;
using Typlo.App.Services;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData("git", "пше", "ru")]
    [InlineData("Git", "Пше", "ru")]
    [InlineData("GIT", "ПШЕ", "ru")]
    [InlineData("git", "пше", "uk")]
    public async Task TeamsGitCorrectsAfterSpaceWithoutPersonalEntry(string keys, string rendered, string source)
    {
        EnableAutomaticCorrection(); _platform.ActiveLayoutId = source;
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.SupportsUnicodeTextReplacement = true; _platform.CopySuffix = "\r\n";
        _platform.Document.Type("готовое "); _platform.TypeAs(keys + " ", rendered + " ");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("готовое " + keys + " ", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task AddingWeakBundledSpellingCreatesExactlyOnePersonalPriority()
    {
        EnableAutomaticCorrection(); _platform.ActiveLayoutId = "ru";
        Assert.True((await _controller.GetPersonalWordStatusAsync("abacus", "en")).NeedsPriority);
        var added = await _controller.AddMissingPersonalWordsAsync([new("abacus", "en"), new("ABACUS", "en")]);
        Assert.Equal(1, added.Added); Assert.Equal(1, added.Skipped); Assert.Equal(1, added.Prioritized);
        _platform.TypeAs("abacus ", "фифсгы "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("abacus ", _platform.Document.Text);
        Assert.Equal(0, (await _controller.AddMissingPersonalWordsAsync([new("abacus", "en")])).Added);
    }

    [Theory]
    [InlineData(false, " ")]
    [InlineData(false, "  ")]
    [InlineData(false, "\u00a0")]
    [InlineData(false, "\u202f")]
    [InlineData(true, " ")]
    [InlineData(true, "  ")]
    [InlineData(true, "\u00a0")]
    public async Task UnknownWordThenSpacesStillConvertsManually(bool native, string spaces)
    {
        EnableAutomaticCorrection(native); _platform.ActiveLayoutId = "ru";
        _platform.ApplicationProcess = _ => "ms-teams"; _platform.SupportsUnicodeTextReplacement = true;
        _platform.CopySuffix = "\r\n";
        _platform.Document.Type("готовое ");
        var text = LayoutConverter.Convert("typloxyz", KeyboardLayouts.English, KeyboardLayouts.Russian, forceSource: true);
        _platform.TypeAs("typloxyz" + new string(' ', spaces.Length), text + spaces);
        await _controller.AutoCorrectionIdleAsync();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("готовое typloxyz" + spaces, _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("готовое " + text + spaces, _platform.Document.Text);
    }
}

public sealed class GitSpaceRangeTests
{
    [Fact]
    public void SpaceAfterThinkingDoesNotDiscardTheJustTypedWord()
    {
        var input = new RecentTyping();
        input.Record('g', 1, 100, 1); input.Record('i', 1, 150, 2); input.Record('t', 1, 200, 3);
        input.Record(' ', 1, 200 + RecentTyping.PauseMilliseconds, 4);
        Assert.Equal("git ", input.Capture(1)?.Keys);
    }
}
