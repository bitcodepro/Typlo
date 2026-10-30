using System.Runtime.InteropServices;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

public sealed partial class WinPlatform
{
    // Explicit test sink, including snapshots created through the controller. No text or HWND values.
    internal Action<NativeEditDiagnostic>? DirectTextDiagnostic { get; set; }
    public bool SupportsDirectTextEditing => !_passive && !Volatile.Read(ref _disposed);
    public bool SupportsAutomaticWordCorrection => SupportsDirectTextEditing;
    public bool SupportsTextRangeBatch => SupportsDirectTextEditing;
    public ulong? ImmediateClipboardSequence => GetClipboardSequenceNumber();
    public unsafe bool TryReplacePreviousText(nint window, int characters, string text, Func<bool> stillValid)
    {
        if (characters is < 1 or > 2000 || text.Length > 4096 || text.IndexOfAny(['\n', '\r', '\t']) >= 0 ||
            !SupportsUnicodeTextReplacement || !stillValid() || ForegroundWindow != window) return false;
        var inputs = new INPUT[characters * 2 + text.Length * 2 + 2];
        var offset = 0;
        void Key(ushort vk, ushort scan, uint flags) => inputs[offset++] = new INPUT
        { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = OwnInputTag } };
        Key(VK_SHIFT, 0, 0);
        for (var i = 0; i < characters; i++) { Key(0x25, 0, 0); Key(0x25, 0, KEYEVENTF_KEYUP); }
        Key(VK_SHIFT, 0, KEYEVENTF_KEYUP);
        foreach (var c in text) { Key(0, c, KEYEVENTF_UNICODE); Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP); }
        if (!stillValid() || ForegroundWindow != window) return false;
        fixed (INPUT* pointer = inputs)
        {
            if (SendInput((uint)inputs.Length, pointer, sizeof(INPUT)) == inputs.Length) return true;
        }
        Send([(VK_SHIFT, true)]);
        throw new IOException("Windows could not send the complete Unicode replacement. The action will not be retried.");
    }
    public bool TrySendTextRangeBatch(nint window, int characters, bool paste, Func<bool> stillValid)
    {
        if (characters is < 1 or > 2000 || !SupportsTextRangeBatch || !stillValid() || ForegroundWindow != window) return false;
        var keys = new List<(ushort, bool)>(characters * 2 + 10) { (VK_SHIFT, false) };
        for (var i = 0; i < characters; i++) { keys.Add((0x25, false)); keys.Add((0x25, true)); }
        keys.Add((VK_SHIFT, true)); keys.Add((VK_CONTROL, false));
        var action = (ushort)(paste ? 0x56 : 0x43);
        keys.Add((action, false)); keys.Add((action, true)); keys.Add((VK_CONTROL, true));
        if (!paste) { keys.Add((0x27, false)); keys.Add((0x27, true)); }
        if (Send(keys)) return true;
        Send([(0x25, true), (VK_SHIFT, true), (VK_CONTROL, true)]);
        throw new IOException("Windows could not send the complete text range action.");
    }
    public bool CanRetainDirectInputTarget(nint window, nint element) => SupportsDirectTextEditing &&
        NativeEditSnapshot.CanRetain(window, element, IsOwnProcess);
    public nint FocusedInputElement
    {
        get
        {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            return SupportsDirectTextEditing && GetGUIThreadInfo(0, ref info) ? info.Focus : 0;
        }
    }

    public Task<DirectTextCapture> CaptureDirectTextAsync(nint window, nint element, Func<bool> stillValid)
        => CaptureDirectTextAsync(window, element, stillValid, DirectTextDiagnostic);

    internal Task<DirectTextCapture> CaptureDirectTextAsync(nint window, nint element, Func<bool> stillValid,
        Action<NativeEditDiagnostic>? diagnostic)
        => !SupportsDirectTextEditing ? Task.FromResult(DirectTextCapture.Unsupported) :
            Task.Run(() => NativeEditSnapshot.Capture(window, element, stillValid,
                pid => Volatile.Read(ref _disposed) || IsOwnProcess(pid), diagnostic));
}
