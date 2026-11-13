param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Updater version must be X.Y.Z'
}
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$relative = [IO.Path]::GetRelativePath($payload, $output)
if (![IO.Path]::IsPathRooted($relative) -and $relative -notmatch '^\.\.([\\/]|$)') {
    throw 'Put the archives outside the payload directory'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$resident = Join-Path $payload 'typlo-rust.exe'
$update = Join-Path $output 'typlo-rust-win-x64.zip'
$portable = Join-Path $output ((Split-Path -Leaf $payload) + '-Portable-win-x64.zip')
foreach ($file in @($update, $portable)) {
    if (Test-Path -LiteralPath $file) { throw "Already exists: $file" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$candidate = Join-Path $output ('.update-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $archive = [IO.Compression.ZipFile]::Open($candidate, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $payload -Recurse -File)) {
            $name = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
            if ($name -eq 'docs/spelling-preview-18.md') { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
    $digest = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    $spec = [Diagnostics.ProcessStartInfo]::new($resident)
    $spec.UseShellExecute = $false
    $spec.CreateNoWindow = $true
    $spec.RedirectStandardError = $true
    foreach ($argument in @('--verify-update-package', $candidate, $Version, $digest)) {
        $spec.ArgumentList.Add($argument)
    }
    $verification = [Diagnostics.Process]::Start($spec)
    try {
        $errorRead = $verification.StandardError.ReadToEndAsync()
        if (!$verification.WaitForExit(60000)) {
            $verification.Kill($true)
            throw 'Update package verification timed out'
        }
        $errorOutput = $errorRead.GetAwaiter().GetResult()
        if ($verification.ExitCode -ne 0) { throw "Update package rejected. $errorOutput" }
    } finally {
        $verification.Dispose()
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $portable, [IO.Compression.CompressionLevel]::Optimal, $true)
    [IO.File]::Move($candidate, $update)
    [IO.File]::WriteAllText("$update.sha256", "$digest  typlo-rust-win-x64.zip" + [Environment]::NewLine)
    Get-Item -LiteralPath $portable,$update
} finally {
    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate }
}
