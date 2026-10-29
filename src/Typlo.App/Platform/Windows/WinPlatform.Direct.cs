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
