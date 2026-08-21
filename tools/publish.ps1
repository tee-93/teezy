<#
.SYNOPSIS
  Builds shippable single-file Teezy executables.

.DESCRIPTION
  Produces one self-contained .exe per architecture under dist\. Each bundles the .NET
  runtime, WPF and the native speech libraries, so a target machine needs nothing installed.
  The ~661 MB model is NOT bundled - the app downloads it on first run.

  Pick by CPU: ARM64 for Snapdragon/Surface Pro X machines, x64 for Intel and AMD. Running
  the x64 build on an ARM64 machine works through emulation but transcribes far slower.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'all')]
    [string]$Rid = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rids = if ($Rid -eq 'all') { @('win-x64', 'win-arm64') } else { @($Rid) }

# A running instance holds its own DLLs open and fails the build with a file-lock error.
Get-Process Teezy -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($r in $rids) {
    $out = Join-Path $root "dist\$r"
    Write-Host "`nPublishing $r ..." -ForegroundColor Cyan
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

    dotnet publish (Join-Path $root 'src\Teezy.App') `
        -c Release -r $r --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $r" }
}

# Verify the architecture actually written, rather than trusting the flag. An ARM64 native
# library inside an x64 exe fails at load with BadImageFormatException - on someone else's
# machine, with no useful message.
function Get-PEMachine([string]$path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C; $fs.Position = $br.ReadInt32(); $null = $br.ReadUInt32()
        switch ($br.ReadUInt16()) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x14C { 'x86' } default { 'unknown' } }
    } finally { $fs.Dispose() }
}

Write-Host "`nResult:" -ForegroundColor Cyan
$expected = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }
foreach ($r in $rids) {
    $exe = Join-Path $root "dist\$r\Teezy.exe"
    $arch = Get-PEMachine $exe
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $ok = $arch -eq $expected[$r]
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-11} {1,6} MB   PE={2,-6} {3}" -f $r, $mb, $arch, $(if ($ok) { 'OK' } else { "EXPECTED $($expected[$r])" })) -ForegroundColor $colour
}
Write-Host "`nCopy the matching Teezy.exe to the target machine and run it." -ForegroundColor Green
