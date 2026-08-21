<#
.SYNOPSIS
  Renders the Teezy mark to a multi-resolution Teezy.ico.

.DESCRIPTION
  Windows needs an icon *resource embedded in the PE file* for Explorer, the Start Menu,
  Alt-Tab and taskbar pinning. Icons drawn at runtime — as the tray icon and window icon are —
  do not satisfy any of those: the shell reads the file, not the running process.

  So this is the one place the mark exists as a binary asset. It is generated rather than
  hand-drawn, and it is generated from src\Teezy.App\Theme.xaml itself — the same
  MarkGeometry, tile metrics and accent colour the running app uses. This script used to
  redraw the mark in GDI+ from remembered coordinates, which matched the app only for as long
  as somebody changed both. Re-run it after changing the mark or the accent.

  Entries are PNG-compressed at every size. Windows Vista and later read PNG icon entries at
  any size, and the app manifest already declares Windows 10 as the floor.

.PARAMETER Out
  Where to write the .ico. Defaults to src\Teezy.App\Teezy.ico.
#>
[CmdletBinding()]
param(
    [string]$Out
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as a parameter default: $PSScriptRoot is not reliably populated
# while param defaults are being evaluated under Windows PowerShell 5.1.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if (-not $Out) { $Out = Join-Path $root 'src\Teezy.App\Teezy.ico' }

# RenderTargetBitmap needs a single-threaded apartment, and a host that started MTA cannot be
# switched in place - so re-launch rather than fail with an opaque COM error.
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Write-Host 'Re-launching in STA (WPF rendering requires it) ...'
    & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Out $Out
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml

# The sizes Windows actually asks for: 16 tray/small, 32 taskbar and Alt-Tab, 48 Explorer
# large, 256 extra-large and Start tiles. The rest keep scaled sizes crisp on high DPI.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$themePath = Join-Path $root 'src\Teezy.App\Theme.xaml'
$stream = [IO.File]::OpenRead($themePath)
try { $theme = [Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }

$glyph  = $theme['MarkGeometry']
$accent = $theme['Accent']
$inset  = $theme['MarkTileInset']
$radius = $theme['MarkTileRadius']
$fill   = $theme['MarkGlyphFill']
$white  = [Windows.Media.Brushes]::White

if (-not $glyph -or -not $accent) { throw "Theme.xaml did not yield MarkGeometry and Accent." }

function New-MarkBitmap([int]$size) {
    # Everything in the resource dictionary is authored on a 100x100 grid.
    $s = $size / 100.0

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    $tile = New-Object Windows.Rect(($inset * $s), ($inset * $s),
                                    ($size - 2 * $inset * $s), ($size - 2 * $inset * $s))
    $squircle = New-Object Windows.Media.RectangleGeometry($tile, ($radius * $s), ($radius * $s))
    $dc.DrawGeometry($accent, $null, $squircle)

    # Centred from the geometry's own bounds rather than remembered numbers, so re-drawing the
    # glyph re-centres it instead of quietly sitting off to one side.
    $b = $glyph.Bounds
    $k = $s * $fill
    $placed = $glyph.Clone()
    $tg = New-Object Windows.Media.TransformGroup
    $tg.Children.Add((New-Object Windows.Media.ScaleTransform($k, $k)))
    $tg.Children.Add((New-Object Windows.Media.TranslateTransform(
        ((($size - $b.Width * $k) / 2) - $b.X * $k),
        ((($size - $b.Height * $k) / 2) - $b.Y * $k))))
    $placed.Transform = $tg
    $dc.DrawGeometry($white, $null, $placed)

    $dc.Close()

    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, 'Pbgra32')
    $rtb.Render($visual)
    return $rtb
}

# Render every size to a PNG blob first; the header needs each blob's length up front.
$blobs = foreach ($size in $sizes) {
    $rtb = New-MarkBitmap $size
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , @{ Size = $size; Bytes = $ms.ToArray() }
}

$fs = [IO.File]::Create($Out)
$w = New-Object IO.BinaryWriter($fs)
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
