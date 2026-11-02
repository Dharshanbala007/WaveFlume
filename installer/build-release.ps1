# Builds a WaveFlume release: tests, self-contained single-file publish, plctag.dll, and (if Inno Setup is
# installed) the installer. Run from the repository root:  powershell -File installer\build-release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'

dotnet test (Join-Path $root 'WaveFlume.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release built.' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\WaveFlume.App') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# libplctag embeds its native library and extracts it next to the exe; do it now so the installer ships it.
Start-Process -FilePath (Join-Path $publish 'WaveFlume.exe') -ArgumentList '--extract-native' -Wait
if (-not (Test-Path (Join-Path $publish 'plctag.dll'))) { throw 'plctag.dll was not extracted.' }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc (Join-Path $PSScriptRoot 'WaveFlume.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
    Write-Host "Installer: $(Join-Path $root 'artifacts\installer')"
} else {
    Write-Host "Inno Setup 6 not found; published files are in $publish. Install Inno Setup and rerun to build the installer."
}
