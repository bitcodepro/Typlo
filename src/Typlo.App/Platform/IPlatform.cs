using Typlo.App.Platform.Shared;
using Typlo.Core.Hotkeys;

namespace Typlo.App.Platform;

/// <summary>Raw clipboard content as read from the OS, before thumbnails/encoding.</summary>
public sealed record RawClip(string? Text, byte[]? Image, bool ImageIsPng, string? SourceApp,
    string? Html = null, string? Rtf = null, IReadOnlyList<string>? Files = null);

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
    /// <summary>Puts text with its formatted versions (HTML fragment, RTF); null formats are left out.</summary>
    void SetRichText(string text, string? html, string? rtf);
    /// <summary>Puts a list of files, as if they were copied in the file manager.</summary>
    void SetFiles(IReadOnlyList<string> paths);
    /// <summary>Puts an image on the clipboard; <paramref name="bgra"/> is top-down 32-bit pixels.</summary>
    void SetImage(byte[] png, byte[] bgra, int width, int height);
    ClipboardSnapshot Snapshot();
    /// <summary>
    /// True if the clipboard holds an editor's "copy the current line" result made with nothing selected
    /// (VS Code's isFromEmptySelection, Visual Studio's line-copy tag), not a real selection.
    /// </summary>
    bool IsEmptySelectionLineCopy();
    /// <summary>
    /// Whether the clipboard was last written by the process that owns <paramref name="window"/>;
    /// null where the OS does not tell (then only the change timing is used).
    /// </summary>
    bool? IsClipboardOwnedBy(nint window);
    void Restore(ClipboardSnapshot snapshot);

    // Input & windows
    /// <summary>Waits until the user lets go of Ctrl/Alt/Shift/Win (then force-releases them).</summary>
    Task WaitForModifiersReleasedAsync(TimeSpan timeout);
    /// <summary>Presses and releases a key combination in the focused app.</summary>
    void SendChord(KeyModifiers modifiers, string key);
    /// <summary>The platform's copy shortcut (Ctrl+C, or Cmd+C on macOS).</summary>
    void SendCopy();
    /// <summary>
    /// Types text into the focused app as Unicode key input, without touching the clipboard.
    /// Typlo's own injected keys never come back as <see cref="KeyTyped"/> events.
    /// </summary>
    void TypeText(string text);
    void SendPaste();
    /// <summary>Opaque handle of the focused window/app (HWND, PID or X11 window id); 0 if unknown.</summary>
    nint ForegroundWindow { get; }
    void ActivateWindow(nint handle);
    /// <summary>Mouse position in physical screen pixels.</summary>
    (int X, int Y) CursorPosition { get; }

    // Typing (for snippets)
    /// <summary>Starts or stops <see cref="KeyTyped"/> events (a global keyboard hook on Windows).</summary>
    void SetTypingWatch(bool enabled);
    /// <summary>Raised on a hook thread for every character the user types; keep handlers short.</summary>
    event Action<TypedKey>? KeyTyped;

    // Keyboard layout
    /// <summary>Switches the input language of <paramref name="window"/> to the given Windows LANGID if supported.</summary>
    bool SwitchKeyboardLayout(nint window, int langId);

    // System
    /// <summary>Tells the OS the display is in use (like video playback): no idle screen-off, sleep or lock.</summary>
    bool SetKeepAwake(bool enabled);
    /// <summary>
    /// Makes the OS see user activity without visible effect (zero-distance mouse move), so presence-based
    /// apps such as Teams do not switch to "Away". Only acts when the user has been idle for a while.
    /// </summary>
    void SignalUserActivity();
    bool AutostartEnabled { get; set; }
}
