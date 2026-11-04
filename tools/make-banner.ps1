<#
.SYNOPSIS
    Draws a release banner (1280x640, also right for GitHub's social preview) from the app's icon and a screenshot.
.EXAMPLE
    .\tools\make-banner.ps1 -Version 2.2 -Screenshot docs\screenshots\overview-dark.png -Out docs\releases\2.2.0\banner.png `
        -Tagline 'Finds out why your laptop crashed.','Walks you through the fix.' -Chips 'Flight recorder','Guided driver updates','NVIDIA + AMD'
    Make the icon with:  LaptopGuardian.exe --write-icon icon.png --size 512
    Make sample screenshots with:  LaptopGuardian.exe --screenshot <folder> --dark --sample --viewport
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Screenshot,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Icon,
    [string[]]$Tagline = @('Finds out why your laptop crashed.', 'Walks you through the fix.'),
    [string[]]$Chips = @()
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Color([string]$hex, [int]$alpha = 255) {
    $hex = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($alpha, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16))
}
function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $p
}
function Font([string[]]$names, [float]$size, [System.Drawing.FontStyle]$style = 'Regular') {
    $have = (New-Object System.Drawing.Text.InstalledFontCollection).Families | ForEach-Object Name
    foreach ($n in $names) { if ($have -contains $n) { return New-Object System.Drawing.Font($n, $size, $style, [System.Drawing.GraphicsUnit]::Pixel) } }
    New-Object System.Drawing.Font('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'

# Background: deep navy, a soft blue glow behind the text and a warmer one behind the screenshot.
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0, 0, $W, $H), (Color '0A0F1A'), (Color '13213A'), 35)
$g.FillRectangle($bg, 0, 0, $W, $H)
foreach ($glow in @(@{ X = -220; Y = -120; S = 820; C = '2F8FE8'; A = 70 }, @{ X = 700; Y = 260; S = 760; C = '8B5CF6'; A = 45 })) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse([float]$glow.X, [float]$glow.Y, [float]$glow.S, [float]$glow.S)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $pg.CenterColor = Color $glow.C $glow.A
    $pg.SurroundColors = @(Color $glow.C 0)
    $g.FillPath($pg, $path)
}
# A faint dot grid for texture.
$dot = New-Object System.Drawing.SolidBrush (Color 'FFFFFF' 10)
for ($x = 24; $x -lt $W; $x += 32) { for ($y = 24; $y -lt $H; $y += 32) { $g.FillEllipse($dot, $x, $y, 2, 2) } }

# The screenshot, bleeding off the right edge, with a shadow and rounded corners.
$shot = [System.Drawing.Image]::FromFile((Resolve-Path $Screenshot))
$sw = 720; $sh = [int]($shot.Height * $sw / $shot.Width); $sx = 610; $sy = 112
for ($i = 1; $i -le 14; $i++) {
    $shadow = Rounded ($sx - $i + 8) ($sy - $i + 18) ($sw + 2 * $i) ($sh + 2 * $i) (16 + $i)
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color '000000' 9)), $shadow)
}
$frame = Rounded $sx $sy $sw $sh 16
$g.SetClip($frame)
$g.DrawImage($shot, $sx, $sy, $sw, $sh)
$g.ResetClip()
$g.DrawPath((New-Object System.Drawing.Pen (Color 'FFFFFF' 40), 1.5), $frame)

# Icon, name and version.
$left = 88
if ($Icon -and (Test-Path $Icon)) {
    $ic = [System.Drawing.Image]::FromFile((Resolve-Path $Icon))
    $g.DrawImage($ic, $left - 6, 120, 112, 112)
}
$titleFont = Font @('Segoe UI Variable Display Semib', 'Segoe UI Semibold') 58 'Regular'
$g.DrawString('LaptopGuardian', $titleFont, [System.Drawing.Brushes]::White, $left - 4, 250)
$titleWidth = $g.MeasureString('LaptopGuardian', $titleFont).Width

$pillFont = Font @('Segoe UI Variable Display Semib', 'Segoe UI Semibold') 22
$pillText = $Version
$pw = $g.MeasureString($pillText, $pillFont).Width + 26
$pill = Rounded ($left + $titleWidth - 10) 270 $pw 38 19
$g.FillPath((New-Object System.Drawing.SolidBrush (Color '2F8FE8')), $pill)
$g.DrawString($pillText, $pillFont, [System.Drawing.Brushes]::White, $left + $titleWidth + 3, 275)

$tagFont = Font @('Segoe UI Variable Display', 'Segoe UI') 27
$ty = 342
foreach ($line in $Tagline) { $g.DrawString($line, $tagFont, (New-Object System.Drawing.SolidBrush (Color 'B8C3D6')), $left - 2, $ty); $ty += 40 }

# Feature chips.
$chipFont = Font @('Segoe UI Variable Text Semibold', 'Segoe UI Semibold') 17
$cx = $left; $cy = $ty + 34
foreach ($chip in $Chips) {
    $cw = $g.MeasureString($chip, $chipFont).Width + 30
    if ($cx + $cw -gt 590) { $cx = $left; $cy += 50 }
    $r = Rounded $cx $cy $cw 38 19
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color 'FFFFFF' 16)), $r)
    $g.DrawPath((New-Object System.Drawing.Pen (Color 'FFFFFF' 46), 1), $r)
    $g.DrawString($chip, $chipFont, (New-Object System.Drawing.SolidBrush (Color 'E6ECF5')), $cx + 15, $cy + 8)
    $cx += $cw + 10
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $shot.Dispose()
Write-Host "Wrote $Out"
