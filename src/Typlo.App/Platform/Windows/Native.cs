using System.Runtime.InteropServices;

namespace Typlo.App.Platform.Windows;

internal static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
    public const int WM_APP = 0x8000;
    public const int WM_CLOSE = 0x0010;
    public const int WM_DESTROY = 0x0002;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_DIBV5 = 17, CF_BITMAP = 2, CF_METAFILEPICT = 3,
        CF_PALETTE = 9, CF_ENHMETAFILE = 14, CF_OWNERDISPLAY = 0x80, CF_DSPBITMAP = 0x82,
        CF_DSPMETAFILEPICT = 0x83, CF_DSPENHMETAFILE = 0x8E, CF_HDROP = 15;

    public const uint GMEM_MOVEABLE = 0x0002;

    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    public static readonly nint HWND_MESSAGE = -3;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumWindowProc(nint hwnd, nint parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // INPUT is a union; MOUSEINPUT is the largest member, so pad to its size.
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] private unsafe fixed byte _pad[32];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AddClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RemoveClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint format, nint mem);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll")] public static partial uint EnumClipboardFormats(uint format);
    [LibraryImport("user32.dll")] public static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint mem);
    [LibraryImport("kernel32.dll")] public static partial nuint GlobalSize(nint mem);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint mem);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")] public static unsafe partial uint SendInput(uint count, INPUT* inputs, int size);
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWinEvent(nint hook);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BringWindowToTop(nint hwnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint parameter);

    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    [LibraryImport("kernel32.dll")] public static partial uint SetThreadExecutionState(uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetLastInputInfo(ref LASTINPUTINFO info);
    [LibraryImport("kernel32.dll")] public static partial ulong GetTickCount64();


    [LibraryImport("user32.dll")] public static partial int GetKeyboardLayoutList(int count, [Out] nint[]? list);
}
