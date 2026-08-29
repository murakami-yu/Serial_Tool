# Generate multi-size PNGs + Windows ICO (PNG-embedded) from icon256.png
# Usage: powershell -NoProfile -Command "Add-Type -AssemblyName System.Drawing; & 'build-icon.ps1'"
# NOTE: icon256.png must be rendered from icon.svg first (Edge headless). This script never overwrites it.
$ErrorActionPreference = "Stop"

$dir = $PSScriptRoot
$bytes = [System.IO.File]::ReadAllBytes((Join-Path $dir 'icon256.png'))
$ms = New-Object System.IO.MemoryStream
$ms.Write($bytes, 0, $bytes.Length)
$src = [System.Drawing.Image]::FromStream($ms)

# Downscale only; 256 comes straight from the source file
foreach ($s in 16, 32, 48) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
    $bmp.Save((Join-Path $dir "icon$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
$src.Dispose()

# Assemble ICO: ICONDIR + ICONDIRENTRY x4 + raw PNG payloads (Vista+ PNG-in-ICO)
$sizes = 16, 32, 48, 256
$data = @()
foreach ($s in $sizes) { $data += ,([System.IO.File]::ReadAllBytes((Join-Path $dir "icon$s.png"))) }

$fs = [System.IO.File]::Create((Join-Path $dir 'app.ico'))
$w = New-Object System.IO.BinaryWriter($fs)
try {
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim)
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$data[$i].Length)
        $w.Write([uint32]$offset)
        $offset += $data[$i].Length
    }
    foreach ($d in $data) { $w.Write($d) }
    $w.Flush()
} finally {
    $w.Close(); $fs.Close()
}
Write-Host ("OK: app.ico " + (Get-Item (Join-Path $dir 'app.ico')).Length + " bytes (16/32/48/256)")
