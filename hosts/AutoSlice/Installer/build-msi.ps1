#!/usr/bin/env powershell
# build-msi.ps1 : build AutoSlice self-contained publish dir and produce a single MSI installer.
# Usage:
#   powershell -File build-msi.ps1 [-PublishDir <abs>] [-OutDir <abs>] [-Version 1.0.0]
# Steps: dotnet publish (win-x64 self-contained; engine + embedded python via AfterPublish pack-runtime.ps1)
#      -> gen-components.ps1 (components.wxs) -> wix build (product.wxs + components.wxs) -> AutoSlice-x64.msi
# NOTE: keep this file pure ASCII. PowerShell 5.1 misreads BOM-less UTF-8 as ANSI (DBCS trail byte issue);
#       all Chinese text lives in product.wxs / License.rtf, not here.
param(
    [Parameter(Mandatory = $false)][string]$PublishDir = "",
    [Parameter(Mandatory = $false)][string]$OutDir = "",
    [Parameter(Mandatory = $false)][string]$Version = "1.0.0",
    # Fixed upgrade code (set once for the family; keep for all future versions for MajorUpgrade).
    [Parameter(Mandatory = $false)][string]$UpgradeCode = "{B4E0435F-2E17-4A6D-9E5F-7A1C3D8B2F01}"
)
$ErrorActionPreference = 'Stop'

# ---- dynamic discovery (no hardcoded paths) ----
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir   = Split-Path -Parent $scriptDir                       # ...\hosts\AutoSlice
if (-not $PublishDir) { $PublishDir = Join-Path $projDir 'bin\publish-selfcontained' }
if (-not $OutDir)     { $OutDir = $scriptDir }

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { Write-Error ".NET SDK 8+ is required (dotnet not found)."; exit 1 }

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) { Write-Error "wix tool is required (dotnet tool install --global wix --version 7.*)."; exit 1 }

# ---- 1) publish (AfterPublish target copies engine + packs embedded python runtime) ----
Write-Host "[build-msi] dotnet publish -> $PublishDir"
& $dotnetCmd.Source publish (Join-Path $projDir 'AutoSlice.csproj') `
    -c Release -r win-x64 --self-contained true `
    -o $PublishDir `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed (exit $LASTEXITCODE)"; exit 1 }

foreach ($need in @('AutoSlice.exe', 'downloader.exe', 'runtime\python')) {
    if (-not (Test-Path (Join-Path $PublishDir $need))) { Write-Error "publish dir missing: $need"; exit 1 }
}
Write-Host "[build-msi] publish dir complete (engine downloader.exe + runtime\python present)"

# ---- 2) generate components.wxs (PublishOutput group, perUser HKCU keypaths) ----
$componentsWxs = Join-Path $OutDir 'components.wxs'
Write-Host "[build-msi] gen-components.ps1 -> $componentsWxs"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'gen-components.ps1') `
    -PublishDir $PublishDir -OutFile $componentsWxs
if ($LASTEXITCODE -ne 0) { Write-Error "gen-components.ps1 failed"; exit 1 }

# ---- 3) wix build ----
$licenseRtf = Join-Path $scriptDir 'License.rtf'
$iconPath   = Join-Path $scriptDir 'AppIcon.ico'
if (-not (Test-Path $licenseRtf)) { Write-Error "License.rtf is missing"; exit 1 }
if (-not (Test-Path $iconPath))   { Write-Error "AppIcon.ico is missing (run gen-icon.ps1 first)"; exit 1 }

$outMsi = Join-Path $OutDir ("AutoSlice-" + $Version.Replace('.', '-') + '-x64.msi')
Write-Host "[build-msi] wix build -> $outMsi"
& $wixCmd.Source build `
    -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
    -arch x64 `
    (Join-Path $scriptDir 'product.wxs') $componentsWxs `
    -d "PublishDir=$PublishDir" `
    -d "LicensePath=$licenseRtf" `
    -d "IconPath=$iconPath" `
    -d "UpgradeCode=$UpgradeCode" `
    -o $outMsi
if ($LASTEXITCODE -ne 0) { Write-Error "wix build failed (exit $LASTEXITCODE)"; exit 1 }

$size = (Get-Item $outMsi).Length
Write-Host "[build-msi] done: $outMsi ($([math]::Round($size / 1MB, 1)) MB)"