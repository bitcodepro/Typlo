using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SharpHook.Data;
using Typlo.App.Platform.Shared;
using Typlo.Core.Layouts;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

/// <summary>Reads enabled Windows layouts and translates physical scan codes without changing keyboard state.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsKeyboardLayouts
{
    // Same physical ANSI positions as KeyboardLayouts.English; these are keys, not a language list.
    private static readonly uint[] Scans =
    [0x29,2,3,4,5,6,7,8,9,10,11,12,13,16,17,18,19,20,21,22,23,24,25,26,27,
     30,31,32,33,34,35,36,37,38,39,40,44,45,46,47,48,49,50,51,52,53,43];

    public static string Id(nint handle) => $"win:{unchecked((uint)(long)handle):x8}";

    // SimpleGlobalHook dispatches synchronously on libuiohook's thread. That library maps letter/digit
    // scans through MapVirtualKeyW using THIS thread's layout, whereas RawCode is the foreground VK.
    // Invert the same mapping before comparing recent keys/snippet triggers with physical layout maps.
    public static char? PhysicalCharacter(KeyCode code, ushort raw, bool shift)
    {
        var inputThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        return PhysicalCharacter(code, raw, shift, GetKeyboardLayout(0), GetKeyboardLayout(inputThread));
    }

    internal static char? PhysicalCharacter(KeyCode code, ushort raw, bool shift, nint hookLayout, nint inputLayout)
    {
        if (code == KeyCode.VcSpace) return ' ';
        uint vk;
        nint layout;
        if (raw is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a)
        {
            var name = KeyCodes.ToName(code);
            if (name is not { Length: 1 } || !char.IsAsciiLetterOrDigit(name[0])) return null;
            vk = name[0];
            layout = hookLayout;
        }
        else if (raw is >= 0xba and <= 0xc0 or >= 0xdb and <= 0xdf or 0xe2)
        {
            // OEM keys are not remapped by libuiohook: their VK belongs to the input window's layout.
            vk = raw;
            layout = inputLayout;
        }
        else return null;
        if (layout == 0) return null;
        var scan = MapVirtualKeyExW(vk, 4 /* MAPVK_VK_TO_VSC_EX */, layout);
        var index = Array.IndexOf(Scans, scan);
        return index < 0 ? null : (shift ? KeyboardLayouts.English.Shifted : KeyboardLayouts.English.Normal)[index];
    }

    public static IReadOnlyList<KeyboardLayout> Read()
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];
        var handles = new nint[count];
        count = GetKeyboardLayoutList(handles.Length, handles);
        var result = new List<KeyboardLayout>();
        foreach (var handle in handles.Take(count))
        {
            var lang = (int)((long)handle & 0xffff);
            var language = "";
            var name = Id(handle);
            try
            {
                var culture = CultureInfo.GetCultureInfo(lang);
                language = culture.TwoLetterISOLanguageName;
                name = culture.NativeName;
            }
            catch (CultureNotFoundException) { }
            var variant = LayoutName(handle, lang);
            if (!string.IsNullOrEmpty(variant)) name += " — " + variant;
            var normal = new string(Scans.Select(scan => Character(handle, scan, false)).ToArray());
            var shifted = new string(Scans.Select(scan => Character(handle, scan, true)).ToArray());
            // ImmIsIME returns nonzero for ordinary TSF-backed layouts on modern Windows.
            // Use the IME module (and legacy IME handle tag) instead of disabling every keyboard.
            var ime = ImeFileLength(handle) > 0 || ((((ulong)(long)handle >> 16) & 0xf000) == 0xe000);
            result.Add(new(Id(handle), name, lang, normal, shifted, language, !ime));
        }
        return result;
    }

    private static string? LayoutName(nint handle, int language)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (root is null) return null;
            var high = ((long)handle >> 16) & 0xffff;
            var keyName = language.ToString("x8");
            if ((high & 0xf000) == 0xf000)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (uint.TryParse(name, NumberStyles.HexNumber, null, out var code) && (code & 0xffff) == language &&
                        int.TryParse(key?.GetValue("Layout Id") as string, NumberStyles.HexNumber, null, out var id) && id == (high & 0x0fff))
                    { keyName = name; break; }
                }
            }
            using var layout = root.OpenSubKey(keyName);
            return layout?.GetValue("Layout Text") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static unsafe char Character(nint layout, uint scan, bool shift)
    {
        var state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if (shift) state[VK_SHIFT] = 0x80;
        var buffer = stackalloc char[8];
        var vk = MapVirtualKeyExW(scan, 3 /* MAPVK_VSC_TO_VK_EX */, layout);
        var count = ToUnicodeEx(vk, scan, state, buffer, 8, 4 /* do not change dead-key state */, layout);
        // Dead keys, ligatures and IME compositions cannot be represented as one physical-key character.
        return count == 1 && !char.IsControl(buffer[0]) && !char.IsSurrogate(buffer[0]) ? buffer[0] : '\0';
    }

    public static string? Current(nint window)
    {
        var thread = window == 0 ? 0 : GetWindowThreadProcessId(window, out _);
        var handle = GetKeyboardLayout(thread);
        return handle == 0 ? null : Id(handle);
    }

    public static bool Switch(nint window, KeyboardLayout layout)
    {
        if (window == 0 || !layout.Id.StartsWith("win:", StringComparison.Ordinal) ||
            !uint.TryParse(layout.Id[4..], NumberStyles.HexNumber, null, out var bits)) return false;
        var count = GetKeyboardLayoutList(0, null);
        var handles = new nint[Math.Max(0, count)];
        GetKeyboardLayoutList(handles.Length, handles);
        var handle = handles.FirstOrDefault(h => unchecked((uint)(long)h) == bits);
        if (handle == 0) return false;
        var root = GetAncestor(window, 2);
        return PostMessageW(root != 0 ? root : window, WM_INPUTLANGCHANGEREQUEST, 0, handle);
    }

    [LibraryImport("user32.dll")] private static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyExW(uint code, uint type, nint layout);
    [LibraryImport("user32.dll")] private static unsafe partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buffer, int length, uint flags, nint layout);
    private static unsafe uint ImeFileLength(nint layout) => ImmGetIMEFileNameW(layout, null, 0);
    [LibraryImport("imm32.dll")] private static unsafe partial uint ImmGetIMEFileNameW(nint layout, char* buffer, uint length);
}
