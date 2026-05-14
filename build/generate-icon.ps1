#!/usr/bin/env pwsh
# Generates src/YtDlpTool/Resources/app.ico from scratch using GDI+ so future
# regeneration is reproducible. Embeds 256/48/32/16 PNG-encoded frames in a
# real multi-size ICO file (modern Vista+ format).
#
# Aesthetic: a circle with the Vivid Sunrise gradient (pink → orange → sky)
# and a bold white downward arrow centred — matches the gradient ellipse +
# accent vocabulary used elsewhere in the app.

[CmdletBinding()]
param(
    [string]$OutDir = "src/YtDlpTool/Resources"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Resolve OutDir relative to repo root (parent of this script's directory).
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$resolved  = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $resolved | Out-Null

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

# Aurora gradient brush (linear at 135deg with 3-stop interpolation).
$rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 255, 90, 143)), ([System.Drawing.Color]::FromArgb(255, 64, 200, 255)), 135.0
$cb = New-Object System.Drawing.Drawing2D.ColorBlend 3
$cb.Colors = @(
  [System.Drawing.Color]::FromArgb(255, 255, 90, 143),    # AuroraA — vivid pink
  [System.Drawing.Color]::FromArgb(255, 255, 169, 64),    # AuroraB — orange
  [System.Drawing.Color]::FromArgb(255, 64, 200, 255)     # AuroraC — sky blue
)
$cb.Positions = @(0.0, 0.5, 1.0)
$brush.InterpolationColors = $cb

$padding = 8
$g.FillEllipse($brush, $padding, $padding, $size - 2*$padding, $size - 2*$padding)

# Bold white downward arrow.
$whitePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 22
$whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$cx = $size / 2
$cy = $size / 2
$stemTop = $cy - 50
$stemBottom = $cy + 40
$g.DrawLine($whitePen, [single]$cx, [single]$stemTop, [single]$cx, [single]$stemBottom)
$g.DrawLine($whitePen, [single]($cx - 40), [single]$cy, [single]$cx, [single]$stemBottom)
$g.DrawLine($whitePen, [single]($cx + 40), [single]$cy, [single]$cx, [single]$stemBottom)

$g.Dispose()

# Save a debug PNG (useful for inspecting the source bitmap without unpacking the ICO).
$pngPath = Join-Path $resolved "app.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Build a multi-size ICO with PNG-encoded frames (Vista+). Each entry's ICONDIRENTRY
# uses width=0/height=0 sentinels for 256 — older parsers handle this correctly.
$sizes = 256, 48, 32, 16
$pngBytes = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $resized = New-Object System.Drawing.Bitmap $s, $s
    $rg = [System.Drawing.Graphics]::FromImage($resized)
    $rg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $rg.DrawImage($bmp, 0, 0, $s, $s)
    $rg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes.Add($ms.ToArray()) | Out-Null
    $resized.Dispose()
    $ms.Dispose()
}
$bmp.Dispose()

$icoPath = Join-Path $resolved "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    # ICONDIR (6 bytes): reserved=0, type=1 (icon), count=N
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$sizes.Count)

    # ICONDIRENTRY x N (16 bytes each)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $b = $pngBytes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim)        # width  (0 = 256)
        $bw.Write([byte]$dim)        # height (0 = 256)
        $bw.Write([byte]0)           # colorCount (0 for >256 colors)
        $bw.Write([byte]0)           # reserved
        $bw.Write([uint16]1)         # planes
        $bw.Write([uint16]32)        # bitCount
        $bw.Write([uint32]$b.Length) # dataSize
        $bw.Write([uint32]$offset)   # dataOffset
        $offset += $b.Length
    }

    foreach ($b in $pngBytes) { $bw.Write($b) }
    $bw.Flush()
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host "Wrote: $icoPath ($((Get-Item $icoPath).Length) bytes)"
Write-Host "Wrote: $pngPath ($((Get-Item $pngPath).Length) bytes)"
