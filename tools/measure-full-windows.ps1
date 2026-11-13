param(
    [Parameter(Mandatory)][string]$RustExe,
    [Parameter(Mandatory)][string]$CSharpDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,20)][int]$Runs = 5
)
$ErrorActionPreference = 'Stop'
$rust = (Resolve-Path -LiteralPath $RustExe).Path
$baseline = (Resolve-Path -LiteralPath $CSharpDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory' }
[IO.Directory]::CreateDirectory($output) | Out-Null
if (Get-Process -Name 'Typlo' -ErrorAction SilentlyContinue) {
    throw 'An existing C# Typlo holds its session-wide mutex'
}

Add-Type @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class TyploBenchmarkDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Startup {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Child { public IntPtr Process, Thread; public int Pid, Tid; }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")]
    public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcessW(string exe, StringBuilder command, IntPtr process, IntPtr thread, bool inherit, int flags, IntPtr environment, string directory, ref Startup startup, out Child child);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static IntPtr Open(string name) {
        var desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        return desktop;
    }

    public static Process Start(string exe, string desktop, string[] arguments) {
        var startup = new Startup { Size=Marshal.SizeOf<Startup>(), Desktop=desktop };
        var command = new StringBuilder("\"" + exe + "\"");
        foreach (var argument in arguments) {
            if (argument.Contains("\"")) throw new ArgumentException("Invalid argument");
            command.Append(" \"").Append(argument).Append('"');
        }
        if (!CreateProcessW(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0,
            IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var child))
            throw new Win32Exception();
        try { return Process.GetProcessById(child.Pid); }
        finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
    }
}
'@

function Start-Trial([string]$Exe, [string[]]$Arguments, [string]$LocalData) {
    $spec = [Diagnostics.ProcessStartInfo]::new($Exe)
    $spec.UseShellExecute = $false
    $spec.CreateNoWindow = $true
    $spec.RedirectStandardError = $true
    $spec.WorkingDirectory = Split-Path -Parent $Exe
    foreach ($argument in $Arguments) { $spec.ArgumentList.Add($argument) }
    if ($LocalData) { $spec.Environment['LOCALAPPDATA'] = $LocalData }
    [Diagnostics.Process]::Start($spec)
}

function Test-LanPort {
    $client = [Net.Sockets.TcpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $client.Connect([Net.IPAddress]::Loopback,49237)
        $true
    } catch [Net.Sockets.SocketException] {
        $false
    } finally {
        $client.Dispose()
    }
}

function Write-TrialSettings([string]$Profile, [bool]$IsCSharp) {
    [IO.Directory]::CreateDirectory($Profile) | Out-Null
    $bindings = [ordered]@{
        switch='layoutSwitchHotkey'; convert='toggleHotkey'; undo='undoHotkey'
        case='caseHotkey'; transliterate='translitHotkey'; spelling='spellingHotkey'
        spelling_review='spellingReviewHotkey'; capture_file='screenshotSaveHotkey'
        capture_edit='screenshotEditorHotkey'; history='clipboardHotkey'; add_word='addWordHotkey'
        choices='choicesHotkey'; accept_choice='acceptChoiceHotkey'; cancel_choice='cancelChoiceHotkey'
        tools='toolsHotkey'; snippet_menu=''; clear_drawing=''
    }
    $rustKeys = @{english=''; russian=''; ukrainian=''; capture_copy='Win+Shift+S'}
    $csharpSettings = @{historyEnabled=$true; layoutsInitialized=$true; windowsShortcutsInitialized=$true; screenshotCopyHotkey='Win+Shift+S'}
    $i = 0
    foreach ($action in $bindings.Keys) {
        $modifier = if ($i -lt 12) { 'Ctrl+Alt' } else { 'Ctrl+Alt+Shift' }
        $key = "$modifier+F$(13 + $i % 12)"
        $rustKeys[$action] = $key
        if ($bindings[$action]) { $csharpSettings[$bindings[$action]] = $key }
        $i++
    }
    $settings = if ($IsCSharp) { $csharpSettings } else { @{history_enabled=$true; hotkeys=$rustKeys} }
    [IO.File]::WriteAllText((Join-Path $Profile 'settings.json'),($settings | ConvertTo-Json -Depth 3 -Compress))
}

function Measure-Trial([string]$Implementation,[int]$Run) {
    $trial = Join-Path $output "$Implementation-$Run"
    [IO.Directory]::CreateDirectory($trial) | Out-Null
    $local = $null
    if ($Implementation -eq 'CSharp') {
        foreach ($name in (Get-ChildItem -LiteralPath $baseline -Name | Where-Object { $_ -ne 'data' })) {
            Copy-Item -LiteralPath (Join-Path $baseline $name) -Destination $trial -Recurse
        }
        $profile = Join-Path $trial 'data'
        Write-TrialSettings $profile $true
        [IO.File]::WriteAllText((Join-Path $profile 'settings.json.seen'),'')
        $readyFile = Join-Path $profile 'typlo.log'
        $readyText = 'Background runtime'
        $exe = Join-Path $trial 'Typlo.exe'
        $arguments = @('--tray')
    } else {
        $local = Join-Path $trial 'local'
        $profile = Join-Path $local 'Typlo-Rust'
        Write-TrialSettings $profile $false
        $readyFile = Join-Path $trial 'ready.txt'
        $readyText = 'Typlo Rust background ready'
        $exe = $rust
        $arguments = @('--measure-resident',$profile,$readyFile)
        if ($Implementation -eq 'RustPaired') {
            if (Test-LanPort) { throw 'Port 49237 is already in use' }
            $pair = Start-Trial $rust @('--prepare-lan-fixture',$profile,(Join-Path $trial 'peer')) $local
            try {
                $errorOutput = $pair.StandardError.ReadToEnd()
                $pair.WaitForExit()
                if ($pair.ExitCode -ne 0) { throw "Could not prepare a paired profile. $errorOutput" }
            } finally { $pair.Dispose() }
        }
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [TyploBenchmarkDesktop]::Start($exe,$desktopName,$arguments)
    try {
        do {
            if ($process.HasExited) { throw "$Implementation exited before readiness with code $($process.ExitCode)" }
            if ($timer.Elapsed.TotalSeconds -gt 12) { throw "$Implementation readiness timed out" }
            $ready = (Test-Path -LiteralPath $readyFile) -and ((Get-Content -LiteralPath $readyFile -Raw) -like "*$readyText*")
            if (!$ready) { Start-Sleep -Milliseconds 15 }
        } until ($ready)
        $readyMs = $timer.Elapsed.TotalMilliseconds
        if ($Implementation -eq 'CSharp' -and (Select-String -LiteralPath $readyFile -SimpleMatch 'Hotkey not registered:' -Quiet)) {
            throw 'C# could not register all benchmark hotkeys'
        }
        Start-Sleep -Milliseconds 900
        if ($Implementation -eq 'RustPaired' -and !(Test-LanPort)) { throw 'The paired Rust listener did not start' }
        $process.Refresh()
        $cpu = $process.TotalProcessorTime.TotalMilliseconds
        $idle = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Milliseconds 2000
        $process.Refresh()
        if ($process.HasExited) { throw "$Implementation exited before sampling" }
        [pscustomobject]@{
            Run=$Run; Implementation=$Implementation; ReadyMs=[math]::Round($readyMs,3)
            WorkingSetMiB=[math]::Round($process.WorkingSet64 / 1MB,3)
            PrivateMiB=[math]::Round($process.PrivateMemorySize64 / 1MB,3)
            IdleCpuMs=[math]::Round($process.TotalProcessorTime.TotalMilliseconds - $cpu,3)
            IdleWallMs=[math]::Round($idle.Elapsed.TotalMilliseconds,3)
            Threads=$process.Threads.Count
        }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

$desktopName = 'TyploBench-' + [Guid]::NewGuid().ToString('N')
$desktop = [TyploBenchmarkDesktop]::Open($desktopName)
try {
    $raw = for ($run=1; $run -le $Runs; $run++) {
        $order = if ($run % 2) { @('CSharp','Rust','RustPaired') } else { @('RustPaired','Rust','CSharp') }
        foreach ($implementation in $order) { Measure-Trial $implementation $run }
    }
} finally {
    [void][TyploBenchmarkDesktop]::CloseDesktop($desktop)
}
$raw | Export-Csv -LiteralPath (Join-Path $output 'resident.csv') -NoTypeInformation
$raw | Format-Table
