param([ValidateSet('Prepare', 'Publish')][string]$Phase = 'Prepare')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-queue.ps1"
$root = $env:GITHUB_WORKSPACE
$record = Get-Content "$env:RUNNER_TEMP/release-plan.json" -Raw | ConvertFrom-Json -AsHashtable
$candidate = Join-Path $env:RUNNER_TEMP 'release-candidate'
if ($Phase -eq 'Prepare') {
    if (Test-Path $candidate) {
        # Restored bytes remain canonical even if a fresh build differs.
        Assert-QueueCandidate $root $record $candidate
    } else {
        Write-QueueCandidate $root $record $root $candidate $record.controller
    }
} else {
    Assert-QueueCandidate $root $record $candidate
    if ($record.preview) { throw 'Preview publication is forbidden' }
    # Revalidate the immutable ledger immediately before exporting any objects.
    $pending = Find-QueueHead $root $record.source $env:GITHUB_REPOSITORY
    if (!$pending -or $pending.commit -cne $record.commit) { throw 'Queue head changed; refusing publication' }
    Publish-QueueCommitObject $root $record $env:GITHUB_REPOSITORY
    & "$PSScriptRoot/publish-windows-release.ps1" -Repository $env:GITHUB_REPOSITORY -Commit $record.commit -Root $candidate -ExactCommit
}
