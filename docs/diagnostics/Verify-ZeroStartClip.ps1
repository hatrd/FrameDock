param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$ClipPath,
    [double]$ExpectedDuration = 12.841
)
$ErrorActionPreference = 'Stop'
function Read-Probe([string]$MediaPath) {
    $probeText = & ffprobe -v error -show_entries 'format=duration:stream=codec_type,start_time,duration' -of json $MediaPath
    if ($LASTEXITCODE -ne 0) { throw 'ffprobe failed' }
    return ($probeText | ConvertFrom-Json)
}
function First-VideoHash([string]$MediaPath) {
    $probeText = & ffprobe -v error -select_streams v:0 -read_intervals '%+#1' -show_packets -show_data_hash sha256 -show_entries packet=data_hash -of json $MediaPath
    if ($LASTEXITCODE -ne 0) { throw 'packet probe failed' }
    return ($probeText | ConvertFrom-Json).packets[0].data_hash
}
$data = Read-Probe $ClipPath
$video = $data.streams | Where-Object codec_type -eq video | Select-Object -First 1
$audio = $data.streams | Where-Object codec_type -eq audio | Select-Object -First 1
$failures = @()
if ($null -eq $video) { $failures += 'missing video' }
else {
    if ([Math]::Abs([double]$video.start_time) -gt 0.1) { $failures += "video starts at $($video.start_time)s" }
    if ($video.duration -and [Math]::Abs([double]$video.duration - $ExpectedDuration) -gt 0.1) { $failures += "video duration is $($video.duration)s" }
    if ($audio -and [Math]::Abs([double]$video.start_time - [double]$audio.start_time) -gt 0.1) { $failures += 'first A/V timestamps differ by more than 100ms' }
    if ((First-VideoHash $SourcePath) -ne (First-VideoHash $ClipPath)) { $failures += 'first copied video packet differs from source first packet' }
}
if ([Math]::Abs([double]$data.format.duration - $ExpectedDuration) -gt 0.1) { $failures += 'container duration differs by more than 100ms' }
if ($failures.Count) { Write-Output ('FAIL: ' + ($failures -join '; ')); exit 1 }
Write-Output "PASS: video begins near zero, A/V starts agree, duration is within 100ms, first encoded video packet matches source. This verifies a zero-start copy only."
