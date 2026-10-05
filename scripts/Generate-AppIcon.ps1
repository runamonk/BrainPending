$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '../src/BrainPending/Assets'
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

Fill-RoundedRect '#282A36' 2 2 252 252 54

# Two lobes, one thought. Still waiting on the thought.
$brainTransform = $graphics.Save()
$graphics.TranslateTransform(128, 128)
$graphics.ScaleTransform(1.24, 1.56)
$graphics.TranslateTransform(-128, -110)
$brain = [Drawing.Drawing2D.GraphicsPath]::new()
$brain.AddBezier(128, 52, 112, 27, 79, 35, 72, 58)
$brain.AddBezier(72, 58, 46, 54, 32, 76, 42, 98)
$brain.AddBezier(42, 98, 19, 117, 33, 147, 51, 151)
$brain.AddBezier(51, 151, 49, 179, 82, 190, 101, 176)
$brain.AddBezier(101, 176, 112, 190, 125, 180, 128, 172)
$brain.AddBezier(128, 172, 141, 190, 155, 183, 162, 175)
$brain.AddBezier(162, 175, 186, 190, 210, 172, 206, 152)
$brain.AddBezier(206, 152, 232, 143, 232, 114, 216, 100)
$brain.AddBezier(216, 100, 227, 77, 210, 54, 187, 58)
$brain.AddBezier(187, 58, 177, 32, 145, 28, 128, 52)
$brain.CloseFigure()
$pink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FF92C2'))
$outline = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#AD497B'), 7)
$outline.StartCap = $outline.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.FillPath($pink, $brain)
$graphics.DrawPath($outline, $brain)
$graphics.DrawBezier($outline, 128, 53, 117, 69, 136, 80, 128, 96)
$graphics.DrawBezier($outline, 72, 60, 68, 77, 92, 76, 87, 95)
$graphics.DrawBezier($outline, 43, 100, 57, 92, 70, 105, 66, 116)
$graphics.DrawBezier($outline, 187, 60, 192, 81, 163, 73, 169, 95)
$graphics.DrawBezier($outline, 215, 102, 198, 94, 187, 106, 193, 119)
$graphics.DrawBezier($outline, 52, 150, 65, 136, 81, 143, 80, 157)
$graphics.DrawBezier($outline, 205, 152, 187, 140, 178, 145, 178, 160)
$brain.Dispose(); $pink.Dispose(); $outline.Dispose()

# Eyes glancing up, one brow raised, and a lopsided smirk.
Fill-RoundedRect '#282A36' 94 110 14 20 7
Fill-RoundedRect '#282A36' 158 117 13 13 6.5
$face = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#282A36'), 5.5)
$face.StartCap = $face.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.DrawBezier($face, 90, 101, 96, 94, 104, 93, 111, 97)
$graphics.DrawLine($face, 154, 119, 174, 118)
$graphics.DrawBezier($face, 114, 151, 128, 153, 140, 152, 150, 142)
$face.Dispose()
$graphics.Restore($brainTransform)
$graphics.Dispose()

$sizes = @(16, 20, 24, 28, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)
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
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'BrainPending.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$canvas.Dispose()

$file = [IO.File]::Create((Join-Path $assetDirectory 'BrainPending.ico'))
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
Write-Output 'Generated BrainPending.png and BrainPending.ico (16–256px).'
