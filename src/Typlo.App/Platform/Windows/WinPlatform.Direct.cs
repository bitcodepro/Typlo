using System.Runtime.InteropServices;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

public sealed partial class WinPlatform
{
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
        => !SupportsDirectTextEditing ? Task.FromResult(DirectTextCapture.Unsupported) :
            Task.Run(() => NativeEditSnapshot.Capture(window, element, stillValid,
                pid => Volatile.Read(ref _disposed) || IsOwnProcess(pid)));
}
