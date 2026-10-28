using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Typlo.App.Services;
using Typlo.Core.Storage;

namespace Typlo.App.Views;

/// <summary>Shows one history item in full: all of its text (selectable), the image at full size, or every file.</summary>
public sealed class EntryViewWindow : Window
{
    public EntryViewWindow(AppController controller, ClipEntry entry, nint targetWindow)
    {
        Title = Loc.T("viewer.title");
        Icon = AppIcon.Create();
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var content = controller.Store.Get(entry.Id);
        Control body = content?.Kind switch
        {
            ClipKind.Image when content.ImagePng is { } png => new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new Image { Source = new Bitmap(new MemoryStream(png)), Stretch = Stretch.None },
            },
            ClipKind.Files => ReadOnlyText(string.Join(Environment.NewLine, content.Files)),
            not null => ReadOnlyText(content.Text ?? ""),
            _ => new TextBlock { Text = Loc.T("history.notFound"), Margin = new Thickness(16) },
        };

        var paste = new Button { Content = Loc.T("history.ctx.paste"), Classes = { "accent" }, IsEnabled = content is not null };
        paste.Click += (_, _) =>
        {
            Close();
            _ = Task.Run(() => controller.PasteAsync(entry.Id, targetWindow));
        };
        var close = new Button { Content = Loc.T("update.close") };
        close.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var info = new TextBlock { Text = entry.SourceApp ?? "", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Closed += (_, _) => { if (body is ScrollViewer { Content: Image { Source: Bitmap bitmap } image }) { image.Source = null; bitmap.Dispose(); } };
        Content = new DockPanel
        {
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Padding = new Thickness(16, 10),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 8,
                                Children = { close, paste },
                            },
                            info,
                        },
                    },
                },
                body,
            },
        };
    }

    private static TextBox ReadOnlyText(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 12, 12, 0),
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
        [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };
}
