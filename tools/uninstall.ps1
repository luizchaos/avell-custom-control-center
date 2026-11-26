<#
.SYNOPSIS
  Removes the AvellCC service and app. Stopping the service hands the fans back to the firmware.
  Backups and settings in C:\ProgramData\AvellCC are kept.

.PARAMETER RestoreVendor
  Re-enables and starts the GCUBridge service (Avell Custom Control).
#>
[CmdletBinding()]
param(
  [string]$InstallDir = (Join-Path $env:ProgramFiles 'AvellCC'),
  [switch]$RestoreVendor
)

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell (as administrator).' }

Get-Process AvellCC -ErrorAction SilentlyContinue | Stop-Process -Force

$svc = Get-Service AvellCC -ErrorAction SilentlyContinue
if ($svc) {
  if ($svc.Status -ne 'Stopped') {
    Stop-Service AvellCC -Force
    (Get-Service AvellCC).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
  }
  & sc.exe delete AvellCC | Out-Null
  Write-Host 'AvellCC service removed (fans handed back to the firmware).'
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AvellCC' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AvellCC.lnk') -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
Write-Host "Files removed from $InstallDir. Backups kept in $env:ProgramData\AvellCC."

if ($RestoreVendor) {
  Set-Service GCUBridge -StartupType Automatic
  Start-Service GCUBridge
  Write-Host 'GCUBridge (Avell Custom Control) re-enabled.'
}
