#!/usr/bin/env powershell
# gen-icon.ps1 : generate AppIcon.ico (multi-size PNG-compressed frames) for AutoSlice.
# Usage: powershell -File gen-icon.ps1 -OutFile AppIcon.ico
# NOTE: keep this file pure ASCII (PowerShell 5.1 BOM-less UTF-8 caveat).
param(
    [Parameter(Mandatory = $false)][string]$OutFile = "AppIcon.ico"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48, 64, 128)

# Draw base 128x128 art: blue rounded square + white down-arrow chevron
$base = New-Object System.Drawing.Bitmap 128, 128
$g = [System.Drawing.Graphics]::FromImage($base)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
    (New-Object System.Drawing.Rectangle 0, 0, 128, 128),
    ([System.Drawing.Color]::FromArgb(0x2E, 0x86, 0xDE)),
    ([System.Drawing.Color]::FromArgb(0x1F, 0x5F, 0xB8)),
    45)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = New-Object System.Drawing.Rectangle 4, 4, 120, 120
$d = 28
$path.AddArc($r, 0, 360)
$g.FillPath($brush, $path)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White, 14)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($pen, 64, 96, 30, 58)
$g.DrawLine($pen, 64, 96, 98, 58)
$g.Dispose(); $path.Dispose(); $pen.Dispose(); $brush.Dispose()

# Render each size to PNG bytes
$chunks = @()
foreach ($sz in $sizes) {
    $b = New-Object System.Drawing.Bitmap $sz, $sz
    $gg = [System.Drawing.Graphics]::FromImage($b)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.DrawImage($base, (New-Object System.Drawing.Rectangle 0, 0, $sz, $sz))
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $chunks += , ($ms.ToArray())
    $gg.Dispose(); $b.Dispose(); $ms.Dispose()
}
$base.Dispose()

# Assemble ICO: ICONDIR(6) + entries(16*n) + PNG blobs
$ms2 = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms2)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$chunks.Count)
$offset = 6 + 16 * $chunks.Count

# second pass with recorded sizes
$ms2.SetLength(0)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$chunks.Count)
$offset = 6 + 16 * $chunks.Count
for ($i = 0; $i -lt $chunks.Count; $i++) {
    $c = $chunks[$i]
    $dim = $sizes[$i]
    $entryDim = if ($dim -lt 256) { $dim } else { 0 }
    $w.Write([Byte]$entryDim)
    $w.Write([Byte]$entryDim)
    $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$c.Length); $w.Write([UInt32]$offset)
    $offset += $c.Length
}
foreach ($c in $chunks) { $w.Write($c) }
$w.Flush()
[System.IO.File]::WriteAllBytes($OutFile, $ms2.ToArray())
$w.Dispose(); $ms2.Dispose()
Write-Host "Generated $OutFile : $($chunks.Count) sizes ($($sizes -join ','))"