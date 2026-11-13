param([switch]$Preview)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-queue.ps1"
$root = Split-Path $PSScriptRoot
$controller = (Queue-Git $root @('rev-parse', 'HEAD')).Text.Trim()
if ($Preview) {
    $sequence = @(Get-QueueSources $root $controller).Count
    if (!$sequence) { throw 'Preview must follow the activation floor' }
    $record = New-QueueCommit $root $controller $sequence $true
} else {
    # The event SHA bounds this run. A later wake-up drains any remaining history.
    $record = Find-QueueHead $root $env:GITHUB_SHA $env:GITHUB_REPOSITORY
}
if (!$record) {
    'pending=false' >> $env:GITHUB_OUTPUT
    Write-Host 'All releases through this event SHA are verified; no writes.'
    exit 0
}
$record | Add-Member -NotePropertyName controller -NotePropertyValue $controller
$record | ConvertTo-Json -Depth 10 | Set-Content "$env:RUNNER_TEMP/release-plan.json" -Encoding utf8
Copy-Item $PSScriptRoot "$env:RUNNER_TEMP/release-controller" -Recurse
Copy-Item "$root/.github/workflows/windows.yml" "$env:RUNNER_TEMP/release-controller/windows-workflow.yml"
Copy-Item "$root/.github/actions/windows-gates" "$env:RUNNER_TEMP/release-action" -Recurse
[void](Queue-Git $root @('checkout', '--detach', $record.commit))
# A queued source can predate this infrastructure (another PR merged while it
# was under review). Keep the reviewed controller recipe outside source history.
if (Test-Path "$root/.release-ci-action") { throw 'Source collides with reserved controller action directory' }
Copy-Item "$env:RUNNER_TEMP/release-action" "$root/.release-ci-action" -Recurse
"pending=true" >> $env:GITHUB_OUTPUT
"commit=$($record.commit)" >> $env:GITHUB_OUTPUT
"version=$($record.version)" >> $env:GITHUB_OUTPUT
"owned_draft=$([string]$record.owned_draft -eq 'True')".ToLowerInvariant() >> $env:GITHUB_OUTPUT
"RELEASE_COMMIT=$($record.commit)" >> $env:GITHUB_ENV
Write-Host "Source $($record.source) -> $($record.version), tested commit $($record.commit)"
