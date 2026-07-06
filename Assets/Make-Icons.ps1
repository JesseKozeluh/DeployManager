#Requires -Version 5.1
# Generates deploymgr.ico (multi-resolution) and favicon-32.png from the icon shapes.
# Run from any location — outputs land next to this script in Assets\.
# Requires: Windows (.NET / GDI+), no external tools needed.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s     = $Size / 256.0
    $navy  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 26,  42,  74))
    $cyan  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 79, 195, 247))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # Monitor frame
    $g.FillRectangle($navy,  [float](16*$s), [float](16*$s), [float](224*$s), [float](162*$s))
    # Screen
    $g.FillRectangle($white, [float](30*$s), [float](30*$s), [float](196*$s), [float](134*$s))
    # Arrow shaft
    $g.FillRectangle($cyan,  [float](106*$s), [float](54*$s), [float](44*$s), [float](64*$s))
    # Arrow head (triangle pointing down)
    $pts = New-Object 'System.Drawing.PointF[]' 3
    $pts[0] = New-Object System.Drawing.PointF([float](64*$s),  [float](118*$s))
    $pts[1] = New-Object System.Drawing.PointF([float](192*$s), [float](118*$s))
    $pts[2] = New-Object System.Drawing.PointF([float](128*$s), [float](162*$s))
    $g.FillPolygon($cyan, $pts)
    # Stand neck
    $g.FillRectangle($navy, [float](110*$s), [float](178*$s), [float](36*$s),  [float](30*$s))
    # Stand base
    $g.FillRectangle($navy, [float](72*$s),  [float](208*$s), [float](112*$s), [float](18*$s))

    $navy.Dispose(); $cyan.Dispose(); $white.Dispose(); $g.Dispose()
    return $bmp
}

$outDir = $PSScriptRoot

# --- ICO (16, 24, 32, 48, 64, 256) -----------------------------------------
$sizes   = @(16, 24, 32, 48, 64, 256)
$pngData = @()

foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData += , $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# Build ICO binary (PNG-compressed frames, compatible with Windows Vista+)
$icoStream = New-Object System.IO.MemoryStream
$w         = New-Object System.IO.BinaryWriter($icoStream)

# Header
$w.Write([uint16]0)               # reserved
$w.Write([uint16]1)               # type = ICO
$w.Write([uint16]$sizes.Count)    # image count

# Image data starts after header (6) + directory (N * 16)
$dataStart = 6 + ($sizes.Count * 16)

# Directory entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $offset = $dataStart
    for ($j = 0; $j -lt $i; $j++) { $offset += $pngData[$j].Length }

    $dim = if ($sizes[$i] -eq 256) { [byte]0 } else { [byte]$sizes[$i] }
    $w.Write($dim)                          # width  (0 = 256)
    $w.Write($dim)                          # height (0 = 256)
    $w.Write([byte]0)                       # color count
    $w.Write([byte]0)                       # reserved
    $w.Write([uint16]1)                     # planes
    $w.Write([uint16]32)                    # bit depth
    $w.Write([uint32]$pngData[$i].Length)   # bytes in resource
    $w.Write([uint32]$offset)               # offset from file start
}

# Image data
foreach ($png in $pngData) { $w.Write($png) }
$w.Flush()

$icoPath = Join-Path $outDir 'deploymgr.ico'
[System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())
$w.Dispose(); $icoStream.Dispose()
Write-Host "  Created $icoPath"

# --- Standalone PNGs for web use --------------------------------------------
foreach ($sz in @(32, 192)) {
    $bmp     = New-IconBitmap $sz
    $outFile = Join-Path $outDir "favicon-${sz}.png"
    $bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  Created $outFile"
}

Write-Host "Done."
