using System.ComponentModel;
using System.Runtime.InteropServices;
using Typlo.Core.Hotkeys;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

public sealed partial class WinPlatform
{
    private sealed record CopyGuard(int Id, nint Window, Func<bool> Valid)
    {
        internal bool Armed, Closed, Draining;
        internal readonly List<INPUT> Pending = [];
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private CopyGuard? _copyGuard;
    private int _copyGuardId;
    private nint _copyGuardHook;
    private CopyKeyProc? _copyGuardProc;
    private const ulong CopyCloseTag = 0x5459504c00000000;
    private const ulong CopySelectTag = 0x5459504d00000000;
    private const ulong CopyDrainTag = 0x5459504e00000000;
    private const ulong CopyReplayTag = 0x5459504f00000000;
    private volatile bool _observingCopyReplay;
    public bool SupportsProtectedCopy => SupportsUnicodeTextReplacement;

    public IDisposable? ProtectCopySelection(nint window, Func<bool> valid)
    {
        if (!SupportsProtectedCopy) return null;
        var guard = new CopyGuard(Interlocked.Increment(ref _copyGuardId), window, valid);
        Invoke(() =>
        {
            if (_copyGuardHook == 0)
            {
                _copyGuardProc = OnCopyKey;
                _copyGuardHook = SetWindowsHookExW(13, _copyGuardProc, GetModuleHandleW(null), 0);
                if (_copyGuardHook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if (_copyGuard is { Closed: false }) throw new IOException("Another protected selection is still active");
            _copyGuard = guard;
        });
        return new Scope(() => RequestCopyClose(guard));
    }

    private nint CopySelectionTag() => Volatile.Read(ref _copyGuard) is { } guard
        ? unchecked((nint)(long)(CopySelectTag | (uint)guard.Id)) : OwnInputTag;

    private unsafe void RequestCopyClose(CopyGuard guard)
    {
        if (Volatile.Read(ref _disposed) || _copyGuardHook == 0) return;
        // The tagged marker is consumed by our hook, never delivered to an application. Both
        // normal completion and an overlapping real key close the lease in the same event stream.
        // A stale completion marker cannot move the caret a second time.
        if (!guard.Done.Task.IsCompleted)
        {
            var inputs = stackalloc INPUT[2];
            for (var i = 0; i < 2; i++) inputs[i] = new INPUT { type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0xe8, dwFlags = i == 0 ? 0 : KEYEVENTF_KEYUP,
                    dwExtraInfo = unchecked((nint)(long)(CopyCloseTag | (uint)guard.Id)) } };
            if (SendInput(2, inputs, sizeof(INPUT)) != 2 || !guard.Done.Task.Wait(TimeSpan.FromMilliseconds(250)))
                throw new IOException("Protected copy cleanup was not acknowledged");
        }
        Invoke(() => { if (ReferenceEquals(_copyGuard, guard)) _copyGuard = null; });
    }

    private unsafe nint OnCopyKey(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(_copyGuardHook, code, message, data);
        var key = *(CopyKey*)data;
        var down = message is 0x100 or 0x104;
        var tag = unchecked((ulong)key.Extra);
        if ((tag & 0xffffffff00000000) == CopyReplayTag)
        {
            _observingCopyReplay = true;
            try { return CallNextHookEx(_copyGuardHook, code, message, data); }
            finally { _observingCopyReplay = false; }
        }
        if ((tag & 0xffffffff00000000) == CopyDrainTag)
        {
            if (down && _copyGuard is { Draining: true } draining && draining.Id == unchecked((int)(uint)tag))
            {
                if (draining.Pending.Count == 0) { draining.Draining = false; draining.Done.TrySetResult(); }
                else SendCopyReplay(draining, []);
            }
            return 1;
        }
        if ((tag & 0xffffffff00000000) == CopyCloseTag)
        {
            if (down && _copyGuard is { } closing && closing.Id == unchecked((int)(uint)tag)) CloseCopyGuard(closing);
            return 1;
        }
        if ((tag & 0xffffffff00000000) == CopySelectTag)
        {
            if (_copyGuard is not { } selecting || selecting.Id != unchecked((int)(uint)tag) || selecting.Closed) return 1;
            if (down && key.Vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
            {
                bool valid;
                try { valid = selecting.Valid(); }
                catch (Exception e) { Log.Error("Copy selection validation failed", e); valid = false; }
                if (!valid) { selecting.Closed = true; selecting.Done.TrySetResult(); return 1; }
                selecting.Armed = true;
            }
            return CallNextHookEx(_copyGuardHook, code, message, data);
        }
        if (key.Extra == OwnInputTag)
        {
            // The guard starts at the first injected Shift+Left batch. A committed Unicode
            // write (or an explicit collapse) consumes ownership before the application sees it.
            if (down && _copyGuard is { Armed: true, Closed: false } owned &&
                (key.Vk is 0xe7 or 0x27 || key.Vk == 0x56 && IsDown(VK_CONTROL)))
            { owned.Closed = true; owned.Done.TrySetResult(); }
            return CallNextHookEx(_copyGuardHook, code, message, data);
        }
        if (((key.Flags & 0x10) == 0 || AcceptSimulatedSystemShortcuts) && _copyGuard is { } guard)
        {
            if (guard.Draining)
            {
                if (CallNextHookEx(_copyGuardHook, code, message, data) == 0) guard.Pending.Add(ReplayKey(guard, key, down));
                return 1;
            }
            if (down && !guard.Closed && !IsCopyModifier(key.Vk) && !IsRegisteredCopyShortcut(key.Vk))
            {
                var collapse = BeginCopyClose(guard);
                if (collapse is not null)
                {
                    if (CallNextHookEx(_copyGuardHook, code, message, data) == 0) guard.Pending.Add(ReplayKey(guard, key, down));
                    SendCopyReplay(guard, collapse);
                    return 1;
                }
            }
        }
        return CallNextHookEx(_copyGuardHook, code, message, data);
    }

    private void CloseCopyGuard(CopyGuard guard)
    {
        if (BeginCopyClose(guard) is { } collapse) SendCopyReplay(guard, collapse);
    }
    private List<(ushort, bool)>? BeginCopyClose(CopyGuard guard)
    {
        if (!ReferenceEquals(_copyGuard, guard) || guard.Closed) return null;
        guard.Closed = true;
        try
        {
            if (!guard.Armed || Volatile.Read(ref _disposed) || GetForegroundWindow() != guard.Window || !guard.Valid()) return null;
            var held = Modifiers.Where(IsDown).ToArray();
            var keys = new List<(ushort, bool)>();
            if (held.Length > 0) keys.AddRange([(VK_CONTROL, false), (VK_CONTROL, true)]);
            keys.AddRange(held.Select(key => (key, true)));
            keys.Add((0x27, false)); keys.Add((0x27, true));
            keys.AddRange(held.Select(key => (key, false)));
            guard.Draining = true;
            return keys;
        }
        catch (Exception e) { Log.Error("Copy selection cleanup failed", e); }
        finally { if (!guard.Draining) guard.Done.TrySetResult(); }
        return null;
    }
    private static INPUT ReplayKey(CopyGuard guard, CopyKey key, bool down) => new() { type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = (ushort)key.Vk, wScan = (ushort)key.Scan,
            dwFlags = ((key.Flags & 1) != 0 ? 1u : 0) | (down ? 0 : KEYEVENTF_KEYUP),
            dwExtraInfo = unchecked((nint)(long)(CopyReplayTag | (uint)guard.Id)) } };
    private unsafe void SendCopyReplay(CopyGuard guard, List<(ushort Key, bool Up)> collapse)
    {
        // Physical events can already be queued as a complete SendInput batch. Keep their
        // original order (including key-up/modifiers) until a private input-stream barrier
        // confirms all events preceding it have been delivered after the collapse.
        var inputs = new INPUT[collapse.Count + guard.Pending.Count + 2];
        var offset = 0;
        foreach (var (key, up) in collapse) inputs[offset++] = new INPUT { type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = key, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (key is VK_RCONTROL or VK_RMENU ? 1u : 0), dwExtraInfo = OwnInputTag } };
        foreach (var input in guard.Pending) inputs[offset++] = input;
        guard.Pending.Clear();
        for (var i = 0; i < 2; i++) inputs[offset++] = new INPUT { type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = 0xe8, dwFlags = i == 0 ? 0 : KEYEVENTF_KEYUP,
                dwExtraInfo = unchecked((nint)(long)(CopyDrainTag | (uint)guard.Id)) } };
        fixed (INPUT* pointer = inputs)
            if (SendInput((uint)inputs.Length, pointer, sizeof(INPUT)) != inputs.Length)
            {
                guard.Draining = false; guard.Done.TrySetResult();
                Log.Error("Windows rejected protected copy input cleanup");
            }
    }
    private bool IsRegisteredCopyShortcut(uint key)
    {
        var mods = _systemShortcuts.ModifiersDown;
        return _nativeHotkeyDefinitions.Values.Any(h => h.Modifiers == mods && VirtualKeys.FromName(h.Key) == key) ||
            key == 0x53 && _systemShortcuts.IsRegistered(new(KeyModifiers.Win | KeyModifiers.Shift, "S")) &&
            mods == (KeyModifiers.Win | KeyModifiers.Shift);
    }
    private static bool IsCopyModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >= 0xa0 and <= 0xa5;
    private void UnhookCopyGuard()
    {
        if (_copyGuard is { } guard) { guard.Closed = true; guard.Done.TrySetResult(); _copyGuard = null; }
        if (_copyGuardHook != 0) { UnhookWindowsHookEx(_copyGuardHook); _copyGuardHook = 0; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CopyKey { internal uint Vk, Scan, Flags, Time; internal nint Extra; }
    private delegate nint CopyKeyProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, CopyKeyProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
}
