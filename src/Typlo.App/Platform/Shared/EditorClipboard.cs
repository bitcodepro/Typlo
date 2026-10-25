using System.Text.Json;

namespace Typlo.App.Platform.Shared;

internal static class EditorClipboard
{
    /// <summary>Reads VS Code's "vscode-editor-data" JSON: true when the copy came from an empty selection.</summary>
    public static bool IsFromEmptySelection(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.TrimEnd('\0'));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("isFromEmptySelection", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
