using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Typlo.App.Platform.Shared;
using Typlo.Core.Layouts;

namespace Typlo.App.Platform.Linux;

[SupportedOSPlatform("linux")]
internal static partial class LinuxKeyboardLayouts
{
    private const string Lib = "libxkbcommon.so.0";
    private static readonly string[] Keys =
    ["TLDE","AE01","AE02","AE03","AE04","AE05","AE06","AE07","AE08","AE09","AE10","AE11","AE12",
     "AD01","AD02","AD03","AD04","AD05","AD06","AD07","AD08","AD09","AD10","AD11","AD12",
     "AC01","AC02","AC03","AC04","AC05","AC06","AC07","AC08","AC09","AC10","AC11",
     "AB01","AB02","AB03","AB04","AB05","AB06","AB07","AB08","AB09","AB10","BKSL"];

    internal sealed record Source(string Layout, string Variant)
    {
        public string Id => $"xkb:{Layout}:{Variant}";
    }

    public static IReadOnlyList<Source> Configured() => ReadSources(GnomeInputSources.IsSession, GnomeInputSources.Run);

    // GNOME on X11 loads only a subset of its configured sources into XKB. The configured catalog
    // and the loaded group indices are different namespaces and must never be used interchangeably.
    internal static IReadOnlyList<Source> ReadSources(bool gnomeSession, Func<string, string[], Proc.Result> run)
    {
        if (gnomeSession)
        {
            var result = run("gsettings", ["get", "org.gnome.desktop.input-sources", "sources"]);
            var sources = Regex.Matches(result.Ok ? result.Text : "", @"\('xkb',\s*'([^']+)'\)")
                .Select(m => m.Groups[1].Value.Split('+', 2)).Select(parts => new Source(parts[0], parts.Length > 1 ? parts[1] : "")).ToArray();
            if (sources.Length > 0) return sources;
        }
        var query = run("setxkbmap", ["-query"]);
        return query.Ok ? ParseQuery(query.Text) : [];
    }

    public static IReadOnlyList<Source> Loaded()
    {
        var result = GnomeInputSources.Run("setxkbmap", ["-query"]);
        return result.Ok ? ParseQuery(result.Text) : [];
    }

    internal static IReadOnlyList<Source> ParseQuery(string query)
    {
        string[] Values(string key) => query.Split('\n').FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.Ordinal))?
            .Split(':', 2)[1].Trim().Split(',', StringSplitOptions.TrimEntries) ?? [];
        var layouts = Values("layout");
        var variants = Values("variant");
        return layouts.Where(l => l.Length > 0).Select((layout, i) => new Source(layout, variants.ElementAtOrDefault(i) ?? "")).ToArray();
    }

    public static IReadOnlyList<KeyboardLayout> Read()
    {
        var sources = Configured();
        XDocument? rules = null;
        try { rules = XDocument.Load("/usr/share/X11/xkb/rules/evdev.xml"); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        var result = new List<KeyboardLayout>();
        foreach (var source in sources)
        {
            var info = rules?.Descendants("layout").FirstOrDefault(l => l.Element("configItem")?.Element("name")?.Value == source.Layout);
            var config = info?.Element("configItem");
            var name = config?.Element("description")?.Value ?? source.Layout;
            if (source.Variant.Length > 0)
                name = info?.Descendants("variant").FirstOrDefault(v => v.Element("configItem")?.Element("name")?.Value == source.Variant)?
                    .Element("configItem")?.Element("description")?.Value ?? name + " (" + source.Variant + ")";
            var iso3 = config?.Element("languageList")?.Element("iso639Id")?.Value;
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.ThreeLetterISOLanguageName == iso3);
            var maps = Map(source);
            result.Add(new(source.Id, name, culture?.LCID ?? 0, maps.Normal, maps.Shifted,
                culture?.TwoLetterISOLanguageName ?? source.Layout));
        }
        return result;
    }

    private static (string Normal, string Shifted) Map(Source source)
    {
        var empty = new string('\0', Keys.Length);
        nint context = 0, keymap = 0, state = 0;
        try
        {
            context = xkb_context_new(0);
            if (context == 0) return (empty, empty);
            var names = new Names { Layout = Marshal.StringToCoTaskMemUTF8(source.Layout), Variant = Marshal.StringToCoTaskMemUTF8(source.Variant) };
            try { keymap = xkb_keymap_new_from_names(context, ref names, 0); }
            finally { Marshal.FreeCoTaskMem(names.Layout); Marshal.FreeCoTaskMem(names.Variant); }
            if (keymap == 0) return (empty, empty);
            state = xkb_state_new(keymap);
            if (state == 0) return (empty, empty);
            string Characters(uint modifiers)
            {
                xkb_state_update_mask(state, modifiers, 0, 0, 0, 0, 0);
                return new string(Keys.Select(name =>
                {
                    var code = xkb_keymap_key_by_name(keymap, name);
                    var value = code == uint.MaxValue ? 0 : xkb_state_key_get_utf32(state, code);
                    return value is > 0 and <= 0xffff && !char.IsSurrogate((char)value) && !char.IsControl((char)value) ? (char)value : '\0';
                }).ToArray());
            }
            var normal = Characters(0);
            var shift = xkb_keymap_mod_get_index(keymap, "Shift");
            return (normal, Characters(shift < 32 ? 1u << (int)shift : 0));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Log.Error("XKB layout discovery unavailable", e); return (empty, empty); }
        finally
        {
            if (state != 0) xkb_state_unref(state);
            if (keymap != 0) xkb_keymap_unref(keymap);
            if (context != 0) xkb_context_unref(context);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Names { public nint Rules, Model, Layout, Variant, Options; }
    [LibraryImport(Lib)] private static partial nint xkb_context_new(uint flags);
    [LibraryImport(Lib)] private static partial void xkb_context_unref(nint context);
    [LibraryImport(Lib)] private static partial nint xkb_keymap_new_from_names(nint context, ref Names names, uint flags);
    [LibraryImport(Lib)] private static partial void xkb_keymap_unref(nint keymap);
    [LibraryImport(Lib)] private static partial nint xkb_state_new(nint keymap);
    [LibraryImport(Lib)] private static partial void xkb_state_unref(nint state);
    [LibraryImport(Lib)] private static partial uint xkb_state_update_mask(nint state, uint depressed, uint latched, uint locked, uint depressedLayout, uint latchedLayout, uint lockedLayout);
    [LibraryImport(Lib)] private static partial uint xkb_state_key_get_utf32(nint state, uint key);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial uint xkb_keymap_key_by_name(nint keymap, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial uint xkb_keymap_mod_get_index(nint keymap, string name);
}
