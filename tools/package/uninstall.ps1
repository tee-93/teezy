<#
.SYNOPSIS
  Removes Teezy for the current user.

.DESCRIPTION
  Closes Teezy, removes the sign-in entry, the Start Menu shortcut and the program folder.

  Your data is kept by default: history, dictionary, settings and the encrypted API key at
  %LOCALAPPDATA%\Teezy. History is the only place dictated text still exists once the app it
  was typed into has moved on, so it is not deleted without being asked for.

.PARAMETER RemoveModel
  Also delete the 661 MB speech model. Reinstalling then needs the package again (or a
  download on first launch).

.PARAMETER RemoveData
  Delete everything under %LOCALAPPDATA%\Teezy - history, dictionary, settings, the
  encrypted API key and the model. This cannot be undone.

.PARAMETER Destination
  The install folder, if it was overridden at install time.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File uninstall.ps1
#>
[CmdletBinding()]
param(
    [switch]$RemoveModel,
    [switch]$RemoveData,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$text) { Write-Host "  $text" }

if (-not $Destination) { $Destination = Join-Path $env:LOCALAPPDATA 'Programs\Teezy' }
$dataDir  = Join-Path $env:LOCALAPPDATA 'Teezy'
$modelDir = Join-Path $dataDir 'models'

Write-Host "`nRemoving Teezy" -ForegroundColor Cyan

$running = Get-Process Teezy -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    Write-Step 'Closed the running copy'
}

# The Run value goes. The StartupApproved veto is deliberately left alone: it records the
# user's own Task Manager choice, and that should outlive an uninstall.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -Path $runKey -Name 'Teezy' -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty -Path $runKey -Name 'Teezy'
    Write-Step 'Sign-in entry removed'
}

$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Teezy.lnk'
if (Test-Path $lnk) {
    Remove-Item $lnk -Force
    Write-Step 'Start Menu shortcut removed'
}

if (Test-Path $Destination) {
    Remove-Item $Destination -Recurse -Force
    Write-Step "Program folder removed ($Destination)"
}

if ($RemoveData) {
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
        Write-Step "All data removed ($dataDir)"
    }
}
elseif ($RemoveModel) {
    if (Test-Path $modelDir) {
        Remove-Item $modelDir -Recurse -Force
        Write-Step 'Speech model removed'
    }
}
else {
    Write-Step "Kept: history, dictionary and settings in $dataDir"
    Write-Step 'Pass -RemoveModel to reclaim the 661 MB model, or -RemoveData to delete everything'
}

Write-Host "`nDone.`n" -ForegroundColor Green
