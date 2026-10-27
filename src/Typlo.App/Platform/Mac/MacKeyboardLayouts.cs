using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Typlo.Core.Layouts;

namespace Typlo.App.Platform.Mac;

[SupportedOSPlatform("macos")]
internal static partial class MacKeyboardLayouts
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> Module = new(() => NativeLibrary.Load(Carbon));
    private static readonly ushort[] Keys =
    [50,18,19,20,21,23,22,26,28,25,29,27,24,12,13,14,15,17,16,32,34,31,35,33,30,
     0,1,2,3,5,4,38,40,37,41,39,6,7,8,9,11,45,46,43,47,44,42];

    private static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Module.Value, name));
    private static nint Property(nint source, string name) => TISGetInputSourceProperty(source, Constant(name));
    private static string String(nint value) => value == 0 ? "" : ObjC.ToStr(value) ?? "";
    private static bool Selectable(nint source) => Property(source, "kTISPropertyInputSourceIsSelectCapable") is var value && value != 0 && CFBooleanGetValue(value);

    public static IReadOnlyList<KeyboardLayout> Read()
    {
        var list = TISCreateInputSourceList(0, false);
        if (list == 0) return [];
        try
        {
            var result = new List<KeyboardLayout>();
            for (nint i = 0; i < CFArrayGetCount(list); i++)
            {
                var source = CFArrayGetValueAtIndex(list, i);
                if (String(Property(source, "kTISPropertyInputSourceCategory")) != String(Constant("kTISCategoryKeyboardInputSource")) || !Selectable(source)) continue;
                var id = String(Property(source, "kTISPropertyInputSourceID"));
                var name = String(Property(source, "kTISPropertyLocalizedName"));
                var languages = Property(source, "kTISPropertyInputSourceLanguages");
                var language = languages != 0 && CFArrayGetCount(languages) > 0 ? String(CFArrayGetValueAtIndex(languages, 0)) : "";
                var langId = 0;
                try { var culture = CultureInfo.GetCultureInfo(language); langId = culture.LCID; language = culture.TwoLetterISOLanguageName; }
                catch (CultureNotFoundException) { }
                var data = Property(source, "kTISPropertyUnicodeKeyLayoutData");
                var layout = data == 0 ? 0 : CFDataGetBytePtr(data);
                var normal = new string(Keys.Select(key => Character(layout, key, false)).ToArray());
                var shifted = new string(Keys.Select(key => Character(layout, key, true)).ToArray());
                result.Add(new("mac:" + id, name, langId, normal, shifted, language, layout != 0));
            }
            return result;
        }
        finally { CFRelease(list); }
    }

    public static string? Current()
    {
        var source = TISCopyCurrentKeyboardInputSource();
        if (source == 0) return null;
        try { return "mac:" + String(Property(source, "kTISPropertyInputSourceID")); }
        finally { CFRelease(source); }
    }

    public static bool Switch(string id)
    {
        var list = TISCreateInputSourceList(0, false);
        if (list == 0) return false;
        try
        {
            for (nint i = 0; i < CFArrayGetCount(list); i++)
            {
                var source = CFArrayGetValueAtIndex(list, i);
                if ("mac:" + String(Property(source, "kTISPropertyInputSourceID")) == id && Selectable(source))
                    return TISSelectInputSource(source) == 0;
            }
            return false;
        }
        finally { CFRelease(list); }
    }

    private static unsafe char Character(nint layout, ushort key, bool shift)
    {
        if (layout == 0) return '\0';
        uint dead = 0;
        var buffer = stackalloc char[8];
        var status = UCKeyTranslate(layout, key, 3 /* display */, shift ? 2u : 0u, LMGetKbdType(),
            1 /* no new dead-key state */, ref dead, 8, out var length, buffer);
        return status == 0 && length == 1 && !char.IsControl(buffer[0]) && !char.IsSurrogate(buffer[0]) ? buffer[0] : '\0';
    }

    [LibraryImport(Carbon)] private static partial nint TISCreateInputSourceList(nint filter, [MarshalAs(UnmanagedType.I1)] bool includeAll);
    [LibraryImport(Carbon)] private static partial nint TISCopyCurrentKeyboardInputSource();
    [LibraryImport(Carbon)] private static partial nint TISGetInputSourceProperty(nint source, nint property);
    [LibraryImport(Carbon)] private static partial int TISSelectInputSource(nint source);
    [LibraryImport(Carbon)] private static partial byte LMGetKbdType();
    [LibraryImport(Carbon)] private static unsafe partial int UCKeyTranslate(nint layout, ushort key, ushort action, uint modifiers,
        uint keyboardType, uint options, ref uint dead, nuint maxLength, out nuint length, char* buffer);
    [LibraryImport(CF)] private static partial nint CFArrayGetCount(nint array);
    [LibraryImport(CF)] private static partial nint CFArrayGetValueAtIndex(nint array, nint index);
    [LibraryImport(CF)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static partial bool CFBooleanGetValue(nint value);
    [LibraryImport(CF)] private static partial void CFRelease(nint value);
}
