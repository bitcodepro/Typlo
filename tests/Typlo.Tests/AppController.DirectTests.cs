using Typlo.App.Platform;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Fact]
    public async Task UnsupportedEditorFocusProxyChangeDoesNotDiscardRecentWord()
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.RetainDirectTarget = (_, _) => false;
        _platform.DirectCapture = (_, _, _) => Task.FromResult(DirectTextCapture.Unsupported);
        _platform.Document.Type("готовое ");
        _platform.Type("ghb");
        _platform.FocusedInputElement = 12; // Same web editor, a different opaque focus HWND.
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое при", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task UnsupportedEditorFocusProxyChangesDuringTypingKeepTheWholeRun()
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.RetainDirectTarget = (_, _) => false;
        _platform.DirectCapture = (_, _, _) => Task.FromResult(DirectTextCapture.Unsupported);
        _platform.Document.Type("готовое ");
        _platform.Type("g");
        _platform.FocusedInputElement = 12;
        _platform.Type("h");
        _platform.FocusedInputElement = 11;
        _platform.Type("b");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое при", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task NativeDeclineCannotPasteIntoAnotherFieldInTheSameWindow()
    {
        EnableDirect(() => _platform.FocusedInputElement = 12, DirectEditStatus.Unsupported);
        _platform.ActiveLayoutId = KeyboardLayouts.English.Id;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
    }

    [Fact]
    public async Task NativeFallbackRechecksTheBoundFieldImmediatelyBeforePaste()
    {
        EnableDirect(outcome: DirectEditStatus.Unsupported);
        _platform.Type("ghb");
        _platform.AfterWrite = text => { if (text == "при") _platform.FocusedInputElement = 12; };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task UnsupportedEditorKeepsRepeatedRussianUkrainianEnglishConversionsOnOneStrategy()
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.RetainDirectTarget = (_, _) => false;
        var probes = 0;
        _platform.DirectCapture = (_, _, _) => { probes++; return Task.FromResult(DirectTextCapture.Unsupported); };
        _controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
        _platform.Document.Type("готовое ");
        _platform.Type("ss");
        for (var i = 0; i < 3; i++)
        {
            _platform.FocusedInputElement++;
            await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
            Assert.Equal("готовое ыы", _platform.Document.Text);
            _platform.FocusedInputElement++;
            await _controller.ConvertSelectionAsync(KeyboardLayouts.Ukrainian);
            Assert.Equal("готовое іі", _platform.Document.Text);
            _platform.FocusedInputElement++;
            await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
            Assert.Equal("готовое ss", _platform.Document.Text);
        }
        Assert.Equal(0, probes);
        Assert.Equal(9, _platform.PasteCount);
    }

    private void EnableDirect(Action? beforeWrite = null, DirectEditStatus outcome = DirectEditStatus.Applied, Action? afterWrite = null)
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.DirectCapture = (window, element, valid) => Task.FromResult(valid()
            ? new DirectTextCapture(DirectCaptureStatus.Ready, new FakeDirect(_platform, window, element, beforeWrite, outcome, afterWrite))
            : DirectTextCapture.Cancelled);
    }

    [Fact]
    public async Task ClosedOrUnsupportedPriorFieldDoesNotStealNewWindowsExplicitSelection()
    {
        EnableDirect();
        _platform.Type("ghb");
        _platform.RetainDirectTarget = (window, _) => window != 1;
        _platform.Window = 2;
        _platform.Document.Type("ghb");
        _platform.Document.SelectAll();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.DocumentFor(1).Text);
        Assert.Equal("при", _platform.DocumentFor(2).Text);
    }

    private sealed class FakeDirect : DirectTextSnapshot
    {
        private readonly FakePlatform _platform;
        private readonly Action? _beforeWrite;
        private readonly DirectEditStatus _outcome;
        private readonly Action? _afterWrite;
        public FakeDirect(FakePlatform platform, nint window, nint element, Action? beforeWrite = null,
            DirectEditStatus outcome = DirectEditStatus.Applied, Action? afterWrite = null)
            : base(window, element, platform.DocumentFor(window).Text,
                Math.Min(platform.DocumentFor(window).Caret, platform.DocumentFor(window).Anchor ?? platform.DocumentFor(window).Caret),
                Math.Max(platform.DocumentFor(window).Caret, platform.DocumentFor(window).Anchor ?? platform.DocumentFor(window).Caret))
            => (_platform, _beforeWrite, _outcome, _afterWrite) = (platform, beforeWrite, outcome, afterWrite);
        public override Task<DirectTextSnapshot?> RefreshAsync(Func<bool> valid) => Task.FromResult<DirectTextSnapshot?>(
            valid() ? new FakeDirect(_platform, Window, Element, _beforeWrite, _outcome, _afterWrite) : null);
        public override async Task<DirectTextEditResult> ReplaceAsync(int start, int length, string replacement, Func<bool> valid)
        {
            _beforeWrite?.Invoke();
            var current = await RefreshAsync(valid);
            if (current is null || !SameContext(current)) return new(DirectEditStatus.Cancelled);
            if (_outcome != DirectEditStatus.Applied) return new(_outcome);
            var doc = _platform.DocumentFor(Window);
            doc.Anchor = start; doc.Caret = start + length;
            doc.Type(replacement);
            _afterWrite?.Invoke();
            return new(DirectEditStatus.Applied, new FakeDirect(_platform, Window, Element));
        }
    }

    [Fact]
    public async Task CancelledDirectCaptureDoesNotFallThroughToClipboardOrLayoutSwitch()
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.DirectCapture = (_, _, _) => Task.FromResult(DirectTextCapture.Cancelled);
        _platform.ActiveLayoutId = KeyboardLayouts.English.Id;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
        Assert.Equal(0, _platform.SuspendCalls);
    }

    [Fact]
    public async Task DirectReplacementCapabilityDeclineBeforeMutationCanUseFallback()
    {
        EnableDirect(outcome: DirectEditStatus.Unsupported);
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task AnotherFieldSelectionDoesNotReusePriorFieldsTypingRun()
    {
        EnableDirect();
        _platform.Type("ghb");
        _platform.FocusedInputElement = 12;
        _platform.Document.SelectAll();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task DisabledRecentInputDoesNotRedirectExplicitSelectionIntoBackgroundWindow()
    {
        EnableDirect();
        var settings = _controller.Settings.Clone();
        settings.ConvertLastWord = false;
        _controller.ApplySettings(settings);
        _platform.Type("ghb");
        _platform.Window = 2;
        _platform.Document.Type("ghb");
        _platform.Document.SelectAll();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.DocumentFor(1).Text);
        Assert.Equal("при", _platform.DocumentFor(2).Text);
    }

    [Fact]
    public async Task DirectConversionContinuesInBoundEditorAfterNotificationTakesFocus()
    {
        EnableDirect(() => { _platform.Window = 2; _platform.ExternalCopy("new clipboard"); });
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.DocumentFor(1).Text);
        Assert.Equal("", _platform.DocumentFor(2).Text);
        Assert.Equal((nint)2, _platform.Window);
        Assert.Equal("new clipboard", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(0, _platform.SuspendCalls);
        Assert.Equal(0, _platform.Activations);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("ghb", _platform.DocumentFor(1).Text);
    }

    [Fact]
    public async Task DirectConversionCanBindRecentInputBeforeHotkeyEvenIfNotificationIsAlreadyForeground()
    {
        EnableDirect();
        _platform.Type("ghb");
        _platform.Window = 2;
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.DocumentFor(1).Text);
        Assert.Equal((nint)2, _platform.Window);
    }

    [Fact]
    public async Task DirectPunctuationReplacesOnlyTrackedSuffixAndSupportsRepeatAndUndo()
    {
        EnableDirect();
        _platform.Document.Type("готовое слово");
        _platform.Type("?");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("готовое слово,", _platform.Document.Text);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("готовое слово?", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("готовое слово,", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task DirectExplicitSelectionSupportsLengthChangingTransformsAndUndo()
    {
        EnableDirect();
        _platform.Document.Type("до Привет после");
        _platform.Document.Anchor = 3; _platform.Document.Caret = 9;
        await _controller.TransliterateAsync();
        Assert.Equal("до Privet после", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("до Привет после", _platform.Document.Text);
    }

    [Theory]
    [InlineData(DirectEditStatus.Cancelled)]
    [InlineData(DirectEditStatus.Uncertain)]
    public async Task UnconfirmedDirectWriteNeverFallsBackOrSwitchesLayout(DirectEditStatus outcome)
    {
        EnableDirect(outcome: outcome);
        _platform.ActiveLayoutId = KeyboardLayouts.English.Id;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
        Assert.Equal(0, _platform.PasteCount);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("ghb", _platform.Document.Text);
    }

    [Fact]
    public async Task UnsupportedDirectControlUsesExistingVerifiedFallback()
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectWriteAbortsOnProgrammaticTextOrCaretChange(bool textChange)
    {
        EnableDirect(() => { if (textChange) _platform.Document.Text = "xyz"; else _platform.Document.Caret = 0; });
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(textChange ? "xyz" : "ghb", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task DirectWriteAbortsOnRealUserInputAndDispose()
    {
        EnableDirect(() => _platform.Type("x"));
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbx", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        EnableDirect(() => _controller.Dispose());
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbx", _platform.Document.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputOrDisposeAfterCompletedWriteCannotPublishRepeatOrUndoRange(bool dispose)
    {
        EnableDirect(afterWrite: () => { if (dispose) _controller.Dispose(); else _platform.Type("x"); });
        _platform.ActiveLayoutId = KeyboardLayouts.English.Id;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        var expected = dispose ? "при" : "приx";
        Assert.Equal(expected, _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
        Assert.Equal(0, _platform.PasteCount);
        await _controller.UndoLastTransformAsync();
        Assert.Equal(expected, _platform.Document.Text);
    }

    [Fact]
    public async Task DirectMismatchedPhysicalInputDoesNotConvertOlderText()
    {
        EnableDirect();
        _platform.TypeAs("ghb", "xyz");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("xyz", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task DirectNoopCycleRetainsLogicalLayoutWithoutChangingTextOrClipboard()
    {
        EnableDirect();
        _platform.TypeAs("vbh", "мир");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("vbh", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task DirectUndoDeclinesIfDocumentChangedOutsideConvertedRange()
    {
        EnableDirect();
        _platform.Document.Type("prefix ");
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        _platform.Document.Text = "PREFIX при";
        await _controller.UndoLastTransformAsync();
        Assert.Equal("PREFIX при", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }
}
