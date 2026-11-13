# Draws the app icon, the same as scripts/make-icon.swift: a dark phone on a green rounded square. Writes
# EmulatorShell\Assets\AppIcon.ico (16 to 256 px) and AppIcon.png (256 px), which the build includes.
# Usage: powershell -File windows\scripts\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot '..\EmulatorShell\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Drawn on the 1024 grid of the macOS icon, top-down.
function Draw([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 1024, $size / 1024)
    # Windows icons fill more of their square than the macOS grid's 824 pt tile.
    $tile = RoundedRect 64 64 896 896 200
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 64), (New-Object System.Drawing.PointF 0, 960),
        ([System.Drawing.Color]::FromArgb(255, 61, 219, 133)), ([System.Drawing.Color]::FromArgb(255, 13, 140, 97))
    $g.FillPath($gradient, $tile)

    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 26, 26, 26))
    $g.FillPath($dark, (RoundedRect 352 210 320 604 58))
    $screen = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 228), (New-Object System.Drawing.PointF 0, 796),
        ([System.Drawing.Color]::FromArgb(255, 158, 237, 255)), ([System.Drawing.Color]::FromArgb(255, 69, 133, 242))
    $g.FillPath($screen, (RoundedRect 370 228 284 568 42))
    $g.FillEllipse($dark, 499, 254, 26, 26)
    # Power and volume keys on the right edge.
    $g.FillPath($dark, (RoundedRect 670 354 14 70 6))
    $g.FillPath($dark, (RoundedRect 670 454 14 100 6))
    $g.Dispose()
    return $bitmap
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = Draw $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bitmap.Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    , $stream.ToArray()
}

# An .ico of PNG images: header, one directory entry per size, then the images.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
Write-Host "Wrote $assets\AppIcon.ico and AppIcon.png"
