using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeamsParagraphSerializationAndBlockedPasteDoNotBreakWordReplacement(bool automatic)
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = automatic; _controller.ApplySettings(settings);
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.SupportsUnicodeTextReplacement = true;
        _platform.CopySuffix = "\r\n";
        _platform.DropNextPaste = true;
        _platform.Document.Type("готовое "); _platform.Type(automatic ? "ghb " : "ghb");
        if (automatic) await _controller.AutoCorrectionIdleAsync();
        else await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(automatic ? "готовое при " : "готовое при", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal(automatic ? "готовое ghb " : "готовое ghb", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal(automatic ? "готовое при " : "готовое при", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task TeamsSingleLineReplacementDoesNotDependOnCtrlV()
    {
        _platform.ApplicationProcess = _ => "Teams";
        _platform.SupportsTextRangeBatch = _platform.SupportsUnicodeTextReplacement = true;
        _platform.Type("ghb"); _platform.DropNextPaste = true;
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Theory]
    [InlineData("\r\n\r\n")]
    [InlineData("older text\r\n")]
    public async Task TeamsRangeMismatchStillAbortsWithoutTyping(string suffix)
    {
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.SupportsTextRangeBatch = _platform.SupportsUnicodeTextReplacement = true;
        _platform.CopySuffix = suffix;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal(0, _platform.TypedTextCount);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task TeamsUncertainUnicodeWriteDoesNotRetryOrReuseAnOldUndoRange()
    {
        _platform.ApplicationProcess = _ => "ms-teams";
        _platform.SupportsTextRangeBatch = _platform.SupportsUnicodeTextReplacement = true;
        _platform.Type("ghb");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("при", _platform.Document.Text);
        _platform.OnTypeChar = _ => throw new IOException("Partial Unicode write fixture");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("g", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        var captures = _platform.SuspendCalls;
        await _controller.UndoLastTransformAsync();
        Assert.Equal(captures, _platform.SuspendCalls);
        Assert.Equal("g", _platform.Document.Text);
    }
}
