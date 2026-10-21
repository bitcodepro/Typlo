using Typlo.Core.Hotkeys;

namespace Typlo.App.Platform;

/// <summary>Raw clipboard content as read from the OS, before thumbnails/encoding.</summary>
public sealed record RawClip(string? Text, byte[]? Image, bool ImageIsPng, string? SourceApp);

/// <summary>Opaque copy of the clipboard formats, used to put the user's clipboard back.</summary>
public sealed class ClipboardSnapshot(IReadOnlyList<(string Format, byte[] Data)> formats)
{
    public IReadOnlyList<(string Format, byte[] Data)> Formats { get; } = formats;
}

/// <summary>What the current OS/session can do, so the UI can explain missing features.</summary>
public sealed record PlatformCapabilities(
    bool GlobalHotkeys,
    bool SimulateInput,
    bool SwitchLayout,
    bool DetectHotkeyConflicts,
    string? Notes = null);

/// <summary>
/// Everything OS-specific the app needs: <see cref="Windows.WinPlatform"/>, <see cref="Mac.MacPlatform"/>,
/// <see cref="Linux.LinuxPlatform"/>. The UI and core never call the OS directly.
/// </summary>
public interface IPlatform : IDisposable
{
    string Name { get; }
    PlatformCapabilities Capabilities { get; }

    // Hotkeys
    /// <summary>Registers a global hotkey; false if it is invalid or (where detectable) owned by another app.</summary>
    bool RegisterHotkey(int id, Hotkey hotkey);
    void UnregisterAllHotkeys();
    /// <summary>Raised on a background thread with the id passed to <see cref="RegisterHotkey"/>.</summary>
    event Action<int>? HotkeyPressed;

    // Clipboard
    /// <summary>Raised on a background thread whenever another app puts something on the clipboard.</summary>
    event Action<RawClip>? ClipboardCaptured;
    /// <summary>While the returned scope is alive, clipboard changes are not reported as captures.</summary>
    IDisposable SuspendCapture();
    /// <summary>Changes whenever the clipboard content changes.</summary>
    ulong ClipboardSequence { get; }
    string? ReadText();
    void SetText(string text);
    /// <summary>Puts an image on the clipboard; <paramref name="bgra"/> is top-down 32-bit pixels.</summary>
    void SetImage(byte[] png, byte[] bgra, int width, int height);
    ClipboardSnapshot Snapshot();
    void Restore(ClipboardSnapshot snapshot);

    // Input & windows
    /// <summary>Waits until the user lets go of Ctrl/Alt/Shift/Win (then force-releases them).</summary>
    Task WaitForModifiersReleasedAsync(TimeSpan timeout);
    /// <summary>Presses and releases a key combination in the focused app.</summary>
    void SendChord(KeyModifiers modifiers, string key);
    /// <summary>The platform's copy shortcut (Ctrl+C, or Cmd+C on macOS).</summary>
    void SendCopy();
    void SendPaste();
    /// <summary>Opaque handle of the focused window/app (HWND, PID or X11 window id); 0 if unknown.</summary>
    nint ForegroundWindow { get; }
    void ActivateWindow(nint handle);
    /// <summary>Mouse position in physical screen pixels.</summary>
    (int X, int Y) CursorPosition { get; }

    // Keyboard layout
    /// <summary>Switches the input language of <paramref name="window"/> to the given Windows LANGID if supported.</summary>
    bool SwitchKeyboardLayout(nint window, int langId);

    // System
    /// <summary>Tells the OS the display is in use (like video playback): no idle screen-off, sleep or lock.</summary>
    bool SetKeepAwake(bool enabled);
    bool AutostartEnabled { get; set; }
}
