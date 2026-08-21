<#
.SYNOPSIS
  Downloads the Parakeet speech model Wisper needs to transcribe.

.DESCRIPTION
  NVIDIA Parakeet TDT 0.6B, exported to ONNX and quantized to int8 (~661 MB).
  Network is needed once, for this download. Transcription itself is fully offline.

  Model weights are CC-BY-4.0 (NVIDIA); sherpa-onnx is Apache-2.0; ONNX Runtime is MIT.

.PARAMETER Variant
  v2 (English only, best English accuracy) or v3 (25 languages). Default v2.
#>
[CmdletBinding()]
param(
    [ValidateSet('v2', 'v3')]
    [string]$Variant = 'v2'
)

$ErrorActionPreference = 'Stop'

$dir  = Join-Path $env:LOCALAPPDATA "Wisper\models\parakeet-$Variant"
$base = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-$Variant-int8/resolve/main"

New-Item -ItemType Directory -Force $dir | Out-Null
Write-Host "Downloading Parakeet $Variant into $dir" -ForegroundColor Cyan

foreach ($f in 'tokens.txt', 'joiner.int8.onnx', 'decoder.int8.onnx', 'encoder.int8.onnx') {
    Write-Host "  $f"
    # curl.exe, with the .exe. Bare `curl` in PowerShell is an alias for Invoke-WebRequest,
    # which is a different program and will buffer the whole 650 MB in memory without -OutFile.
    curl.exe -L --fail --progress-bar -o (Join-Path $dir $f) "$base/$f"
}

# A truncated download is the most common failure and it does not announce itself: a partial
# encoder fails later with an opaque protobuf parse error that names nothing useful.
$expected = @{
    'encoder.int8.onnx' = 652183000
    'decoder.int8.onnx' = 7257753
    'joiner.int8.onnx'  = 1739080
    'tokens.txt'        = 9384
}

Write-Host "`nVerifying:" -ForegroundColor Cyan
$bad = $false
foreach ($k in $expected.Keys | Sort-Object) {
    $p = Join-Path $dir $k
    if (-not (Test-Path $p)) { Write-Host ("  {0,-20} MISSING" -f $k) -ForegroundColor Red; $bad = $true; continue }
    $got = (Get-Item $p).Length
    if ([math]::Abs($got - $expected[$k]) -lt 2MB) {
        Write-Host ("  {0,-20} {1,10:N1} MB  OK" -f $k, ($got / 1MB)) -ForegroundColor Green
    } else {
        Write-Host ("  {0,-20} {1,10:N1} MB  SIZE MISMATCH - re-run this script" -f $k, ($got / 1MB)) -ForegroundColor Red
        $bad = $true
    }
}

if ($bad) { exit 1 }
Write-Host "`nDone. Start Wisper and hold Right Ctrl to dictate." -ForegroundColor Green
