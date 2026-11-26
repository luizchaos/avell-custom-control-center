<#
.SYNOPSIS
  Diagnostico SOMENTE LEITURA do Avell A65 LIV (plataforma Uniwill).

.DESCRIPTION
  - Le registradores do EC via WMI (AcpiTest_MULong.GetSetULong) usando APENAS o formato de leitura
    (Data = 0x10000000000 + addr). Este script nao contem nenhuma funcao de escrita no EC.
  - Le a tabela de ventoinha (0xF00-0xF5F) e grava o backup ORIGINAL em
    %ProgramData%\AvellCC\backup\fan-table.original.json SOMENTE se ele ainda nao existir (nunca sobrescreve).
  - Amostra temperaturas (EC 0x430-0x45F x Thermal Zone do Windows x nvidia-smi) para correlacionar enderecos.
  - Lista os dispositivos HID ITE (048D) abrindo-os sem direito de acesso (nenhum comando enviado).
  - Gera um relatorio em <repo>\diag-output\.

  Requer PowerShell como Administrador para a parte do EC. Sem admin, so roda a parte HID.

.PARAMETER Samples
  Numero de amostras de temperatura/RPM (1 por segundo). Padrao: 5.
#>
[CmdletBinding()]
param(
  [int]$Samples = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# ----------------------------------------------------------------------------------------------
# Constantes (ver docs/HARDWARE.md)
# ----------------------------------------------------------------------------------------------
$WmiNamespace   = 'root\WMI'
$WmiObjectPath  = "AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'"
$EcReadFlag     = [UInt64]0x10000000000      # bit 40 = leitura. SEM ele a chamada vira ESCRITA.
$FanTableStart  = 0xF00
$FanTableLength = 0x60                       # 0xF00..0xF5F (6 blocos x 16)
$ModeRegisters  = [ordered]@{
  'FAN_CONTROL_0x751'   = 0x751
  'AP_OEM_BYTE3_0x7A5'  = 0x7A5
  'AP_OEM_BYTE2_0x78C'  = 0x78C
  'MODE_INDEX_0x7AB'    = 0x7AB
  'BIOS_OEM_BYTE2_0x782'= 0x782
}
$IdentityRegisters = [ordered]@{
  'PROJECT_ID_0x740'    = 0x740
  'AP_OEM_BYTE_0x741'   = 0x741
  'SUPPORT_BYTE5_0x742' = 0x742
  'KB_TYPE?_0x73C'      = 0x73C
  'SUPPORT_BYTE1_0x765' = 0x765
  'SUPPORT_BYTE2_0x766' = 0x766
  'STATUS_BYTE_0x768'   = 0x768
  'BIOS_INFO3_0x49F'    = 0x49F
}
$VendorProcesses = 'GCUService', 'GCUBridge', 'ControlCenterU', 'OSDTpDetect'

$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outDir    = Join-Path $repoRoot 'diag-output'
$backupDir = Join-Path $env:ProgramData 'AvellCC\backup'
$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$report = [ordered]@{
  tool      = 'Invoke-AvellDiag.ps1'
  timestamp = (Get-Date).ToString('o')
}

function Write-Section([string]$title) { Write-Host ''; Write-Host "=== $title ===" -ForegroundColor Cyan }
function Hex2([int]$v) { '0x{0:X2}' -f $v }

# ----------------------------------------------------------------------------------------------
# 1. Sistema
# ----------------------------------------------------------------------------------------------
Write-Section 'Sistema'
$cs   = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$report.system = [ordered]@{
  manufacturer = $cs.Manufacturer; model = $cs.Model
  bios = $bios.SMBIOSBIOSVersion; biosVendor = $bios.Manufacturer
  os = (Get-CimInstance Win32_OperatingSystem).Version
  isAdmin = $isAdmin
}
"{0} {1} | BIOS {2} | admin={3}" -f $cs.Manufacturer, $cs.Model, $bios.SMBIOSBIOSVersion, $isAdmin

$vendor = @(Get-Process -Name $VendorProcesses -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name -Unique)
$report.vendorRunning = $vendor
if ($vendor.Count -gt 0) {
  Write-Warning ("Avell Custom Control em execucao: {0}. Leituras sao seguras, mas pare o vendor antes de usar o AvellCC para escrever (docs/ARCHITECTURE.md, Regra 1)." -f ($vendor -join ', '))
}

# ----------------------------------------------------------------------------------------------
# 2. HID ITE (somente enumeracao; abre com acesso 0, nao envia nada)
# ----------------------------------------------------------------------------------------------
Write-Section 'HID ITE (VID 048D)'
if (-not ('AvellDiag.Hid' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace AvellDiag {
public static class Hid {
  [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int Flags; public IntPtr Reserved; }
  [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
  [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS { public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved; public ushort a,b,c,d,e,f,g2,h,i,j; }
  [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
  [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
  [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
  [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
  [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int req, IntPtr dev);
  [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
  public static List<string[]> Enumerate(ushort vid) {
    var res = new List<string[]>();
    Guid g; HidD_GetHidGuid(out g);
    IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
    var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
    for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++) {
      int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
      IntPtr buf = Marshal.AllocHGlobal(req);
      Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
      string path = null;
      if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
      Marshal.FreeHGlobal(buf);
      if (path == null || path.IndexOf("vid_" + vid.ToString("x4"), StringComparison.OrdinalIgnoreCase) < 0) continue;
      using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {   // access = 0: so metadados
        if (h.IsInvalid) continue;
        var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a); HidD_GetAttributes(h, ref a);
        IntPtr p; var c = new HIDP_CAPS();
        if (HidD_GetPreparsedData(h, out p)) { HidP_GetCaps(p, ref c); HidD_FreePreparsedData(p); }
        res.Add(new[] { a.ProductID.ToString("X4"), a.VersionNumber.ToString("X4"), c.UsagePage.ToString("X4"), c.Usage.ToString("X2"),
                        c.InputReportByteLength.ToString(), c.OutputReportByteLength.ToString(), c.FeatureReportByteLength.ToString(), path });
      }
    }
    SetupDiDestroyDeviceInfoList(set);
    return res;
  }
}}
'@
}
$hid = foreach ($r in [AvellDiag.Hid]::Enumerate(0x048D)) {
  $role = switch ($r[2]) { 'FF12' { 'keyboard-4zone' } 'FF03' { 'lightbar/ME_2nd' } 'FF02' { 'ME_1st' } 'FF89' { 'vendor-ctrl' } default { '?' } }
  [pscustomobject]@{ pid = "048D:$($r[0])"; rev = $r[1]; usagePage = "0x$($r[2])"; usage = "0x$($r[3])"; inLen = [int]$r[4]; outLen = [int]$r[5]; featLen = [int]$r[6]; role = $role; path = $r[7] }
}
$report.hid = @($hid)
$hid | Format-Table pid, rev, usagePage, usage, inLen, outLen, featLen, role -AutoSize | Out-String -Width 200 | Write-Host

if (-not $isAdmin) {
  Write-Warning 'Sem privilegios de administrador: leitura do EC pulada. Rode de novo em um PowerShell "Executar como administrador".'
  $file = Join-Path $outDir "avell-diag-$stamp.json"
  $report | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 $file
  Write-Host "Relatorio parcial: $file"
  return
}

# ----------------------------------------------------------------------------------------------
# 3. EC via WMI (SOMENTE LEITURA)
# ----------------------------------------------------------------------------------------------
Write-Section 'EC (WMI AcpiTest_MULong) - somente leitura'
$script:ecObj = New-Object System.Management.ManagementObject($WmiNamespace, $WmiObjectPath, $null)

function Read-EcByte([int]$addr) {
  if ($addr -lt 0 -or $addr -gt 0xFFFF) { throw "Endereco EC invalido: $addr" }
  $in = $script:ecObj.GetMethodParameters('GetSetULong')
  $in['Data'] = [UInt64]($EcReadFlag + [UInt64]$addr)     # formato de LEITURA, nunca montar de outra forma
  $out = $script:ecObj.InvokeMethod('GetSetULong', $in, $null)
  return [int]([UInt64]$out['Return'] -band 0xFF)
}
function Read-EcRange([int]$start, [int]$len) { $b = New-Object byte[] $len; for ($i = 0; $i -lt $len; $i++) { $b[$i] = [byte](Read-EcByte ($start + $i)) }; return ,$b }
function Format-HexDump([byte[]]$bytes, [int]$base) {
  for ($o = 0; $o -lt $bytes.Length; $o += 16) {
    $n = [Math]::Min(16, $bytes.Length - $o)
    $chunk = ($bytes[$o..($o + $n - 1)] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    '{0:X4}: {1}' -f ($base + $o), $chunk
  }
}

# 3a. Sanidade: a mesma leitura repetida deve dar o mesmo valor
$pid1 = Read-EcByte 0x740; $pid2 = Read-EcByte 0x740; $pid3 = Read-EcByte 0x740
if (($pid1 -ne $pid2) -or ($pid2 -ne $pid3)) { Write-Warning "Leituras inconsistentes de 0x740: $pid1 $pid2 $pid3" }

$ident = [ordered]@{}; foreach ($k in $IdentityRegisters.Keys) { $ident[$k] = Hex2 (Read-EcByte $IdentityRegisters[$k]) }
$modes = [ordered]@{}; foreach ($k in $ModeRegisters.Keys) { $modes[$k] = Hex2 (Read-EcByte $ModeRegisters[$k]) }
$report.ecIdentity = $ident
$report.ecModes = $modes
Write-Host 'Identidade:'; $ident.GetEnumerator() | ForEach-Object { '  {0,-22} {1}' -f $_.Key, $_.Value } | Write-Host
Write-Host 'Modos:';      $modes.GetEnumerator() | ForEach-Object { '  {0,-22} {1}' -f $_.Key, $_.Value } | Write-Host

# 3b. Tabela de ventoinha
Write-Section 'Tabela de ventoinha (0xF00-0xF5F)'
$table = Read-EcRange $FanTableStart $FanTableLength
$tableAgain = Read-EcRange $FanTableStart $FanTableLength
$stable = ([Convert]::ToBase64String($table) -eq [Convert]::ToBase64String($tableAgain))
if (-not $stable) { Write-Warning 'A tabela mudou entre duas leituras seguidas (o vendor pode estar escrevendo). O backup original NAO sera criado.' }
Format-HexDump $table $FanTableStart | Write-Host

function ConvertTo-FanLevels([byte[]]$t, [int]$upOff, [int]$downOff, [int]$dutyOff) {
  for ($i = 0; $i -lt 16; $i++) {
    [pscustomobject]@{ level = $i; upRaw = $t[$upOff + $i]; downRaw = $t[$downOff + $i]; dutyRaw = $t[$dutyOff + $i]; dutyPct = [Math]::Round($t[$dutyOff + $i] / 2.0, 1) }
  }
}
$cpuLevels = @(ConvertTo-FanLevels $table 0x00 0x10 0x20)
$gpuLevels = @(ConvertTo-FanLevels $table 0x30 0x40 0x50)
Write-Host 'CPU:'; $cpuLevels | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'GPU:'; $gpuLevels | Format-Table -AutoSize | Out-String | Write-Host

$sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($table)).Replace('-', '')
$snapshot = [ordered]@{
  schema       = 'avellcc.fan-table.v1'
  createdAt    = (Get-Date).ToString('o')
  createdBy    = 'tools/diag/Invoke-AvellDiag.ps1'
  machine      = [ordered]@{ manufacturer = $cs.Manufacturer; model = $cs.Model; bios = $bios.SMBIOSBIOSVersion; projectId = Hex2 $pid1 }
  vendorRunning= $vendor
  baseAddress  = '0x0F00'
  rawHex       = (($table | ForEach-Object { '{0:X2}' -f $_ }) -join '')
  sha256       = $sha
  modeRegisters= $modes
}
$report.fanTable = $snapshot
$report.fanTableStable = $stable

# 3c. Backup ORIGINAL (cria uma unica vez; nunca sobrescreve)
Write-Section 'Backup original'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$originalPath = Join-Path $backupDir 'fan-table.original.json'
if (Test-Path $originalPath) {
  $existing = Get-Content $originalPath -Raw | ConvertFrom-Json
  $same = ($existing.sha256 -eq $sha)
  "Ja existe: $originalPath (criado em $($existing.createdAt)). Nao foi alterado. Igual ao EC atual: $same" | Write-Host
  $report.originalBackup = [ordered]@{ path = $originalPath; created = $false; matchesCurrent = $same }
} elseif (-not $stable) {
  Write-Warning 'Backup original NAO criado porque a tabela nao estava estavel. Pare o vendor e rode de novo.'
  $report.originalBackup = [ordered]@{ path = $originalPath; created = $false; reason = 'unstable' }
} else {
  $json = $snapshot | ConvertTo-Json -Depth 6
  # FileMode.CreateNew: falha se o arquivo aparecer entre o Test-Path e aqui (nunca sobrescreve)
  $fs = [System.IO.File]::Open($originalPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
  try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
  (Get-Item $originalPath).IsReadOnly = $true
  Write-Host "Backup original criado (somente leitura): $originalPath" -ForegroundColor Green
  $report.originalBackup = [ordered]@{ path = $originalPath; created = $true }
}
$snapPath = Join-Path $backupDir "fan-table.$stamp.json"
$snapshot | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 $snapPath
Write-Host "Snapshot desta execucao: $snapPath"

# 3d. Amostras: RPM + candidatos de temperatura x referencias do Windows
Write-Section "Amostras ($Samples x 1 s): RPM e temperaturas"
$nvsmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
$samplesOut = @()
for ($s = 0; $s -lt $Samples; $s++) {
  $fan1 = ((Read-EcByte 0x464) -shl 8) -bor (Read-EcByte 0x465)
  $fan2 = ((Read-EcByte 0x46C) -shl 8) -bor (Read-EcByte 0x46D)   # byte baixo real e 0x46D (0x46B e fixo neste modelo)
  $ecTemps = Read-EcRange 0x430 0x30
  $tz = @(Get-CimInstance Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue |
          ForEach-Object { [pscustomobject]@{ zone = $_.Name; celsius = [Math]::Round($_.HighPrecisionTemperature / 10.0 - 273.15, 1) } })
  $gpu = $null
  if ($nvsmi) { try { $gpu = [int](& $nvsmi.Source --query-gpu=temperature.gpu --format=csv,noheader,nounits | Select-Object -First 1) } catch { } }
  $samplesOut += [pscustomobject]@{
    t = (Get-Date).ToString('HH:mm:ss'); fan1Raw = $fan1; fan2Raw = $fan2
    ec43E = $ecTemps[0x0E]; ec44F = $ecTemps[0x1F]
    ecRange0430 = (($ecTemps | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
    thermalZones = $tz; nvidiaGpuC = $gpu
  }
  '{0}  fan1={1,5}  fan2={2,5}  EC[43E]={3,3}C  EC[44F]={4,3}C  TZ={5}  GPU(nvsmi)={6}' -f `
    $samplesOut[-1].t, $fan1, $fan2, $ecTemps[0x0E], $ecTemps[0x1F], (($tz | ForEach-Object { "$($_.celsius)" }) -join '/'), $gpu | Write-Host
  if ($s -lt $Samples - 1) { Start-Sleep -Seconds 1 }
}
$report.samples = $samplesOut
Write-Host ''
Write-Host 'Faixa EC 0x430-0x45F da ultima amostra (procure bytes ~ temperatura da CPU/GPU):'
Format-HexDump (Read-EcRange 0x430 0x30) 0x430 | Write-Host

# ----------------------------------------------------------------------------------------------
# 4. Relatorio
# ----------------------------------------------------------------------------------------------
$file = Join-Path $outDir "avell-diag-$stamp.json"
$report | ConvertTo-Json -Depth 8 | Out-File -Encoding utf8 $file
Write-Section 'Concluido'
Write-Host "Relatorio: $file"
Write-Host 'Nenhuma escrita foi feita no EC nem nos dispositivos HID.'
