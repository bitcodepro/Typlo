using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Typlo.App.Views;

/// <summary>Owns the decoded image only while its virtualized history row is attached.</summary>
public sealed class ClipThumbnail : Image
{
    private Bitmap? _owned;
    private bool _attached;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; Load(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; Release(); base.OnDetachedFromVisualTree(e); }
    protected override void OnDataContextChanged(EventArgs e)
    { base.OnDataContextChanged(e); if (_attached) Load(); }
    private void Load()
    {
        Release();
        try
        {
            if (DataContext is ClipItemViewModel { IsImage: true } item && item.LoadThumbnail() is { Length: > 0 } bytes)
            { _owned = new Bitmap(new MemoryStream(bytes)); Source = _owned; }
        }
        catch (Exception e) { Log.Error("Thumbnail unavailable", e); }
    }
    private void Release() { Source = null; _owned?.Dispose(); _owned = null; }
}
