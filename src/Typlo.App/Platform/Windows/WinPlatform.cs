using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Typlo.Core.Hotkeys;
using Microsoft.Win32;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

/// <summary>
/// Windows implementation. A hidden message-only window on its own thread receives
/// WM_HOTKEY and WM_CLIPBOARDUPDATE; all hotkey and clipboard calls are marshalled onto it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinPlatform : IPlatform
{
    private const int WM_INVOKE = WM_APP + 1;
    private const int WM_RESUME_CAPTURE = WM_APP + 2;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Typlo";
    private const string LegacyRunValue = "LangSwitch"; // the app's previous name

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _wndProc; // keeps the delegate alive for native code
    private readonly HashSet<int> _hotkeyIds = [];
    private readonly HashSet<uint> _ownSequences = [];
    private readonly uint _fmtPng, _fmtExclude, _fmtCanInclude, _fmtViewerIgnore;
    private nint _hwnd;
    private int _suspendCount;

    public event Action<int>? HotkeyPressed;
    public event Action<RawClip>? ClipboardCaptured;

    public string Name => "Windows";

    public PlatformCapabilities Capabilities { get; } = new(
        GlobalHotkeys: true, SimulateInput: true, SwitchLayout: true, DetectHotkeyConflicts: true);

    public WinPlatform()
    {
        _fmtPng = RegisterClipboardFormatW("PNG");
        _fmtExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _fmtCanInclude = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _fmtViewerIgnore = RegisterClipboardFormatW("Clipboard Viewer Ignore");
        _wndProc = WindowProc;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Typlo.MessageLoop" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_hwnd == 0) throw new InvalidOperationException("Failed to create the message window.");
    }

    // ---------------------------------------------------------------- message thread

    private void MessageLoop()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "Typlo.MessageWindow." + Environment.ProcessId,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "Typlo", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, wc.hInstance, 0);
        if (_hwnd != 0) AddClipboardFormatListener(_hwnd);
        _ready.Set();
        if (_hwnd == 0) return;

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                var id = (int)wParam;
                ThreadPool.QueueUserWorkItem(_ => HotkeyPressed?.Invoke(id));
                return 0;
            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                return 0;
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action)) action();
                return 0;
            case WM_RESUME_CAPTURE:
                _suspendCount = Math.Max(0, _suspendCount - 1);
                return 0;
            case WM_CLOSE:
                RemoveClipboardFormatListener(hwnd);
                foreach (var hk in _hotkeyIds) UnregisterHotKey(hwnd, hk);
                DestroyWindow(hwnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private T Invoke<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return func();
        using var done = new ManualResetEventSlim();
        T result = default!;
        Exception? error = null;
        _queue.Enqueue(() =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        PostMessageW(_hwnd, WM_INVOKE, 0, 0);
        if (!done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Message thread did not respond.");
        if (error is not null) throw error;
        return result;
    }

    private void Invoke(Action action) => Invoke(() => { action(); return 0; });

    // ---------------------------------------------------------------- hotkeys

    public bool RegisterHotkey(int id, Hotkey hotkey)
    {
        var vk = VirtualKeys.FromName(hotkey.Key);
        if (vk == 0) return false;
        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Ctrl)) mods |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Win)) mods |= MOD_WIN;
        return Invoke(() =>
        {
            if (_hotkeyIds.Remove(id)) UnregisterHotKey(_hwnd, id);
            if (!RegisterHotKey(_hwnd, id, mods, vk)) return false;
            _hotkeyIds.Add(id);
            return true;
        });
    }

    public void UnregisterAllHotkeys() => Invoke(() =>
    {
        foreach (var id in _hotkeyIds) UnregisterHotKey(_hwnd, id);
        _hotkeyIds.Clear();
    });

    // ---------------------------------------------------------------- clipboard

    public ulong ClipboardSequence => GetClipboardSequenceNumber();

    public IDisposable SuspendCapture()
    {
        Invoke(() => _suspendCount++);
        return new Scope(() => PostMessageW(_hwnd, WM_RESUME_CAPTURE, 0, 0));
    }

    private void OnClipboardUpdate()
    {
        var seq = GetClipboardSequenceNumber();
        if (_ownSequences.Remove(seq) || _suspendCount > 0 || ClipboardCaptured is null) return;
        RawClip? clip = null;
        try
        {
            clip = WithClipboard(() =>
            {
                if (IsClipboardFormatAvailable(_fmtExclude) || IsClipboardFormatAvailable(_fmtViewerIgnore)) return null;
                if (IsClipboardFormatAvailable(_fmtCanInclude) && ReadBytes(_fmtCanInclude) is { Length: >= 4 } flag &&
                    BitConverter.ToInt32(flag) == 0) return null;

                var app = OwnerProcessName();
                if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    var text = ReadUnicode();
                    if (!string.IsNullOrEmpty(text)) return new RawClip(text, null, false, app);
                }
                if (_fmtPng != 0 && IsClipboardFormatAvailable(_fmtPng) && ReadBytes(_fmtPng) is { Length: > 0 } png)
                    return new RawClip(null, png, true, app);
                var dibFormat = IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : IsClipboardFormatAvailable(CF_DIB) ? CF_DIB : 0;
                if (dibFormat != 0 && ReadBytes(dibFormat) is { Length: > 40 } dib)
                    return new RawClip(null, Dib.ToBmpFile(dib), false, app);
                return null;
            });
        }
        catch (Exception e)
        {
            Log.Error("Clipboard read failed", e);
        }
        if (clip is not null) ThreadPool.QueueUserWorkItem(_ => ClipboardCaptured?.Invoke(clip));
    }

    public string? ReadText() => Invoke(() => WithClipboard(() =>
        IsClipboardFormatAvailable(CF_UNICODETEXT) ? ReadUnicode() : null));

    public void SetText(string text) => Invoke(() => WriteClipboard(() =>
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        SetBytes(CF_UNICODETEXT, bytes);
    }));

    public void SetImage(byte[] png, byte[] bgra, int width, int height) => Invoke(() => WriteClipboard(() =>
    {
        SetBytes(CF_DIB, Dib.FromBgra(bgra, width, height));
        if (_fmtPng != 0) SetBytes(_fmtPng, png);
    }));

    public ClipboardSnapshot Snapshot() => Invoke(() => WithClipboard(() =>
    {
        var list = new List<(string, byte[])>();
        uint fmt = 0;
        while ((fmt = EnumClipboardFormats(fmt)) != 0)
        {
            // GDI handles and private formats are not plain memory; CF_BITMAP etc. are synthesized from CF_DIB anyway.
            if (fmt is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or CF_OWNERDISPLAY
                or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE || fmt is >= 0x200 and <= 0x3FF) continue;
            if (ReadBytes(fmt) is { } data) list.Add((fmt.ToString(System.Globalization.CultureInfo.InvariantCulture), data));
        }
        return new ClipboardSnapshot(list);
    }) ?? new ClipboardSnapshot([]));

    public void Restore(ClipboardSnapshot snapshot) => Invoke(() => WriteClipboard(() =>
    {
        foreach (var (fmt, data) in snapshot.Formats)
            if (uint.TryParse(fmt, System.Globalization.CultureInfo.InvariantCulture, out var id)) SetBytes(id, data);
    }));

    private T? WithClipboard<T>(Func<T?> read)
    {
        if (!OpenClipboardRetry()) return default;
        try { return read(); }
        finally { CloseClipboard(); }
    }

    private void WriteClipboard(Action write)
    {
        if (!OpenClipboardRetry()) throw new IOException("Clipboard is locked by another application.");
        try
        {
            EmptyClipboard();
            write();
        }
        finally
        {
            CloseClipboard();
            _ownSequences.Add(GetClipboardSequenceNumber());
            if (_ownSequences.Count > 64) _ownSequences.Clear();
        }
    }

    private bool OpenClipboardRetry()
    {
        for (var i = 0; i < 20; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static string? ReadUnicode()
    {
        var bytes = ReadBytes(CF_UNICODETEXT);
        if (bytes is null) return null;
        var text = System.Text.Encoding.Unicode.GetString(bytes);
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;
        var size = (int)GlobalSize(handle);
        if (size <= 0) return null;
        var ptr = GlobalLock(handle);
        if (ptr == 0) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, size);
            return data;
        }
        finally { GlobalUnlock(handle); }
    }

    private static void SetBytes(uint format, byte[] data)
    {
        var mem = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (mem == 0) throw new OutOfMemoryException();
        var ptr = GlobalLock(mem);
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) == 0) GlobalFree(mem); // on success the system owns the memory
    }

    private static string? OwnerProcessName()
    {
        var owner = GetClipboardOwner();
        if (owner == 0) owner = GetForegroundWindow();
        return ProcessNameOf(owner);
    }

    private static string? ProcessNameOf(nint hwnd)
    {
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return pid == 0 ? null : Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // ---------------------------------------------------------------- input

    private static readonly ushort[] Modifiers =
        [VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN];

    public async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!Modifiers.Any(IsDown)) return;
            await Task.Delay(10);
        }
        // Still held: release them synthetically so our Ctrl+C/V is not seen as e.g. Ctrl+Alt+C.
        // A Ctrl tap first keeps a lone Alt release from opening the target's menu bar.
        var held = Modifiers.Where(IsDown).ToList();
        if (held.Count == 0) return;
        var keys = new List<(ushort, bool)> { (VK_CONTROL, false), (VK_CONTROL, true) };
        keys.AddRange(held.Select(vk => (vk, true)));
        Send(keys);
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void SendCopy() => SendChord(KeyModifiers.Ctrl, "C");
    public void SendPaste() => SendChord(KeyModifiers.Ctrl, "V");

    public void SendChord(KeyModifiers modifiers, string key)
    {
        var mods = new List<ushort>();
        if (modifiers.HasFlag(KeyModifiers.Ctrl)) mods.Add(VK_CONTROL);
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods.Add(VK_MENU);
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods.Add(VK_SHIFT);
        if (modifiers.HasFlag(KeyModifiers.Win)) mods.Add(VK_LWIN);
        var vk = (ushort)VirtualKeys.FromName(key);
        var seq = mods.Select(m => (m, false)).Append((vk, false)).Append((vk, true))
            .Concat(Enumerable.Reverse(mods).Select(m => (m, true))).ToList();
        Send(seq);
    }

    private static unsafe void Send(IReadOnlyList<(ushort Vk, bool Up)> keys)
    {
        var inputs = stackalloc INPUT[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            inputs[i] = default;
            inputs[i].type = INPUT_KEYBOARD;
            inputs[i].ki.wVk = keys[i].Vk;
            inputs[i].ki.dwFlags = keys[i].Up ? KEYEVENTF_KEYUP : 0;
        }
        SendInput((uint)keys.Count, inputs, sizeof(INPUT));
    }

    public nint ForegroundWindow => GetForegroundWindow();

    public void ActivateWindow(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return;
        if (SetForegroundWindow(handle)) return;
        // Foreground lock: borrow the current foreground thread's input state.
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(handle);
        SetForegroundWindow(handle);
        AttachThreadInput(me, fgThread, false);
    }

    public (int X, int Y) CursorPosition => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    // ---------------------------------------------------------------- layouts

    public bool SwitchKeyboardLayout(nint window, int langId)
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return false;
        var list = new nint[count];
        GetKeyboardLayoutList(count, list);
        var hkl = list.FirstOrDefault(h => (h & 0xFFFF) == langId);
        if (hkl == 0 || window == 0) return false;
        var root = GetAncestor(window, 2 /* GA_ROOT */);
        return PostMessageW(root != 0 ? root : window, WM_INPUTLANGCHANGEREQUEST, 0, hkl);
    }

    // ---------------------------------------------------------------- keep awake

    // The execution state belongs to the calling thread, so it is set on the long-lived message thread.
    public bool SetKeepAwake(bool enabled) => Invoke(() =>
        SetThreadExecutionState(enabled ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED : ES_CONTINUOUS) != 0);

    // ---------------------------------------------------------------- autostart

    public bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0) PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }
}
