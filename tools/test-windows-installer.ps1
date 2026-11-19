param([Parameter(Mandatory)][string]$InstallerPath, [Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
# This test writes per-user uninstall/shortcut metadata: CI disposable runners ONLY.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') { throw 'Installer smoke requires a disposable GitHub-hosted runner' }
$installer = (Resolve-Path $InstallerPath).Path
$root = Join-Path $env:RUNNER_TEMP ('Typlo installer smoke ' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $root 'Installed app'
$expected = Join-Path $root 'Expected payload'
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $expected
function Invoke-Setup([string]$Exe, [string]$Arguments, [int]$ExpectedExit = 0,
                      [string]$Stage = 'setup', [string]$ExpectedLog = '') {
    $logPath = Join-Path $root "$Stage.log"
    Write-Host "Installer smoke: $Stage (expected exit $ExpectedExit)"
    $p = Start-Process -FilePath $Exe -ArgumentList "$Arguments /LOG=`"$logPath`"" -PassThru
    try {
        if (!$p.WaitForExit(120000)) { $p.Kill($true); throw "Installer timed out: $Stage" }
        if ($p.ExitCode -ne $ExpectedExit) { throw "Unexpected setup exit at ${Stage}: $($p.ExitCode), expected $ExpectedExit" }
        if ($ExpectedLog -and (!(Test-Path $logPath) -or !(Select-String -LiteralPath $logPath -SimpleMatch $ExpectedLog -Quiet))) {
            throw "Expected refusal reason missing at ${Stage}: $ExpectedLog"
        }
        Write-Host "PASS installer stage: $Stage"
    }
    finally {
        if (Test-Path $logPath) { Get-Content -LiteralPath $logPath -Tail 100 | Write-Host }
        $p.Dispose()
    }
}
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`"" -Stage 'fresh-install'
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
# InitializeSetup=False is exit 1, not user-cancel exit 2 (Inno Setup exit-code contract).
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`"" 1 -Stage 'registered-same-directory' -ExpectedLog 'Typlo setup is already registered for this user.'
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$root/Alternate clean app`"" 1 -Stage 'registered-alternate-directory' -ExpectedLog 'Typlo setup is already registered for this user.'
if (Test-Path "$root/Alternate clean app") { throw 'Refused setup created an alternate installation' }
if ((Get-Content "$installed/data/installer-smoke.txt" -Raw).Trim() -cne 'preserve me') { throw 'Refused setup modified profile' }
# Simulate updater-owned bytes and marker without invoking any live update feed.
New-Item -ItemType Directory "$installed/versions/99.0.0" -Force | Out-Null
Set-Content "$installed/versions/99.0.0/Typlo.exe" 'updater-owned sentinel'
Set-Content "$installed/rust-current.txt" '99.0.0'
Invoke-Setup "$installed/unins000.exe" '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Stage 'uninstall'
if (Test-Path "$installed/Typlo.exe") { throw 'Uninstall retained the base executable' }
if (Test-Path $shortcut) { throw 'Uninstall retained shortcut' }
if ((Get-Content "$installed/data/installer-smoke.txt" -Raw).Trim() -cne 'preserve me') { throw 'Uninstall modified profile' }
# Refuse updater-managed folders, even if their base executable is missing.
if ((Get-Content "$installed/versions/99.0.0/Typlo.exe" -Raw).Trim() -cne 'updater-owned sentinel') { throw 'Uninstall modified updater-owned files' }
Invoke-Setup $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installed`"" 7 -Stage 'updater-state-reinstall' -ExpectedLog 'An existing Typlo installation or updater state was found.'
if ((Get-Content "$installed/rust-current.txt" -Raw).Trim() -cne '99.0.0') { throw 'Setup modified updater marker' }
Write-Host 'PASS fresh install, payload hashes, shortcut, existing-install refusal, uninstall and profile preservation'
