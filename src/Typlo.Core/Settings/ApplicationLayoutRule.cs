namespace Typlo.Core.Settings;

/// <summary>An exact process-name match and an ordered override for Typlo's layout cycle.</summary>
public sealed class ApplicationLayoutRule
{
    public string ProcessName { get; set; } = "";
    public List<string> Layouts { get; set; } = [];
    public bool? AutoCorrection { get; set; }
    public bool? SnippetExpansion { get; set; }
    public bool Enabled { get; set; } = true;

    public static string ProcessKey(string? value)
    {
        var name = (value ?? "").Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
