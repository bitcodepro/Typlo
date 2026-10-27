using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public class LayoutImportTests
{
    [Fact]
    public void Import_RejectsQwertzAliasCollisionsBeforeApplyingAnySettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typlo-import-layouts-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var existing = new AppSettings { MaxItems = 42 };
        store.Save(existing);
        var before = File.ReadAllBytes(store.Path);
        var file = Path.Combine(directory, "import.json");
        var en = KeyboardLayouts.English;
        static string Qwertz(string s) => s.Replace('y', '\0').Replace('z', 'y').Replace('\0', 'z')
            .Replace('Y', '\0').Replace('Z', 'Y').Replace('\0', 'Z');
        var de = new KeyboardLayout("native:de", "Deutsch", 0, Qwertz(en.Normal), Qwertz(en.Shifted), "de");
        try
        {
            store.Export(new AppSettings { Snippets = [new() { Trigger = "az", Text = "FIRST" },
                new() { Trigger = "ay", Text = "SECOND" }] }, file);
            // The old, catalog-free import missed the collision introduced by the German Y/Z aliases.
            Assert.Empty(store.Import(file).Validate());
            var error = Assert.Throws<SettingsImportException>(() => store.Import(file, [en, de]));
            Assert.Contains(error.Problems, p => p is { Kind: SettingsProblemKind.DuplicateTrigger, Trigger: "ay" });
            Assert.Equal(before, File.ReadAllBytes(store.Path));

            store.Export(new AppSettings { Snippets = [new() { Trigger = "az", Text = "FIRST" },
                new() { Trigger = "ax", Text = "SECOND" }] }, file);
            Assert.Equal(2, store.Import(file, [en, de]).Snippets.Count);
            Assert.Equal(before, File.ReadAllBytes(store.Path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
