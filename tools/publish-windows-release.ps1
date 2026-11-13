param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
function Invoke-GitHub {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0])" }
    return $result
}
function Get-RemoteRelease {
    param([long]$Id)
    return (Invoke-GitHub @('api', "repos/$Repository/releases/$Id") | ConvertFrom-Json)
}
$version = Get-ReleaseVersion
$tag = "v$version"
$directory = Join-Path (Split-Path $PSScriptRoot) 'dist'
$manifest = Read-ReleaseManifest -Directory $directory -Commit $Commit -Version $version
$pages = Invoke-GitHub @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
$releases = @($pages | ForEach-Object { foreach ($release in $_) { $release } })
$existing = @($releases | Where-Object { $_.tag_name -ceq $tag })
if ($existing.Count -gt 1) { throw 'Duplicate release tag' }
$refs = @(Invoke-GitHub @('api', "repos/$Repository/git/matching-refs/tags/$tag") | ConvertFrom-Json | Where-Object { $_.ref -ceq "refs/tags/$tag" })
if ($refs.Count) {
    $tagCommit = Invoke-GitHub @('api', "repos/$Repository/commits/$tag", '--jq', '.sha')
    if ($tagCommit -cne $Commit) { throw 'Tag belongs to another commit; refusing to overwrite' }
    if (!$existing.Count) { throw 'Tag already exists without a release; refusing to adopt it' }
}
if ($existing.Count) {
    $release = Get-RemoteRelease $existing[0].id
    if ($release.target_commitish -cne $Commit -or $release.prerelease) { throw 'Release provenance/type mismatch' }
    if (!$release.draft) {
        if (!$refs.Count -or !$release.published_at) { throw 'Published release has no verified tag/date' }
        # A rebuild can have different ZIP timestamps. Validate the original assets instead.
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory $temporary)
        [void](Invoke-GitHub @('release', 'download', $tag, '--repo', $Repository, '--dir', $temporary))
        $original = Read-ReleaseManifest -Directory $temporary -Commit $Commit -Version $version
        $digest = (Get-FileHash "$temporary/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
        Test-UploadedAssets -Assets $release.assets -Manifest $original -ManifestDigest $digest
        Write-Host "Release $tag already published and verified for $Commit"
        return
    }
} else {
    foreach ($release in $releases) {
        if (!$release.draft -and !$release.prerelease -and $release.tag_name -match '^v(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -ge [version]$version) {
            throw 'Cargo version must be newer than published releases'
        }
    }
    # Exact commit, never a moving branch. No tag-trigger workflow is needed.
    [void](Invoke-GitHub @('release', 'create', $tag, '--repo', $Repository, '--target', $Commit, '--draft', '--title', "Typlo $version", '--notes', "Windows x64 release from commit $Commit."))
    $release = Invoke-GitHub @('api', "repos/$Repository/releases/tags/$tag") | ConvertFrom-Json
}
if (!$release.draft -or $release.target_commitish -cne $Commit) { throw 'Draft ownership mismatch' }
$manifestDigest = (Get-FileHash "$directory/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = @($manifest.assets) + @([pscustomobject]@{ name = 'release-manifest.json'; sha256 = $manifestDigest })
foreach ($asset in $release.assets) {
    $match = @($expected | Where-Object { $_.name -ceq $asset.name -and "sha256:$($_.sha256)" -ceq $asset.digest })
    if ($match.Count -ne 1 -or $asset.state -ne 'uploaded') { throw 'Existing draft asset differs; refusing to overwrite' }
}
foreach ($asset in $expected) {
    if (@($release.assets | Where-Object name -CEQ $asset.name).Count) { continue }
    [void](Invoke-GitHub @('release', 'upload', $tag, (Join-Path $directory $asset.name), '--repo', $Repository))
}
$release = Get-RemoteRelease $release.id
if (!$release.draft -or $release.prerelease -or $release.target_commitish -cne $Commit) { throw 'Draft provenance changed while uploading' }
Test-UploadedAssets -Assets $release.assets -Manifest $manifest -ManifestDigest $manifestDigest
# Recheck the tag if GitHub created it while preparing the draft.
$refs = @(Invoke-GitHub @('api', "repos/$Repository/git/matching-refs/tags/$tag") | ConvertFrom-Json | Where-Object { $_.ref -ceq "refs/tags/$tag" })
if ($refs.Count) {
    $tagCommit = Invoke-GitHub @('api', "repos/$Repository/commits/$tag", '--jq', '.sha')
    if ($tagCommit -cne $Commit) { throw 'Tag changed while uploading' }
}
[void](Invoke-GitHub @('api', '--method', 'PATCH', "repos/$Repository/releases/$($release.id)", '-F', 'draft=false', '-F', 'prerelease=false'))
$published = Get-RemoteRelease $release.id
$tagCommit = Invoke-GitHub @('api', "repos/$Repository/commits/$tag", '--jq', '.sha')
if ($published.draft -or $published.prerelease -or !$published.published_at -or $published.target_commitish -cne $Commit -or $tagCommit -cne $Commit) { throw 'Published release verification failed' }
Test-UploadedAssets -Assets $published.assets -Manifest $manifest -ManifestDigest $manifestDigest
