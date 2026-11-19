param([Parameter(Mandatory)][string]$InstallerPath, [Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
# This test writes per-user uninstall/shortcut metadata: CI disposable runners ONLY.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') { throw 'Installer smoke requires a disposable GitHub-hosted runner' }
$installer = (Resolve-Path $InstallerPath).Path
$root = Join-Path $env:RUNNER_TEMP ('Typlo installer smoke ' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $root 'Installed app'
$expected = Join-Path $root 'Expected payload'
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $expected
function Invoke-Setup([string]$Exe, [string]$Arguments, [bool]$ShouldPass = $true) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    if (!$p.WaitForExit(120000)) { $p.Kill($true); throw 'Installer timed out' }
    if (($p.ExitCode -eq 0) -ne $ShouldPass) { throw "Unexpected setup exit: $($p.ExitCode)" }
}
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`" /LOG=`"$root/install.log`""
foreach ($file in (Get-ChildItem $expected -Recurse -File)) {
    $relative = [IO.Path]::GetRelativePath($expected, $file.FullName)
    if ((Get-FileHash $file.FullName).Hash -cne (Get-FileHash (Join-Path $installed $relative)).Hash) { throw "Installed bytes differ: $relative" }
}
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Typlo.lnk'
if (!(Test-Path $shortcut)) { throw 'Start menu shortcut missing' }
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
if ($link.TargetPath -ine (Join-Path $installed 'Typlo.exe')) { throw 'Shortcut target mismatch' }
New-Item -ItemType Directory "$installed/data" | Out-Null
Set-Content "$installed/data/installer-smoke.txt" 'preserve me'
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`"" $false
# Simulate updater-owned bytes and marker without invoking any live update feed.
New-Item -ItemType Directory "$installed/versions/99.0.0" -Force | Out-Null
Set-Content "$installed/versions/99.0.0/Typlo.exe" 'updater-owned sentinel'
Set-Content "$installed/rust-current.txt" '99.0.0'
Invoke-Setup "$installed/unins000.exe" '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
if (Test-Path "$installed/Typlo.exe") { throw 'Uninstall retained the base executable' }
if (Test-Path $shortcut) { throw 'Uninstall retained shortcut' }
if ((Get-Content "$installed/data/installer-smoke.txt" -Raw).Trim() -cne 'preserve me') { throw 'Uninstall modified profile' }
# Refuse updater-managed folders, even if their base executable is missing.
if ((Get-Content "$installed/versions/99.0.0/Typlo.exe" -Raw).Trim() -cne 'updater-owned sentinel') { throw 'Uninstall modified updater-owned files' }
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`"" $false
if ((Get-Content "$installed/rust-current.txt" -Raw).Trim() -cne '99.0.0') { throw 'Setup modified updater marker' }
Write-Host 'PASS fresh install, payload hashes, shortcut, existing-install refusal, uninstall and profile preservation'
