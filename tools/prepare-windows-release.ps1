param([switch]$SoftwareOpenGL, [string]$Root = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
Set-Location $root
$version = Get-ReleaseVersion -Root $root
$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'git failed' }
$payload = Join-Path $root "package/Typlo-$version"
$output = Join-Path $root 'dist'
if ((Test-Path $payload) -or (Test-Path $output)) { throw 'Package/output directories must be clean' }
[void](New-Item -ItemType Directory -Path $payload)
Copy-Item resources $payload -Recurse
Copy-Item README.md,LICENSE $payload
$renamed = Test-Path (Join-Path $root 'src/bin/typlo.rs')
$binaries = if ($renamed) { @('Typlo.exe', 'typlo-settings.exe', 'typlo-rust.exe') } else { @('typlo-rust.exe', 'typlo-settings.exe') }
foreach ($name in $binaries) {
    $target = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $root 'target' }
    $builtName = if ($name -ceq 'Typlo.exe') { 'typlo.exe' } else { $name }
    Copy-Item (Join-Path $target "x86_64-pc-windows-msvc/release/$builtName") (Join-Path $payload $name)
}
# Static CRT is required: updater deliberately does not accept arbitrary runtime DLLs.
$vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$vs) { throw 'Visual C++ tools not found' }
$dumpbin = Get-ChildItem "$vs/VC/Tools/MSVC/*/bin/Hostx64/x64/dumpbin.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (!$dumpbin) { throw 'dumpbin not found' }
foreach ($name in $binaries) {
    $imports = & $dumpbin.FullName /dependents (Join-Path $payload $name)
    if ($LASTEXITCODE -ne 0) { throw 'dumpbin failed' }
    $dlls = @($imports | ForEach-Object { if ($_ -match '^\s+([\w.-]+\.dll)\s*$') { $Matches[1] } })
    Test-WindowsImports $dlls
    Write-Host "$name imports: $($dlls -join ', ')"
}
# Consolidate notices to stay below updater's 128-entry ceiling.
$json = cargo metadata --locked --format-version 1 --filter-platform x86_64-pc-windows-msvc --features gui
if ($LASTEXITCODE -ne 0) { throw 'cargo metadata failed' }
$metadata = $json | ConvertFrom-Json
$notices = [Text.StringBuilder]::new()
$sysroot = rustc --print sysroot
if ($LASTEXITCODE -ne 0) { throw 'rustc sysroot failed' }
$rustNotices = Join-Path $sysroot 'share/doc/rust/COPYRIGHT-library.html'
if (!(Test-Path $rustNotices)) { throw 'Install rust-docs to include Rust standard library licenses' }
[void]$notices.AppendLine('=== Rust standard library copyright and licenses (HTML) ===')
[void]$notices.AppendLine([IO.File]::ReadAllText($rustNotices))
foreach ($file in (Get-ChildItem (Join-Path $sysroot 'share/doc/rust/licenses') -File)) {
    [void]$notices.AppendLine("--- Rust: $($file.Name) ---")
    [void]$notices.AppendLine([IO.File]::ReadAllText($file.FullName))
}
foreach ($package in ($metadata.packages | Sort-Object name,version)) {
    if (!$package.source) { continue }
    [void]$notices.AppendLine("`n=== $($package.name) $($package.version) | $($package.license) ===")
    $directory = Split-Path $package.manifest_path
    $licenses = @(Get-ChildItem $directory -File -Recurse | Where-Object Name -Match '(?i)(LICENSE|LICENCE|COPYING|NOTICE|^OFL\.txt$|^UFL\.txt$)')
    if ($package.license_file) { $licenses += Get-Item (Join-Path $directory $package.license_file) }
    foreach ($subdir in @('licenses', 'LICENSES')) {
        if (Test-Path (Join-Path $directory $subdir)) { $licenses += Get-ChildItem (Join-Path $directory $subdir) -File -Recurse }
    }
    $licenses = @($licenses | Sort-Object FullName -Unique)
    $supplement = Join-Path $PSScriptRoot "release-licenses/$($package.name)-$($package.version).txt"
    if (Test-Path $supplement) { $licenses += Get-Item $supplement }
    if (!$licenses.Count) { throw "Missing license text: $($package.name) $($package.version)" }
    foreach ($file in $licenses) {
        [void]$notices.AppendLine("--- $($file.Name) ---")
        [void]$notices.AppendLine([IO.File]::ReadAllText($file.FullName))
    }
}
$noticeDirectory = Join-Path $payload 'Third-party-licenses/dependencies'
[void](New-Item -ItemType Directory -Path $noticeDirectory -Force)
[IO.File]::WriteAllText((Join-Path $noticeDirectory 'LICENSE.txt'), $notices.ToString())
if ($SoftwareOpenGL) {
    & "$PSScriptRoot/windows-software-gl-smoke.ps1" -PayloadDirectory $payload
} else {
    & "$PSScriptRoot/windows-ui-smoke.ps1" -Resident (Join-Path $payload $binaries[0]) -Settings "$payload/typlo-settings.exe"
}
& "$PSScriptRoot/package-windows.ps1" -PayloadDirectory $payload -OutputDirectory $output -Version $version
if (@(Get-ChildItem $output -File -Filter '*.zip').Count -ne 1) { throw 'Expected one Windows installation/update ZIP' }
$assets = @(Get-ChildItem $output -File | Sort-Object Name | ForEach-Object {
    @{ name = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ schema_version = 1; platform = 'windows-x64'; version = $version; commit = $commit; assets = $assets } | ConvertTo-Json -Depth 5 | Set-Content "$output/release-manifest.json" -Encoding utf8
[void](Read-ReleaseManifest -Directory $output -Commit $commit -Version $version)
