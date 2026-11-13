$ErrorActionPreference = 'Stop'
function Test-WindowsImports {
    param([string[]]$Dlls)
    if (!$Dlls.Count) { throw 'No PE dependencies detected' }
    $system = @('advapi32.dll', 'bcrypt.dll', 'bcryptprimitives.dll', 'combase.dll', 'comdlg32.dll', 'crypt32.dll', 'dwmapi.dll', 'gdi32.dll', 'imm32.dll', 'iphlpapi.dll', 'kernel32.dll', 'ntdll.dll', 'ole32.dll', 'oleaut32.dll', 'opengl32.dll', 'shell32.dll', 'shlwapi.dll', 'user32.dll', 'uxtheme.dll', 'ws2_32.dll')
    foreach ($dll in $Dlls) {
        # Core API sets are virtual Windows contracts, not files in System32.
        if ($dll -notin $system -and $dll -notmatch '^api-ms-win-core-[a-z0-9-]+\.dll$') { throw "Unpackaged runtime dependency: $dll" }
    }
}
function Get-ReleaseVersion {
    param([string]$Root = (Split-Path $PSScriptRoot))
    $manifest = Get-Content (Join-Path $Root 'Cargo.toml') -Raw
    $version = [regex]::Match($manifest, '(?ms)^\[package\]\s*.*?^version\s*=\s*"([^"]+)"').Groups[1].Value
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Release version must be X.Y.Z' }
    $lock = Get-Content (Join-Path $Root 'Cargo.lock') -Raw
    if ($lock -notmatch ('(?m)^name = "typlo-rust"\r?\nversion = "' + [regex]::Escape($version) + '"')) { throw 'Cargo.lock version mismatch' }
    return $version
}
function Read-ReleaseManifest {
    param([string]$Directory, [string]$Commit, [string]$Version)
    $manifest = Get-Content (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.commit -cne $Commit -or $manifest.version -cne $Version) { throw 'Artifact provenance mismatch' }
    $expected = @("Typlo-$Version-Portable-win-x64.zip", 'typlo-rust-win-x64.zip', 'typlo-rust-win-x64.zip.sha256')
    if (@($manifest.assets).Count -ne 3) { throw 'Unexpected asset count' }
    foreach ($name in $expected) {
        $entries = @($manifest.assets | Where-Object { $_.name -ceq $name })
        if ($entries.Count -ne 1 -or $entries[0].sha256 -cnotmatch '^[a-f0-9]{64}$') { throw "Invalid asset: $name" }
        $file = Join-Path $Directory $name
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entries[0].sha256) { throw "Asset digest mismatch: $name" }
    }
    $line = (Get-Content (Join-Path $Directory 'typlo-rust-win-x64.zip.sha256') -Raw).Trim()
    $digest = ($manifest.assets | Where-Object name -eq 'typlo-rust-win-x64.zip').sha256
    if ($line -cne "$digest  typlo-rust-win-x64.zip") { throw 'Updater checksum mismatch' }
    return $manifest
}
function Test-UploadedAssets {
    param($Assets, $Manifest, [string]$ManifestDigest)
    $expected = @($Manifest.assets) + @([pscustomobject]@{ name = 'release-manifest.json'; sha256 = $ManifestDigest })
    if (@($Assets).Count -ne $expected.Count) { throw 'Unexpected uploaded asset count' }
    foreach ($file in $expected) {
        $matches = @($Assets | Where-Object { $_.name -ceq $file.name })
        if ($matches.Count -ne 1 -or $matches[0].state -ne 'uploaded' -or $matches[0].digest -cne "sha256:$($file.sha256)") {
            throw "Uploaded asset digest/state mismatch: $($file.name)"
        }
    }
}
