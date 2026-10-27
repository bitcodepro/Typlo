using Typlo.Core.Hotkeys;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public class SettingsAndHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", KeyModifiers.Ctrl | KeyModifiers.Shift, "V")]
    [InlineData("ctrl + alt + shift + space", KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "Space")]
    [InlineData("Win+f12", KeyModifiers.Win, "F12")]
    [InlineData("Alt+1", KeyModifiers.Alt, "1")]
    public void Hotkey_Parses(string text, KeyModifiers mods, string key)
    {
        var hk = Hotkey.TryParse(text);
        Assert.NotNull(hk);
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(key, hk.Key);
        Assert.Equal(hk, Hotkey.TryParse(hk.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("F25")]
    public void Hotkey_RejectsGarbage(string text) => Assert.Null(Hotkey.TryParse(text));

    [Fact]
    public void Hotkey_Usable_RequiresModifierUnlessFunctionKey()
    {
        Assert.False(Hotkey.TryParse("A")!.IsUsable);
        Assert.False(Hotkey.TryParse("Shift+A")!.IsUsable);
        Assert.True(Hotkey.TryParse("Ctrl+A")!.IsUsable);
        Assert.True(Hotkey.TryParse("F9")!.IsUsable);
    }

    [Fact]
    public void DefaultHotkeys_AreValidAndDistinct()
    {
        var s = new AppSettings();
        var all = new[] { s.ToggleHotkey, s.ClipboardHotkey }.Concat(s.LayoutHotkeys.Select(h => h.Hotkey))
            .Select(Hotkey.TryParse).ToList();
        Assert.All(all, h => Assert.True(h is { IsUsable: true }));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Settings_SaveLoad_AndRecoverFromBrokenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "ls-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        var store = new SettingsStore(path);
        var s = store.Load();
        s.MaxItems = 42;
        s.ClipboardHotkey = "Ctrl+Alt+V";
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(42, loaded.MaxItems);
        Assert.Equal("Ctrl+Alt+V", loaded.ClipboardHotkey);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(500, store.Load().MaxItems);
        Assert.True(File.Exists(path + ".broken"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Normalize_ClampsAndPreservesNativeLayoutIds()
    {
        var s = new AppSettings { MaxItems = -5, Layouts = ["xx", "ru"] };
        s.Normalize();
        Assert.Equal(10, s.MaxItems);
        Assert.Equal(["xx", "ru"], s.Layouts); // OS identities are resolved by the runtime catalog
    }
}
