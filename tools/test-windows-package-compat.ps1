param([Parameter(Mandatory)][string]$ArchivePath, [string]$Root = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
$version = Get-ReleaseVersion -Root $Root
$candidate = (Resolve-Path -LiteralPath $ArchivePath).Path
$candidateHash = (Get-FileHash $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('TyploLegacyPackage-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $temporary)
$legacy = Join-Path $temporary 'v0.1.7.zip'
# Last published client before the EXE rename. Its immutable archive contract
# rejects Typlo.exe; this is an explicit manual handoff, not an automatic upgrade.
Invoke-WebRequest 'https://github.com/bitcodepro/Typlo/releases/download/v0.1.7/typlo-win-x64.zip' -OutFile $legacy -TimeoutSec 180
if ((Get-FileHash $legacy -Algorithm SHA256).Hash.ToLowerInvariant() -cne '22fa7baa2c43b052ca97ac1c1372fd69bed75b7cb20fb8d2419f356c14bf9845') { throw 'Published v0.1.7 ZIP SHA256 mismatch' }
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
    if (!$process.WaitForExit(60000)) { $process.Kill($true); throw 'v0.1.7 package verification timed out' }
    $detail = $errorRead.GetAwaiter().GetResult()
    if ($process.ExitCode -eq 0 -or $detail -notlike '*Unsafe or oversized update entry*') { throw "Expected v0.1.7 to reject renamed archive, got $($process.ExitCode): $detail" }
} finally { $process.Dispose() }
# Simulate a manual extraction without launching a resident or using a real profile.
$manual = Join-Path $temporary 'manual install with spaces'
[IO.Compression.ZipFile]::ExtractToDirectory($candidate, $manual)
foreach ($name in @('Typlo.exe', 'typlo-rust.exe', 'typlo-settings.exe', 'resources/language-detection/words.sqlite3', 'README.md', 'LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $manual $name) -PathType Leaf)) { throw "Manual installation missing: $name" }
}
# Exercise the shipped old-name launcher with real arguments and native exit
# codes. Both valid and invalid hashes must reach the canonical verifier.
$forwardedArchive = Join-Path $temporary 'archive with spaces.zip'
Copy-Item -LiteralPath $candidate -Destination $forwardedArchive
foreach ($hash in @($candidateHash, ('0' * 64))) {
    $spec = [Diagnostics.ProcessStartInfo]::new((Join-Path $manual 'typlo-rust.exe'))
    $spec.UseShellExecute = $false
    $spec.CreateNoWindow = $true
    $spec.RedirectStandardError = $true
    foreach ($argument in @('--verify-update-package', $forwardedArchive, $version, $hash)) { $spec.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($spec)
    try {
        $errors = $child.StandardError.ReadToEndAsync()
        if (!$child.WaitForExit(60000)) { $child.Kill($true); throw 'Legacy launcher timed out' }
        $detail = $errors.GetAwaiter().GetResult()
        if (($hash -ceq $candidateHash) -ne ($child.ExitCode -eq 0)) { throw "Legacy launcher lost verifier status: $detail" }
        if ($hash -cne $candidateHash -and $detail -notlike '*SHA-256 does not match*') { throw "Legacy launcher did not forward the digest check: $detail" }
    } finally { $child.Dispose() }
}
Write-Host 'PASS manual layout and legacy shortcut argument/exit forwarding; published v0.1.7 rejects the new format, so manual handoff is required (no live upgrade tested)'
