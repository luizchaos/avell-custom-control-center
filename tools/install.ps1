<#
.SYNOPSIS
  Instala (ou atualiza) o AvellCC: servico Windows "AvellCC" + app de bandeja.

.DESCRIPTION
  1. Compila e publica o servico e o app em C:\Program Files\AvellCC.
  2. Regra 1 (docs/ARCHITECTURE.md): para o Avell Custom Control e desativa o servico GCUBridge
     (use -KeepVendor para nao mexer nele; o AvellCC ficara bloqueado enquanto o vendor rodar).
  3. Cria o servico AvellCC (LocalSystem, inicio automatico, reinicio automatico em falha).
  4. Cria atalho no Menu Iniciar e (opcional) inicio do app com o Windows para o usuario atual.

  Backups e configuracoes ficam em C:\ProgramData\AvellCC e nunca sao apagados por este script.
  Execute em um PowerShell "Executar como administrador", na pasta do repositorio.
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
if (-not $isAdmin) { throw 'Execute este script em um PowerShell como administrador.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK nao encontrado (winget install Microsoft.DotNet.SDK.8).' }

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. build + testes
Step 'Compilando e testando'
& dotnet test (Join-Path $repo 'AvellCC.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam: instalacao abortada.' }

$stage = Join-Path $env:TEMP ('avellcc-publish-' + [guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $repo 'src\AvellCC.Service\AvellCC.Service.csproj') -c Release -o (Join-Path $stage 'Service') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o servico.' }
& dotnet publish (Join-Path $repo 'src\AvellCC.App\AvellCC.App.csproj') -c Release -o (Join-Path $stage 'App') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o app.' }
& dotnet publish (Join-Path $repo 'src\AvellCC.Cli\AvellCC.Cli.csproj') -c Release -o (Join-Path $stage 'Cli') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar a CLI.' }

# ---------------------------------------------------------------- 2. vendor
if (-not $KeepVendor) {
  Step 'Parando o Avell Custom Control (Regra 1: um controlador por vez)'
  Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force
  $vendor = Get-Service GCUBridge -ErrorAction SilentlyContinue
  if ($vendor) {
    if ($vendor.Status -ne 'Stopped') { Stop-Service GCUBridge -Force }
    Set-Service GCUBridge -StartupType Disabled
    Write-Host '  GCUBridge parado e desativado. Para voltar: tools\uninstall.ps1 -RestoreVendor'
  }
}

# ---------------------------------------------------------------- 3. servico
Step "Instalando em $InstallDir"
$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
  Stop-Service $serviceName -Force     # a parada devolve as ventoinhas ao firmware
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
  & sc.exe create $serviceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= "AvellCC (ventoinhas e teclado)" | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'sc create falhou.' }
} else {
  & sc.exe config $serviceName binPath= "`"$exe`"" start= auto | Out-Null
}
& sc.exe description $serviceName 'Controla as ventoinhas (curva do AvellCC) e o RGB do teclado do Avell. Parar o servico devolve as ventoinhas ao firmware.' | Out-Null
# Reinicio automatico: 2 s, 5 s, 10 s; zera a contagem apos 1 dia.
& sc.exe failure $serviceName reset= 86400 actions= restart/2000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Step 'Iniciando o servico'
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "  Servico: $((Get-Service $serviceName).Status)"

# ---------------------------------------------------------------- 4. app
Step 'Atalhos'
$appExe = Join-Path $InstallDir 'App\AvellCC.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AvellCC.lnk'))
$lnk.TargetPath = $appExe
$lnk.WorkingDirectory = Split-Path $appExe
$lnk.Description = 'AvellCC - ventoinhas e teclado'
$lnk.Save()
Write-Host '  Menu Iniciar: AvellCC'

if (-not $NoAppStartup) {
  Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AvellCC' -Value "`"$appExe`" --minimized"
  Write-Host '  App inicia na bandeja ao entrar no Windows (desative em Configuracoes no app).'
}

Write-Host ''
Write-Host 'Instalado. As ventoinhas comecam no modo firmware; escolha uma curva no app (aba Ventoinhas).' -ForegroundColor Green
Write-Host "CLI de diagnostico: `"$InstallDir\Cli\avellcc.exe`" (pare o servico antes de usar comandos que gravam)."
Start-Process $appExe
