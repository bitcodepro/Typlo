param(
    [Parameter(Mandatory)] [string]$TyploReleaseExe,
    [Parameter(Mandatory)] [string]$DotNetBenchmarkExe,
    [ValidateRange(1, 20)] [int]$Runs = 5,
    [ValidateRange(1, 10000000)] [int]$Iterations = 100000,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rust = (Resolve-Path (Join-Path $root 'target/release/typlo-rust.exe')).Path
$fixture = Join-Path $PSScriptRoot 'fixtures.tsv'
$TyploReleaseExe = (Resolve-Path -LiteralPath $TyploReleaseExe).Path
$DotNetBenchmarkExe = (Resolve-Path -LiteralPath $DotNetBenchmarkExe).Path
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $root ('target/comparison-' + [guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

function Measure-Engine($implementation, $exe, [string[]]$arguments) {
    $result = & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$implementation benchmark exited with $LASTEXITCODE" }
    if ($result -notmatch 'cases=(\d+) iterations=(\d+) elapsed_ms=([0-9.]+) checksum=([0-9a-f]+)') {
        throw "$implementation produced invalid benchmark output"
    }
    [pscustomobject]@{
        Implementation = $implementation
        Cases = [int]$Matches[1]
        Iterations = [int]$Matches[2]
        ElapsedMs = [double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture)
        Checksum = $Matches[4]
    }
}

function Measure-Resident($implementation, $exe, $run) {
    $directory = Join-Path $OutputDirectory "$implementation-$run"
    New-Item -ItemType Directory $directory | Out-Null
    $report = Join-Path $directory 'ready.txt'
    $profile = Join-Path $directory 'profile'
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $exe
    $arguments = if ($implementation -eq 'Rust') {
        @('--background-probe', $profile, $report)
    } else {
        @('--background-probe', '--profile', $profile, '--report', $report, '--lifetime-seconds', '30', '--tray')
    }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        while (!(Test-Path -LiteralPath $report)) {
            if ($process.HasExited) { throw "$implementation exited before readiness with code $($process.ExitCode)" }
            if ($timer.Elapsed.TotalSeconds -gt 12) { throw "$implementation readiness timed out" }
            Start-Sleep -Milliseconds 10
        }
        $startup = $timer.Elapsed.TotalMilliseconds
        Start-Sleep -Milliseconds 600
        $process.Refresh()
        if ($process.HasExited) { throw "$implementation exited before memory sampling" }
        [pscustomobject]@{
            Implementation = $implementation
            ReadyMs = [math]::Round($startup, 2)
            WorkingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)
            PrivateMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        }
    } finally {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        $process.Dispose()
    }
}

$engineResults = @()
$residentResults = @()
for ($run = 1; $run -le $Runs; $run++) {
    $order = if ($run % 2) { @('DotNet', 'Rust') } else { @('Rust', 'DotNet') }
    foreach ($implementation in $order) {
        if ($implementation -eq 'Rust') {
            $engineResults += Measure-Engine Rust $rust @('--engine-bench', $fixture, $Iterations)
            $residentResults += Measure-Resident Rust $rust $run
        } else {
            $engineResults += Measure-Engine DotNet $DotNetBenchmarkExe @($fixture, $Iterations)
            $residentResults += Measure-Resident DotNet $TyploReleaseExe $run
        }
    }
}
if (@($engineResults.Checksum | Select-Object -Unique).Count -ne 1) {
    throw 'The engine outputs differ'
}
$engineResults | Export-Csv (Join-Path $OutputDirectory 'engine.csv') -NoTypeInformation
$residentResults | Export-Csv (Join-Path $OutputDirectory 'resident.csv') -NoTypeInformation
$engineResults | Format-Table
$residentResults | Format-Table
Write-Output "Results in $OutputDirectory"
