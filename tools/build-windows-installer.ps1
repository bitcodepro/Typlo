param([Parameter(Mandatory)][string]$ArchivePath,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid installer version' }
$compiler = "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe"
if (!(Test-Path $compiler)) { throw 'Install official Inno Setup 6 (ISCC.exe); Windows hosted runners provide it.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$installer = Join-Path $output 'typlo-win-x64-setup.exe'
if ((Test-Path $installer) -or (Test-Path "$installer.sha256")) { throw 'Installer output already exists' }
$payload = Join-Path ([IO.Path]::GetTempPath()) ('typlo-installer-payload-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $payload
    foreach ($name in @('Typlo.exe', 'typlo-settings.exe', 'resources', 'LICENSE')) {
        if (!(Test-Path (Join-Path $payload $name))) { throw "Missing installer payload: $name" }
    }
    foreach ($name in @('data', 'versions', 'rust-current.txt')) {
        if (Test-Path (Join-Path $payload $name)) { throw "Forbidden profile/update state in installer: $name" }
    }
    & $compiler "/DAppVersion=$Version" "/DPayloadDirectory=$payload" "/DOutputDirectory=$output" "$PSScriptRoot/windows-installer.iss"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $installer)) { throw 'Inno Setup compilation failed' }
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$installer.sha256", "$hash  typlo-win-x64-setup.exe`n")
} finally {
    if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
}
