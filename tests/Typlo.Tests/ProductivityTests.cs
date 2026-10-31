using Typlo.App.Services;
using Typlo.Core.Languages;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.Core.Text;

namespace Typlo.Tests;

public sealed class TechnicalTokenTests
{
    [Theory]
    [InlineData("https://example.com")][InlineData("example.com")][InlineData("README.md")][InlineData("C:\\Temp\\data")]
    [InlineData("/usr/bin")][InlineData("../data")][InlineData("user@example.org")][InlineData("camelCase")][InlineData("snake_case")]
    [InlineData("--help")][InlineData("$HOME")][InlineData("foo()")][InlineData("x->y")][InlineData("foo::bar")]
    [InlineData("value=hello")][InlineData("`hello`")][InlineData("sql123")]
    public void CodeAndPathsAreProtected(string text)
    {
        Assert.True(TechnicalToken.IsProtected(text));
    }
    [Theory]
    [InlineData("Hello")][InlineData("привіт")][InlineData("рев'ювати")][InlineData("htdm.dftim")][InlineData("hello!")][InlineData("state-of-the-art")]
    public void OrdinaryWordsAndReportedWrongLayoutWordRemainEligible(string text) => Assert.False(TechnicalToken.IsProtected(text));
}

public sealed partial class AppControllerTests
{
    [Fact]
    public async Task RapidChoiceCommandsAreSerializedWithoutLosingTheSecondCycle()
    {
        EnableAutomaticCorrection(); _platform.Type("ghb");
        await Task.WhenAll(_controller.CycleChoicesAsync(), _controller.CycleChoicesAsync());
        var state = await _controller.GetToolsStatusAsync(); Assert.True(state.Choices.Length >= 2); Assert.Equal(1, state.ChoiceIndex);
        _controller.CancelChoices(); await _controller.ApplyChoiceAsync(); Assert.Equal("ghb", _platform.Document.Text);
    }
    [Fact]
    public async Task ShellCommandsStayUntouchedUnlessTheUserExplicitlyEnablesTheApplicationProfile()
    {
        EnableAutomaticCorrection(); _platform.ApplicationProcess = _ => "pwsh.exe";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync(); Assert.Equal("ghb ", _platform.Document.Text);
        Assert.False(_controller.DictionaryIsOpen);
        var settings = _controller.Settings.Clone(); settings.ApplicationLayouts = [new() { ProcessName = "pwsh", AutoCorrection = true }];
        _controller.ApplySettings(settings); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("ghb при ", _platform.Document.Text);
    }
    [Fact]
    public async Task ProfilesCanEnableAutomaticCorrectionWhenGlobalIsOffWithoutProcessIoInTheKeyboardHook()
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false;
        settings.ApplicationLayouts = [new() { ProcessName = "editor.exe", Layouts = [], AutoCorrection = true }];
        _controller.ApplySettings(settings);
        _platform.ApplicationProcess = _ => { Assert.False(_platform.InTypingEvent); return "EDITOR"; };
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ", _platform.Document.Text);
        _platform.ApplicationProcess = _ => "other"; _platform.ActiveLayoutId = "en";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ghb ", _platform.Document.Text);
    }

    [Fact]
    public async Task ProfilesIndependentlyDisableCorrectionAndSnippetsAndInheritGlobalValues()
    {
        EnableAutomaticCorrection();
        var settings = _controller.Settings.Clone(); settings.Snippets = [new Snippet { Trigger = ";hi", Text = "hello" }];
        settings.ApplicationLayouts = [new() { ProcessName = "editor", AutoCorrection = false, SnippetExpansion = false }];
        _controller.ApplySettings(settings);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        await _controller.ExpandSnippetAsync(settings.Snippets[0], _platform.Window);
        Assert.Equal("ghb ", _platform.Document.Text);
        var clone = settings.Clone(); Assert.False(clone.ApplicationLayouts[0].AutoCorrection); Assert.False(clone.ApplicationLayouts[0].SnippetExpansion);
        clone.ApplicationLayouts[0].AutoCorrection = null; clone.ApplicationLayouts[0].SnippetExpansion = null;
        _controller.ApplySettings(clone); _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync(); Assert.EndsWith("при ", _platform.Document.Text);
    }

    [Fact]
    public async Task CompatibilityProbeReadsTheFieldWithoutReplacingTextOrWritingClipboard()
    {
        EnableDirect(); _platform.Document.Type("private text");
        var clipboard = _platform.ClipboardText; var writes = _platform.PasteCount;
        await _controller.ProbeFieldAsync(); var status = await _controller.GetToolsStatusAsync();
        Assert.NotNull(status.Field); Assert.DoesNotContain("private text", status.Field);
        Assert.Equal("private text", _platform.Document.Text); Assert.Equal(clipboard, _platform.ClipboardText); Assert.Equal(writes, _platform.PasteCount);
        Assert.Contains(status.Recent, p => p.Operation == "field-probe");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSpellingChoiceAppliesOnlyAfterAcceptAndRemainsUndoable(bool native)
    {
        EnableAutomaticCorrection(native);
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false; settings.Layouts = ["en", "ru", "uk"];
        _controller.ApplySettings(settings); _platform.Type("ghb");
        await _controller.CycleChoicesAsync(); var status = await _controller.GetToolsStatusAsync(); Assert.NotEmpty(status.Choices);
        Assert.Equal("ghb", _platform.Document.Text);
        await _controller.ApplyChoiceAsync(); Assert.Equal("при", _platform.Document.Text);
        await _controller.UndoLastTransformAsync(); Assert.Equal("ghb", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData("typing")][InlineData("focus")][InlineData("cancel")][InlineData("settings")]
    public async Task CandidateSelectionCannotWriteAfterContextChanges(string change)
    {
        EnableAutomaticCorrection(); _platform.Type("ghb"); await _controller.CycleChoicesAsync();
        if (change == "typing") _platform.Type("x");
        if (change == "focus") _platform.Window = 2;
        if (change == "cancel") _controller.CancelChoices();
        if (change == "settings") _controller.ApplySettings(_controller.Settings.Clone());
        await _controller.ApplyChoiceAsync(); Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("ghb" + (change == "typing" ? "x" : ""), _platform.DocumentFor(1).Text);
    }

    [Fact]
    public async Task PerformanceHistoryIsBoundedAndContainsNoWordsOrClipboardData()
    {
        EnableAutomaticCorrection();
        _platform.Type("ghb"); await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Contains((await _controller.GetToolsStatusAsync()).Recent, s => s.Operation == "text-replacement" && s.Phases.Length > 0);
        for (var i = 0; i < 60; i++) await _controller.ProbeFieldAsync();
        var report = await _controller.GetToolsStatusAsync(); Assert.Equal(50, report.Recent.Length);
        if (OperatingSystem.IsWindows()) Assert.True(report.PrivateBytes > 0);
        else Assert.True(report.PrivateBytes == -1 || report.PrivateBytes > 0);
        Assert.True(report.ManagedBytes > 0);
        Assert.All(report.Recent, s => { Assert.InRange(s.Milliseconds, 0, 60_000); Assert.DoesNotContain("ghb", string.Join(" ", s.Phases)); Assert.DoesNotContain("original clipboard", string.Join(" ", s.Phases)); });
    }
}
