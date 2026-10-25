using Typlo.Core.Settings;
using Typlo.Core.Text;

namespace Typlo.Tests;

public class TextToolsTests
{
    [Theory]
    [InlineData("hello world", "Hello World")]
    [InlineData("Hello World", "HELLO WORLD")]
    [InlineData("HELLO WORLD", "hello world")]
    [InlineData("привет мир", "Привет Мир")]
    [InlineData("ПРИВЕТ", "привет")]
    [InlineData("123 -", "123 -")]
    [InlineData("don't stop", "Don't Stop")]
    public void Case_Cycles(string input, string expected) => Assert.Equal(expected, CaseConverter.Cycle(input));

    [Theory]
    [InlineData("Привет, мир!", "Privet, mir!")]
    [InlineData("Щука и ёжик", "Shchuka i yozhik")]
    [InlineData("ЖУК", "ZHUK")]
    [InlineData("Объявление", "Obyavlenie")]
    [InlineData("Київ і Гуцульщина", "Kyiv i Hutsulshchyna")]
    public void ToLatin(string input, string expected) => Assert.Equal(expected, Transliterator.ToLatin(input));

    [Theory]
    [InlineData("Privet, mir!", "Привет, мир!")]
    [InlineData("Shchuka", "Щука")]
    [InlineData("ZHUK", "ЖУК")]
    [InlineData("yandex", "яндекс")]
    public void ToCyrillic(string input, string expected) => Assert.Equal(expected, Transliterator.ToCyrillic(input));

    [Fact]
    public void Translit_Toggle_PicksDirection()
    {
        Assert.Equal("Privet", Transliterator.Toggle("Привет"));
        Assert.Equal("Привет", Transliterator.Toggle("Privet"));
    }

    [Fact]
    public void Snippets_MatchTypedTrigger_CaseAndLayoutInsensitive()
    {
        var m = new SnippetMatcher();
        m.SetSnippets([new Snippet { Trigger = ";addr", Text = "Kyiv, Khreshchatyk 1" }, new Snippet { Trigger = ";hi", Text = "Hello" }]);
        Snippet? hit = null;
        foreach (var c in "hello ;ADDR") hit = m.OnChar(c) ?? hit;
        Assert.Equal("Kyiv, Khreshchatyk 1", hit?.Text);

        hit = null;
        foreach (var c in ";фввк") hit = m.OnChar(c) ?? hit; // same keys in the Russian layout
        Assert.Equal("Kyiv, Khreshchatyk 1", hit?.Text);
    }

    [Fact]
    public void Snippets_ResetAndBackspace()
    {
        var m = new SnippetMatcher();
        m.SetSnippets([new Snippet { Trigger = ";mail", Text = "me@example.com" }]);
        foreach (var c in ";ma") m.OnChar(c);
        m.Reset();
        Snippet? hit = null;
        foreach (var c in "il") hit = m.OnChar(c) ?? hit;
        Assert.Null(hit);

        foreach (var c in ";maxx") m.OnChar(c);
        m.Backspace();
        m.Backspace();
        foreach (var c in "il") hit = m.OnChar(c) ?? hit;
        Assert.Equal("me@example.com", hit?.Text);
        Assert.Null(m.OnChar('\n'));
    }

    [Fact]
    public void Settings_ExportImport_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typlo-export-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = new AppSettings { UiLanguage = "uk", UiTheme = "dark", Snippets = [new Snippet { Trigger = ";hi", Text = "Hello" }] };
        var file = Path.Combine(dir, "export.json");
        store.Export(s, file);
        var back = store.Import(file);
        Assert.Equal("uk", back.UiLanguage);
        Assert.Equal("dark", back.UiTheme);
        Assert.Equal("Hello", Assert.Single(back.Snippets).Text);
        File.WriteAllText(file, "not json");
        Assert.Throws<SettingsImportException>(() => store.Import(file));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"uiLanguage":"de"}""")]
    [InlineData("""{"format":"typlo-settings","schemaVersion":99}""")]
    public void Import_RejectsFilesThatAreNotTyploSettings(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), "typlo-import-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, json);
        try
        {
            var e = Assert.Throws<SettingsImportException>(() => new SettingsStore(file).Import(file));
            Assert.Empty(e.Problems);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Import_RejectsBadSnippets_AndAcceptsLegacyFiles()
    {
        var file = Path.Combine(Path.GetTempPath(), "typlo-import-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var longTrigger = ";" + new string('x', 70);
            File.WriteAllText(file, $$"""
                {"format":"typlo-settings","snippets":[{"trigger":";a1","text":null},{"trigger":";dup","text":"1"},
                 {"trigger":";DUP","text":"2"},{"trigger":"{{longTrigger}}","text":"x"},{"trigger":";","text":"x"}]}
                """);
            var e = Assert.Throws<SettingsImportException>(() => new SettingsStore(file).Import(file));
            Assert.Contains(e.Problems, p => p is { Kind: SettingsProblemKind.EmptyText, Trigger: ";a1" });
            Assert.Contains(e.Problems, p => p is { Kind: SettingsProblemKind.DuplicateTrigger, Trigger: ";DUP" });
            Assert.Contains(e.Problems, p => p.Kind == SettingsProblemKind.TriggerTooLong);
            Assert.Contains(e.Problems, p => p.Kind == SettingsProblemKind.TriggerTooShort);

            // v0.2.x exports have no format marker but carry the hotkey fields.
            File.WriteAllText(file, """{"layouts":["en","ru"],"clipboardHotkey":"Ctrl+Alt+V","snippets":null,"layoutHotkeys":[null]}""");
            var ok = new SettingsStore(file).Import(file);
            Assert.Equal("Ctrl+Alt+V", ok.ClipboardHotkey);
            Assert.Empty(ok.Snippets);
            Assert.Empty(ok.LayoutHotkeys);
        }
        finally { File.Delete(file); }
    }
}
