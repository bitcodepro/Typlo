using Typlo.App.Platform;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    private void EnableDirect(Action? beforeWrite = null, DirectEditStatus outcome = DirectEditStatus.Applied)
    {
        _platform.SupportsDirectTextEditing = true;
        _platform.DirectCapture = (window, element, valid) => Task.FromResult(valid()
            ? new DirectTextCapture(DirectCaptureStatus.Ready, new FakeDirect(_platform, window, element, beforeWrite, outcome))
            : DirectTextCapture.Cancelled);
    }

    private sealed class FakeDirect : DirectTextSnapshot
    {
        private readonly FakePlatform _platform;
        private readonly Action? _beforeWrite;
        private readonly DirectEditStatus _outcome;
        public FakeDirect(FakePlatform platform, nint window, nint element, Action? beforeWrite = null,
            DirectEditStatus outcome = DirectEditStatus.Applied)
            : base(window, element, platform.DocumentFor(window).Text,
                Math.Min(platform.DocumentFor(window).Caret, platform.DocumentFor(window).Anchor ?? platform.DocumentFor(window).Caret),
                Math.Max(platform.DocumentFor(window).Caret, platform.DocumentFor(window).Anchor ?? platform.DocumentFor(window).Caret))
            => (_platform, _beforeWrite, _outcome) = (platform, beforeWrite, outcome);
        public override Task<DirectTextSnapshot?> RefreshAsync(Func<bool> valid) => Task.FromResult<DirectTextSnapshot?>(
            valid() ? new FakeDirect(_platform, Window, Element, _beforeWrite, _outcome) : null);
        public override async Task<DirectTextEditResult> ReplaceAsync(int start, int length, string replacement, Func<bool> valid)
        {
            _beforeWrite?.Invoke();
            var current = await RefreshAsync(valid);
            if (current is null || !SameContext(current)) return new(DirectEditStatus.Cancelled);
            if (_outcome != DirectEditStatus.Applied) return new(_outcome);
            var doc = _platform.DocumentFor(Window);
            doc.Anchor = start; doc.Caret = start + length;
            doc.Type(replacement);
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
