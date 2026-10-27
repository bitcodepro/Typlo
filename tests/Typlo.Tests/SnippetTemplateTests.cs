using Typlo.Core.Text;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public class SnippetTemplateTests
{
    [Theory]
    [InlineData("hello", "hello", null)]
    [InlineData("Hello, {cursor}!\n\nRegards,\n", "Hello, !\n\nRegards,\n", 7)]
    [InlineData("{cursor}first\r\n\tsecond  ", "first\r\n\tsecond  ", 0)]
    [InlineData("end{cursor}", "end", 3)]
    [InlineData("{{cursor}} {cursor}", "{cursor} ", 9)]
    public void Rendering_PreservesWhitespaceAndRemovesOnlyTheMarker(string template, string text, int? cursor)
    {
        var parsed = SnippetTemplate.Parse(template);
        Assert.Equal(text, parsed.Text);
        Assert.Equal(cursor, parsed.CursorOffset);
    }

    [Fact]
    public void CursorMovement_CountsGraphemesAndCrLfOnce()
    {
        var parsed = SnippetTemplate.Parse("before{cursor}😀e\u0301\r\nx");
        Assert.Equal(4, parsed.CursorSteps);
    }

    [Fact]
    public void Validation_RejectsAmbiguousOrEmptyTemplates()
    {
        var settings = new AppSettings { Snippets = [new() { Trigger = ";a", Text = "a{cursor}b{cursor}" },
            new() { Trigger = ";b", Text = "{cursor}" }] };
        Assert.Contains(settings.Validate(), p => p.Kind == SettingsProblemKind.MultipleCursors);
        Assert.Contains(settings.Validate(), p => p.Kind == SettingsProblemKind.EmptyText);
    }

    [Fact]
    public void ExportImport_PreservesMultilineTemplatesAndCycleOrder()
    {
        var file = Path.Combine(Path.GetTempPath(), "typlo-template-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var text = "Hello, {cursor}!\r\n\tline\r\n\r\n";
            var settings = new AppSettings { Layouts = ["native:fr", "native:de", "native:uk"], LayoutsInitialized = true,
                Snippets = [new() { Trigger = ";g", Text = text }] };
            var store = new SettingsStore(file);
            store.Export(settings, file);
            var imported = store.Import(file);
            Assert.Equal(settings.Layouts, imported.Layouts);
            Assert.Equal(text, Assert.Single(imported.Snippets).Text);
            Assert.True(imported.LayoutsInitialized);
        }
        finally { File.Delete(file); }
    }
}
