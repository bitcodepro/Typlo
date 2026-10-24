using System.Buffers.Binary;
using System.Text;

namespace Typlo.App.Platform.Windows;

/// <summary>CF_HDROP: a DROPFILES header followed by NUL-separated paths and a final NUL.</summary>
internal static class DropFiles
{
    private const int HeaderSize = 20; // pFiles, pt.x, pt.y, fNC, fWide

    public static List<string>? Parse(byte[] data)
    {
        if (data.Length < HeaderSize) return null;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(data);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)) != 0;
        if (offset < HeaderSize || offset >= data.Length) return null;
        var text = wide ? Encoding.Unicode.GetString(data, offset, (data.Length - offset) & ~1) : Encoding.Default.GetString(data, offset, data.Length - offset);
        var end = text.IndexOf("\0\0", StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static byte[] Build(IReadOnlyList<string> paths)
    {
        var body = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var data = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1); // fWide
        body.CopyTo(data, HeaderSize);
        return data;
    }
}
