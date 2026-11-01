using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData(false, "ChatGPT")]
    [InlineData(true, "ChatGPT")]
    [InlineData(false, "chatgpt.exe")]
    [InlineData(true, @"C:\Apps\ChatGPT.exe")]
    public async Task ChatGptAppendPasteHandlerCannotDuplicateTheSource(bool automatic, string process)
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = automatic; _controller.ApplySettings(settings);
        _platform.ApplicationProcess = _ => process;
        _platform.SupportsUnicodeTextReplacement = true;
        _platform.AppendPaste = true;
        _platform.Document.Type("готовое "); _platform.Type(automatic ? "rfr " : "rfr");
        if (automatic) await _controller.AutoCorrectionIdleAsync();
        else await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(automatic ? "готовое как " : "готовое как", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal(automatic ? "готовое rfr " : "готовое rfr", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal(automatic ? "готовое как " : "готовое как", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatGptContinuingInputOrFocusChangeDuringCopyNeverWrites(bool changeFocus)
    {
        EnableAutomaticCorrection();
        _platform.ApplicationProcess = _ => "ChatGPT";
        _platform.SupportsUnicodeTextReplacement = true;
        _platform.AppendPaste = true;
        _platform.AfterCopy = () =>
        {
            _platform.AfterCopy = null;
            if (changeFocus) _platform.Window = 2; else _platform.Type("x");
        };
        _platform.Type("rfr "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(changeFocus ? "rfr " : "rfr x", _platform.DocumentFor(1).Text);
        Assert.Equal(0, _platform.TypedTextCount); Assert.Equal(0, _platform.PasteCount);
        Assert.Empty(_platform.LayoutSwitches);
    }

    [Fact]
    public async Task ChatGptManualCommandDuringAutomaticCopyDoesNotDoubleConvert()
    {
        EnableAutomaticCorrection();
        _platform.ApplicationProcess = _ => "ChatGPT";
        _platform.SupportsUnicodeTextReplacement = true;
        _platform.AppendPaste = true;
        Task? manual = null;
        _platform.AfterCopy = () =>
        {
            _platform.AfterCopy = null;
            manual = _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        };
        _platform.Type("rfr ");
        await _controller.AutoCorrectionIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(manual); await manual.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("как ", _platform.Document.Text);
        Assert.Equal(1, _platform.TypedTextCount); Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ChatGptFastContinuousTypingDoesNoClipboardOrEditorWork()
    {
        EnableAutomaticCorrection();
        _platform.ApplicationProcess = _ => "ChatGPT";
        _platform.SupportsUnicodeTextReplacement = true;
        var text = string.Concat(Enumerable.Repeat("rfr ", 100)) + "unfinished";
        _platform.Type(text); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal(text, _platform.Document.Text);
        Assert.Equal(0, _platform.SuspendCalls); Assert.Equal(0, _platform.TypedTextCount);
        Assert.Equal(0, _platform.PasteCount); Assert.False(_controller.DictionaryIsOpen);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ChatGptPartialUnicodeWriteIsNeverRetriedAndHasNoStaleUndo()
    {
        _platform.ApplicationProcess = _ => "ChatGPT";
        _platform.SupportsTextRangeBatch = _platform.SupportsUnicodeTextReplacement = true;
        _platform.AppendPaste = true; _platform.Type("rfr");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("как", _platform.Document.Text);
        _platform.OnTypeChar = _ => throw new IOException("Partial Unicode write fixture");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("r", _platform.Document.Text);
        var writes = _platform.TypedTextCount;
        await _controller.UndoLastTransformAsync();
        Assert.Equal(writes, _platform.TypedTextCount);
        Assert.Equal("r", _platform.Document.Text); Assert.Equal(0, _platform.PasteCount);
    }
}
