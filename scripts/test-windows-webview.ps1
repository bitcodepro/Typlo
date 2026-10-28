param(
    [string]$ResultsDirectory = 'TestResults/webview2',
    [switch]$InstallRuntimeIfMissing
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path -Parent $PSScriptRoot
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
Push-Location $projectRoot
try {
    dotnet build tests/Typlo.WindowsIntegration -c Release -m:1 -p:UseSharedCompilation=false --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 test build failed' }
    $executable = Join-Path $projectRoot 'tests/Typlo.WindowsIntegration/bin/Release/net10.0-windows/Typlo.WindowsIntegration.exe'
    & $executable --check-runtime
    if ($LASTEXITCODE -ne 0) {
        if (!$InstallRuntimeIfMissing) { throw 'Install the Microsoft Edge WebView2 Runtime before running this test.' }
        # CI opt-in only. The application itself does not acquire a WebView2 dependency.
        $installer = Join-Path $results 'MicrosoftEdgeWebview2Setup.exe'
        Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $installer
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw 'WebView2 installer is not signed by Microsoft.'
        }
        $setup = Start-Process -FilePath $installer -ArgumentList '/silent','/install' -WindowStyle Hidden -PassThru
        if (!$setup.WaitForExit(60000)) { $setup.Kill(); throw 'WebView2 Runtime setup timed out' }
        $available = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            & $executable --check-runtime
            if ($LASTEXITCODE -eq 0) { $available = $true; break }
            Start-Sleep -Seconds 2
        }
        if (!$available) { throw 'WebView2 Runtime did not become available' }
    }
    $process = Start-Process -FilePath $executable -ArgumentList ('"' + $results + '"') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $results 'stdout.txt') -RedirectStandardError (Join-Path $results 'stderr.txt')
    if (!$process.WaitForExit(45000)) { $process.Kill(); throw 'WebView2 editor checks timed out' }
    Get-Content -LiteralPath (Join-Path $results 'webview2.txt')
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $results 'stderr.txt')
        throw 'WebView2 editor checks failed'
    }
    $runtimeApp = Join-Path $results 'runtime-app'
    dotnet publish src/Typlo.App -c Release -r win-x64 --self-contained true -o $runtimeApp -m:1 -p:UseSharedCompilation=false --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Background Native AOT build failed' }
    $runtimeResults = Join-Path $results 'runtime'
    $runtimeExe = Join-Path $runtimeApp 'Typlo.exe'
    $arguments = '--background-runtime-check "' + $runtimeExe + '" "' + $runtimeResults + '"'
    $runtimeProcess = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$runtimeProcess.WaitForExit(120000)) { $runtimeProcess.Kill(); throw 'Background runtime checks timed out' }
    Get-Content -LiteralPath (Join-Path $runtimeResults 'runtime.txt')
    if ($runtimeProcess.ExitCode -ne 0) { throw 'Background runtime checks failed' }
    $selftestReport = Join-Path $runtimeResults 'desktop-selftest.txt'
    $selftest = Start-Process -FilePath $runtimeExe -ArgumentList ('--selftest "' + $selftestReport + '"') -WindowStyle Hidden -PassThru
    if (!$selftest.WaitForExit(90000)) { $selftest.Kill(); throw 'Native AOT desktop checks timed out' }
    Get-Content -LiteralPath $selftestReport
    if ($selftest.ExitCode -ne 0) { throw 'Native AOT desktop checks failed' }

} finally { Pop-Location }
