<#
.SYNOPSIS
  Installs (or updates) AvellCC: the "AvellCC" Windows service + the tray app.

.DESCRIPTION
  1. Runs the tests, then builds and publishes the service, app and CLI to C:\Program Files\AvellCC.
  2. Rule 1 (docs/ARCHITECTURE.md): stops Avell Custom Control and disables its GCUBridge service
     (use -KeepVendor to leave it alone; AvellCC then stays blocked while the vendor app runs).
  3. Creates the AvellCC service (LocalSystem, automatic start, automatic restart on failure).
  4. Creates a Start Menu shortcut and (optionally) starts the app with Windows for the current user.

  Backups and settings live in C:\ProgramData\AvellCC and are never deleted by this script.
  Run it from an elevated PowerShell ("Run as administrator") in the repository folder.
#>
[CmdletBinding()]
param(
  [string]$InstallDir = (Join-Path $env:ProgramFiles 'AvellCC'),
  [switch]$KeepVendor,
  [switch]$NoAppStartup
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$serviceName = 'AvellCC'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell (as administrator).' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK not found (winget install Microsoft.DotNet.SDK.8).' }

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. build + tests
Step 'Building and testing'
& dotnet test (Join-Path $repo 'AvellCC.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed: installation aborted.' }

$stage = Join-Path $env:TEMP ('avellcc-publish-' + [guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $repo 'src\AvellCC.Service\AvellCC.Service.csproj') -c Release -o (Join-Path $stage 'Service') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the service.' }
& dotnet publish (Join-Path $repo 'src\AvellCC.App\AvellCC.App.csproj') -c Release -o (Join-Path $stage 'App') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the app.' }
& dotnet publish (Join-Path $repo 'src\AvellCC.Cli\AvellCC.Cli.csproj') -c Release -o (Join-Path $stage 'Cli') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the CLI.' }

# ---------------------------------------------------------------- 2. vendor app
if (-not $KeepVendor) {
  Step 'Stopping Avell Custom Control (Rule 1: one controller at a time)'
  Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force
  $vendor = Get-Service GCUBridge -ErrorAction SilentlyContinue
  if ($vendor) {
    if ($vendor.Status -ne 'Stopped') { Stop-Service GCUBridge -Force }
    Set-Service GCUBridge -StartupType Disabled
    Write-Host '  GCUBridge stopped and disabled. To undo: tools\uninstall.ps1 -RestoreVendor'
  }
}

# ---------------------------------------------------------------- 3. service
Step "Installing to $InstallDir"
$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
  Stop-Service $serviceName -Force     # stopping hands the fans back to the firmware
  (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
Get-Process AvellCC -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($part in 'Service', 'App', 'Cli') {
  $dest = Join-Path $InstallDir $part
  if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
  Copy-Item -Recurse (Join-Path $stage $part) $dest
}
Copy-Item -Recurse -Force (Join-Path $repo 'docs') (Join-Path $InstallDir 'docs')
Remove-Item -Recurse -Force $stage

$exe = Join-Path $InstallDir 'Service\AvellCC.Service.exe'
if (-not $existing) {
  & sc.exe create $serviceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= "AvellCC (fans and keyboard)" | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'sc create failed.' }
} else {
  & sc.exe config $serviceName binPath= "`"$exe`"" start= auto | Out-Null
}
& sc.exe description $serviceName 'Controls the fans (AvellCC curve) and keyboard RGB of Avell/Uniwill laptops. Stopping the service hands the fans back to the firmware.' | Out-Null
# Automatic restart: 2 s, 5 s, 10 s; failure count resets after 1 day.
& sc.exe failure $serviceName reset= 86400 actions= restart/2000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Step 'Starting the service'
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "  Service: $((Get-Service $serviceName).Status)"

# ---------------------------------------------------------------- 4. app
Step 'Shortcuts'
$appExe = Join-Path $InstallDir 'App\AvellCC.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AvellCC.lnk'))
$lnk.TargetPath = $appExe
$lnk.WorkingDirectory = Split-Path $appExe
$lnk.Description = 'AvellCC - fans and keyboard'
$lnk.Save()
Write-Host '  Start Menu: AvellCC'

if (-not $NoAppStartup) {
  Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AvellCC' -Value "`"$appExe`" --minimized"
  Write-Host '  The app starts in the tray when signing in to Windows (turn off in the app under Settings).'
}

Write-Host ''
Write-Host 'Installed. Fans start in firmware mode; pick a curve in the app (Fans tab).' -ForegroundColor Green
Write-Host "Diagnostic CLI: `"$InstallDir\Cli\avellcc.exe`" (stop the service before using commands that write)."
Start-Process $appExe
