<#
.SYNOPSIS
    Frames part of a screenshot for release notes: rounded corners and a shadow on the banner's background.
.EXAMPLE
    .\tools\make-feature.ps1 -Screenshot drivers-dark.png -Out docs\releases\2.2.0\drivers.png -Left 236 -Top 0 -Height 820
#>
param(
    [Parameter(Mandatory)] [string]$Screenshot,
    [Parameter(Mandatory)] [string]$Out,
    [int]$Left = 236, [int]$Top = 0, [int]$Width = 0, [int]$Height = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Color([string]$hex, [int]$alpha = 255) {
    $hex = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($alpha, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16))
}
function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); $p
}

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Screenshot))
if ($Width -le 0) { $Width = $src.Width - $Left }
if ($Height -le 0) { $Height = $src.Height - $Top }
$pad = 56
$W = $Width + 2 * $pad; $H = $Height + 2 * $pad
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'

$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0, 0, $W, $H), (Color '0A0F1A'), (Color '13213A'), 35)
$g.FillRectangle($bg, 0, 0, $W, $H)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddEllipse([float](-$W * 0.2), [float](-$H * 0.3), [float]($W * 0.8), [float]($H * 0.9))
$pg = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
$pg.CenterColor = Color '2F8FE8' 55; $pg.SurroundColors = @(Color '2F8FE8' 0)
$g.FillPath($pg, $path)

for ($i = 1; $i -le 12; $i++) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color '000000' 10)), (Rounded ($pad - $i + 6) ($pad - $i + 14) ($Width + 2 * $i) ($Height + 2 * $i) (14 + $i)))
}
$frame = Rounded $pad $pad $Width $Height 14
$g.SetClip($frame)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle $pad, $pad, $Width, $Height), (New-Object System.Drawing.Rectangle $Left, $Top, $Width, $Height), [System.Drawing.GraphicsUnit]::Pixel)
$g.ResetClip()
$g.DrawPath((New-Object System.Drawing.Pen (Color 'FFFFFF' 40), 1.5), $frame)

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $src.Dispose()
Write-Host "Wrote $Out"
