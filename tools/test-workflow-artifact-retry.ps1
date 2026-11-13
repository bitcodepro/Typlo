param([string]$WorkflowPath = "$PSScriptRoot/../.github/workflows/windows.yml")
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -LiteralPath $WorkflowPath -Raw
# Read the actual action inputs: the immutable artifact store rejects duplicate
# names within a run, even when their bytes are identical. Do not overwrite the
# original backup when retrying publication after an upload succeeded.
$uploads = [regex]::Matches($workflow, '(?ms)^      - uses: actions/upload-artifact@[^\r\n]+\r?\n(?<inputs>.*?)(?=^      - |^  [a-z]|\z)')
if ($uploads.Count -ne 2) { throw 'Expected preview and publication artifact uploads' }
foreach ($upload in $uploads) {
    $inputs = $upload.Groups['inputs'].Value
    if ($inputs -match '(?m)^\s+overwrite:') { throw 'Retry must preserve original artifacts without overwrite' }
    $template = [regex]::Match($inputs, '(?m)^          name: (.+)\r?$').Groups[1].Value.Trim()
    if (!$template.Contains('${{ github.run_attempt }}')) { throw 'Artifact name must distinguish attempts of the same run' }
    $store = @{}
    $bytes = 'canonical candidate bytes restored from the source-commit cache'
    function Upload-Attempt([int]$Attempt) {
        $name = $template.Replace('${{ github.sha }}', ('a' * 40)).Replace('${{ steps.plan.outputs.commit }}', ('b' * 40)).Replace('${{ github.run_attempt }}', [string]$Attempt)
        if ($name.Contains('${{')) { throw 'Unmodeled artifact expression' }
        if ($store.ContainsKey($name)) { throw '409 Conflict: immutable artifact already exists in this workflow run' }
        $store.Add($name, $bytes)
        return $name
    }
    $first = Upload-Attempt 1
    # Publication failed after the successful upload/cache save. Re-run failed
    # jobs uses the same run/source and restored canonical bytes, but attempt 2.
    $second = Upload-Attempt 2
    if ($first -ceq $second -or $store.Count -ne 2 -or $store[$first] -cne $bytes -or $store[$second] -cne $bytes) { throw 'Retry replaced or lost the original backup' }
    $rejected = $false
    try { Upload-Attempt 1 | Out-Null } catch { $rejected = $true }
    if (!$rejected -or $store.Count -ne 2) { throw 'Artifact-store mock did not enforce the duplicate-name conflict' }
}
Write-Host 'PASS actual workflow artifact inputs: post-upload failure and same-run retry preserve both immutable attempt backups'
