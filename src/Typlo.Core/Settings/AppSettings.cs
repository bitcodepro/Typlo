using System.Text.Json;
using System.Text.Json.Serialization;
using Typlo.Core.Text;

namespace Typlo.Core.Settings;

public sealed class LayoutHotkey
{
    public string LayoutId { get; set; } = "";
    public string Hotkey { get; set; } = "";
}

public sealed class AppSettings
{
    /// <summary>Layouts taking part in conversion, in priority order; Toggle flips between the first two found.</summary>
    public List<string> Layouts { get; set; } = ["en", "ru"];

    /// <summary>Converts the selection to the other layout of the pair.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+Shift+Space";

    /// <summary>Converts the selection into a specific layout.</summary>
    public List<LayoutHotkey> LayoutHotkeys { get; set; } =
    [
        new() { LayoutId = "en", Hotkey = "Ctrl+Alt+Shift+E" },
        new() { LayoutId = "ru", Hotkey = "Ctrl+Alt+Shift+R" },
    ];

    /// <summary>Cycles the case of the selection: lower → Title → UPPER.</summary>
    public string CaseHotkey { get; set; } = "Ctrl+Alt+Shift+C";

    /// <summary>Transliterates the selection: «Привет» ⇄ "Privet".</summary>
    public string TranslitHotkey { get; set; } = "Ctrl+Alt+Shift+T";

    /// <summary>Typed triggers replaced by text (";addr" → address). Stored in settings.json, not encrypted.</summary>
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>With nothing selected, convert the word left of the caret (like Punto Switcher).</summary>
    public bool ConvertLastWord { get; set; } = true;

    /// <summary>Also switch the system keyboard layout to the target after converting.</summary>
    public bool SwitchSystemLayout { get; set; } = true;

    public string ClipboardHotkey { get; set; } = "Ctrl+Shift+V";

    public bool HistoryEnabled { get; set; } = true;
    public int MaxItems { get; set; } = 500;
    public int MaxTotalMegabytes { get; set; } = 200;
    public int MaxImageMegabytes { get; set; } = 20;
    public int MaxTextKilobytes { get; set; } = 1024;

    /// <summary>Process names (without .exe) whose copies are never stored, e.g. password managers.</summary>
    public List<string> ExcludedApps { get; set; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden"];

    public bool StartWithSystem { get; set; }

    /// <summary>Interface theme: "system" (default), "light" or "dark".</summary>
    public string UiTheme { get; set; } = "system";

    /// <summary>Interface language: "en" (default), "ru", "uk", "de", "es", "pt", "fr", "it", "zh" or "ja".</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Keep the display on and the session unlocked, the way video players do.</summary>
    public bool PreventScreenLock { get; set; }

    [JsonIgnore] public long MaxTotalBytes => MaxTotalMegabytes * 1024L * 1024L;
    [JsonIgnore] public long MaxImageBytes => MaxImageMegabytes * 1024L * 1024L;
    [JsonIgnore] public long MaxTextBytes => MaxTextKilobytes * 1024L;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings),
            SettingsJson.Default.AppSettings)!;

    /// <summary>Clamps values loaded from a hand-edited file into sane ranges.</summary>
    public void Normalize()
    {
        MaxItems = Math.Clamp(MaxItems, 10, 100_000);
        MaxTotalMegabytes = Math.Clamp(MaxTotalMegabytes, 1, 100_000);
        MaxImageMegabytes = Math.Clamp(MaxImageMegabytes, 1, 1000);
        MaxTextKilobytes = Math.Clamp(MaxTextKilobytes, 1, 100_000);
        Layouts = (Layouts ?? [])
            .Where(id => global::Typlo.Core.Layouts.KeyboardLayouts.Find(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Layouts.Count < 2) Layouts = ["en", "ru"];
        if (UiLanguage is not ("en" or "ru" or "uk" or "de" or "es" or "pt" or "fr" or "it" or "zh" or "ja")) UiLanguage = "en";
        if (UiTheme is not ("system" or "light" or "dark")) UiTheme = "system";
        Snippets = (Snippets ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Trigger)).ToList();
        CaseHotkey ??= "";
        TranslitHotkey ??= "";
        LayoutHotkeys ??= [];
        ExcludedApps ??= [];
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;

public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsJson.Default.AppSettings);
                if (s is not null) { s.Normalize(); return s; }
            }
        }
        catch (JsonException)
        {
            // A broken file falls back to defaults; keep a copy so the user's edits are not lost.
            File.Copy(Path, Path + ".broken", overwrite: true);
        }
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    /// <summary>Writes the settings to a file chosen by the user (for moving to another computer).</summary>
    public void Export(AppSettings settings, string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
    }

    /// <summary>Reads exported settings; throws if the file is not a Typlo settings file.</summary>
    public AppSettings Import(string file)
    {
        var s = JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJson.Default.AppSettings)
                ?? throw new InvalidDataException("Empty settings file.");
        s.Normalize();
        return s;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
        File.Move(tmp, Path, overwrite: true);
    }
}
