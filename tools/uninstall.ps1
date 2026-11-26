<#
.SYNOPSIS
  Remove o servico e o app do AvellCC. Parar o servico devolve as ventoinhas ao firmware.
  Backups e configuracoes em C:\ProgramData\AvellCC sao mantidos.

.PARAMETER RestoreVendor
  Reativa e inicia o servico GCUBridge (Avell Custom Control).
#>
[CmdletBinding()]
param(
  [string]$InstallDir = (Join-Path $env:ProgramFiles 'AvellCC'),
  [switch]$RestoreVendor
)

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Execute este script em um PowerShell como administrador.' }

Get-Process AvellCC -ErrorAction SilentlyContinue | Stop-Process -Force

$svc = Get-Service AvellCC -ErrorAction SilentlyContinue
if ($svc) {
  if ($svc.Status -ne 'Stopped') {
    Stop-Service AvellCC -Force
    (Get-Service AvellCC).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
  }
  & sc.exe delete AvellCC | Out-Null
  Write-Host 'Servico AvellCC removido (ventoinhas devolvidas ao firmware).'
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AvellCC' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AvellCC.lnk') -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
Write-Host "Arquivos removidos de $InstallDir. Backups mantidos em $env:ProgramData\AvellCC."

if ($RestoreVendor) {
  Set-Service GCUBridge -StartupType Automatic
  Start-Service GCUBridge
  Write-Host 'GCUBridge (Avell Custom Control) reativado.'
}
