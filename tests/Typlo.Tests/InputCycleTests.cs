using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Fact]
    public async Task InputCycleUsesAppOrderGlobalFallbackAndSingleLanguageWithoutEditingText()
    {
        _controller.ConfigureDiagnosticLayouts([KeyboardLayouts.English, KeyboardLayouts.Russian, KeyboardLayouts.Ukrainian]);
        _platform.ApplicationProcess = window => window == 1 ? "ms-teams" : window == 2 ? "browser" : "powershell";
        var settings = _controller.Settings.Clone(); settings.AutoCorrectEnabled = false;
        settings.Layouts = ["uk", "ru", "en"];
        settings.ApplicationLayouts = [new() { ProcessName = "ms-teams", Layouts = ["en", "uk"] },
            new() { ProcessName = "browser", Layouts = [] }, new() { ProcessName = "powershell", Layouts = ["en"] }];
        _controller.ApplySettings(settings); _platform.ActiveLayoutId = "en";
        _platform.Type("ghb");
        foreach (var expected in new[] { "uk", "en", "uk", "en" })
        { await _controller.CycleInputLayoutAsync(); Assert.Equal(expected, _platform.ActiveLayoutId); }
        Assert.Equal("ghb", _platform.Document.Text);
        _platform.Window = 2; _platform.ActiveLayoutId = "uk";
        foreach (var expected in new[] { "ru", "en", "uk" })
        { await _controller.CycleInputLayoutAsync(); Assert.Equal(expected, _platform.ActiveLayoutId); }
        _platform.Window = 3;
        await _controller.ApplicationLayoutsIdleAsync(); Assert.Equal("en", _platform.ActiveLayoutId);
        await _controller.CycleInputLayoutAsync(); Assert.Equal("en", _platform.ActiveLayoutId);
        Assert.Equal(0, _platform.PasteCount); Assert.Equal(0, _platform.TypedTextCount); Assert.Equal("original clipboard", _platform.ClipboardText);
    }
}
