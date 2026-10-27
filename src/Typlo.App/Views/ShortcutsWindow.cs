using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typlo.App.Services;
using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.App.Views;

/// <summary>Every keyboard shortcut of Typlo in one place, with the user's current global hotkeys.</summary>
public sealed class ShortcutsWindow : Window
{
    public ShortcutsWindow(AppSettings settings, Action? openSettings, IReadOnlyList<KeyboardLayout>? layouts = null)
    {
        Title = Loc.T("shortcuts.title");
        Icon = AppIcon.Create();
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(22, 10, 22, 22), Spacing = 2 };
        string Key(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? Loc.T("hotkey.off") : hotkey;

        Section(body, Loc.T("shortcuts.global"), Loc.T("shortcuts.globalHint"));
        Row(body, Key(settings.ToggleHotkey), Loc.T("shortcuts.toggle"));
        foreach (var lh in settings.LayoutHotkeys)
            if (layouts?.FirstOrDefault(l => l.Id == lh.LayoutId) is { } layout)
                Row(body, Key(lh.Hotkey), Loc.T("settings.convertTo", layout.Name));
        Row(body, Key(settings.CaseHotkey), Loc.T("shortcuts.case"));
        Row(body, Key(settings.TranslitHotkey), Loc.T("shortcuts.translit"));
        Row(body, Key(settings.UndoHotkey), Loc.T("shortcuts.undo"));
        Row(body, Key(settings.ClipboardHotkey), Loc.T("shortcuts.history"));
        Row(body, Key(settings.ClipboardHotkey) + " ×2", Loc.T("shortcuts.previous"));

        Section(body, Loc.T("capture.title"), Loc.T("capture.settingsHint"));
        Row(body, Key(settings.ScreenshotCopyHotkey), Loc.T("capture.copy"));
        Row(body, Key(settings.ScreenshotSaveHotkey), Loc.T("capture.save"));
        Row(body, "R / F", Loc.T("capture.changeMode"));
        Row(body, "Esc", Loc.T("settings.cancel"));

        Section(body, Loc.T("shortcuts.historyWindow"), null);
        Row(body, "↑ ↓  PgUp PgDn", Loc.T("shortcuts.select"));
        Row(body, "Enter", Loc.T("history.ctx.paste"));
        Row(body, "Shift+Enter", Loc.T("history.ctx.plain"));
        Row(body, "Ctrl+1 … Ctrl+9", Loc.T("shortcuts.quick"));
        Row(body, "Ctrl+O", Loc.T("history.ctx.view"));
        Row(body, "Ctrl+T", Loc.T("history.ctx.ocr"));
        Row(body, "Ctrl+P", Loc.T("history.ctx.pin") + " / " + Loc.T("history.ctx.unpin"));
        Row(body, "Del", Loc.T("history.ctx.delete"));
        Row(body, Loc.T("shortcuts.typing"), Loc.T("shortcuts.search"));
        Row(body, "Esc", Loc.T("update.close"));

        Section(body, Loc.T("settings.snippetsHeader"), null);
        Row(body, settings.Snippets.Count > 0 ? string.Join("  ", settings.Snippets.Select(s => s.Trigger)) : ";addr", Loc.T("shortcuts.snippets"));

        var settingsButton = new Button { Content = Loc.T("shortcuts.change"), IsVisible = openSettings is not null };
        settingsButton.Click += (_, _) => { Close(); openSettings?.Invoke(); };
        var close = new Button { Content = Loc.T("update.close"), Classes = { "accent" } };
        close.Click += (_, _) => Close();
        Content = new DockPanel
        {
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Padding = new Thickness(20, 12),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBorderBrush"),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { settingsButton, close },
                    },
                },
                new ScrollViewer { Content = body },
            },
        };
    }

    private static void Section(Panel body, string title, string? hint)
    {
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        if (hint is not null)
            body.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
    }

    private static void Row(Panel body, string keys, string action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*"), Margin = new Thickness(0, 3) };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BadgeBrush"),
            Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12.5 },
        };
        var text = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        body.Children.Add(grid);
    }
}
