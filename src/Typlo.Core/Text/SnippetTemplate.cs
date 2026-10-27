using System.Globalization;
using System.Text;

namespace Typlo.Core.Text;

/// <summary>A plain-text template. Line breaks/indentation are literal; {{cursor}} escapes the marker.</summary>
public sealed record SnippetTemplate(string Text, int? CursorOffset, int CursorCount)
{
    public const string Marker = "{cursor}";
    public string Suffix => CursorOffset is { } offset ? Text[offset..] : "";
    public int CursorSteps => StringInfo.ParseCombiningCharacters(Suffix.Replace("\r\n", "\n")).Length;

    public static SnippetTemplate Parse(string text)
    {
        var output = new StringBuilder();
        int? cursor = null;
        var count = 0;
        for (var i = 0; i < text.Length;)
        {
            if (text.AsSpan(i).StartsWith("{{cursor}}", StringComparison.Ordinal))
            {
                output.Append(Marker);
                i += Marker.Length + 2;
            }
            else if (text.AsSpan(i).StartsWith(Marker, StringComparison.Ordinal))
            {
                cursor ??= output.Length;
                count++;
                i += Marker.Length;
            }
            else output.Append(text[i++]);
        }
        return new(output.ToString(), cursor, count);
    }
}
