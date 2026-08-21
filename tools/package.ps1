<#
.SYNOPSIS
  Assembles the folder you carry to another machine to install Teezy.

.DESCRIPTION
  Builds both architectures, then stages dist\Teezy-Setup containing the two executables,
  the ~661 MB speech model, and the installer. About 740 MB in total.

  The model is bundled deliberately. Teezy can download it on first launch, but that is the
  single step most likely to fail on a managed network - corporate proxies and TLS
  inspection break large Hugging Face transfers, and the failure lands at the worst moment,
  on a machine you may not be able to debug on.

  The result works two ways. Running install.ps1 puts Teezy in a permanent per-user
  location; if scripts are blocked, the same files can be arranged by hand into a portable
  folder, because Teezy also looks for the model next to its own executable.

.PARAMETER SkipBuild
  Use whatever is already in dist\win-x64 and dist\win-arm64 instead of republishing.

.PARAMETER ModelSource
  Folder holding the four Parakeet files. Defaults to the model this machine already uses,
  %LOCALAPPDATA%\Teezy\models\parakeet-v2. Run tools\download-model.ps1 first if absent.

.PARAMETER Zip
  Also produce dist\Teezy-Setup.zip. Worth it for a download or a cloud share; skip it for
  a USB stick, where it only doubles the copying.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$ModelSource,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $ModelSource) {
    $ModelSource = Join-Path $env:LOCALAPPDATA 'Teezy\models\parakeet-v2'
}

$staging = Join-Path $root 'dist\Teezy-Setup'

# ---------------------------------------------------------------------------------- build

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Rid all
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}

# Verify the architecture actually written rather than trusting the folder name - with
# -SkipBuild these executables could be anything, and an ARM64 binary handed to an x64
# machine fails at load with no useful message, on a machine you cannot debug on.
function Get-PEMachine([string]$path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C; $fs.Position = $br.ReadInt32(); $null = $br.ReadUInt32()
        switch ($br.ReadUInt16()) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x14C { 'x86' } default { 'unknown' } }
    } finally { $fs.Dispose() }
}

$builds = @(
    @{ Rid = 'win-x64';   Arch = 'x64';   Pe = 'x64' }
    @{ Rid = 'win-arm64'; Arch = 'arm64'; Pe = 'ARM64' }
)

foreach ($b in $builds) {
    $exe = Join-Path $root ('dist\' + $b.Rid + '\Teezy.exe')
    if (-not (Test-Path $exe)) { throw "Missing $exe - run without -SkipBuild." }
    $pe = Get-PEMachine $exe
    if ($pe -ne $b.Pe) { throw "$exe is a $pe binary, expected $($b.Pe)." }
}

# ---------------------------------------------------------------------------------- model

# Mirrors ModelLocator.Expected and install.ps1. Checked here as well as at install time,
# because a package built from a truncated model is a failure discovered on the far machine.
$expected = @{
    'encoder.int8.onnx' = 652183000
    'decoder.int8.onnx' = 7257753
    'joiner.int8.onnx'  = 1739080
    'tokens.txt'        = 9384
}
$tolerance = 2MB

if (-not (Test-Path $ModelSource)) {
    throw "Model not found at $ModelSource. Run tools\download-model.ps1 first, or pass -ModelSource."
}

foreach ($name in $expected.Keys | Sort-Object) {
    $p = Join-Path $ModelSource $name
    if (-not (Test-Path $p)) { throw "Model file missing: $p" }
    $have = (Get-Item $p).Length
    if ([math]::Abs($have - $expected[$name]) -ge $tolerance) {
        throw "$name is $([math]::Round($have / 1MB, 1)) MB, expected about $([math]::Round($expected[$name] / 1MB, 1)) MB - re-run tools\download-model.ps1."
    }
}

# -------------------------------------------------------------------------------- staging

Write-Host "`nStaging $staging" -ForegroundColor Cyan
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null

foreach ($b in $builds) {
    $to = Join-Path $staging ('bin\' + $b.Arch)
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item (Join-Path $root ('dist\' + $b.Rid + '\Teezy.exe')) (Join-Path $to 'Teezy.exe')
    Write-Host ('  bin\{0}\Teezy.exe' -f $b.Arch)
}

$modelTo = Join-Path $staging 'model\parakeet-v2'
New-Item -ItemType Directory -Force -Path $modelTo | Out-Null
foreach ($name in $expected.Keys | Sort-Object) {
    Write-Host "  model\parakeet-v2\$name"
    Copy-Item (Join-Path $ModelSource $name) (Join-Path $modelTo $name)
}

foreach ($f in 'install.ps1', 'uninstall.ps1', 'READ-ME-FIRST.txt') {
    Copy-Item (Join-Path $PSScriptRoot ('package\' + $f)) (Join-Path $staging $f)
    Write-Host "  $f"
}

$bytes = (Get-ChildItem $staging -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("`n  {0:N0} MB total" -f [math]::Round($bytes / 1MB))

# ------------------------------------------------------------------------------------ zip

if ($Zip) {
    $zipPath = Join-Path $root 'dist\Teezy-Setup.zip'
    Write-Host "`nCompressing to $zipPath (a minute or two) ..." -ForegroundColor Cyan
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    # ZipFile rather than Compress-Archive: the built-in cmdlet in Windows PowerShell 5.1
    # buffers badly at this size. Fastest, because the model is already quantized and
    # squeezes by only a few percent for a great deal more time.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $zipPath, [System.IO.Compression.CompressionLevel]::Fastest, $false)

    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB)
    Write-Host ("  {0:N0} MB" -f $zipMb)
}

Write-Host "`nReady." -ForegroundColor Green
Write-Host "  Copy $staging to the target machine, then run, in that folder:"
Write-Host '    powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart'
Write-Host "  READ-ME-FIRST.txt covers SmartScreen, blocked scripts and what to tell IT.`n"
