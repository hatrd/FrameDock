# The same geometric mark is used in the SVG, README and every Windows icon size.
# Run with Windows PowerShell; uses System.Drawing, with no extra tools to install.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\FrameDock\Assets'
$brandDirectory = Join-Path $PSScriptRoot '..\docs\brand'
New-Item -ItemType Directory -Path $assetDirectory, $brandDirectory -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
  <title>FrameDock — a frame, an F, and a trim boundary</title>
  <rect x="2" y="2" width="60" height="60" rx="14" fill="#151e2b"/>
  <path d="M13 13H37V20H20V29H33V36H20V51H13Z" fill="#66e2cb"/>
  <path d="M41 13H51V51H35V44H44V20H41Z" fill="#ffba66"/>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $brandDirectory 'framedock.svg'), $svg + "`n", $utf8)

function New-BrandBitmap([int]$Size) {
    $canvas = New-Object System.Drawing.Bitmap(($Size * 4), ($Size * 4))
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(($Size * 4 / 64.0), ($Size * 4 / 64.0))
    $background = New-Object System.Drawing.Drawing2D.GraphicsPath
    $background.AddArc(2, 2, 28, 28, 180, 90)
    $background.AddArc(34, 2, 28, 28, 270, 90)
    $background.AddArc(34, 34, 28, 28, 0, 90)
    $background.AddArc(2, 34, 28, 28, 90, 90)
    $background.CloseFigure()
    $navy = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#151e2b'))
    $mint = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#66e2cb'))
    $amber = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#ffba66'))
    $graphics.FillPath($navy, $background)
    [System.Drawing.PointF[]]$f = @(
        [System.Drawing.PointF]::new(13,13), [System.Drawing.PointF]::new(37,13),
        [System.Drawing.PointF]::new(37,20), [System.Drawing.PointF]::new(20,20),
        [System.Drawing.PointF]::new(20,29), [System.Drawing.PointF]::new(33,29),
        [System.Drawing.PointF]::new(33,36), [System.Drawing.PointF]::new(20,36),
        [System.Drawing.PointF]::new(20,51), [System.Drawing.PointF]::new(13,51))
    [System.Drawing.PointF[]]$boundary = @(
        [System.Drawing.PointF]::new(41,13), [System.Drawing.PointF]::new(51,13),
        [System.Drawing.PointF]::new(51,51), [System.Drawing.PointF]::new(35,51),
        [System.Drawing.PointF]::new(35,44), [System.Drawing.PointF]::new(44,44),
        [System.Drawing.PointF]::new(44,20), [System.Drawing.PointF]::new(41,20))
    $graphics.FillPolygon($mint, $f)
    $graphics.FillPolygon($amber, $boundary)
    $graphics.Dispose()
    $background.Dispose()
    $navy.Dispose(); $mint.Dispose(); $amber.Dispose()
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $resize = [System.Drawing.Graphics]::FromImage($bitmap)
    $resize.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $resize.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $resize.DrawImage($canvas, 0, 0, $Size, $Size)
    $resize.Dispose()
    $canvas.Dispose()
    return ,$bitmap
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-BrandBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $frameWriter = New-Object System.IO.BinaryWriter($stream)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    # Use a 32-bit DIB plus AND mask at every size. System.Drawing.Icon.ToBitmap
    # does not reliably decode PNG-compressed small ICO frames.
    $frameWriter.Write([uint32]40)
    $frameWriter.Write([int32]$size); $frameWriter.Write([int32]($size * 2))
    $frameWriter.Write([uint16]1); $frameWriter.Write([uint16]32)
    $frameWriter.Write([uint32]0); $frameWriter.Write([uint32]($size * $size * 4 + $maskStride * $size))
    for ($field = 0; $field -lt 4; $field++) { $frameWriter.Write([uint32]0) }
    $pixels = $bitmap.LockBits([System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($size * 4)
        for ($line = $size - 1; $line -ge 0; $line--) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($pixels.Scan0, $line * $pixels.Stride), $row, 0, $row.Length)
            $frameWriter.Write([byte[]]$row)
        }
    } finally { $bitmap.UnlockBits($pixels) }
    $frameWriter.Write([byte[]](New-Object byte[] ($maskStride * $size)))
    $frameWriter.Flush()
    $frames += ,$stream.ToArray()
    $frameWriter.Dispose()
    $stream.Dispose()
    $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assetDirectory 'FrameDock.ico'))
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
$logo = New-BrandBitmap 512
$logo.Save((Join-Path $brandDirectory 'framedock.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host 'Generated SVG, PNG and nine-resolution ICO.'
