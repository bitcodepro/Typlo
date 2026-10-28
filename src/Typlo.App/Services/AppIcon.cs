using Avalonia.Controls;

namespace Typlo.App.Services;

/// <summary>The same embedded icon is shared with the native tray; loading it does not draw text.</summary>
public static class AppIcon
{
    public static WindowIcon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Typlo.Assets.Typlo.ico")
            ?? throw new InvalidOperationException("Missing application icon");
        return new WindowIcon(stream);
    }
}