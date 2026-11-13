$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-common.ps1"
$script:QueueFloor = '1a46ca872083eb901916a2189d725ba8d8832d88'
$script:QueueBaseline = '1a46ca872083eb901916a2189d725ba8d8832d88'
function Invoke-QueueProcess {
    param([string]$File, [string[]]$Arguments, [byte[]]$InputBytes, [hashtable]$Environment = @{})
    $spec = [Diagnostics.ProcessStartInfo]::new($File)
    $spec.UseShellExecute = $false
    $spec.CreateNoWindow = $true
    $spec.RedirectStandardInput = $true
    $spec.RedirectStandardOutput = $true
    $spec.RedirectStandardError = $true
    foreach ($a in $Arguments) { $spec.ArgumentList.Add($a) }
    foreach ($key in $Environment.Keys) { $spec.Environment[$key] = $Environment[$key] }
    $p = [Diagnostics.Process]::Start($spec)
    $buffer = [IO.MemoryStream]::new()
    try {
        $copy = $p.StandardOutput.BaseStream.CopyToAsync($buffer)
        $errorRead = $p.StandardError.ReadToEndAsync()
        if ($InputBytes) { $p.StandardInput.BaseStream.Write($InputBytes) }
        $p.StandardInput.Close()
        if (!$p.WaitForExit(60000)) { $p.Kill($true); throw "$File timed out" }
        $copy.GetAwaiter().GetResult()
        $detail = $errorRead.GetAwaiter().GetResult()
        if ($p.ExitCode -ne 0) { throw "$File failed ($($p.ExitCode)): $detail" }
        $bytes = $buffer.ToArray()
        return [pscustomobject]@{ Bytes = $bytes; Text = [Text.Encoding]::UTF8.GetString($bytes) }
    } finally { $buffer.Dispose(); $p.Dispose() }
}
function Queue-Git {
    param([string]$Root, [string[]]$Arguments, [byte[]]$InputBytes, [hashtable]$Environment = @{})
    Invoke-QueueProcess git (@('-C', $Root) + $Arguments) $InputBytes $Environment
}
function Queue-Api {
    param([string]$Endpoint, $Body)
    $args = @('api', $Endpoint)
    if ($null -ne $Body) {
        $file = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 30 -Compress))
        $args += @('--method', 'POST', '--input', $file)
    }
    $json = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "GitHub API failed: $Endpoint" }
    return ($json | ConvertFrom-Json)
}
function Get-QueueSources {
    param([string]$Root, [string]$Tip, [string]$Floor = $script:QueueFloor)
    $chain = @((Queue-Git $Root @('rev-list', '--first-parent', $Tip)).Text.Trim() -split '\r?\n')
    if ($Floor -notin $chain) { throw 'Queue anchor is not on the first-parent chain; refusing rewritten history' }
    @((Queue-Git $Root @('rev-list', '--first-parent', '--reverse', "$Floor..$Tip")).Text.Trim() -split '\r?\n' | Where-Object { $_ })
}
function New-QueueCommit {
    param([string]$Root, [ValidatePattern('^[a-f0-9]{40}$')][string]$Source, [int]$Sequence, [bool]$Preview = $false)
    if ($Sequence -lt 1 -or $Sequence -gt 999997) { throw 'Queue sequence outside updater numeric-version limits' }
    $version = "0.1.$($Sequence + 2)"
    $inputFiles = @{}
    foreach ($name in @('Cargo.toml', 'Cargo.lock')) {
        $mode = (Queue-Git $Root @('ls-tree', $Source, '--', $name)).Text
        if (!$mode.StartsWith('100644 blob ')) { throw "Invalid source mode: $name" }
        $inputFiles[$name] = (Queue-Git $Root @('cat-file', 'blob', "${Source}:$name")).Text
    }
    $match = [regex]::Match($inputFiles['Cargo.toml'], '(?ms)^\[package\].*?^version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
    if (!$match.Success) { throw 'Source package version missing' }
    $old = $match.Groups[1].Value
    $lockPattern = '(?m)(^name = "typlo-rust"\r?\nversion = ")' + [regex]::Escape($old) + '("$)'
    if ([regex]::Matches($inputFiles['Cargo.lock'], $lockPattern).Count -ne 1) { throw 'Source Cargo.lock application version mismatch' }
    $inputFiles['Cargo.toml'] = [regex]::new('(?ms)(^\[package\].*?^version\s*=\s*")[^"]+(")').Replace($inputFiles['Cargo.toml'], { param($m) $m.Groups[1].Value + $version + $m.Groups[2].Value }, 1)
    $inputFiles['Cargo.lock'] = [regex]::new($lockPattern).Replace($inputFiles['Cargo.lock'], { param($m) $m.Groups[1].Value + $version + $m.Groups[2].Value }, 1)
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('TyploVersionIndex-' + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory $temp)
    $envs = @{ GIT_INDEX_FILE = (Join-Path $temp 'index') }
    [void](Queue-Git $Root @('read-tree', $Source) -Environment $envs)
    $blobs = @{}
    foreach ($name in @('Cargo.toml', 'Cargo.lock')) {
        $blob = (Queue-Git $Root @('hash-object', '-w', '--stdin') ([Text.Encoding]::UTF8.GetBytes($inputFiles[$name]))).Text.Trim()
        $blobs[$name] = $blob
        [void](Queue-Git $Root @('update-index', '--cacheinfo', "100644,$blob,$name") -Environment $envs)
    }
    $tree = (Queue-Git $Root @('write-tree') -Environment $envs).Text.Trim()
    $epoch = [long](Queue-Git $Root @('show', '-s', '--format=%ct', $Source)).Text.Trim()
    $date = [DateTimeOffset]::FromUnixTimeSeconds($epoch).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $message = "Typlo automated release $version`n`nSource: $Source`n"
    $identity = @{ GIT_AUTHOR_NAME = 'Typlo Release'; GIT_COMMITTER_NAME = 'Typlo Release'; GIT_AUTHOR_EMAIL = 'release@typlo.invalid'; GIT_COMMITTER_EMAIL = 'release@typlo.invalid'; GIT_AUTHOR_DATE = "$epoch +0000"; GIT_COMMITTER_DATE = "$epoch +0000" }
    $commit = (Queue-Git $Root @('-c', 'commit.gpgsign=false', 'commit-tree', $tree, '-p', $Source) ([Text.Encoding]::UTF8.GetBytes($message)) $identity).Text.Trim()
    [pscustomobject]@{ source = $Source; sequence = $Sequence; version = $version; commit = $commit; tree = $tree; date = $date; message = $message; blobs = $blobs; preview = $Preview }
}
function Get-QueueReleases {
    param([string]$Repository)
    $json = gh api --paginate --slurp "repos/$Repository/releases?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read release ledger' }
    @($json | ConvertFrom-Json | ForEach-Object { foreach ($item in $_) { $item } })
}
function Get-QueueTag {
    param([string]$Repository, [string]$Tag)
    $refs = @(Queue-Api "repos/$Repository/git/matching-refs/tags/$Tag" | Where-Object { $_.ref -ceq "refs/tags/$Tag" })
    if ($refs.Count -gt 1) { throw 'Duplicate tag ref' }
    if (!$refs.Count) { return $null }
    $object = $refs[0].object
    for ($n = 0; $n -lt 8; $n++) {
        if ($object.type -eq 'commit') { return $object.sha }
        if ($object.type -ne 'tag') { throw 'Release tag does not target a commit' }
        $object = (Queue-Api "repos/$Repository/git/tags/$($object.sha)").object
    }
    throw 'Too many annotated tag levels'
}
function Assert-QueueInputs {
    param($Manifest, $Record, [string]$Root)
    Test-ReleaseDescriptor $Manifest $Record.commit $Record.version
    $i = $Manifest.build_inputs
    if (!$i -or $i.anchor -cne $script:QueueFloor -or $i.source_commit -cne $Record.source -or $i.sequence -ne $Record.sequence -or $i.preview -isnot [bool] -or $i.preview -ne $Record.preview -or $i.cargo_toml_blob -cne $Record.blobs['Cargo.toml'] -or $i.cargo_lock_blob -cne $Record.blobs['Cargo.lock'] -or $i.controller_commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Queue build-input provenance mismatch' }
    if (!$Record.preview) { [void](Queue-Git $Root @('merge-base', '--is-ancestor', $i.controller_commit, 'origin/main')) }
}
function Assert-QueuePublished {
    param([string]$Repository, $Release, [string]$Commit, [string]$Version, $Record, [string]$Root)
    if (!$Release -or $Release.draft -or $Release.prerelease -or !$Release.published_at -or $Release.tag_name -cne "v$Version" -or $Release.target_commitish -cne $Commit -or (Get-QueueTag $Repository "v$Version") -cne $Commit) { throw 'Published queue/tag provenance conflict' }
    $meta = @($Release.assets | Where-Object name -CEQ 'release-manifest.json')
    $url = "https://github.com/$Repository/releases/download/v$Version/release-manifest.json"
    if ($meta.Count -ne 1 -or $meta[0].browser_download_url -cne $url) { throw 'Invalid manifest URL' }
    $path = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.json')
    Invoke-WebRequest $url -OutFile $path -TimeoutSec 60
    if ((Get-Item $path).Length -gt 65536) { throw 'Oversized release manifest' }
    $manifest = Get-Content $path -Raw | ConvertFrom-Json
    Test-ReleaseDescriptor $manifest $Commit $Version
    if ($Record) { Assert-QueueInputs $manifest $Record $Root }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Test-UploadedAssets $Release.assets $manifest $hash
}
function Find-QueueHead {
    param([string]$Root, [string]$Tip, [string]$Repository)
    $all = @(Get-QueueReleases $Repository)
    $base = @($all | Where-Object tag_name -CEQ 'v0.1.2')
    if ($base.Count -ne 1) { throw 'Missing/duplicate immutable baseline v0.1.2' }
    Assert-QueuePublished $Repository $base[0] $script:QueueBaseline '0.1.2'
    if ((Queue-Git $Root @('rev-parse', "$($script:QueueFloor)")).Text.Trim() -cne $script:QueueBaseline) { throw 'Invalid activation floor' }
    $sources = @(Get-QueueSources $Root $Tip)
    for ($n = 0; $n -lt $sources.Count; $n++) {
        $r = New-QueueCommit $Root $sources[$n] ($n + 1)
        $found = @($all | Where-Object tag_name -CEQ "v$($r.version)")
        if ($found.Count -gt 1) { throw 'Duplicate queued release' }
        if ($found.Count -and !$found[0].draft) {
            Assert-QueuePublished $Repository $found[0] $r.commit $r.version $r $Root
            continue
        }
        $tag = Get-QueueTag $Repository "v$($r.version)"
        if ($tag -and (!$found.Count -or $tag -cne $r.commit)) { throw 'Conflicting tag without an owned draft' }
        if ($found.Count -and ($found[0].target_commitish -cne $r.commit -or $found[0].prerelease)) { throw 'Foreign queued draft' }
        $r | Add-Member -NotePropertyName owned_draft -NotePropertyValue ([bool]$found.Count)
        return $r
    }
    return $null
}
function Write-QueueCandidate {
    param([string]$Root, $Record, [string]$SourceRoot, [string]$Destination, [string]$Controller)
    if (Test-Path $Destination) { throw 'Candidate directory must be absent' }
    [void](New-Item -ItemType Directory $Destination)
    Copy-Item (Join-Path $SourceRoot 'dist') $Destination -Recurse
    foreach ($name in @('Cargo.toml', 'Cargo.lock')) { [IO.File]::WriteAllBytes((Join-Path $Destination $name), (Queue-Git $Root @('cat-file', 'blob', "$($Record.commit):$name")).Bytes) }
    $path = Join-Path $Destination 'dist/release-manifest.json'
    $manifest = Get-Content $path -Raw | ConvertFrom-Json
    $inputs = @{ anchor = $script:QueueFloor; source_commit = $Record.source; sequence = $Record.sequence; preview = $Record.preview; controller_commit = $Controller; cargo_toml_blob = $Record.blobs['Cargo.toml']; cargo_lock_blob = $Record.blobs['Cargo.lock'] }
    $manifest | Add-Member -NotePropertyName build_inputs -NotePropertyValue $inputs
    $manifest | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8
    Assert-QueueCandidate $Root $Record $Destination
}
function Assert-QueueCandidate {
    param([string]$Root, $Record, [string]$Directory)
    if ((Get-ReleaseVersion $Directory) -cne $Record.version) { throw 'Candidate Cargo version mismatch' }
    foreach ($name in @('Cargo.toml', 'Cargo.lock')) {
        $hash = (Queue-Git $Root @('hash-object', '--stdin') ([IO.File]::ReadAllBytes((Join-Path $Directory $name)))).Text.Trim()
        if ($hash -cne $Record.blobs[$name]) { throw 'Candidate Cargo bytes mismatch' }
    }
    $manifest = Read-ReleaseManifest (Join-Path $Directory 'dist') $Record.commit $Record.version
    Assert-QueueInputs $manifest $Record $Root
}
function Publish-QueueCommitObject {
    param([string]$Root, $Record, [string]$Repository)
    if ($Record.preview) { throw 'Cannot export preview objects' }
    $entries = @()
    foreach ($name in @('Cargo.toml', 'Cargo.lock')) {
        $bytes = (Queue-Git $Root @('cat-file', 'blob', "$($Record.commit):$name")).Bytes
        $blob = Queue-Api "repos/$Repository/git/blobs" @{ content = [Convert]::ToBase64String($bytes); encoding = 'base64' }
        if ($blob.sha -cne $Record.blobs[$name]) { throw 'Server blob SHA mismatch' }
        $entries += @{ path = $name; mode = '100644'; type = 'blob'; sha = $blob.sha }
    }
    $baseTree = (Queue-Git $Root @('rev-parse', "$($Record.source)^{tree}")).Text.Trim()
    $tree = Queue-Api "repos/$Repository/git/trees" @{ base_tree = $baseTree; tree = $entries }
    if ($tree.sha -cne $Record.tree) { throw 'Server tree SHA mismatch' }
    $who = @{ name = 'Typlo Release'; email = 'release@typlo.invalid'; date = $Record.date }
    $commit = Queue-Api "repos/$Repository/git/commits" @{ message = $Record.message; tree = $Record.tree; parents = @($Record.source); author = $who; committer = $who }
    if ($commit.sha -cne $Record.commit) { throw 'Server commit SHA mismatch; no tag/release may be created' }
}
