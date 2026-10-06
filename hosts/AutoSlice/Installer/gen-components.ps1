#!/usr/bin/env powershell
# gen-components.ps1 : generate components.wxs (WiX 4+ syntax, nested Directory tree)
# Usage: powershell -File gen-components.ps1 -PublishDir <abs path> -OutFile components.wxs
# NOTE: keep this file pure ASCII. PowerShell 5.1 misreads BOM-less UTF-8 as ANSI
#       and DBCS trail bytes can swallow ASCII chars that follow CJK text.
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $false)][string]$OutFile = "components.wxs",
    # Component Bitness attribute; x64 packages use "always64",
    # x86 packages should pass empty string "" (no Bitness attribute).
    [Parameter(Mandatory = $false)][string]$Bitness = "always64"
)

$root = $PublishDir.TrimEnd('\')
if (-not (Test-Path $root)) { Write-Error "Publish dir not found: $root"; exit 1 }
# PowerShell 5.1 -File mode cannot pass an empty string argument;
# use "none" as the placeholder for "no Bitness attribute" (x86 package).
if ($Bitness -eq "none") { $Bitness = "" }
$bitAttr = if ($Bitness) { " Bitness=`"$Bitness`"" } else { "" }

function Get-DeterministicGuid([string]$rel) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("AutoSlice:" + $rel.ToLower()))
    $b = New-Object byte[] 16
    [Array]::Copy($hash, $b, 16)
    $b[6] = (($b[6] -band 0x0f) -bor 0x30)
    $b[8] = (($b[8] -band 0x3f) -bor 0x80)
    return ([System.Guid]::new($b)).ToString("B").ToUpperInvariant()
}

$refs = [System.Collections.Generic.List[string]]::new()
$sb = [System.Text.StringBuilder]::new()

function Write-FileComponent([System.IO.FileInfo]$f, [string]$indent) {
    # Explicit File Id based on full relative path, so files with the same
    # basename in different dirs (e.g. __pycache__/*.pyc) stay unique.
    # WiX warns above 72 chars; truncate and append a short hash when needed.
    $rel = $f.FullName.Substring($root.Length + 1)
    $fileId = "f_" + ($rel -replace '[^A-Za-z0-9_]', '_')
    if ($fileId.Length -gt 72) {
        $fileId = $fileId.Substring(0, 60) + "_" + (Get-DeterministicGuid $rel).Substring(1, 8)
    }
    $compId = "C_" + ($rel -replace '[^A-Za-z0-9_]', '_').ToUpper()
    if ($compId.Length -gt 72) {
        $compId = $compId.Substring(0, 60) + "_" + (Get-DeterministicGuid $rel).Substring(1, 8)
    }
    $refs.Add($compId) | Out-Null
    $guid = Get-DeterministicGuid $rel
    $fileLine = "$indent  <File Id=`"$fileId`" Source=`"`$(var.PublishDir)\$rel`" />"
    [void]$sb.AppendLine("$indent<Component Id=`"$compId`" Guid=`"$guid`"$bitAttr>")
    [void]$sb.AppendLine($fileLine)
    # perUser package: components under a user-profile directory must use an
    # HKCU registry key as KeyPath (ICE38), so add one per component.
    [void]$sb.AppendLine("$indent  <RegistryValue Root=`"HKCU`" Key=`"Software\WorkBuddy\AutoSlice\Components\0\$compId`" Name=`"installed`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine("$indent</Component>")
}

# One empty component per directory: RemoveFolder (On=uninstall) so ICE64 is
# satisfied for user-profile directories, plus an HKCU KeyPath (ICE38).
function Write-DirComponent([string]$rel, [string]$dirId, [string]$indent) {
    $compId = "C_D_" + ($rel -replace '[^A-Za-z0-9_]', '_').ToUpper()
    if ($compId.Length -gt 72) {
        $compId = $compId.Substring(0, 60) + "_" + (Get-DeterministicGuid ("dir:" + $rel)).Substring(1, 8)
    }
    $refs.Add($compId) | Out-Null
    $guid = Get-DeterministicGuid ("dir:" + $rel)
    [void]$sb.AppendLine("$indent  <Component Id=`"$compId`" Guid=`"$guid`"$bitAttr>")
    [void]$sb.AppendLine("$indent    <RemoveFolder Id=`"R_$compId`" Directory=`"$dirId`" On=`"uninstall`" />")
    [void]$sb.AppendLine("$indent    <RegistryValue Root=`"HKCU`" Key=`"Software\WorkBuddy\AutoSlice\Components\1\$compId`" Name=`"installed`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine("$indent  </Component>")
}

function Write-Tree([string]$abs, [int]$depth) {
    $indent = '    ' * $depth
    $rel = $abs.Substring($root.Length + 1)
    $dirId = "D_" + ($rel -replace '[^A-Za-z0-9_]', '_')
    if ($dirId.Length -gt 72) {
        $dirId = $dirId.Substring(0, 60) + "_" + (Get-DeterministicGuid $rel).Substring(1, 8)
    }
    $dirLine = "$indent<Directory Id=`"$dirId`" Name=`"$(Split-Path -Leaf $abs)`">"
    [void]$sb.AppendLine($dirLine)
    foreach ($f in Get-ChildItem -Force $abs -File)     { Write-FileComponent $f "$indent  " }
    foreach ($d in Get-ChildItem -Force $abs -Directory){ Write-Tree $d.FullName ($depth + 1) }
    Write-DirComponent $rel $dirId "$indent"
    [void]$sb.AppendLine("$indent</Directory>")
}

[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
# INSTALLFOLDER is declared in product.wxs; here we only mount content under it.
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
foreach ($f in Get-ChildItem -Force $root -File){ Write-FileComponent $f '      ' }
foreach ($d in Get-ChildItem -Force $root -Directory){ Write-Tree $d.FullName 2 }
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('    <ComponentGroup Id="PublishOutput">')
foreach ($r in $refs) { [void]$sb.AppendLine("      <ComponentRef Id=`"$r`" />") }
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

# Set-Content -Encoding UTF8 writes a BOM on PS 5.1; fine for the data file.
$sb.ToString() | Set-Content -Encoding UTF8 $OutFile
Write-Host "Generated $OutFile : $($refs.Count) file components"