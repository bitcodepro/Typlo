param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Commit,
    [string]$Root = (Split-Path $PSScriptRoot),
    [switch]$ExactCommit
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
$version = Get-ReleaseVersion -Root $Root
$tag = "v$version"
$directory = Join-Path $Root 'dist'
$manifest = Read-ReleaseManifest -Directory $directory -Commit $Commit -Version $version
$pages = Invoke-GitHub @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
$releases = @($pages | ForEach-Object { foreach ($release in $_) { $release } })
$existing = @($releases | Where-Object { $_.tag_name -ceq $tag })
if ($existing.Count -gt 1) { throw 'Duplicate release tag' }
$refs = @(Invoke-GitHub @('api', "repos/$Repository/git/matching-refs/tags/$tag") | ConvertFrom-Json | Where-Object { $_.ref -ceq "refs/tags/$tag" })
$tagCommit = $null
if ($refs.Count) {
    $tagCommit = Invoke-GitHub @('api', "repos/$Repository/commits/$tag", '--jq', '.sha')
    if ($tagCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid tag commit' }
    if (!$existing.Count) { throw 'Tag already exists without a release; refusing to adopt it' }
}
if ($existing.Count) {
    $release = Get-RemoteRelease $existing[0].id
    if ($release.tag_name -cne $tag -or $release.prerelease) { throw 'Release identity/type mismatch' }
    if (!$release.draft) {
        if ($ExactCommit -and $tagCommit -cne $Commit) { throw 'Queued release belongs to another commit' }
        if ($refs.Count -ne 1 -or !$release.published_at -or $release.target_commitish -cne $tagCommit) { throw 'Published release provenance mismatch' }
        # A rebuild can have different ZIP timestamps. Validate the original assets instead.
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory $temporary)
        [void](Invoke-GitHub @('release', 'download', $tag, '--repo', $Repository, '--dir', $temporary))
        $original = Read-ReleaseManifest -Directory $temporary -Commit $tagCommit -Version $version -AllowLegacy
        $digest = (Get-FileHash "$temporary/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
        Test-UploadedAssets -Assets $release.assets -Manifest $original -ManifestDigest $digest
        if ($tagCommit -ceq $Commit) {
            Write-Host "Release $tag already published and verified for $Commit"
        } else {
            Write-Host "Cargo version $version is already published and verified for $tagCommit; no release for $Commit. Bump Cargo.toml/Cargo.lock for a new release."
        }
        return
    }
    if ($release.target_commitish -cne $Commit -or ($tagCommit -and $tagCommit -cne $Commit)) { throw 'Draft provenance mismatch; refusing to adopt another commit' }
} else {
    foreach ($release in $releases) {
        if (!$release.draft -and !$release.prerelease -and $release.tag_name -match '^v(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -ge [version]$version) {
            throw 'Cargo version must be newer than published releases'
        }
    }
    # Exact commit, never a moving branch. No tag-trigger workflow is needed.
    # Drafts without a published tag are not available through releases/tags.
    # Keep the ID returned by creation for all subsequent draft operations.
    $release = Invoke-GitHub @('api', '--method', 'POST', "repos/$Repository/releases", '-f', "tag_name=$tag", '-f', "target_commitish=$Commit", '-F', 'draft=true', '-F', 'prerelease=false', '-f', "name=Typlo $version", '-f', "body=Windows x64 release from commit $Commit.") | ConvertFrom-Json
}
if ($release.id -le 0 -or $release.tag_name -cne $tag -or !$release.draft -or $release.prerelease -or $release.target_commitish -cne $Commit) { throw 'Draft ownership mismatch' }
$manifestDigest = (Get-FileHash "$directory/release-manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = @($manifest.assets) + @([pscustomobject]@{ name = 'release-manifest.json'; sha256 = $manifestDigest })
foreach ($asset in $release.assets) {
    $match = @($expected | Where-Object { $_.name -ceq $asset.name -and "sha256:$($_.sha256)" -ceq $asset.digest })
    if ($match.Count -ne 1 -or $asset.state -ne 'uploaded') { throw 'Existing draft asset differs; refusing to overwrite' }
}
foreach ($asset in $expected) {
    if (@($release.assets | Where-Object name -CEQ $asset.name).Count) { continue }
    # Use the release ID directly; do not resolve an unpublished draft by tag.
    $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($asset.name))"
    [void](Invoke-GitHub @('api', '--method', 'POST', $upload, '--input', (Join-Path $directory $asset.name), '-H', 'Content-Type: application/octet-stream'))
}
$release = Get-RemoteRelease $release.id
if ($release.tag_name -cne $tag -or !$release.draft -or $release.prerelease -or $release.target_commitish -cne $Commit) { throw 'Draft provenance changed while uploading' }
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
