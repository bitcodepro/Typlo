using Typlo.App.Services;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    private static readonly Lazy<SpellingEngine> QuickEngine = new(() => new(Path.Combine(AppContext.BaseDirectory, "spelling")));
    private void PrepareQuick(string text)
    {
        _platform.Document.Type(text); _platform.Document.SelectAll();
        _controller.QuickSpellingCheck = (request, _) => Task.FromResult(QuickEngine.Value.Check(request));
    }
    [Theory]
    [InlineData("speling", "spelling")]
    [InlineData("(Превет)", "(Привет)")]
    [InlineData("Привітт", "Привіт")]
    public async Task QuickSpellingUsesRealDictionariesWithoutPreviewOrFocusActivation(string text, string expected)
    {
        PrepareQuick(text); var windows = 0; _controller.SpellingRequested += _ => windows++;
        _platform.PressHotkey("Ctrl+Shift+L");
        await _controller.QuickSpellingIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, _platform.Document.Text); Assert.Equal(0, windows); Assert.Equal(0, _platform.Activations);
        Assert.Empty(_platform.LayoutSwitches); Assert.Equal("original clipboard", _platform.ClipboardText);
        await _controller.UndoLastTransformAsync(); Assert.Equal(text, _platform.Document.Text);
    }
    [Fact]
    public async Task ReviewShortcutAloneOpensPreviewAndDoesNotReplace()
    {
        PrepareQuick("speling"); var opened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.SpellingRequested += id => opened.TrySetResult(id);
        _platform.PressHotkey("Ctrl+Shift+P"); var id = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("speling", (await _controller.GetSpellingSelectionAsync(id))!.Text);
        Assert.Equal("speling", _platform.Document.Text); Assert.Equal(0, _platform.PasteCount);
    }
    [Theory]
    [InlineData("typing")]
    [InlineData("focus")]
    [InlineData("field")]
    [InlineData("clipboard")]
    public async Task QuickSpellingAbortsWhenContextChangesDuringWorker(string change)
    {
        PrepareQuick("speling"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.QuickSpellingCheck = async (_, token) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { finished.TrySetResult(); } return new([], 0, 0, 0, 0); };
        var pending = _controller.CorrectSpellingAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (change == "typing") _platform.Type("x");
        else if (change == "focus") _platform.Window = 2;
        else if (change == "field") _platform.FocusedInputElement++;
        else _platform.ExternalCopy("foreign");
        await pending.WaitAsync(TimeSpan.FromSeconds(3)); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(change == "typing" ? "x" : "speling", _platform.DocumentFor(1).Text);
        Assert.Equal(0, _platform.PasteCount); Assert.Equal(0, _platform.Activations);
    }
    [Fact]
    public async Task ManualConversionCancelsQuickWorkerWithoutDeadlocking()
    {
        PrepareQuick("rfr"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.QuickSpellingCheck = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new([], 0, 0, 0, 0); };
        var pending = _controller.CorrectSpellingAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await _controller.ConvertSelectionAsync(Typlo.Core.Layouts.KeyboardLayouts.Russian).WaitAsync(TimeSpan.FromSeconds(3));
        await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal("как", _platform.Document.Text);
    }
    [Fact]
    public async Task RepeatedQuickCommandCancelsOldWorkerAndOnlyLatestApplies()
    {
        PrepareQuick("speling"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        _controller.QuickSpellingCheck = async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return QuickEngine.Value.Check(request);
        };
        var first = _controller.CorrectSpellingAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = _controller.CorrectSpellingAsync(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("spelling", _platform.Document.Text); Assert.Equal(1, _platform.PasteCount);
    }
    [Fact]
    public async Task PersonalPairsOverrideDictionaryAndExceptionsStillWin()
    {
        await _controller.SetSpellingCorrectionAsync("превет", "привет");
        await _controller.SetSpellingCorrectionAsync("hello", "world");
        await _controller.SetExcludedWordAsync("hello");
        PrepareQuick("превет hello"); await _controller.CorrectSpellingAsync();
        Assert.Equal("привет hello", _platform.Document.Text);
    }
    [Fact]
    public async Task PairCrudDoesNotOverwriteOtherSettingsOrRestoreStaleEntries()
    {
        await _controller.SetSpellingCorrectionAsync("превет", "привет");
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.SetSpellingCorrectionAsync("ПРЕВЕТ", "привет"));
        var settings = _controller.Settings.Clone(); settings.ClipboardHotkey = "F18"; _controller.ApplySettings(settings);
        await _controller.SetSpellingCorrectionAsync("превет", "привіт", "превет");
        Assert.Equal("F18", _controller.Settings.ClipboardHotkey);
        Assert.Equal("привіт", Assert.Single(await _controller.GetSpellingCorrectionsAsync()).Correction);
        await _controller.SetSpellingCorrectionAsync("превет", "", "превет", remove: true);
        await Assert.ThrowsAsync<ArgumentException>(() => _controller.SetSpellingCorrectionAsync("превет", "привет", "превет"));
        Assert.Empty(await _controller.GetSpellingCorrectionsAsync());
    }
    [Fact]
    public async Task CorrectionsAndBothShortcutsSurviveFullBackupRestore()
    {
        await _controller.SetSpellingCorrectionAsync("превет", "привет");
        var file = Path.Combine(_dir, "quick.typlo"); await _controller.ExportBackupAsync(file); var info = await _controller.PreviewBackupAsync(file);
        await _controller.SetSpellingCorrectionAsync("превет", "", "превет", remove: true);
        await _controller.RestoreBackupAsync(file, info.Hash);
        Assert.Equal("привет", Assert.Single(await _controller.GetSpellingCorrectionsAsync()).Correction);
        Assert.Equal("Ctrl+Shift+L", _controller.Settings.SpellingHotkey); Assert.Equal("Ctrl+Shift+P", _controller.Settings.SpellingReviewHotkey);
    }
    [Fact]
    public async Task QuickSpellingDoesNothingWithoutReliableTargetOrSelection()
    {
        PrepareQuick("speling"); _platform.SupportsForegroundNotifications = false;
        _controller.QuickSpellingCheck = (_, _) => throw new InvalidOperationException("must not start worker");
        await _controller.CorrectSpellingAsync(); Assert.Equal("speling", _platform.Document.Text); Assert.Equal(0, _platform.SuspendCalls);
    }
    [Fact]
    public async Task DisposalAndUpdateDrainQuickWorkerBeforeReplacingFiles()
    {
        PrepareQuick("speling"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        _controller.QuickSpellingCheck = async (_, token) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { closed = true; } return new([], 0, 0, 0, 0); };
        var pending = _controller.CorrectSpellingAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await _controller.ReplaceUpdateFilesAsync(() => Assert.True(closed)).WaitAsync(TimeSpan.FromSeconds(3));
        await pending; Assert.Equal("speling", _platform.Document.Text);
    }
    [Fact]
    public async Task QuickSpellingDoesNotDeadlockWithSpaceAutocorrection()
    {
        EnableAutomaticCorrection(); _platform.Type("rfr ");
        _platform.Document.SelectAll();
        _controller.QuickSpellingCheck = (_, _) => Task.FromResult(new SpellingResult([], 0, 0, 0, 0));
        await _controller.CorrectSpellingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await _controller.AutoCorrectionIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("rfr ", _platform.Document.Text);
    }
    [Fact]
    public async Task ManualCommandAfterQuickPasteWaitsForConfirmedRange()
    {
        PrepareQuick("speling"); Task? manual = null;
        _platform.AfterPaste = () => { _platform.AfterPaste = null; manual = _controller.ConvertSelectionAsync(Typlo.Core.Layouts.KeyboardLayouts.Russian); };
        await _controller.CorrectSpellingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(manual); await manual!.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = Typlo.Core.Layouts.LayoutConverter.Convert("spelling", Typlo.Core.Layouts.KeyboardLayouts.English, Typlo.Core.Layouts.KeyboardLayouts.Russian, forceSource: true);
        Assert.Equal(expected, _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }
}

public sealed class PersonalCorrectionTests
{
    [Fact]
    public void PersonalCorrectionWorksWithoutDictionaryFilesAndPreservesCase()
    {
        var engine = new SpellingEngine("missing-spelling-directory");
        var result = engine.Check(new("hello Hello HELLO", Corrections: [new("hello", "world")]));
        Assert.Equal("world World WORLD", SpellingText.Apply("hello Hello HELLO", result.Issues, result.Issues.Select(i => i.Suggestions[0]).ToArray()));
        Assert.Empty(engine.Check(new("hello", Corrections: [new("hello", "world")], Excluded: ["HELLO"])).Issues);
    }
    [Fact]
    public void PairsAreSinglePassAndDuplicateOrInvalidImportsAreRejected()
    {
        var engine = new SpellingEngine("missing-spelling-directory");
        var result = engine.Check(new("hello", Corrections: [new("hello", "world"), new("world", "other")]));
        Assert.Equal("world", Assert.Single(result.Issues).Suggestions[0]);
        var settings = new AppSettings { SpellingCorrections = [new("hello", "world"), new("HELLO", "other")] };
        Assert.Contains(settings.Validate(), p => p.Kind == SettingsProblemKind.InvalidSpellingCorrections);
        Assert.Throws<ArgumentException>(() => engine.Check(new("hello", Corrections: settings.SpellingCorrections.ToArray())));
        settings.SpellingCorrections = [new("hello", "two words")];
        Assert.Contains(settings.Validate(), p => p.Kind == SettingsProblemKind.InvalidSpellingCorrections);
    }
}
