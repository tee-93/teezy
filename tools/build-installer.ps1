<#
.SYNOPSIS
  Builds dist\Teezy-Setup.exe - the download-and-double-click installer.

.DESCRIPTION
  Publishes both architectures, then compiles tools\installer\Teezy.iss into a single
  installer of roughly 150 MB. It needs no administrator rights on the target machine:
  everything installs under %LOCALAPPDATA%, and there are no wizard pages to click through.

  The speech model is not included - the app downloads it on first launch. Use
  tools\package.ps1 instead for the fully offline package, which is the fallback if the
  target network blocks huggingface.co.

  Requires Inno Setup 6. It installs per-user and needs no elevation:
      winget install -e --id JRSoftware.InnoSetup

.PARAMETER Version
  Version stamped into the installer and shown in Apps & features. Defaults to the
  AppVersion in Teezy.iss, which is the version of record — a default here as well would be
  a second place to forget.

.PARAMETER SkipBuild
  Use whatever is already in dist\win-x64 and dist\win-arm64.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$iss = Join-Path $PSScriptRoot 'installer\Teezy.iss'

if (-not $Version) {
    $match = Select-String -Path $iss -Pattern '#define\s+AppVersion\s+"([^"]+)"' | Select-Object -First 1
    if (-not $match) { throw "No AppVersion found in $iss." }
    $Version = $match.Matches[0].Groups[1].Value
}

# ----------------------------------------------------------------------------------- iscc

# winget installs Inno Setup per-user by default, which is why no elevation was needed - so
# the per-user location is checked first, not last.
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install -e --id JRSoftware.InnoSetup"
}

# ---------------------------------------------------------------------------------- build

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Rid all
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}

# Verify the architecture actually written rather than trusting the folder name. Both
# payloads go into one installer and the wrong one is chosen silently at install time, on
# someone else's machine, where a BadImageFormatException says nothing useful.
function Get-PEMachine([string]$path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C; $fs.Position = $br.ReadInt32(); $null = $br.ReadUInt32()
        switch ($br.ReadUInt16()) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x14C { 'x86' } default { 'unknown' } }
    } finally { $fs.Dispose() }
}

foreach ($b in @(@{ Rid = 'win-x64'; Pe = 'x64' }, @{ Rid = 'win-arm64'; Pe = 'ARM64' })) {
    $exe = Join-Path $root ('dist\' + $b.Rid + '\Teezy.exe')
    if (-not (Test-Path $exe)) { throw "Missing $exe - run without -SkipBuild." }
    $pe = Get-PEMachine $exe
    if ($pe -ne $b.Pe) { throw "$exe is a $pe binary, expected $($b.Pe)." }
}

# -------------------------------------------------------------------------------- compile

Write-Host "`nCompiling installer ($Version) ..." -ForegroundColor Cyan

& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$out = Join-Path $root 'dist\Teezy-Setup.exe'
if (-not (Test-Path $out)) { throw "ISCC reported success but $out is missing." }

$mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "`n$out" -ForegroundColor Green
Write-Host ("  {0} MB, version {1}" -f $mb, $Version)
Write-Host '  Unsigned, so SmartScreen warns on first run: More info, then Run anyway.'
Write-Host "  Attach it to a GitHub release and the download URL is the 'website'.`n"
