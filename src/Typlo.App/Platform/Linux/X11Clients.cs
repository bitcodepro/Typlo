using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Platform.Linux;

/// <summary>
/// X11 facts Typlo needs to tell its own input and the target app's copies apart: the process behind the clipboard
/// owner (X-Resource extension) and the spare key code libuiohook types text with.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class X11Clients
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXRes = "libXRes.so.1";
    private const uint XResClientIdPidMask = 1 << 1;

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)] private static partial nint XOpenDisplay(string? name);
    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(LibX11)] private static partial nuint XGetSelectionOwner(nint display, nuint selection);
    [LibraryImport(LibX11)] private static partial nuint XDefaultRootWindow(nint display);
    [LibraryImport(LibX11)] private static partial int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, [MarshalAs(UnmanagedType.Bool)] bool delete, nuint requestedType,
        out nuint actualType, out int format, out nuint count, out nuint remaining, out nint data);
    [LibraryImport(LibX11)] private static partial int XDisplayKeycodes(nint display, out int min, out int max);
    [LibraryImport(LibX11)] private static partial nint XGetKeyboardMapping(nint display, byte first, int count, out int keysymsPerKeycode);
    [LibraryImport(LibX11)] private static partial int XFree(nint data);
    [LibraryImport(LibXRes)] private static partial int XResQueryClientIds(nint display, nint numSpecs, ref ClientIdSpec specs, out nint numIds, out nint ids);
    [LibraryImport(LibXRes)] private static partial void XResClientIdsDestroy(nint numIds, nint ids);
    [LibraryImport(LibXRes)] private static partial int XResGetClientPid(nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientIdSpec
    {
        public nuint Client;
        public uint Mask;
    }

    // XResClientIdValue: { XResClientIdSpec spec; long length; void *value; }
    private static readonly int ClientIdValueSize = Marshal.SizeOf<ClientIdSpec>() + 2 * nint.Size;

    private static readonly Lock Gate = new();
    private static nint _display;
    private static bool _opened, _xresMissing;

    private static nint Display()
    {
        if (!_opened)
        {
            _opened = true;
            try { _display = XOpenDisplay(null); }
            catch (Exception e) { Log.Error("Cannot open the X display", e); }
        }
        return _display;
    }

    /// <summary>The EWMH active window, without starting an xdotool process for every typed character.</summary>
    public static nint ActiveWindow()
    {
        lock (Gate)
        {
            var display = Display();
            if (display == 0) return 0;
            var atom = XInternAtom(display, "_NET_ACTIVE_WINDOW", true);
            if (atom == 0) return 0;
            var status = XGetWindowProperty(display, XDefaultRootWindow(display), atom, 0, 1, false, 0,
                out _, out var format, out var count, out _, out var data);
            try
            {
                // Xlib represents a format-32 property as native longs, padded to 64 bits on a 64-bit host.
                return status == 0 && format == 32 && count == 1 && data != 0 ? Marshal.ReadIntPtr(data) : 0;
            }
            finally { if (data != 0) XFree(data); }
        }
    }

    /// <summary>
    /// Whether the clipboard owner and <paramref name="window"/> belong to the same process, or null when the
    /// X server cannot say (no X-Resource extension, no PID for one of them).
    /// </summary>
    public static bool? ClipboardOwnedBySameProcess(nint window)
    {
        lock (Gate)
        {
            if (_xresMissing || window == 0 || Display() is var display && display == 0) return null;
            try
            {
                var owner = XGetSelectionOwner(display, XInternAtom(display, "CLIPBOARD", false));
                if (owner == 0) return false; // nobody owns the clipboard: certainly not the target
                if (ProcessOf(display, owner) is not { } ownerPid || ProcessOf(display, (nuint)window) is not { } windowPid) return null;
                return ownerPid == windowPid;
            }
            catch (DllNotFoundException)
            {
                _xresMissing = true;
                Log.Info("libXRes is not installed: clipboard copies are checked without knowing their owner");
                return null;
            }
        }
    }

    private static int? ProcessOf(nint display, nuint resource)
    {
        var spec = new ClientIdSpec { Client = resource, Mask = XResClientIdPidMask };
        if (XResQueryClientIds(display, 1, ref spec, out var count, out var ids) != 0) return null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var pid = XResGetClientPid(ids + i * ClientIdValueSize);
                if (pid > 0) return pid;
            }
            return null;
        }
        finally { XResClientIdsDestroy(count, ids); }
    }

    /// <summary>
    /// The key code libuiohook's text entry will use: the highest one with no key symbols, which it remaps to each
    /// character in turn (post_event.c, find_unused_keycode). A real key press never comes from it.
    /// </summary>
    public static ushort? TextEntryKeycode()
    {
        lock (Gate)
        {
            if (Display() is var display && display == 0) return null;
            if (XDisplayKeycodes(display, out var min, out var max) == 0) return null;
            var map = XGetKeyboardMapping(display, (byte)min, max - min + 1, out var perKeycode);
            if (map == 0) return null;
            try
            {
                for (var keycode = max; keycode >= min; keycode--)
                {
                    var used = false;
                    for (var i = 0; i < perKeycode && !used; i++)
                        used = Marshal.ReadIntPtr(map, ((keycode - min) * perKeycode + i) * nint.Size) != 0;
                    if (!used) return (ushort)keycode;
                }
                return null;
            }
            finally { XFree(map); }
        }
    }
}
