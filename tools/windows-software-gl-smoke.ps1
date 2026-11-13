param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
# Use a deterministic software OpenGL context on hosted Windows. Test the identical release
# executables with a pinned software driver in a disposable copy of the payload.
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('TyploSoftwareGL-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $temporary 'payload'
[void](New-Item -ItemType Directory $fixture -Force)
Copy-Item "$PayloadDirectory/*" $fixture -Recurse
$archive = Join-Path $temporary 'mesa.7z'
Invoke-WebRequest 'https://github.com/pal1000/mesa-dist-win/releases/download/26.2.5/mesa3d-26.2.5-release-mingw.7z' -OutFile $archive -TimeoutSec 180
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne '2471864068672b82032d8f8cccfb8da8e196b1b06b316b99838b47c35d738a17') { throw 'Mesa SHA256 mismatch' }
$sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
& $sevenZip x $archive "-o$temporary/mesa" 'x64/opengl32.dll' 'x64/libgallium_wgl.dll' -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Mesa extraction failed' }
$dlls = @(Get-ChildItem "$temporary/mesa/x64/*.dll")
if ($dlls.Count -ne 2 -or !($dlls | Where-Object Name -EQ 'opengl32.dll') -or !($dlls | Where-Object Name -EQ 'libgallium_wgl.dll')) { throw 'Mesa OpenGL driver missing' }
Copy-Item $dlls.FullName $fixture
$oldDriver = $env:GALLIUM_DRIVER
$oldSoftware = $env:LIBGL_ALWAYS_SOFTWARE
try {
    $env:GALLIUM_DRIVER = 'llvmpipe'
    $env:LIBGL_ALWAYS_SOFTWARE = 'true'
    & "$PSScriptRoot/windows-ui-smoke.ps1" -Resident "$fixture/typlo-rust.exe" -Settings "$fixture/typlo-settings.exe"
} finally {
    $env:GALLIUM_DRIVER = $oldDriver
    $env:LIBGL_ALWAYS_SOFTWARE = $oldSoftware
}
