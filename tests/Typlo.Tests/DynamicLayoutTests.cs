using Typlo.Core.Layouts;
using Typlo.Core.Settings;
using Typlo.App.Platform.Windows;
using Typlo.App.Platform.Linux;

namespace Typlo.Tests;

public class DynamicLayoutTests
{
    private static KeyboardLayout Layout(string id, string language) => new(id, id, 0,
        KeyboardLayouts.English.Normal, KeyboardLayouts.English.Shifted, language);

    [Fact]
    public void FirstSetup_UsesAllFiveSystemLayouts_OnlyFirstThreeHaveShortcuts()
    {
        var installed = new[] { Layout("native:en", "en"), Layout("native:uk", "uk"), Layout("native:ru", "ru"),
            Layout("native:de", "de"), Layout("native:fr", "fr") };
        var catalog = new LayoutCatalog(installed);
        var settings = new AppSettings();
        catalog.Synchronize(settings);
        Assert.Equal(installed.Select(l => l.Id), settings.Layouts);
        Assert.Equal(new[] { "Ctrl+Alt+Shift+1", "Ctrl+Alt+Shift+2", "Ctrl+Alt+Shift+3", "", "" }, settings.LayoutHotkeys.Select(h => h.Hotkey));
        Assert.True(settings.LayoutsInitialized);
        Assert.Null(catalog.Find("xx"));
    }

    [Fact]
    public void Migration_PreservesExistingBindingsOrderAndDisabledShortcuts()
    {
        var catalog = new LayoutCatalog([Layout("native:uk", "uk"), Layout("native:en", "en"), Layout("native:de", "de")]);
        var settings = new AppSettings { Layouts = ["en", "uk"], LayoutHotkeys =
            [new() { LayoutId = "en", Hotkey = "F9" }, new() { LayoutId = "uk", Hotkey = "" }] };
        catalog.Synchronize(settings);
        Assert.Equal(["native:en", "native:uk"], settings.Layouts);
        Assert.Equal("F9", settings.LayoutHotkeys.First(h => h.LayoutId == "native:en").Hotkey);
        Assert.Equal("", settings.LayoutHotkeys.First(h => h.LayoutId == "native:uk").Hotkey);
        Assert.Equal("", settings.LayoutHotkeys.First(h => h.LayoutId == "native:de").Hotkey);
        catalog.Synchronize(settings);
        Assert.Equal(3, settings.LayoutHotkeys.Count);
    }

    [Fact]
    public void FailedDiscovery_DoesNotEraseConfiguredLayoutsOrShortcuts()
    {
        var settings = new AppSettings { Layouts = ["native:fr", "native:de"], LayoutsInitialized = true,
            LayoutHotkeys = [new() { LayoutId = "native:fr", Hotkey = "F8" }] };
        new LayoutCatalog([]).Synchronize(settings);
        Assert.Equal(["native:fr", "native:de"], settings.Layouts);
        Assert.Equal("F8", Assert.Single(settings.LayoutHotkeys).Hotkey);
    }

    [Fact]
    public void Cycle_RespectsArbitraryOrderAndWrapsFiveMembers()
    {
        var all = Enumerable.Range(0, 5).Select(i => Layout("native:" + i, "language" + i)).ToArray();
        var cycle = new[] { all[3], all[0], all[4], all[2], all[1] };
        var converter = new LayoutConverter(all, cycle);
        for (var i = 0; i < cycle.Length; i++)
            Assert.Same(cycle[(i + 1) % cycle.Length], converter.Toggle("same alphabet", cycle[i]).Target);
    }

    [Fact]
    public void Cycle_ExcludesUnselectedLayouts_AndHandlesOneSource()
    {
        var all = new[] { Layout("one", "en"), Layout("two", "fr"), Layout("three", "de") };
        Assert.Same(all[2], new LayoutConverter(all, [all[0], all[2]]).Toggle("text", all[0]).Target);
        Assert.False(new LayoutConverter([all[0]]).Toggle("text").Changed);
        Assert.False(new LayoutConverter([]).Toggle("text").Changed);
    }

    [Fact]
    public void SharedAlphabet_UsesPhysicalMappingsAndSourceHint()
    {
        var en = KeyboardLayouts.English;
        static string Swap(string value) => value.Replace('q', '\0').Replace('a', 'q').Replace('\0', 'a')
            .Replace('Q', '\0').Replace('A', 'Q').Replace('\0', 'A');
        var azerty = new KeyboardLayout("native:azerty", "AZERTY", 0, Swap(en.Normal), Swap(en.Shifted), "fr");
        var converter = new LayoutConverter([en, azerty]);
        Assert.Equal("aqqA", converter.ConvertTo("qaaQ", azerty, en).Text);
        Assert.Equal("qaaQ", converter.ConvertTo("aqqA", en, azerty).Text);
    }

    [Fact]
    public void InputMethods_AreVisibleButExcludedFromMechanicalCycle()
    {
        var ime = new KeyboardLayout("native:ime", "IME", 0, new string('\0', KeyboardLayouts.English.Normal.Length),
            new string('\0', KeyboardLayouts.English.Normal.Length), "ja", supportsConversion: false);
        var catalog = new LayoutCatalog([KeyboardLayouts.English, ime]);
        var settings = new AppSettings();
        catalog.Synchronize(settings);
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal(["en"], settings.Layouts);
        Assert.False(new LayoutConverter(catalog.All).ConvertTo("hello", ime).Changed);
    }

    [Fact]
    public void WindowsDiscovery_ReadsActualLayoutsWithoutChangingTheActiveOne()
    {
        if (!OperatingSystem.IsWindows()) return;
        var before = WindowsKeyboardLayouts.Current(0);
        var layouts = WindowsKeyboardLayouts.Read();
        Assert.NotEmpty(layouts);
        Assert.All(layouts, l => Assert.Equal(KeyboardLayouts.English.Normal.Length, l.Normal.Length));
        Assert.All(layouts, l => Assert.StartsWith("win:", l.Id));
        foreach (var layout in layouts.Where(l => l.LanguageCode is "en" or "ru"))
            Assert.True(layout.SupportsConversion, $"Ordinary system keyboard {layout.Id} was incorrectly classified as an input method");
        Assert.Equal(before, WindowsKeyboardLayouts.Current(0));
    }

    [Fact]
    public void XkbQuery_PreservesVariantsAndArbitraryLayoutNames()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sources = LinuxKeyboardLayouts.ParseQuery("rules: evdev\nlayout: us,fr,de,ua\nvariant: ,oss,nodeadkeys,\n");
        Assert.Equal("xkb:us:", sources[0].Id);
        Assert.Equal("xkb:fr:oss", sources[1].Id);
        Assert.Equal("xkb:de:nodeadkeys", sources[2].Id);
        Assert.Equal("xkb:ua:", sources[3].Id);
    }

    [Fact]
    public void SnippetTriggers_UseTheDiscoveredPhysicalMapForAnotherLatinLayout()
    {
        var en = KeyboardLayouts.English;
        var fr = new KeyboardLayout("native:fr", "AZERTY", 0, en.Normal.Replace('a', '\0').Replace('q', 'a').Replace('\0', 'q'),
            en.Shifted.Replace('A', '\0').Replace('Q', 'A').Replace('\0', 'Q'), "fr");
        var matcher = new Typlo.Core.Text.SnippetMatcher();
        matcher.SetSnippets([new() { Trigger = ";az", Text = "done" }], [fr]);
        Typlo.Core.Text.Snippet? hit = null;
        foreach (var key in ";qz") hit = matcher.OnChar(key) ?? hit;
        Assert.Equal("done", hit?.Text);
    }

    [Fact]
    public void AllTranslations_IncludeTheNewCycleAndTemplateControls()
    {
        foreach (var (language, _) in Typlo.App.Services.Loc.Languages)
            Assert.Empty(Typlo.App.Services.Loc.MissingKeys(language));
    }

    [Fact]
    public void VersionOneSettings_AreBackedUpBeforeMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typlo-layout-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        const string old = "{\"schemaVersion\":1,\"layouts\":[\"ru\",\"en\"],\"layoutHotkeys\":[{\"layoutId\":\"ru\",\"hotkey\":\"F9\"}]}";
        try
        {
            File.WriteAllText(path, old);
            var settings = new SettingsStore(path).Load();
            Assert.Equal(old, File.ReadAllText(path + ".v1"));
            Assert.Equal(["ru", "en"], settings.Layouts);
            Assert.Equal("F9", Assert.Single(settings.LayoutHotkeys).Hotkey);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
