using System.Runtime.InteropServices;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

public sealed partial class WinPlatform
{
    // Explicit test sink, including snapshots created through the controller. No text or HWND values.
    internal Action<NativeEditDiagnostic>? DirectTextDiagnostic { get; set; }
    public bool SupportsDirectTextEditing => !_passive && !Volatile.Read(ref _disposed);
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
