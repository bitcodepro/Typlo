using System.Text.Json;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed class CaptureSettingsTests
{
    [Fact]
    public void OldSettingsGainDefaultsAndCustomOrDisabledShortcutsSurviveClone()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal("Ctrl+Alt+Shift+S", old.ScreenshotCopyHotkey);
        Assert.Equal("Ctrl+Alt+Shift+F", old.ScreenshotSaveHotkey);
        old.ScreenshotCopyHotkey = "Ctrl+Alt+R";
        old.ScreenshotSaveHotkey = "";
        Assert.Equal("Ctrl+Alt+R", old.Clone().ScreenshotCopyHotkey);
        Assert.Equal("", old.Clone().ScreenshotSaveHotkey);
        old.ScreenshotCopyHotkey = null!;
        old.ScreenshotSaveHotkey = null!;
        old.Normalize();
        Assert.Equal("", old.ScreenshotCopyHotkey);
        Assert.Equal("", old.ScreenshotSaveHotkey);
    }
}
