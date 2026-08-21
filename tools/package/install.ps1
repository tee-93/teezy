<#
.SYNOPSIS
  Installs Teezy for the current user. No administrator rights, no network.

.DESCRIPTION
  Copies the executable matching this machine's CPU to %LOCALAPPDATA%\Programs\Teezy and
  the speech model to %LOCALAPPDATA%\Teezy\models\parakeet-v2, then adds a Start Menu entry.

  Everything is per-user and under %LOCALAPPDATA%, so nothing needs elevation and nothing is
  written to Program Files, HKLM, or any other machine-wide location.

  The model travels with this package. Teezy therefore never contacts the network on first
  launch - the download window only appears if the model is missing or damaged.

.PARAMETER Autostart
  Also start Teezy at sign-in, via HKCU\...\CurrentVersion\Run. This is the same entry the
  in-app setting writes, and it shows up in Task Manager's Startup tab. You can equally
  leave this off and use Settings > "Start Teezy when I sign in" later.

.PARAMETER Destination
  Override the install folder. Defaults to %LOCALAPPDATA%\Programs\Teezy.

.PARAMETER NoShortcut
  Skip the Start Menu shortcut.

.PARAMETER NoLaunch
  Do not start Teezy when the install finishes.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart
#>
[CmdletBinding()]
param(
    [switch]$Autostart,
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty when a script is dot-sourced or piped, and this one may well be run
# in odd ways on a locked-down machine.
$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }

function Write-Step([string]$text) { Write-Host "  $text" }

# ---------------------------------------------------------------------------- architecture

# RuntimeInformation reports the OS, not the host process, so a 32-bit PowerShell on 64-bit
# Windows still gets this right. $env:PROCESSOR_ARCHITECTURE does not.
$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$arch = switch ($osArch) {
    'X64'   { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "Teezy has no build for $osArch. Only x64 and ARM64 Windows are supported." }
}

$source = Join-Path $here "bin\$arch\Teezy.exe"
if (-not (Test-Path $source)) {
    throw "Missing $source - this package is incomplete. Re-copy the whole folder."
}

Write-Host "`nInstalling Teezy" -ForegroundColor Cyan
Write-Step "CPU              $osArch, using the $arch build"

# ------------------------------------------------------------------------------ where to

if (-not $Destination) { $Destination = Join-Path $env:LOCALAPPDATA 'Programs\Teezy' }
$exePath   = Join-Path $Destination 'Teezy.exe'
$modelDest = Join-Path $env:LOCALAPPDATA 'Teezy\models\parakeet-v2'

Write-Step "Program          $Destination"
Write-Step "Model            $modelDest"

# A running instance holds its own executable open, and the copy fails with a file lock.
$running = Get-Process Teezy -ErrorAction SilentlyContinue
if ($running) {
    Write-Step 'Closing the running copy of Teezy'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

# -------------------------------------------------------------------------------- the exe

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item $source $exePath -Force

# Files that arrived by USB stick, download, or network share carry a mark-of-the-web
# alternate data stream. Windows then shows a "publisher could not be verified" prompt every
# single launch, and some Defender / SmartScreen policies refuse outright.
Unblock-File $exePath -ErrorAction SilentlyContinue

$exeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Step "Copied Teezy.exe ($exeMb MB)"

# ------------------------------------------------------------------------------ the model

# Mirrors ModelLocator.Expected. A truncated model does not announce itself - a partial
# encoder surfaces much later as an opaque protobuf parse error naming nothing useful.
$expected = @{
    'encoder.int8.onnx' = 652183000
    'decoder.int8.onnx' = 7257753
    'joiner.int8.onnx'  = 1739080
    'tokens.txt'        = 9384
}
$tolerance = 2MB

$modelSource = Join-Path $here 'model\parakeet-v2'
if (-not (Test-Path $modelSource)) {
    throw "Missing $modelSource - this package is incomplete. Re-copy the whole folder."
}

New-Item -ItemType Directory -Force -Path $modelDest | Out-Null

foreach ($name in $expected.Keys | Sort-Object) {
    $src = Join-Path $modelSource $name
    $dst = Join-Path $modelDest $name

    if (-not (Test-Path $src)) { throw "Missing $src - this package is incomplete." }

    # Already there and the right size: skip. The encoder alone is 622 MB, and reinstalling
    # over a good copy should not mean copying it again.
    if (Test-Path $dst) {
        $have = (Get-Item $dst).Length
        if ([math]::Abs($have - $expected[$name]) -lt $tolerance) {
            Write-Step "Model $name already in place"
            continue
        }
    }

    Write-Step "Copying $name"
    Copy-Item $src $dst -Force
    Unblock-File $dst -ErrorAction SilentlyContinue
}

# Verify what actually landed, rather than trusting that the copies succeeded. A USB stick
# pulled early truncates the last file written and reports no error.
$bad = @()
foreach ($name in $expected.Keys | Sort-Object) {
    $dst = Join-Path $modelDest $name
    if (-not (Test-Path $dst)) { $bad += "$name is missing"; continue }
    $have = (Get-Item $dst).Length
    if ([math]::Abs($have - $expected[$name]) -ge $tolerance) {
        $mb = [math]::Round($have / 1MB, 1)
        $want = [math]::Round($expected[$name] / 1MB, 1)
        $bad += "$name is $mb MB, expected about $want MB - the copy was truncated"
    }
}
if ($bad.Count -gt 0) {
    Write-Host "`nThe model did not copy cleanly:" -ForegroundColor Red
    foreach ($b in $bad) { Write-Host "  $b" -ForegroundColor Red }
    throw 'Model verification failed. Re-copy the package and run install.ps1 again.'
}
Write-Step 'Model verified'

# --------------------------------------------------------------------------- Start Menu

if (-not $NoShortcut) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $lnk = Join-Path $startMenu 'Teezy.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Description = 'Push-to-talk dictation'
    $shortcut.Save()
    Write-Step 'Start Menu shortcut created'
}

# ---------------------------------------------------------------------------- autostart

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

$existing = (Get-ItemProperty -Path $runKey -Name 'Teezy' -ErrorAction SilentlyContinue).Teezy

# Quoted: the path contains spaces on any machine whose user name does, and an unquoted value
# is parsed as a command plus arguments. The --startup flag is how Teezy tells a sign-in
# launch from someone double-clicking it, and stays in the tray rather than opening its
# window. It must match WindowsAutostart.StartupFlag.
$runCommand = '"' + $exePath + '" --startup'

if ($Autostart) {
    Set-ItemProperty -Path $runKey -Name 'Teezy' -Value $runCommand

    # Writing the Run value alone is not enough. If Teezy was ever switched off in Task
    # Manager, Windows records that veto separately and it wins - the entry would look
    # enabled and nothing would happen at sign-in.
    if (Test-Path $approvedKey) {
        Remove-ItemProperty -Path $approvedKey -Name 'Teezy' -ErrorAction SilentlyContinue
    }
    Write-Step 'Registered to start at sign-in'
}
elseif ($existing) {
    # An entry from a previous install points at the old executable, or predates the
    # --startup flag. Teezy repairs it itself at launch - but only if it launches, and it
    # will not launch from a path that no longer exists. So fix it here, where both halves
    # are still known. Compared as a whole command line, so a flagless entry is upgraded too.
    if ($existing.Trim() -ne $runCommand) {
        Set-ItemProperty -Path $runKey -Name 'Teezy' -Value $runCommand
        Write-Step 'Existing sign-in entry brought up to date'
    }
}

# -------------------------------------------------------------------------------- finish

Write-Host "`nInstalled." -ForegroundColor Green
Write-Host '  Hold Ctrl + Win together, speak, release. Teezy lives in the system tray -'
Write-Host '  click the ^ arrow next to the clock if you cannot see it.'
Write-Host "  Uninstall with: powershell -ExecutionPolicy Bypass -File uninstall.ps1`n"

if (-not $NoLaunch) {
    Start-Process $exePath
    Write-Host 'Teezy is starting. The tray icon appears once the model has loaded (~2 s).'
}
