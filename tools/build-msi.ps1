<#
.SYNOPSIS
  Builds the AvellCC installer: dist\AvellCC-<version>-x64.msi

.DESCRIPTION
  1. Runs the tests.
  2. Publishes the app and the service self-contained (win-x64, no .NET runtime needed on the target) into one
     folder, and the CLI as a single-file exe in Cli\ (its name, avellcc.exe, would clash with AvellCC.exe).
  3. Builds installer\AvellCC.wxs with the WiX Toolset 5 local tool (.config\dotnet-tools.json).
  Does not need administrator rights. Version = <Version> in src\AvellCC.App\AvellCC.App.csproj.
#>
[CmdletBinding()]
param(
  [switch]$SkipTests,
  # Overrides the csproj version (e.g. to test upgrades between two builds)
  [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repo
function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Invoke-Checked([string]$what, [scriptblock]$block) { & $block; if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." } }

[xml]$csproj = Get-Content (Join-Path $repo 'src\AvellCC.App\AvellCC.App.csproj')
$version = if ($Version) { $Version } else { ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version }
if (-not $version) { throw 'No <Version> in AvellCC.App.csproj.' }

$work = Join-Path $repo 'artifacts\msi'
$stage = Join-Path $work 'stage'
$dist = Join-Path $repo 'dist'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force -Path $stage, $dist | Out-Null

if (-not $SkipTests) {
  Step 'Tests'
  Invoke-Checked 'dotnet test' { dotnet test AvellCC.sln -c Release --nologo -v q }
}

Step "Publishing $version (self-contained, win-x64)"
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-v', 'q', '-p:DebugType=none')
Invoke-Checked 'publish app' { dotnet publish src\AvellCC.App\AvellCC.App.csproj @common -o $stage }
Invoke-Checked 'publish service' { dotnet publish src\AvellCC.Service\AvellCC.Service.csproj @common -o $stage }
Invoke-Checked 'publish cli' {
  dotnet publish src\AvellCC.Cli\AvellCC.Cli.csproj @common -o (Join-Path $stage 'Cli') `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
}
Copy-Item LICENSE (Join-Path $stage 'LICENSE.txt')

Step 'License for the installer dialog (RTF)'
$text = (Get-Content LICENSE -Raw) -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}'
$rtf = '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fmodern Consolas;}}\f0\fs16 ' + ($text -replace "`r?`n", "\par`n") + '}'
$licenseRtf = Join-Path $work 'License.rtf'
[IO.File]::WriteAllText($licenseRtf, $rtf, [Text.Encoding]::ASCII)

Step 'Building the MSI (WiX 5)'
Invoke-Checked 'dotnet tool restore' { dotnet tool restore }
foreach ($ext in 'WixToolset.Util.wixext/5.0.2', 'WixToolset.UI.wixext/5.0.2') {
  Invoke-Checked "wix extension $ext" { dotnet tool run wix extension add -g $ext }
}
$msi = Join-Path $dist "AvellCC-$version-x64.msi"
Invoke-Checked 'wix build' {
  dotnet tool run wix build installer\AvellCC.wxs -arch x64 `
    -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
    -d "Version=$version" -d "SourceDir=$stage" -d "IconFile=$repo\src\AvellCC.App\Assets\AvellCC.ico" -d "LicenseRtf=$licenseRtf" `
    -intermediatefolder (Join-Path $work 'obj') -o $msi
}
Remove-Item (Join-Path $dist "AvellCC-$version-x64.wixpdb") -ErrorAction SilentlyContinue

$size = (Get-Item $msi).Length / 1MB
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash
Set-Content -Path "$msi.sha256" -Value "$hash  $(Split-Path $msi -Leaf)" -Encoding ascii
Write-Host ''
Write-Host ("Built {0} ({1:N1} MB)" -f $msi, $size) -ForegroundColor Green
Write-Host "SHA-256 $hash"
