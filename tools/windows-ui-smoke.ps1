param(
    [Parameter(Mandatory)][string]$Resident,
    [Parameter(Mandatory)][string]$Settings
)
$ErrorActionPreference = 'Stop'
$native = @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class TyploUiSmoke {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Bounds, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
        public uint Size;
        public string Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedBytes;
        public IntPtr ReservedData, In, Out, Err;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {
        public IntPtr Process, Thread;
        public uint Pid, Tid;
    }
    delegate bool WindowCallback(IntPtr window, IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop, WindowCallback callback, IntPtr context);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconExW(string path, int index, IntPtr large, IntPtr small, uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string app, StringBuilder args, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);

    public static void Verify(string resident, string settings, string profile) {
        if (ExtractIconExW(resident, -1, IntPtr.Zero, IntPtr.Zero, 0) == 0 ||
            ExtractIconExW(settings, -1, IntPtr.Zero, IntPtr.Zero, 0) == 0)
            throw new InvalidOperationException("Typlo icon resource missing from an executable");
        Console.WriteLine("PASS both executables contain a Windows icon group");
        string desktopName = "TyploSmoke_" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        IntPtr oldDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            CheckWindow(settings, desktop, desktopName, "--profile " + Quote(profile), "Typlo2.0 — settings", (rect, _) => {
                var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                var display = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfoW(MonitorFromPoint(center, 2), ref display)) throw new Win32Exception();
                Rect area = display.Work;
                if (rect.Left < area.Left || rect.Right > area.Right || rect.Top < area.Top || rect.Bottom > area.Bottom)
                    throw new InvalidOperationException("The settings window extends outside the monitor work area");
            });
            CheckThemeFrame(settings, desktop, desktopName, profile, "light", "#fafafc");
            CheckThemeFrame(settings, desktop, desktopName, profile, "dark", "#202020");
            CheckWindow(settings, desktop, desktopName, "--shortcuts " + Quote(profile), "Typlo2.0 — keyboard shortcuts", null);
            string russian = Path.Combine(profile, "explicit-russian");
            Directory.CreateDirectory(russian);
            File.WriteAllText(Path.Combine(russian, "settings.json"), "{\"ui_language\":\"ru\"}");
            CheckWindow(settings, desktop, desktopName, "--profile " + Quote(russian), "Typlo2.0 — настройки", null);
            File.WriteAllBytes(Path.Combine(profile, "latest-screenshot.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAYAAAC09K7GAAAAEklEQVR4nGMwSlnwHxkzEBQAAJPZGn0DcOafAAAAAElFTkSuQmCC"));
            CheckWindow(settings, desktop, desktopName, "--screenshot-editor " + Quote(profile) + " file", "Typlo2.0 — Screenshot editor", null);
            MonitorInfo monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfoW(MonitorFromPoint(new Point(0, 0), 1), ref monitor)) throw new Win32Exception();
            Rect work = monitor.Work;
            CheckPopup(settings, desktop, desktopName, profile, new Point(work.Left + 34, work.Top + 34), work);
            CheckPopup(settings, desktop, desktopName, profile, new Point(work.Right - 15, work.Bottom - 15), work);
        } finally {
            if (oldDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(oldDpi);
            CloseDesktop(desktop);
        }
    }

    public static void VerifyPortableStartup(string resident, string settings, string fixture) {
        string install = Path.Combine(fixture, "portable");
        string appData = Path.Combine(fixture, "local");
        string legacy = Path.Combine(appData, "Typlo-Rust");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"ui_language\":\"ru\",\"history_items\":73}");
        string settingsCopy = Path.Combine(install, Path.GetFileName(settings));
        string residentCopy = Path.Combine(install, Path.GetFileName(resident));
        File.Copy(settings, settingsCopy);
        File.Copy(resident, residentCopy);
        string softwareGl = Path.Combine(Path.GetDirectoryName(settings), "opengl32.dll");
        if (File.Exists(softwareGl)) File.Copy(softwareGl, Path.Combine(install, "opengl32.dll"));

        string desktopName = "TyploPortable_" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        string previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", appData);
            // An explicit test profile must never initialize the installation's default profile.
            CheckWindow(settingsCopy, desktop, desktopName, "--history " + Quote(Path.Combine(fixture, "explicit")),
                "Typlo2.0 — clipboard history", null);
            string data = Path.Combine(install, "data");
            if (Directory.Exists(data)) throw new InvalidOperationException("An explicit profile created the default profile");

            CheckWindow(settingsCopy, desktop, desktopName, "--history", "Typlo2.0 — история буфера", null);
            string migrated = Path.Combine(data, "settings.json");
            if (File.ReadAllText(migrated) != File.ReadAllText(Path.Combine(legacy, "settings.json")) ||
                !File.Exists(Path.Combine(data, "history.sqlite3")))
                throw new InvalidOperationException("History did not migrate the default profile before opening");

            if (Mutex.TryOpenExisting(@"Local\Typlo.Rust.Resident.v1", out Mutex existing)) {
                existing.Dispose();
                CheckWindow(settingsCopy, desktop, desktopName, "", "Typlo2.0 — настройки", null);
                Console.WriteLine("PASS first launch through --history migrates the profile; resident check skipped because Typlo is already running");
                return;
            }
            var background = Start(residentCopy, desktopName, "");
            try {
                string log = Path.Combine(data, "logs", "typlo.log");
                for (int i = 0; i < 100; i++) {
                    if (File.Exists(log) && File.ReadAllText(log).Contains($"pid={background.Pid} Resident ready")) {
                        Console.WriteLine("PASS first launch through --history migrates the profile before resident startup");
                        return;
                    }
                    if (WaitForSingleObject(background.Process, 0) == 0) break;
                    Thread.Sleep(20);
                }
                throw new InvalidOperationException("Resident did not start with the migrated portable profile");
            } finally {
                EnumDesktopWindows(desktop, (window, _) => {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid == background.Pid) PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                CloseProcess(background);
            }
        } finally {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal);
            CloseDesktop(desktop);
        }
    }

    static void CheckThemeFrame(string exe, IntPtr desktop, string name, string profile, string theme, string expected) {
        string fixture = Path.Combine(profile, theme);
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "settings.json"), "{\"ui_theme\":\"" + theme + "\"}");
        string report = Path.Combine(fixture, "render.txt");
        string frame = Path.Combine(fixture, "settings.png");
        var process = Start(exe, name, "--ui-probe " + Quote(report) + " " + Quote(fixture) + " " + Quote(frame));
        try {
            if (WaitForSingleObject(process.Process, 15000) != 0)
                throw new TimeoutException($"Settings did not produce a {theme} frame");
            string result = File.Exists(report) ? File.ReadAllText(report) : "No renderer report";
            if (!GetExitCodeProcess(process.Process, out uint code) || code != 0 ||
                !result.Contains("background=" + expected) || !File.Exists(frame))
                throw new InvalidOperationException($"Expected a {theme} background of {expected}. {result}");
            Console.WriteLine($"PASS {theme} settings framebuffer {expected} ({frame})");
        } finally {
            CloseProcess(process);
        }
        CheckWindow(exe, desktop, name, "--profile " + Quote(fixture), "Typlo2.0 — settings", null, theme == "dark");
    }

    static void CheckPopup(string exe, IntPtr desktop, string name, string profile, Point anchor, Rect work) {
        CheckWindow(exe, desktop, name, "--history " + Quote(profile) + $" 0 0 0 {anchor.X} {anchor.Y}",
            "Typlo2.0 — clipboard history", (rect, dpi) => {
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                int expectedWidth = Math.Min(460 * (int)dpi / 96, work.Right - work.Left - 16);
                int expectedHeight = Math.Min(560 * (int)dpi / 96, work.Bottom - work.Top - 16);
                int x = Math.Clamp(anchor.X + 12, work.Left + 8, work.Right - width - 8);
                int y = Math.Clamp(anchor.Y + 12 + height > work.Bottom - 8 ? anchor.Y - height - 12 : anchor.Y + 12,
                    work.Top + 8, work.Bottom - height - 8);
                if (Math.Abs(expectedWidth - width) > 2 || Math.Abs(expectedHeight - height) > 2 ||
                    Math.Abs(rect.Left - x) > 2 || Math.Abs(rect.Top - y) > 2)
                    throw new InvalidOperationException($"Popup {rect.Left},{rect.Top} {width}×{height}; expected {x},{y} {expectedWidth}×{expectedHeight}; DPI {dpi}");
            });
    }

    static void CheckWindow(string exe, IntPtr desktop, string name, string arguments, string title, Action<Rect, uint> check, bool? expectedDark = null) {
        var process = Start(exe, name, arguments);
        IntPtr found = IntPtr.Zero;
        try {
            for (int i = 0; i < 160 && found == IntPtr.Zero; i++) {
                if (WaitForSingleObject(process.Process, 0) == 0) {
                    GetExitCodeProcess(process.Process, out var code);
                    throw new InvalidOperationException($"{title} exited with {code} before showing a window");
                }
                EnumDesktopWindows(desktop, (window, _) => {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid != process.Pid || !IsWindowVisible(window)) return true;
                    var caption = new StringBuilder(160);
                    GetWindowTextW(window, caption, caption.Capacity);
                    if (caption.ToString() != title) return true;
                    found = window;
                    return false;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) Thread.Sleep(50);
            }
            if (found == IntPtr.Zero || !GetWindowRect(found, out var rect))
                throw new InvalidOperationException($"Window did not appear: {title}");
            check?.Invoke(rect, GetDpiForWindow(found));
            if (expectedDark is bool dark) {
                for (int i = 0; i < 20; i++) {
                    if (DwmGetWindowAttribute(found, 20, out int value, sizeof(int)) < 0) break;
                    if ((value != 0) == dark) {
                        Console.WriteLine($"PASS Windows title bar follows {(dark ? "Dark" : "Light")}");
                        break;
                    }
                    if (i == 19) throw new InvalidOperationException("The Windows title bar did not follow the selected theme");
                    Thread.Sleep(50);
                }
            }
            Console.WriteLine($"PASS {title} at {rect.Left},{rect.Top} {rect.Right - rect.Left}×{rect.Bottom - rect.Top}");
        } finally {
            if (found != IntPtr.Zero) PostMessageW(found, 0x0010, IntPtr.Zero, IntPtr.Zero);
            CloseProcess(process);
        }
    }

    static ProcessInfo Start(string exe, string desktop, string arguments) {
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)), Desktop = desktop };
        if (!CreateProcessW(exe, new StringBuilder(Quote(exe) + " " + arguments), IntPtr.Zero, IntPtr.Zero,
            false, 0, IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var process))
            throw new Win32Exception();
        return process;
    }

    static void CloseProcess(ProcessInfo process) {
        if (WaitForSingleObject(process.Process, 1500) != 0) {
            TerminateProcess(process.Process, 1);
            WaitForSingleObject(process.Process, 3000);
        }
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
    }
    static string Quote(string value) => "\"" + value + "\"";
}
'@
Add-Type -TypeDefinition $native
$profile = Join-Path ([System.IO.Path]::GetTempPath()) ('TyploUiSmoke-' + [guid]::NewGuid().ToString('N'))
try {
    [TyploUiSmoke]::Verify((Resolve-Path -LiteralPath $Resident).Path, (Resolve-Path -LiteralPath $Settings).Path, $profile)
    [TyploUiSmoke]::VerifyPortableStartup((Resolve-Path -LiteralPath $Resident).Path, (Resolve-Path -LiteralPath $Settings).Path, "$profile/portable-startup")
} catch {
    # Preserve renderer/startup errors when the private-desktop process exits.
    $failure = $_
    $spec = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Settings).Path)
    $spec.UseShellExecute = $false
    $spec.RedirectStandardError = $true
    foreach ($argument in @('--ui-probe', "$profile/diagnostic.txt", $profile, "$profile/diagnostic.png")) { $spec.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($spec)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill($true) }
        Write-Host ($errorRead.GetAwaiter().GetResult())
    } finally { $process.Dispose() }
    throw $failure
}
