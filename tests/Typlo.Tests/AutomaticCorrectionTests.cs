using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData("yfgbib", "yfgbib", "en", "напиши")]
    [InlineData("sql", "ыйд", "ru", "sql")]
    [InlineData("sql", "ійд", "uk", "sql")]
    public async Task ReportedWordsAreCorrectedAfterSpace(string keys, string typed, string source, string expected)
    {
        EnableAutomaticCorrection(native: true);
        _platform.ActiveLayoutId = source;
        _platform.TypeAs(keys + " ", typed + " ");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(expected + " ", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReleasesDictionaryAndCorrectionRecoversAfterReplacementOrFailure(bool fail)
    {
        EnableAutomaticCorrection(native: true);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.True(_controller.DictionaryIsOpen);
        void Replace()
        {
            Assert.False(_controller.DictionaryIsOpen);
            _platform.ActiveLayoutId = "en";
            _platform.Type("ghb ");
            if (fail) throw new IOException("Update fixture: locked destination");
        }
        if (fail) await Assert.ThrowsAsync<IOException>(() => _controller.ReplaceUpdateFilesAsync(Replace));
        else await _controller.ReplaceUpdateFilesAsync(Replace);
        await _controller.AutoCorrectionIdleAsync();
        Assert.False(_controller.DictionaryIsOpen);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.True(_controller.DictionaryIsOpen);
        Assert.EndsWith("при ", _platform.Document.Text);
    }

    private void EnableAutomaticCorrection(bool native = false)
    {
        _platform.SupportsAutomaticWordCorrection = true;
        _platform.SupportsTextRangeBatch = true;
        _platform.ApplicationProcess = _ => "editor";
        _platform.ActiveLayoutId = "en";
        _controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = true; settings.Layouts = ["en", "ru", "uk"];
        _controller.ApplySettings(settings);
        if (native) EnableDirect();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpaceCorrectsOnlyTheTrackedWordAndUndoWorks(bool native)
    {
        EnableAutomaticCorrection(native);
        _platform.Document.Type("готовое ");
        _platform.Type("ghb ");
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("готовое при ", _platform.Document.Text);
        Assert.Equal("ru", _platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("готовое ghb ", _platform.Document.Text);
    }
    [Fact]
    public async Task AutomaticCorrectionRespectsPerApplicationLanguagesAndEmptyRuleUsesGlobalCycle()
    {
        EnableAutomaticCorrection();
        _platform.ApplicationProcess = window => window == 1 ? "ms-teams" : "chrome";
        SetApplicationRules(new() { ProcessName = "ms-teams", Layouts = ["en", "uk"] }, new() { ProcessName = "chrome", Layouts = [] });
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ", _platform.Document.Text); Assert.Equal("uk", _platform.ActiveLayoutId);
        _platform.Window = 2; _platform.ActiveLayoutId = "en";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ", _platform.Document.Text); Assert.Equal("ru", _platform.ActiveLayoutId);
    }
    [Fact]
    public async Task ContinuingTypingOrDisablingAutomaticCorrectionDoesNotOpenDictionaryOrReplaceText()
    {
        EnableAutomaticCorrection();
        _platform.Type("ghb x"); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb x", _platform.Document.Text); Assert.False(_controller.DictionaryIsOpen);
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false; _controller.ApplySettings(settings);
        _platform.Type(" ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb x ghb ", _platform.Document.Text); Assert.False(_controller.DictionaryIsOpen);
    }
    [Fact]
    public async Task AutomaticCorrectionWorksWhenManualRecentInputOptionIsDisabled()
    {
        EnableAutomaticCorrection(true);
        var settings = _controller.Settings.Clone(); settings.ConvertLastWord = false; _controller.ApplySettings(settings);
        _platform.Type("htdm.dftim "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ревьюваешь ", _platform.Document.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgrammaticNativeFieldChangeStopsAutomaticCorrectionWithoutTyping(bool duringWrite)
    {
        EnableAutomaticCorrection(true);
        _platform.Type("ghb ");
        if (duringWrite) _platform.AfterWrite = text =>
        {
            if (text == "при ") _platform.FocusedInputElement = 22;
        };
        else _platform.FocusedInputElement = 22;
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb ", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("en", _platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        if (!duringWrite) Assert.False(_controller.DictionaryIsOpen);
    }
    [Fact]
    public async Task ManualCommandCancelsPendingAutomaticWork()
    {
        EnableAutomaticCorrection(true);
        _platform.Type("ghb ");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Ukrainian);
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ", _platform.Document.Text); Assert.Equal("uk", _platform.ActiveLayoutId);
        Assert.False(_controller.DictionaryIsOpen);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 99)]
    public async Task ManualToggleCancelsActiveAutomaticCorrectionBeforePaste(bool duringCopy, int externalOwner)
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.Layouts = ["en", "ru"]; _controller.ApplySettings(settings);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Pause() { entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        if (duringCopy) _platform.AfterCopy = () => { _platform.AfterCopy = null; Pause(); };
        else _platform.AfterWrite = text =>
        {
            if (text != "при ") return;
            _platform.AfterWrite = null; Pause();
        };
        _platform.Document.Type("готовое "); _platform.Type("ghb ");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var manual = _controller.ConvertSelectionAsync(null);
            Assert.False(manual.IsCompleted); // waits for automatic cleanup, without changing the input version
            if (externalOwner != 0) _platform.ExternalCopy("fresh external copy", externalOwner);
            release.TrySetResult();
            await manual.WaitAsync(TimeSpan.FromSeconds(10));
            await _controller.AutoCorrectionIdleAsync();
            Assert.Equal("готовое при ", _platform.Document.Text);
            Assert.Equal(1, _platform.PasteCount); // only the explicit command may replace the text
            Assert.Equal(["ru"], _platform.LayoutSwitches.Select(x => x.Layout));
            Assert.Empty(_platform.Document.Selection);
            Assert.Equal(externalOwner == 0 ? "original clipboard" : "fresh external copy", _platform.ClipboardText);
            await _controller.UndoLastTransformAsync();
            Assert.Equal("готовое ghb ", _platform.Document.Text);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ManualToggleAfterAutomaticPasteWaitsForItsVerifiedResult()
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.Layouts = ["en", "ru"]; _controller.ApplySettings(settings);
        Task? manual = null;
        _platform.AfterPaste = () =>
        {
            _platform.AfterPaste = null;
            manual = _controller.ConvertSelectionAsync(null);
        };
        _platform.Type("ghb ");
        await _controller.AutoCorrectionIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(manual);
        await manual.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ghb ", _platform.Document.Text);
        Assert.Equal(2, _platform.PasteCount);
        Assert.Equal(["ru", "en"], _platform.LayoutSwitches.Select(x => x.Layout));
        Assert.Equal("original clipboard", _platform.ClipboardText);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("при ", _platform.Document.Text);
    }
    [Fact]
    public async Task InterruptedPasteMustNotLeaveAnOwnedSelectionThatBreaksTheNextCommand()
    {
        _platform.Document.Type("готовое "); _platform.Type("ghb");
        _platform.AfterWrite = text =>
        {
            if (text != "при") return;
            _platform.AfterWrite = null; _platform.ExternalCopy("external copy", owner: 99);
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое ghb", _platform.Document.Text);
        Assert.Equal("external copy", _platform.ClipboardText);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое при", _platform.Document.Text);
        Assert.Equal("external copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task TypingDuringAutomaticCopyAppendsInsteadOfReplacingTheWord()
    {
        EnableAutomaticCorrection();
        _platform.Type("ghb ");
        _platform.AfterCopy = () => { _platform.AfterCopy = null; _platform.Type("x"); };
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb x", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ExternalCopyDuringAutomaticCancellationIsPreserved()
    {
        EnableAutomaticCorrection();
        _platform.Type("ghb ");
        _platform.AfterCopy = () =>
        {
            _platform.AfterCopy = null; _platform.Type("x"); _platform.ExternalCopy("fresh external copy", owner: 99);
        };
        await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb x", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("fresh external copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task TeamsIgnoredPasteKeepsSourceForNextManualCommandAndDoesNotSwitchLayout()
    {
        _platform.SupportsTextRangeBatch = true;
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.ActiveLayoutId = "en";
        _platform.Document.Type("готовое "); _platform.Type("ghb");
        _platform.DropNextPaste = true;
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое ghb", _platform.Document.Text);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal("en", _platform.ActiveLayoutId);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое при", _platform.Document.Text);
        Assert.Equal("ru", _platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false, '\u00a0')]
    [InlineData(true, '\u00a0')]
    [InlineData(false, '\u202f')]
    public async Task ContenteditableTrailingNonBreakingSpaceMatchesThePhysicalSpace(bool automatic, char space)
    {
        if (automatic) EnableAutomaticCorrection();
        _platform.TypeAs("ghb ", "ghb" + space);
        if (automatic) await _controller.AutoCorrectionIdleAsync();
        else await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при" + space, _platform.Document.Text);
    }

    [Fact]
    public async Task TeamsCanSerializePastedSpaceAsNbspBeforeRepeatedConversionAndUndo()
    {
        _platform.SupportsTextRangeBatch = true;
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.Document.Type("готовое "); _platform.Type("ghb ");
        _platform.AfterPaste = () => _platform.Document.Text = _platform.Document.Text.Replace(' ', '\u00a0');
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое\u00a0при\u00a0", _platform.Document.Text);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("готовое\u00a0ghb\u00a0", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("готовое\u00a0при\u00a0", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }
}
