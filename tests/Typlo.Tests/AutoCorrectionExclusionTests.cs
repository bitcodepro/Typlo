using Typlo.App.Services;
using Typlo.App.Platform.Shared;
using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeverCorrectUsesExactSourceSpellingAndManualConversionStillWorks(bool native)
    {
        EnableAutomaticCorrection(native);
        await _controller.SetExcludedWordAsync("GhB");
        _platform.Type("ghb! "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb! ", _platform.Document.Text);
        Assert.Equal("en", _platform.ActiveLayoutId);
        Assert.Equal(AutoCorrectionReason.ExcludedWord, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        Assert.False(_controller.DictionaryIsOpen); // No bundled vocabulary or text replacement needed.
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при! ", _platform.Document.Text);
        await _controller.SetExcludedWordAsync("ghb", remove: true);
        _platform.ActiveLayoutId = "en";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.EndsWith("при ", _platform.Document.Text);
    }

    [Fact]
    public async Task AddingAnExclusionCancelsAnAutomaticCopyBeforeItCanPaste()
    {
        EnableAutomaticCorrection();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.AfterCopy = () => { _platform.AfterCopy = null; entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); };
        _platform.Type("ghb ");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var excluding = _controller.SetExcludedWordAsync("ghb");
            release.TrySetResult(); await excluding; await _controller.AutoCorrectionIdleAsync();
            Assert.Equal("ghb ", _platform.Document.Text); Assert.Equal(0, _platform.PasteCount);
            _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
            Assert.Equal("ghb ghb ", _platform.Document.Text);
            Assert.Equal(AutoCorrectionReason.ExcludedWord, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoProtectsRestoredWordButNotTheNextOccurrence(bool native)
    {
        EnableAutomaticCorrection(native);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        await _controller.UndoLastTransformAsync(); Assert.Equal("ghb ", _platform.Document.Text);
        _platform.UserKey(TypedKey.BackspaceKey); _platform.Document.Backspace();
        _platform.Type(" "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb ", _platform.Document.Text);
        Assert.Equal(AutoCorrectionReason.UndoSuppressed, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        _platform.ActiveLayoutId = "en";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb при ", _platform.Document.Text);
        Assert.Equal(AutoCorrectionReason.Corrected, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        Assert.Empty((await _controller.QueryPersonalWordsAsync(new(Exclusions: true))).Items);
    }

    [Fact]
    public async Task ManualConversionOfAnUndoProtectedWordRemainsAvailable()
    {
        EnableAutomaticCorrection(); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        await _controller.UndoLastTransformAsync();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Ukrainian);
        Assert.Equal("при ", _platform.Document.Text); Assert.Equal("uk", _platform.ActiveLayoutId);
    }

    [Fact]
    public async Task LayoutOnlyGestureDoesNotRevokeUndoProtectionAtTheSameCaret()
    {
        EnableAutomaticCorrection(); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        await _controller.UndoLastTransformAsync();
        _platform.UserKey(TypedKey.LayoutGestureKey);
        _platform.UserKey(TypedKey.BackspaceKey); _platform.Document.Backspace(); _platform.Type(" ");
        await _controller.AutoCorrectionIdleAsync(); Assert.Equal("ghb ", _platform.Document.Text);
        Assert.Equal(AutoCorrectionReason.UndoSuppressed, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task TypingDuringUndoCannotPublishProtectionOverNewerInput()
    {
        EnableAutomaticCorrection(); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        _platform.AfterPaste = () => { _platform.AfterPaste = null; _platform.Type("x"); };
        await _controller.UndoLastTransformAsync(); Assert.Equal("ghb x", _platform.Document.Text);
        _platform.ActiveLayoutId = "en"; _platform.Type(" ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb x при ", _platform.Document.Text);
        Assert.DoesNotContain((await _controller.GetAutomaticStatusAsync()).Recent, e => e.Reason == AutoCorrectionReason.UndoSuppressed);
    }

    [Fact]
    public async Task AnUnconfirmedUndoDoesNotPublishASuppressionOrChangeTheClipboard()
    {
        EnableAutomaticCorrection(); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        _platform.DropNextPaste = true; await _controller.UndoLastTransformAsync();
        Assert.Equal("при ", _platform.Document.Text);
        _platform.ActiveLayoutId = "en"; _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при при ", _platform.Document.Text);
        Assert.DoesNotContain((await _controller.GetAutomaticStatusAsync()).Recent, e => e.Reason == AutoCorrectionReason.UndoSuppressed);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }
}

public sealed class UndoTypingScopeTests
{
    [Fact]
    public void ProtectionSurvivesPausesAndWordEditsButEndsAtNewWordOrDeletion()
    {
        var input = new RecentTyping();
        input.RestoreAfterUndo(new(1, "ghb "), 1, 100, 11, 7);
        input.Backspace(1, 50_000, 2); input.Backspace(1, 50_001, 3);
        input.Record('b', 1, 50_002, 4); input.Record(' ', 1, 50_003, 5);
        Assert.True(input.Capture(1)!.SuppressAutomatic);
        input.Record('g', 1, 50_004, 6); Assert.False(input.Capture(1)!.SuppressAutomatic);
        input.RestoreAfterUndo(new(1, "ghb "), 6, 50_005, 11, 7);
        for (var i = 0; i < 4; i++) input.Backspace(1, 50_006 + i, 7 + i);
        input.Record('g', 1, 50_010, 11); Assert.False(input.Capture(1)!.SuppressAutomatic);
    }

    [Theory]
    [InlineData(2, 11, 7)]
    [InlineData(1, 12, 7)]
    [InlineData(1, 11, 9)]
    public void DifferentWindowFieldOrFocusEpochRevokesProtection(int window, int element, int focus)
    {
        var input = new RecentTyping(); input.RestoreAfterUndo(new(1, "ghb "), 1, 100, 11, 7);
        input.ValidateUndoContext(window, element, focus);
        Assert.Null(input.Capture(1));
    }

    [Fact]
    public void NavigationAndNewerInputCannotInheritOldUndoProtection()
    {
        var input = new RecentTyping(); input.RestoreAfterUndo(new(1, "ghb "), 1, 100, 11, 7);
        input.Reset(2); input.Record('x', 1, 200, 3);
        input.RestoreAfterUndo(new(1, "ghb "), 1, 100, 11, 7);
        Assert.Equal("x", input.Capture(1)!.Keys); Assert.False(input.Capture(1)!.SuppressAutomatic);
    }
}
