using System.Text.Json;
using System.Text.Json.Serialization;
using Typlo.Core.Text;
using Typlo.Core.Imaging;

namespace Typlo.Core.Settings;

public sealed class LayoutHotkey
{
    public string LayoutId { get; set; } = "";
    public string Hotkey { get; set; } = "";
}

/// <summary>Something wrong with a snippet, reported to the user before settings are saved or imported.</summary>
public sealed record SettingsProblem(SettingsProblemKind Kind, string Trigger);

public enum SettingsProblemKind
{
    TriggerTooShort,
    TriggerTooLong,
    DuplicateTrigger,
    EmptyText,
    MultipleCursors,
    DuplicateAppProcess,
    EmptyAppLayouts,
}

/// <summary>An exported settings file that is not valid; <see cref="Problems"/> is empty for "not a Typlo file".</summary>
public sealed class SettingsImportException(string message, IReadOnlyList<SettingsProblem> problems) : Exception(message)
{
    public IReadOnlyList<SettingsProblem> Problems { get; } = problems;
}

public sealed class AppSettings
{
    public const string FormatId = "typlo-settings";
    public const int CurrentSchemaVersion = 2;
    /// <summary>Longest trigger the typing buffer can recognise.</summary>
    public const int MaxTriggerLength = 64;

    /// <summary>Marks the file as Typlo settings (checked on import).</summary>
    public string Format { get; set; } = FormatId;

    /// <summary>Settings file layout version, for future migrations.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Native layout IDs in the user's cycle order; populated from the OS on first start.</summary>
    public List<string> Layouts { get; set; } = [];
    public bool LayoutsInitialized { get; set; }
    public List<ApplicationLayoutRule> ApplicationLayouts { get; set; } = [];

    /// <summary>Converts the selection to the other layout of the pair.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+Shift+Space";
    public string LayoutSwitchHotkey { get; set; } = "";
    public bool WindowsShortcutsInitialized { get; set; }

    public void InitializeWindowsShortcuts()
    {
        if (WindowsShortcutsInitialized) return;
        if (LayoutSwitchHotkey.Length == 0) LayoutSwitchHotkey = "Ctrl+Shift";
        if (ScreenshotCopyHotkey == "Ctrl+Alt+Shift+S") ScreenshotCopyHotkey = "Win+Shift+S";
        WindowsShortcutsInitialized = true;
    }

    /// <summary>Converts the selection into a specific layout.</summary>
    public List<LayoutHotkey> LayoutHotkeys { get; set; } = [];

    /// <summary>Cycles the case of the selection: lower → Title → UPPER.</summary>
    public string CaseHotkey { get; set; } = "Ctrl+Alt+Shift+C";

    /// <summary>Transliterates the selection: «Привет» ⇄ "Privet".</summary>
    public string TranslitHotkey { get; set; } = "Ctrl+Alt+Shift+T";

    /// <summary>Puts back the text replaced by the last layout fix, case change or transliteration.</summary>
    public string UndoHotkey { get; set; } = "Ctrl+Alt+Shift+Z";

    /// <summary>Typed triggers replaced by text (";addr" → address). Stored in settings.json, not encrypted.</summary>
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>With nothing selected, convert only the recently typed input run; never include older adjacent text.</summary>
    public bool ConvertLastWord { get; set; } = true;
    public bool AutoCorrectEnabled { get; set; } = true;

    /// <summary>Also switch the system keyboard layout to the target after converting.</summary>
    public bool SwitchSystemLayout { get; set; } = true;

    public string ClipboardHotkey { get; set; } = "Ctrl+Shift+V";

    public CaptureShape ScreenshotShape { get; set; } = CaptureShape.Rectangle;
    public bool ScreenshotEditorAfterCopy { get; set; } = true;
    public string ScreenshotCopyHotkey { get; set; } = "Ctrl+Alt+Shift+S";
    public string ScreenshotSaveHotkey { get; set; } = "Ctrl+Alt+Shift+F";

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
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (UiLanguage is not ("en" or "ru" or "uk" or "de" or "es" or "pt" or "fr" or "it" or "zh" or "ja")) UiLanguage = "en";
        if (UiTheme is not ("system" or "light" or "dark")) UiTheme = "system";
        Snippets = (Snippets ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Trigger)).ToList();
        foreach (var s in Snippets)
        {
            s.Trigger = s.Trigger.Trim();
            s.Text ??= "";
        }
        LayoutHotkeys = (LayoutHotkeys ?? []).Where(h => h is not null && h.LayoutId is not null).ToList();
        ApplicationLayouts = (ApplicationLayouts ?? []).Where(r => r is not null).ToList();
        foreach (var rule in ApplicationLayouts)
        {
            rule.ProcessName = ApplicationLayoutRule.ProcessKey(rule.ProcessName);
            rule.Layouts = (rule.Layouts ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var h in LayoutHotkeys) h.Hotkey ??= "";
        ExcludedApps = (ExcludedApps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        ToggleHotkey ??= "";
        LayoutSwitchHotkey ??= "";
        ClipboardHotkey ??= "";
        Format = FormatId;
        SchemaVersion = CurrentSchemaVersion;
        CaseHotkey ??= "";
        TranslitHotkey ??= "";
        UndoHotkey ??= "";
        if (!Enum.IsDefined(ScreenshotShape)) ScreenshotShape = CaptureShape.Rectangle;
        ScreenshotCopyHotkey ??= "";
        ScreenshotSaveHotkey ??= "";
    }

    /// <summary>Snippet problems that would make a snippet never fire or fire ambiguously.</summary>
    public IReadOnlyList<SettingsProblem> Validate(IReadOnlyList<global::Typlo.Core.Layouts.KeyboardLayout>? layouts = null)
    {
        var problems = new List<SettingsProblem>();
        var seen = new HashSet<string>();
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ApplicationLayouts ?? [])
        {
            if (!rule.Enabled) continue;
            var process = ApplicationLayoutRule.ProcessKey(rule.ProcessName);
            if (!processes.Add(process)) problems.Add(new(SettingsProblemKind.DuplicateAppProcess, process));
            if (process.Length == 0) problems.Add(new(SettingsProblemKind.EmptyAppLayouts, process));
        }
        foreach (var s in Snippets ?? [])
        {
            var trigger = (s?.Trigger ?? "").Trim();
            if (trigger.Length < 2) problems.Add(new(SettingsProblemKind.TriggerTooShort, trigger));
            else if (trigger.Length > MaxTriggerLength) problems.Add(new(SettingsProblemKind.TriggerTooLong, trigger));
            else
            {
                var keys = SnippetMatcher.NormalizedKeys(trigger, layouts);
                if (keys.Any(seen.Contains)) problems.Add(new(SettingsProblemKind.DuplicateTrigger, trigger));
                foreach (var key in keys) seen.Add(key);
            }
            if (string.IsNullOrEmpty(s?.Text)) problems.Add(new(SettingsProblemKind.EmptyText, trigger));
            else if (SnippetTemplate.Parse(s.Text).CursorCount > 1) problems.Add(new(SettingsProblemKind.MultipleCursors, trigger));
            else if (SnippetTemplate.Parse(s.Text).Text.Length == 0) problems.Add(new(SettingsProblemKind.EmptyText, trigger));
        }
        return problems;
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
                if (s is not null)
                {
                    if (s.SchemaVersion < AppSettings.CurrentSchemaVersion && !File.Exists(Path + ".v1")) File.Copy(Path, Path + ".v1");
                    s.Normalize();
                    return s;
                }
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
    /// <summary>
    /// Reads exported settings. Throws <see cref="SettingsImportException"/> for a file that is not Typlo settings
    /// (an empty object included) or that has invalid snippets; nothing is applied in that case.
    /// </summary>
    public AppSettings Import(string file, IReadOnlyList<global::Typlo.Core.Layouts.KeyboardLayout>? layouts = null)
    {
        var json = File.ReadAllText(file);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var marked = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out var f) &&
                         f.ValueKind == JsonValueKind.String && f.GetString() == AppSettings.FormatId;
            // Files exported before the format marker existed (v0.2.x) still carry the hotkey fields.
            var legacy = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("clipboardHotkey", out _) &&
                         root.TryGetProperty("layouts", out _);
            if (!marked && !legacy) throw new SettingsImportException("Not a Typlo settings file.", []);
            if (root.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var version) && version > AppSettings.CurrentSchemaVersion)
                throw new SettingsImportException("Settings from a newer Typlo version.", []);
        }
        catch (JsonException e)
        {
            throw new SettingsImportException("Not a Typlo settings file: " + e.Message, []);
        }

        var s = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)
                ?? throw new SettingsImportException("Empty settings file.", []);
        var problems = s.Validate(layouts);
        if (problems.Count > 0) throw new SettingsImportException("Invalid snippets.", problems);
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
