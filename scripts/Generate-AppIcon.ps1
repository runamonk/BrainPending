$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '../src/MyNotes/Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null

# Vector geometry rendered at 4x resolution, then downsampled for each Windows icon size.
$canvas = [Drawing.Bitmap]::new(1024, 1024)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.ScaleTransform(4, 4)

function Fill-RoundedRect($color, [single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = 2 * $radius
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc(($x + $width - $diameter), $y, $diameter, $diameter, 270, 90)
    $path.AddArc(($x + $width - $diameter), ($y + $height - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc($x, ($y + $height - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($color))
    $graphics.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
}

Fill-RoundedRect '#44475A' 44 18 184 222 18
Fill-RoundedRect '#F8F8F2' 61 202 155 28 9
Fill-RoundedRect '#BD93F9' 44 16 184 202 18
Fill-RoundedRect '#6272A4' 44 16 30 202 14
foreach ($ringY in @(51, 93, 135, 177)) { Fill-RoundedRect '#8BE9FD' 27 $ringY 37 12 6 }
$bookmark = [Drawing.PointF[]]@(
    [Drawing.PointF]::new(180,16), [Drawing.PointF]::new(202,16),
    [Drawing.PointF]::new(202,81), [Drawing.PointF]::new(191,72),
    [Drawing.PointF]::new(180,81)
)
$brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FF79C6'))
$graphics.FillPolygon($brush, $bookmark)
$brush.Dispose()
Fill-RoundedRect '#44475A' 94 105 88 10 5
Fill-RoundedRect '#44475A' 94 132 88 10 5
Fill-RoundedRect '#44475A' 94 159 59 10 5
$graphics.Dispose()

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($canvas, [Drawing.Rectangle]::new(0, 0, $size, $size))
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'MyNotes.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$canvas.Dispose()

$file = [IO.File]::Create((Join-Path $assetDirectory 'MyNotes.ico'))
$writer = [IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
Write-Output 'Generated MyNotes.png and MyNotes.ico (16–256px).'
