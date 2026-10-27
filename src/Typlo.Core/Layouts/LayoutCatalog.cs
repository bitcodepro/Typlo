using Typlo.Core.Settings;

namespace Typlo.Core.Layouts;

/// <summary>Installed OS layouts and migration of saved language aliases to stable native identities.</summary>
public sealed class LayoutCatalog(IEnumerable<KeyboardLayout> installed)
{
    public IReadOnlyList<KeyboardLayout> All { get; } = installed.DistinctBy(l => l.Id).ToArray();
    public KeyboardLayout? Find(string? id) => id is null ? null :
        All.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ??
        All.FirstOrDefault(l => l.LanguageCode.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void Synchronize(AppSettings settings)
    {
        if (All.Count == 0) return; // a failed OS query must not erase the user's configuration
        var fresh = !settings.LayoutsInitialized && settings.Layouts.Count == 0 && settings.LayoutHotkeys.Count == 0;
        settings.Layouts = settings.Layouts.Select(id => Find(id)?.Id ?? id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var row in settings.LayoutHotkeys) row.LayoutId = Find(row.LayoutId)?.Id ?? row.LayoutId;
        settings.LayoutHotkeys = settings.LayoutHotkeys.DistinctBy(h => h.LayoutId, StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh || settings.Layouts.Count == 0)
            settings.Layouts = All.Where(l => l.SupportsConversion).Select(l => l.Id).ToList();
        foreach (var layout in All)
        {
            if (settings.LayoutHotkeys.Any(h => h.LayoutId == layout.Id)) continue;
            var index = All.ToList().IndexOf(layout);
            var shortcut = fresh && index < 3 ? $"Ctrl+Alt+Shift+{index + 1}" : "";
            settings.LayoutHotkeys.Add(new LayoutHotkey { LayoutId = layout.Id, Hotkey = shortcut });
        }
        settings.LayoutsInitialized = true;
    }

    public IReadOnlyList<KeyboardLayout> Cycle(AppSettings settings) => settings.Layouts
        .Select(Find).OfType<KeyboardLayout>().Where(l => l.SupportsConversion).DistinctBy(l => l.Id).ToArray();
}
