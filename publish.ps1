param(
    [string]$MpvExe = 'C:\Users\R\Tools\mpv\player\mpv.exe',
    [string]$Output = (Join-Path $PSScriptRoot 'artifacts\win-x64')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $MpvExe -PathType Leaf)) {
    throw "mpv.exe not found: $MpvExe"
}

dotnet publish (Join-Path $PSScriptRoot 'FrameDock\FrameDock.csproj') -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath $MpvExe -Destination (Join-Path $Output 'mpv.exe') -Force
$d3d = Join-Path (Split-Path -Parent $MpvExe) 'd3dcompiler_43.dll'
if (Test-Path -LiteralPath $d3d -PathType Leaf) {
    Copy-Item -LiteralPath $d3d -Destination (Join-Path $Output 'd3dcompiler_43.dll') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY.md') -Destination (Join-Path $Output 'THIRD_PARTY.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $Output 'README.md') -Force
Write-Host "Published to $Output"
