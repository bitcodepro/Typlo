param([Parameter(Mandatory)][string]$ArchivePath, [string]$Root = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
$version = Get-ReleaseVersion -Root $Root
$candidate = (Resolve-Path -LiteralPath $ArchivePath).Path
$candidateHash = (Get-FileHash $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('TyploLegacyPackage-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $temporary)
$legacy = Join-Path $temporary 'v0.1.0.zip'
# Published v0.1.0 from 43b5643; pin its bytes, never modify its release assets.
Invoke-WebRequest 'https://github.com/bitcodepro/Typlo2.0/releases/download/v0.1.0/typlo-rust-win-x64.zip' -OutFile $legacy -TimeoutSec 180
if ((Get-FileHash $legacy -Algorithm SHA256).Hash.ToLowerInvariant() -cne '35517d4b707d87b280dd1451be070a7735ed5c9264ec71a762242d650b81c25d') { throw 'Published v0.1.0 ZIP SHA256 mismatch' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($legacy)
$resident = Join-Path $temporary 'typlo-rust.exe'
try {
    $entry = $zip.GetEntry('typlo-rust.exe')
    if (!$entry) { throw 'Published resident missing' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $resident)
} finally { $zip.Dispose() }
$spec = [Diagnostics.ProcessStartInfo]::new($resident)
$spec.UseShellExecute = $false
$spec.CreateNoWindow = $true
$spec.RedirectStandardError = $true
$spec.WorkingDirectory = $temporary
foreach ($argument in @('--verify-update-package', $candidate, $version, $candidateHash)) { $spec.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($spec)
try {
    $errorRead = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(60000)) { $process.Kill($true); throw 'v0.1.0 package verification timed out' }
    $detail = $errorRead.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "v0.1.0 rejected the package: $detail" }
} finally { $process.Dispose() }
# Simulate a manual extraction without launching a resident or using a real profile.
$manual = Join-Path $temporary 'manual-install'
[IO.Compression.ZipFile]::ExtractToDirectory($candidate, $manual)
foreach ($name in @('typlo-rust.exe', 'typlo-settings.exe', 'resources/language-detection/words.sqlite3', 'README.md', 'LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $manual $name) -PathType Leaf)) { throw "Manual installation missing: $name" }
}
Write-Host 'PASS internal ZIP format and isolated manual extraction; v0.1.0 cannot discover typlo-win-x64.zip (no automatic migration or live update tested)'
