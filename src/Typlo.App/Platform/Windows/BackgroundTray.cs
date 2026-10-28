using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class BackgroundTray : IDisposable
{
    internal sealed record Item(string Label, Action<nint>? Click = null, bool Checked = false, bool Enabled = true);
    private readonly WndProc _proc;
    private readonly Func<IReadOnlyList<Item>> _items;
    private readonly Action _open;
    private readonly Func<nint> _target;
    private readonly uint _taskbarCreated;
    private nint _window, _icon;
    private string _tip = "Typlo";
    private bool _disposed;
    internal BackgroundTray(Func<IReadOnlyList<Item>> items, Action open, Func<nint> target)
    {
        _items = items; _open = open; _target = target; _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var cls = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = "Typlo.Tray." + Environment.ProcessId };
        if (RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("Cannot register tray window");
        _window = CreateWindowEx(0, cls.lpszClassName, "Typlo", 0, 0, 0, 0, 0, 0, 0, cls.hInstance, 0);
        if (_window == 0) throw new InvalidOperationException("Cannot create tray window");
        using var stream = typeof(BackgroundTray).Assembly.GetManifestResourceStream("Typlo.Assets.Typlo.ico") ?? throw new InvalidOperationException("Missing icon");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); var ico = bytes.ToArray();
        var offset = BitConverter.ToInt32(ico, 18); var length = BitConverter.ToInt32(ico, 14);
        _icon = CreateIconFromResourceEx(ico.AsSpan(offset, length).ToArray(), (uint)length, true, 0x30000, 32, 32, 0);
        if (_icon == 0) throw new InvalidOperationException("Cannot load tray icon");
        AddIcon();
    }
    private NOTIFYICONDATA Data() => new() { Size = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), Window = _window, Id = 1, Flags = 7, Callback = WM_APP + 7, Icon = _icon, Tip = _tip, Info = "", InfoTitle = "" };
    internal void SetTip(string text) { _tip = text.Length > 127 ? text[..127] : text; var data = Data(); Shell_NotifyIconW(1, ref data); }
    private void AddIcon() { var data = Data(); if (!Shell_NotifyIconW(0, ref data)) Log.Error("Tray icon unavailable"); }
    internal void Run() { while (GetMessageW(out var msg, 0, 0, 0) > 0) { TranslateMessage(in msg); DispatchMessageW(in msg); } }
    internal void Stop() { if (_window != 0) PostMessageW(_window, WM_CLOSE, 0, 0); }
    private nint WindowProc(nint window, uint message, nint wp, nint lp)
    {
        if (message == _taskbarCreated) { AddIcon(); return 0; }
        if (message == WM_APP + 7)
        {
            try
            {
                if ((uint)lp == 0x202) _open();
                else if ((uint)lp is 0x205 or 0x7b) ShowMenu();
            }
            catch (Exception e) { Log.Error("Tray action failed", e); }
            return 0;
        }
        if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
        if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(window, message, wp, lp);
    }
    private void ShowMenu()
    {
        var target = _target(); var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            var items = _items();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                AppendMenuW(menu, item.Label.Length == 0 ? 0x800u : (item.Checked ? 8u : 0u) | (item.Enabled ? 0u : 1u), (nuint)(i + 1), item.Label);
            }
            GetCursorPos(out var point); SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _window, 0);
            PostMessageW(_window, 0, 0, 0);
            if (selected > 0 && selected <= items.Count) items[(int)selected - 1].Click?.Invoke(target);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; var data = Data(); Shell_NotifyIconW(2, ref data);
        if (_icon != 0) DestroyIcon(_icon); _icon = 0;
        if (_window != 0) DestroyWindow(_window); _window = 0;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
