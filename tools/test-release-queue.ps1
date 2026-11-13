$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-queue.ps1"
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (!$failed) { throw 'Expected fail-closed result' }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TyploQueueTests-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $fixture)
[void](Queue-Git $fixture @('init', '-b', 'main'))
[IO.File]::WriteAllText("$fixture/Cargo.toml", "[package]`nname = `"typlo-rust`"`nversion = `"0.1.2`"`n")
[IO.File]::WriteAllText("$fixture/Cargo.lock", "[[package]]`nname = `"typlo-rust`"`nversion = `"0.1.2`"`n")
[void](Queue-Git $fixture @('add', '.'))
[void](Queue-Git $fixture @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-m', 'anchor'))
$anchor = (Queue-Git $fixture @('rev-parse', 'HEAD')).Text.Trim()
$tree = (Queue-Git $fixture @('rev-parse', 'HEAD^{tree}')).Text.Trim()
function Fixture-Commit([string[]]$Parents, [string]$Message) {
    $args = @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', 'commit-tree', $tree)
    foreach ($p in $Parents) { $args += @('-p', $p) }
    (Queue-Git $fixture $args ([Text.Encoding]::UTF8.GetBytes("$Message`n"))).Text.Trim()
}
# Two PRs fork the same main; merge order, not branch creation time, defines ordinals.
$a = Fixture-Commit @($anchor) 'PR A'
$b = Fixture-Commit @($anchor) 'PR B'
$mergeB = Fixture-Commit @($anchor, $b) 'merge B'
$mergeA = Fixture-Commit @($mergeB, $a) 'merge A'
$sources = @(Get-QueueSources $fixture $mergeA $anchor)
if ($sources.Count -ne 2 -or $sources[0] -cne $mergeB -or $sources[1] -cne $mergeA) { throw 'Concurrent PR merge order lost' }
Expect-Failure { Get-QueueSources $fixture $b $mergeB }
$r1 = New-QueueCommit $fixture $mergeB 1
$retry = New-QueueCommit $fixture $mergeB 1
$r2 = New-QueueCommit $fixture $mergeA 2
if ($r1.version -cne '0.1.3' -or $r2.version -cne '0.1.4' -or $r1.commit -cne $retry.commit) { throw 'Unstable version/SHA on retry' }
$changes = (Queue-Git $fixture @('diff-tree', '--no-commit-id', '--name-only', '-r', $r1.commit)).Text.Trim() -split '\r?\n'
if (($changes -join ',') -cne 'Cargo.lock,Cargo.toml') { throw 'Release commit changed application logic' }
foreach ($path in @('Cargo.toml', 'Cargo.lock')) {
    if ((Queue-Git $fixture @('show', "$($r1.commit):$path")).Text -notmatch 'version = "0.1.3"') { throw 'Cargo versions differ' }
}
Expect-Failure { New-QueueCommit $fixture $mergeB 0 }
Expect-Failure { New-QueueCommit $fixture $mergeB 999998 }
Write-Host 'PASS real Git concurrent branches, first-parent order, deterministic retries, exact two-file version commit and history rewrite rejection'

# Candidate cache/artifact validation binds ZIP bytes and both Cargo blobs to source.
$script:QueueFloor = $anchor
[void](Queue-Git $fixture @('update-ref', 'refs/remotes/origin/main', $mergeA))
[void](New-Item -ItemType Directory "$fixture/dist")
[IO.File]::WriteAllText("$fixture/dist/typlo-win-x64.zip", 'mock ZIP bytes; no installation')
$digest = (Get-FileHash "$fixture/dist/typlo-win-x64.zip").Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$fixture/dist/typlo-win-x64.zip.sha256", "$digest  typlo-win-x64.zip`n")
$assets = @('typlo-win-x64.zip', 'typlo-win-x64.zip.sha256') | ForEach-Object { @{ name = $_; sha256 = (Get-FileHash "$fixture/dist/$_").Hash.ToLowerInvariant() } }
@{ schema_version = 1; platform = 'windows-x64'; version = $r1.version; commit = $r1.commit; assets = $assets } | ConvertTo-Json -Depth 10 | Set-Content "$fixture/dist/release-manifest.json"
$candidate = "$fixture/candidate"
Write-QueueCandidate $fixture $r1 $fixture $candidate $mergeA
Assert-QueueCandidate $fixture $r1 $candidate
$path = "$candidate/dist/release-manifest.json"
$original = Get-Content $path -Raw
$m = $original | ConvertFrom-Json
$m.build_inputs.source_commit = 'f' * 40
$m | ConvertTo-Json -Depth 10 | Set-Content $path
Expect-Failure { Assert-QueueCandidate $fixture $r1 $candidate }
[IO.File]::WriteAllText($path, $original)
Add-Content "$candidate/Cargo.toml" '# tamper'
Expect-Failure { Assert-QueueCandidate $fixture $r1 $candidate }
[IO.File]::WriteAllBytes("$candidate/Cargo.toml", (Queue-Git $fixture @('cat-file', 'blob', "$($r1.commit):Cargo.toml")).Bytes)
Add-Content "$candidate/dist/typlo-win-x64.zip" 'tamper'
Expect-Failure { Assert-QueueCandidate $fixture $r1 $candidate }
Write-Host 'PASS canonical candidate validation, Cargo byte/hash binding, source provenance and ZIP tamper rejection'

# Export contract reproduces local Git object SHAs. Nothing contacts GitHub here.
$global:QueueObjectWrites = 0
function Queue-Api($Endpoint, $Body) {
    $global:QueueObjectWrites++
    if ($Endpoint.EndsWith('/blobs')) {
        $bytes = [Convert]::FromBase64String($Body.content)
        return @{ sha = (Queue-Git $fixture @('hash-object', '--stdin') $bytes).Text.Trim() }
    }
    if ($Endpoint.EndsWith('/trees')) { return @{ sha = $r1.tree } }
    if ($Endpoint.EndsWith('/commits')) {
        $identity = @{ GIT_AUTHOR_NAME = $Body.author.name; GIT_AUTHOR_EMAIL = $Body.author.email; GIT_COMMITTER_NAME = $Body.committer.name; GIT_COMMITTER_EMAIL = $Body.committer.email; GIT_AUTHOR_DATE = $Body.author.date; GIT_COMMITTER_DATE = $Body.committer.date }
        return @{ sha = (Queue-Git $fixture @('-c', 'commit.gpgsign=false', 'commit-tree', $Body.tree, '-p', $Body.parents[0]) ([Text.Encoding]::UTF8.GetBytes($Body.message)) $identity).Text.Trim() }
    }
    throw 'Unexpected endpoint'
}
Publish-QueueCommitObject $fixture $r1 example/repo
if ($global:QueueObjectWrites -ne 4) { throw 'Unexpected export mutation' }
$r1.preview = $true
Expect-Failure { Publish-QueueCommitObject $fixture $r1 example/repo }
if ($global:QueueObjectWrites -ne 4) { throw 'Preview wrote objects' }
$r1.preview = $false
function Queue-Api { return @{ sha = '0' * 40 } }
Expect-Failure { Publish-QueueCommitObject $fixture $r1 example/repo }
Write-Host 'PASS Git API object contract, preview isolation and server-SHA mismatch rejection before tags/releases'

# Simulate 130 merges, 129 lost/overflowed Actions wake-ups, reversed wake-up order,
# and a scheduled worker. The ledger, not the Actions pending queue, retains work.
$global:QueueLedger = @([pscustomobject]@{ tag_name = 'v0.1.2'; draft = $false })
$script:QueueFloor = $script:QueueBaseline
$global:QueueTips = 130
$global:QueueConflictTag = $null
$global:QueueValidated = @()
function Get-QueueSources { 1..$global:QueueTips | ForEach-Object { $_.ToString('x40') } }
function Get-QueueReleases { $global:QueueLedger }
function Queue-Git { [pscustomobject]@{ Text = $script:QueueBaseline } }
function New-QueueCommit($Root, $Source, $Sequence) { [pscustomobject]@{ source = $Source; sequence = $Sequence; version = "0.1.$($Sequence + 2)"; commit = ($Sequence + 1000).ToString('x40') } }
function Get-QueueTag { $global:QueueConflictTag }
function Assert-QueuePublished($Repository, $Release, $Commit, $Version, $Record) {
    if ($Version -ne '0.1.2' -and $Release.target_commitish -cne $Record.commit) { throw 'Original provenance mismatch' }
    $global:QueueValidated += $Version
}
$head = Find-QueueHead $fixture ignored example/repo
if ($head.sequence -ne 1) { throw 'Newest wake-up skipped old merge' }
for ($i = 1; $i -le 130; $i++) {
    $head = Find-QueueHead $fixture ignored example/repo
    if ($head.sequence -ne $i) { throw 'Lost/reordered merge' }
    $global:QueueLedger += [pscustomobject]@{ tag_name = "v$($head.version)"; draft = $false; target_commitish = $head.commit }
}
if ($null -ne (Find-QueueHead $fixture ignored example/repo)) { throw 'Complete queue created another version' }
$global:QueueTips = 1
if ($null -ne (Find-QueueHead $fixture ignored example/repo)) { throw 'Older event rerun created a release' }
$global:QueueTips = 131
$head = Find-QueueHead $fixture ignored example/repo
if ($head.sequence -ne 131) { throw 'Scheduled drain lost overflowed merge' }
$before = $global:QueueLedger | ConvertTo-Json -Depth 10 -Compress
$global:QueueConflictTag = 'f' * 40
Expect-Failure { Find-QueueHead $fixture ignored example/repo }
$global:QueueConflictTag = $null
$global:QueueLedger += [pscustomobject]@{ tag_name = 'v0.1.133'; draft = $true; target_commitish = 'e' * 40; prerelease = $false }
Expect-Failure { Find-QueueHead $fixture ignored example/repo }
$global:QueueLedger = @($global:QueueLedger | Where-Object tag_name -NE 'v0.1.133')
if (($global:QueueLedger | ConvertTo-Json -Depth 10 -Compress) -cne $before) { throw 'Historical ledger mutated' }
$global:QueueLedger = @()
Expect-Failure { Find-QueueHead $fixture ignored example/repo }
Write-Host 'PASS 130 merges, dropped/reordered wake-ups, old-event rerun, periodic recovery, baseline guard, foreign draft/tag rejection and immutable old ledger'
