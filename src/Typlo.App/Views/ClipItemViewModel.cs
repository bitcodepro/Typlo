using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Typlo.App.Services;
using Typlo.Core.Storage;

namespace Typlo.App.Views;

public sealed class ClipItemViewModel : INotifyPropertyChanged
{
    private string _badge = "";
    private bool _pinned;
    private Bitmap? _thumbnail;

    public ClipItemViewModel(ClipEntry entry)
    {
        Entry = entry;
        _pinned = entry.Pinned;
        Title = entry.Kind switch
        {
            ClipKind.Text => Shorten(entry.PreviewText),
            ClipKind.Files => FileTitle(entry.Files),
            _ => "",
        };
        var kind = entry.Kind switch
        {
            ClipKind.Image => Loc.T("history.image", entry.Width, entry.Height),
            ClipKind.Files => entry.Files.Count == 1 ? Loc.T("history.file") : Loc.T("history.files", entry.Files.Count),
            _ => FormatChars(entry),
        };
        var formatted = entry.HasFormatting ? Loc.T("history.formatted") : null;
        Meta = string.Join("  ·  ", new[] { kind, formatted, entry.SourceApp, Ago(entry.LastUsed) }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public ClipEntry Entry { get; }
    public bool IsImage => Entry.Kind == ClipKind.Image;
    public bool IsText => Entry.Kind is ClipKind.Text or ClipKind.Files;

    /// <summary>"📄 report.pdf" lines for the first few files, then "and N more".</summary>
    private static string FileTitle(IReadOnlyList<string> files)
    {
        const int shown = 4;
        var lines = files.Take(shown).Select(f => "📄 " + Path.GetFileName(f.TrimEnd('\\', '/')));
        var title = string.Join('\n', lines);
        return files.Count > shown ? title + "\n" + Loc.T("history.more", files.Count - shown) : title;
    }
    public string Title { get; }

    /// <summary>Hover text: the stored start of the text (up to 400 chars) or every file path; Ctrl+O shows everything.</summary>
    public string? Tooltip => Entry.Kind switch
    {
        ClipKind.Text => Entry.PreviewText.Length >= ClipEntry.PreviewChars ? Entry.PreviewText + "…" : Entry.PreviewText,
        ClipKind.Files => string.Join('\n', Entry.Files),
        _ => null,
    };
    public string Meta { get; }

    /// <summary>Thumbnails are decoded lazily, only for items that get displayed.</summary>
    public Bitmap? Thumbnail => _thumbnail ??= Entry.Thumbnail is { Length: > 0 } t ? new Bitmap(new MemoryStream(t)) : null;

    public string Badge
    {
        get => _badge;
        set => Set(ref _badge, value);
    }

    public bool Pinned
    {
        get => _pinned;
        set => Set(ref _pinned, value);
    }

    public bool Matches(string query) =>
        string.IsNullOrEmpty(query) ||
        Entry.PreviewText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        (Entry.SourceApp?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (IsImage && (Loc.T("history.imageSearch") + " image").Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private static string Shorten(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(4);
        var joined = string.Join('\n', lines).Replace('\t', ' ');
        return joined.Length > 300 ? joined[..300] + "…" : joined;
    }

    private static string FormatChars(ClipEntry e)
    {
        var lines = e.PreviewText.Count(c => c == '\n') + 1;
        var size = e.Size < 1024 ? Loc.T("history.bytes", e.Size) : Loc.T("history.kb", e.Size / 1024.0);
        return lines > 1 ? Loc.T("history.lines", lines, size) : size;
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalMinutes < 1) return Loc.T("history.justNow");
        if (d.TotalHours < 1) return Loc.T("history.minAgo", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Loc.T("history.hAgo", (int)d.TotalHours);
        if (d.TotalDays < 7) return Loc.T("history.dAgo", (int)d.TotalDays);
        return t.LocalDateTime.ToString("d MMM yyyy");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
