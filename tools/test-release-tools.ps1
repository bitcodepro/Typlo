$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'Expected rejection' }
}
Test-WindowsImports @('KERNEL32.dll', 'api-ms-win-core-synch-l1-2-0.dll')
Assert-Rejected { Test-WindowsImports @('vcruntime140.dll') }
Assert-Rejected { Test-WindowsImports @('sqlite3.dll') }
Assert-Rejected { Test-WindowsImports @() }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TyploReleaseTests-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $fixture)
Set-Content "$fixture/Cargo.toml" "[package]`nname = `"typlo-rust`"`nversion = `"0.1.0`""
Set-Content "$fixture/Cargo.lock" "[[package]]`nname = `"typlo-rust`"`nversion = `"0.1.0`""
if ((Get-ReleaseVersion $fixture) -cne '0.1.0') { throw 'Version parsing failed' }
foreach ($bad in @('0.1.0-rc1', '0.1.0-pr1', 'v0.1.0', '01.1.0', '0.1')) {
    Set-Content "$fixture/Cargo.toml" "[package]`nversion = `"$bad`""
    Assert-Rejected { Get-ReleaseVersion $fixture }
}
Set-Content "$fixture/Cargo.toml" "[package]`nversion = `"0.2.0`""
Assert-Rejected { Get-ReleaseVersion $fixture }
$commit = 'a' * 40
Set-Content "$fixture/Typlo-0.1.0-Portable-win-x64.zip" 'portable fixture'
Set-Content "$fixture/typlo-rust-win-x64.zip" 'update fixture'
$digest = (Get-FileHash "$fixture/typlo-rust-win-x64.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$fixture/typlo-rust-win-x64.zip.sha256" "$digest  typlo-rust-win-x64.zip"
$assets = @('Typlo-0.1.0-Portable-win-x64.zip', 'typlo-rust-win-x64.zip', 'typlo-rust-win-x64.zip.sha256') | ForEach-Object {
    [pscustomobject]@{ name = $_; sha256 = (Get-FileHash (Join-Path $fixture $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
@{ version = '0.1.0'; commit = $commit; assets = @($assets) } | ConvertTo-Json -Depth 5 | Set-Content "$fixture/release-manifest.json"
$manifest = Read-ReleaseManifest $fixture $commit '0.1.0'
Assert-Rejected { Read-ReleaseManifest $fixture ('b' * 40) '0.1.0' }
Assert-Rejected { Read-ReleaseManifest $fixture $commit '0.2.0' }
$manifestHash = (Get-FileHash "$fixture/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
$uploaded = @($assets | ForEach-Object { [pscustomobject]@{ name = $_.name; digest = "sha256:$($_.sha256)"; state = 'uploaded' } })
$uploaded += [pscustomobject]@{ name = 'release-manifest.json'; digest = "sha256:$manifestHash"; state = 'uploaded' }
Test-UploadedAssets $uploaded $manifest $manifestHash
Assert-Rejected { Test-UploadedAssets $uploaded[0..2] $manifest $manifestHash }
$uploaded[0].digest = 'sha256:' + ('0' * 64)
Assert-Rejected { Test-UploadedAssets $uploaded $manifest $manifestHash }
$uploaded[0].digest = $null
Assert-Rejected { Test-UploadedAssets $uploaded $manifest $manifestHash }
Add-Content "$fixture/typlo-rust-win-x64.zip" 'tampered'
Assert-Rejected { Read-ReleaseManifest $fixture $commit '0.1.0' }
Write-Host 'PASS release version, lockfile, provenance, asset completeness, tampering and uploaded digest checks'

# Exercise publication control flow entirely offline. No executable gh is called.
$mockRoot = Join-Path $fixture 'publisher'
[void](New-Item -ItemType Directory "$mockRoot/tools" -Force)
[void](New-Item -ItemType Directory "$mockRoot/dist" -Force)
Copy-Item "$PSScriptRoot/release-common.ps1","$PSScriptRoot/publish-windows-release.ps1" "$mockRoot/tools"
Set-Content "$mockRoot/Cargo.toml" "[package]`nversion = `"0.1.0`""
Copy-Item "$fixture/Cargo.lock" $mockRoot
Set-Content "$fixture/typlo-rust-win-x64.zip" 'update fixture'
foreach ($name in @($assets.name) + 'release-manifest.json') { Copy-Item (Join-Path $fixture $name) "$mockRoot/dist" }
$global:TyploReleaseMockDirectory = "$mockRoot/dist"
$global:TyploReleaseMockManifest = Read-ReleaseManifest $global:TyploReleaseMockDirectory $commit '0.1.0'
$global:TyploReleaseMockManifestHash = (Get-FileHash "$mockRoot/dist/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE = 0
    $key = $Arguments -join ' '
    if ($key -match '^api --paginate --slurp ') { return (ConvertTo-Json -InputObject @(@($global:TyploReleaseMock.releases)) -Depth 15 -Compress) }
    if ($key -match '^api repos/example/repo/git/matching-refs/') {
        if ($global:TyploReleaseMock.tag) { return '[{"ref":"refs/tags/v0.1.0"}]' }; return '[]'
    }
    if ($key -eq 'api repos/example/repo/commits/v0.1.0 --jq .sha') { return $global:TyploReleaseMock.tag }
    if ($key -match '^api repos/example/repo/releases/') { return ($global:TyploReleaseMock.release | ConvertTo-Json -Depth 15 -Compress) }
    if ($key -match '^release download ') {
        $target = $Arguments[[array]::IndexOf($Arguments, '--dir') + 1]
        Copy-Item "$global:TyploReleaseMockDirectory/*" $target
        return
    }
    if ($key -match '^release create ') {
        $global:TyploReleaseMock.writes++
        $global:TyploReleaseMock.release = [pscustomobject]@{ id = 1; tag_name = 'v0.1.0'; target_commitish = ('a' * 40); draft = $true; prerelease = $false; assets = @(); published_at = $null }
        return
    }
    if ($key -match '^release upload ') {
        $global:TyploReleaseMock.writes++
        $file = Get-Item $Arguments[3]
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($global:TyploReleaseMock.badDigest) { $hash = '0' * 64 }
        $global:TyploReleaseMock.release.assets += [pscustomobject]@{ name = $file.Name; digest = "sha256:$hash"; state = 'uploaded' }
        return
    }
    if ($key -match '^api --method PATCH ') {
        $global:TyploReleaseMock.writes++
        $global:TyploReleaseMock.published = $true
        $global:TyploReleaseMock.release.draft = $false
        $global:TyploReleaseMock.release.published_at = '2026-01-01T00:00:00Z'
        $global:TyploReleaseMock.tag = 'a' * 40
        return '{}'
    }
    throw "Unexpected mock gh call: $key"
}
$global:TyploReleaseMock = @{ releases = @(); tag = $null; writes = 0; published = $false }
& "$mockRoot/tools/publish-windows-release.ps1" -Repository example/repo -Commit $commit
if (!$global:TyploReleaseMock.published -or $global:TyploReleaseMock.writes -ne 6) { throw 'Draft publication sequence failed' }
$global:TyploReleaseMock.releases = @($global:TyploReleaseMock.release)
$global:TyploReleaseMock.writes = 0
& "$mockRoot/tools/publish-windows-release.ps1" -Repository example/repo -Commit $commit
if ($global:TyploReleaseMock.writes) { throw 'Published rerun mutated release' }
$partial = $global:TyploReleaseMock.release | ConvertTo-Json -Depth 15 | ConvertFrom-Json
$partial.draft = $true
$partial.published_at = $null
$partial.assets = @($partial.assets[0])
$global:TyploReleaseMock = @{ releases = @($partial); release = $partial; tag = $commit; writes = 0; published = $false }
& "$mockRoot/tools/publish-windows-release.ps1" -Repository example/repo -Commit $commit
if (!$global:TyploReleaseMock.published -or $global:TyploReleaseMock.writes -ne 4) { throw 'Partial draft resume failed' }
$global:TyploReleaseMock.writes = 0
$global:TyploReleaseMock.tag = 'b' * 40
Assert-Rejected { & "$mockRoot/tools/publish-windows-release.ps1" -Repository example/repo -Commit $commit }
if ($global:TyploReleaseMock.writes) { throw 'Conflicting tag mutated release' }
$global:TyploReleaseMock = @{ releases = @(); tag = $null; writes = 0; published = $false; badDigest = $true }
Assert-Rejected { & "$mockRoot/tools/publish-windows-release.ps1" -Repository example/repo -Commit $commit }
if ($global:TyploReleaseMock.published) { throw 'Bad digest was published' }
Write-Host 'PASS draft publication, published rerun, conflicting SHA and bad remote digest (mock GitHub)'
Assert-Rejected { & "$PSScriptRoot/package-windows.ps1" -PayloadDirectory $fixture -OutputDirectory "$fixture/output" -Version '0.1.0-rc1' }
Assert-Rejected { & "$PSScriptRoot/package-windows.ps1" -PayloadDirectory $fixture -OutputDirectory "$fixture/output" -Version '0.1.0' }
Write-Host 'PASS packaging refuses preview versions and nested archive directories'
