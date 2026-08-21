<#
.SYNOPSIS
  Renders the Teezy mark to a multi-resolution Teezy.ico.

.DESCRIPTION
  Windows needs an icon *resource embedded in the PE file* for Explorer, the Start Menu,
  Alt-Tab and taskbar pinning. Icons drawn at runtime — as the tray icon and window icon are —
  do not satisfy any of those: the shell reads the file, not the running process.

  So this is the one place the mark exists as a binary asset. It is generated rather than
  hand-drawn, from the same geometry Brand.MarkGeometry and TrayIcons use, so the three cannot
  drift. Re-run it after changing the mark or the accent colour.

  Entries are PNG-compressed at every size. Windows Vista and later read PNG icon entries at
  any size, and the app manifest already declares Windows 10 as the floor.
#>
[CmdletBinding()]
param(
    [string]$Out
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as a parameter default: $PSScriptRoot is not reliably populated
# while param defaults are being evaluated under Windows PowerShell 5.1.
if (-not $Out) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Out = Join-Path (Split-Path -Parent $here) 'src\Teezy.App\Teezy.ico'
}
Add-Type -AssemblyName System.Drawing

# The sizes Windows actually asks for: 16 tray/small, 32 taskbar and Alt-Tab, 48 Explorer
# large, 256 extra-large and Start tiles. The rest keep scaled sizes crisp on high DPI.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$accent = [System.Drawing.Color]::FromArgb(0x1E, 0x5F, 0x8E)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-MarkBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # One 100x100 grid for every element, matching Brand.MarkGeometry.
    $s = $size / 100.0
    $back = New-Object System.Drawing.SolidBrush $accent
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

    # Full-bleed squircle. A tiny inset keeps the anti-aliased edge from being clipped.
    $sq = New-RoundedPath (2 * $s) (2 * $s) (96 * $s) (96 * $s) (28 * $s)
    $g.FillPath($back, $sq); $sq.Dispose()

    $bar = New-RoundedPath (22 * $s) (25 * $s) (56 * $s) (14 * $s) (7 * $s)
    $g.FillPath($white, $bar); $bar.Dispose()

    $stem = New-RoundedPath (43 * $s) (25 * $s) (14 * $s) (45 * $s) (7 * $s)
    $g.FillPath($white, $stem); $stem.Dispose()

    $back.Dispose(); $white.Dispose(); $g.Dispose()
    return $bmp
}

# Render every size to a PNG blob first; the header needs each blob's length up front.
$blobs = foreach ($size in $sizes) {
    $bmp = New-MarkBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , @{ Size = $size; Bytes = $ms.ToArray() }
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR
    $w.Write([uint16]0)               # reserved
    $w.Write([uint16]1)               # type: 1 = icon
    $w.Write([uint16]$blobs.Count)

    # Image data starts after the directory and all its entries.
    $offset = 6 + (16 * $blobs.Count)

    foreach ($b in $blobs) {
        # 256 is written as 0 — the field is one byte and 256 does not fit.
        $dim = if ($b.Size -ge 256) { 0 } else { $b.Size }
        $w.Write([byte]$dim)          # width
        $w.Write([byte]$dim)          # height
        $w.Write([byte]0)             # palette size (0 = truecolour)
        $w.Write([byte]0)             # reserved
        $w.Write([uint16]1)           # colour planes
        $w.Write([uint16]32)          # bits per pixel
        $w.Write([uint32]$b.Bytes.Length)
        $w.Write([uint32]$offset)
        $offset += $b.Bytes.Length
    }

    foreach ($b in $blobs) { $w.Write($b.Bytes) }
}
finally {
    $w.Dispose()
    $fs.Dispose()
}

# Built into variables first: Windows PowerShell 5.1 mis-parses a quoted string inside an
# interpolated subexpression inside a double-quoted string.
$kb = [math]::Round((Get-Item $Out).Length / 1KB, 1)
$list = $sizes -join ', '
$count = $blobs.Count
Write-Host "Wrote $Out - $count sizes ($list), $kb KB" -ForegroundColor Green
