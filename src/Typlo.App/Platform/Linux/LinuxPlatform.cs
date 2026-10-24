using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Typlo.App.Platform.Shared;

namespace Typlo.App.Platform.Linux;

/// <summary>
/// Linux. X11: hotkeys and paste via libuiohook/XTest, clipboard via <c>xclip</c>, windows via <c>xdotool</c>.
/// Wayland: clipboard history via <c>wl-clipboard</c>; compositors do not allow global hooks or synthetic
/// input, so shortcuts are bound in the desktop settings to <c>Typlo --history</c> / <c>--convert</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatform : HookPlatformBase
{
    private const string TextTarget = "UTF8_STRING";
    private const string PngTarget = "image/png";

    private readonly bool _wayland;
    private Process? _inhibitor;
    private Process? _watcher;
    private readonly SemaphoreSlim _changed = new(0);

    public LinuxPlatform() : this(IsWaylandSession()) { }

    private LinuxPlatform(bool wayland) : base(startHook: !wayland)
    {
        _wayland = wayland;
        StartWatcher();
        StartClipboardMonitor();
    }

    private static bool IsWaylandSession() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
         string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));

    public override string Name => _wayland ? "Linux (Wayland)" : "Linux (X11)";

    public override PlatformCapabilities Capabilities
    {
        get
        {
            var missing = (_wayland ? new[] { "wl-copy", "wl-paste" } : ["xclip", "xdotool"]).Where(t => !Proc.Has(t)).ToList();
            var notes = new List<string>();
            if (missing.Count > 0) notes.Add(Services.Loc.T("note.linux.install", string.Join(", ", missing)));
            if (_wayland)
                notes.Add(Services.Loc.T("note.linux.wayland"));
            return new PlatformCapabilities(
                GlobalHotkeys: !_wayland, SimulateInput: !_wayland, SwitchLayout: false, DetectHotkeyConflicts: false,
                Notes: notes.Count > 0 ? string.Join(' ', notes) : null);
        }
    }

    // ---------------------------------------------------------------- change notification

    private void StartWatcher()
    {
        // Prefer event-driven notifications; fall back to polling in the base class.
        if (_wayland && Proc.Has("wl-paste")) _watcher = Proc.Start("wl-paste", "--watch", "echo", "x");
        else if (!_wayland && Proc.Has("clipnotify")) _watcher = Proc.Start("sh", "-c", "while clipnotify -s clipboard; do echo x; done");
        if (_watcher is null) return;
        var watcher = _watcher;
        new Thread(() =>
        {
            try
            {
                while (watcher.StandardOutput.ReadLine() is not null) _changed.Release();
            }
            catch (Exception) { }
        }) { IsBackground = true, Name = "Typlo.ClipboardWatcher" }.Start();
    }

    protected override void WaitForClipboardChange(CancellationToken ct)
    {
        if (_watcher is { HasExited: false }) _changed.Wait(TimeSpan.FromSeconds(5), ct);
        else base.WaitForClipboardChange(ct);
    }

    protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(600);

    // ---------------------------------------------------------------- clipboard tools

    private List<string> Targets()
    {
        var r = _wayland ? Proc.Run("wl-paste", ["--list-types"]) : Proc.Run("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"]);
        return r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [];
    }

    private byte[]? Read(string target)
    {
        var r = _wayland
            ? Proc.Run("wl-paste", ["--no-newline", "--type", target], timeoutMs: 5000)
            : Proc.Run("xclip", ["-selection", "clipboard", "-t", target, "-o"], timeoutMs: 5000);
        return r.Ok ? r.Output : null;
    }

    private void Write(string target, byte[] data)
    {
        if (_wayland) Proc.Run("wl-copy", ["--type", target], data, captureOutput: false);
        else Proc.Run("xclip", ["-selection", "clipboard", "-t", target, "-i"], data, captureOutput: false);
    }

    private static string? TextTargetOf(List<string> targets) =>
        new[] { TextTarget, "text/plain;charset=utf-8", "text/plain", "STRING", "TEXT" }.FirstOrDefault(targets.Contains);

    protected override ulong ReadChangeToken()
    {
        var targets = Targets();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var t in targets) hash.AppendData(System.Text.Encoding.UTF8.GetBytes(t + "\n"));
        var content = TextTargetOf(targets) is { } tt ? Read(tt) : targets.Contains(PngTarget) ? Read(PngTarget) : null;
        if (content is not null) hash.AppendData(content);
        return BitConverter.ToUInt64(hash.GetHashAndReset());
    }

    protected override RawClip? ReadClip()
    {
        var targets = Targets();
        // KeePassXC & co. mark secrets so clipboard managers skip them.
        if (targets.Contains("x-kde-passwordManagerHint")) return null;
        var app = ActiveProcessName();
        if (targets.Contains(UriListTarget) && Read(UriListTarget) is { Length: > 0 } uris && ParseUriList(uris) is { Count: > 0 } files)
            return new RawClip(null, null, false, app, Files: files);
        if (TextTargetOf(targets) is { } tt && Read(tt) is { Length: > 0 } bytes)
        {
            var html = targets.Contains("text/html") && Read("text/html") is { Length: > 0 } h ? System.Text.Encoding.UTF8.GetString(h) : null;
            var rtfTarget = new[] { "text/rtf", "application/rtf" }.FirstOrDefault(targets.Contains);
            var rtf = rtfTarget is not null && Read(rtfTarget) is { Length: > 0 } r ? System.Text.Encoding.Latin1.GetString(r) : null;
            return new RawClip(System.Text.Encoding.UTF8.GetString(bytes), null, false, app, html, rtf);
        }
        if (targets.Contains(PngTarget) && Read(PngTarget) is { Length: > 0 } png)
            return new RawClip(null, png, true, app);
        return null;
    }

    public override string? ReadText()
    {
        var tt = TextTargetOf(Targets());
        return tt is not null && Read(tt) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    protected override void WriteText(string text) =>
        Write(_wayland ? "text/plain;charset=utf-8" : TextTarget, System.Text.Encoding.UTF8.GetBytes(text));

    protected override void WriteImage(byte[] png, byte[] bgra, int width, int height) => Write(PngTarget, png);

    private const string UriListTarget = "text/uri-list";

    private static List<string> ParseUriList(byte[] data) =>
        System.Text.Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#') && Uri.TryCreate(l, UriKind.Absolute, out var u) && u.IsFile)
            .Select(l => new Uri(l).LocalPath).ToList();

    // xclip/wl-copy serve one type per selection: plain text pastes everywhere, so formatting is not restored here.
    protected override void WriteRichText(string text, string? html, string? rtf) => WriteText(text);

    protected override void WriteFiles(IReadOnlyList<string> paths) =>
        Write(UriListTarget, System.Text.Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => new Uri(p).AbsoluteUri + "\r\n"))));

    protected override ClipboardSnapshot ReadSnapshot()
    {
        // xclip/wl-copy serve one type at a time, so keep the most useful one.
        var targets = Targets();
        if (TextTargetOf(targets) is { } tt && Read(tt) is { } text) return new ClipboardSnapshot([("text", text)]);
        if (targets.Contains(PngTarget) && Read(PngTarget) is { } png) return new ClipboardSnapshot([(PngTarget, png)]);
        return new ClipboardSnapshot([]);
    }

    protected override void WriteSnapshot(ClipboardSnapshot snapshot)
    {
        if (snapshot.Formats.Count == 0) { Write(_wayland ? "text/plain;charset=utf-8" : TextTarget, []); return; }
        var (format, data) = snapshot.Formats[0];
        if (format == "text") Write(_wayland ? "text/plain;charset=utf-8" : TextTarget, data);
        else Write(format, data);
    }

    // ---------------------------------------------------------------- windows

    public override nint ForegroundWindow
    {
        get
        {
            if (_wayland || !Proc.Has("xdotool")) return 0;
            var r = Proc.Run("xdotool", ["getactivewindow"]);
            return r.Ok && long.TryParse(r.Text.Trim(), out var id) ? (nint)id : 0;
        }
    }

    public override void ActivateWindow(nint handle)
    {
        if (handle == 0 || _wayland || !Proc.Has("xdotool")) return;
        var id = ((long)handle).ToString();
        if (!Proc.Run("xdotool", ["windowactivate", "--sync", id]).Ok) Proc.Run("xdotool", ["windowfocus", "--sync", id]);
    }

    private string? ActiveProcessName()
    {
        if (_wayland || !Proc.Has("xdotool")) return null;
        var r = Proc.Run("xdotool", ["getactivewindow", "getwindowpid"]);
        if (!r.Ok || !int.TryParse(r.Text.Trim(), out var pid)) return null;
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch (IOException) { return null; }
    }

    public override bool SwitchKeyboardLayout(nint window, int langId) => false;

    // ---------------------------------------------------------------- system

    public override bool SetKeepAwake(bool enabled)
    {
        Proc.Stop(ref _inhibitor);
        if (!enabled) return true;
        // The held command exits when we do, so a crash never leaves the inhibitor behind.
        var hold = $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 20; done";
        if (Proc.Has("gnome-session-inhibit"))
            _inhibitor = Proc.Start("gnome-session-inhibit", "--inhibit", "idle:suspend", "--reason", "Typlo: keep the screen on", "sh", "-c", hold);
        else if (Proc.Has("systemd-inhibit"))
            _inhibitor = Proc.Start("systemd-inhibit", "--what=idle:sleep", "--who=Typlo", "--why=Keep the screen on", "--mode=block", "sh", "-c", hold);
        else if (!_wayland && (Proc.Has("xdg-screensaver") || Proc.Has("xset")))
        {
            // No session inhibitor: keep resetting the X11 idle timer, which is what older video players do.
            var reset = Proc.Has("xdg-screensaver") ? "xdg-screensaver reset" : "xset s reset";
            _inhibitor = Proc.Start("sh", "-c", $"while kill -0 {Environment.ProcessId} 2>/dev/null; do {reset} >/dev/null 2>&1; sleep 30; done");
        }
        return _inhibitor is not null;
    }

    private static string DesktopFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "typlo.desktop");

    public override bool AutostartEnabled
    {
        get => File.Exists(DesktopFile);
        set
        {
            if (!value) { if (File.Exists(DesktopFile)) File.Delete(DesktopFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, $"""
                [Desktop Entry]
                Type=Application
                Name=Typlo
                Exec="{Environment.ProcessPath}" --tray
                X-GNOME-Autostart-enabled=true
                """);
        }
    }

    public override void Dispose()
    {
        Proc.Stop(ref _inhibitor);
        Proc.Stop(ref _watcher);
        base.Dispose();
    }
}
