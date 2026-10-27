using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Platform.Linux;

/// <summary>
/// Switches the X11 keyboard layout group (XkbLockGroup), the way layout indicators do.
/// The group order comes from <c>setxkbmap -query</c> ("layout: us,ru").
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class XkbLayouts
{
    private const string LibX11 = "libX11.so.6";
    private const uint XkbUseCoreKbd = 0x0100;

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)] private static partial nint XOpenDisplay(string? name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(LibX11)] private static partial int XFlush(nint display);
    [LibraryImport(LibX11)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool XkbLockGroup(nint display, uint device, uint group);
    [LibraryImport(LibX11)] private static partial int XkbGetState(nint display, uint device, nint state);

    public static bool Available => Proc.Has("setxkbmap") && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static IReadOnlyList<string> ConfiguredLayouts()
    {
        var r = Proc.Run("setxkbmap", ["-query"]);
        if (!r.Ok) return [];
        var line = r.Text.Split('\n').FirstOrDefault(l => l.StartsWith("layout:", StringComparison.Ordinal));
        return line is null ? [] : line["layout:".Length..].Trim().Split(',', StringSplitOptions.TrimEntries);
    }

    public static bool SwitchGroup(int group) => group is >= 0 and < 4 && WithDisplay(d => XkbLockGroup(d, XkbUseCoreKbd, (uint)group));

    /// <summary>The active group index, or -1.</summary>
    public static int CurrentGroup()
    {
        var group = -1;
        WithDisplay(d =>
        {
            var state = Marshal.AllocHGlobal(64);
            try
            {
                if (XkbGetState(d, XkbUseCoreKbd, state) != 0) return false;
                group = Marshal.ReadByte(state); // XkbStateRec.group
                return true;
            }
            finally { Marshal.FreeHGlobal(state); }
        });
        return group;
    }

    private static bool WithDisplay(Func<nint, bool> action)
    {
        try
        {
            var display = XOpenDisplay(null);
            if (display == 0) return false;
            try
            {
                var ok = action(display);
                XFlush(display);
                return ok;
            }
            finally { XCloseDisplay(display); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("libX11 unavailable", e);
            return false;
        }
    }
}
