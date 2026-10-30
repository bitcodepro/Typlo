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

    [Theory]
    [InlineData("typing")]
    [InlineData("focus")]
    [InlineData("focus-roundtrip")]
    public async Task CanceledPersonalWordCopyDoesNotRequestADialogAndReleasesTheOperation(string interruption)
    {
        const string word = "ревьюкать";
        _platform.Document.Type(word); _platform.Document.Anchor = 0;
        var requested = 0;
        _controller.PersonalWordRequested += (_, _) => requested++;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.AfterCopy = () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var pending = Task.Run(_controller.CapturePersonalWordAsync);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.IsCompleted);
            if (interruption == "typing") _platform.Type("x");
            else
            {
                _platform.Window = 2; _platform.Document.Type("other app");
                if (interruption == "focus-roundtrip") _platform.Window = 1;
            }
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, requested);
            Assert.Equal(interruption == "focus" ? (nint)2 : (nint)1, _platform.Window);
            Assert.Equal(interruption == "typing" ? "x" : word, _platform.DocumentFor(1).Text);
            Assert.Equal(0, _platform.Activations);
            Assert.Equal(0, _platform.SuspendDepth);
            Assert.Equal(0, _platform.PasteCount);
            // Cleanup must leave the gate available for a later, intentional command.
            _platform.AfterCopy = null; _platform.Window = 1;
            _platform.Document.Text = word; _platform.Document.Caret = word.Length; _platform.Document.Anchor = 0;
            await _controller.CapturePersonalWordAsync();
            Assert.Equal(1, requested);
        }
        finally { release.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task FailedPersonalWordCopyDoesNotOpenABlankDialog()
    {
        _platform.Document.Type("word"); _platform.Document.Anchor = 0;
        var requested = false;
        _controller.PersonalWordRequested += (_, _) => requested = true;
        _platform.AfterCopy = () => throw new IOException("copy fixture failure");
        await _controller.CapturePersonalWordAsync();
        Assert.False(requested);
        Assert.Equal(0, _platform.SuspendDepth);
    }
}
