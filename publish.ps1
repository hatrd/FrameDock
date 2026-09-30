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
$brandOutput = Join-Path $Output 'docs\brand'
New-Item -ItemType Directory -Path $brandOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\brand\framedock.png') -Destination $brandOutput -Force
$screenshotsOutput = Join-Path $Output 'docs\screenshots'
New-Item -ItemType Directory -Path $screenshotsOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\screenshots\framedock-workflow.png') -Destination $screenshotsOutput -Force
Write-Host "Published to $Output"
