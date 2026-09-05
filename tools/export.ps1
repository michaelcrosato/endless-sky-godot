#Requires -Version 7
<#
.SYNOPSIS
  Exports a playable build using a preset from export_presets.cfg.
.PARAMETER Preset
  Preset name as it appears in export_presets.cfg (default 'Windows Desktop').
.PARAMETER Release
  Export optimised/release instead of debug.
.PARAMETER OutputPath
  Override the preset's export path.
.NOTES
  Requires matching export templates:  pwsh tools/install-export-templates.ps1
.EXAMPLE
  pwsh tools/export.ps1
  pwsh tools/export.ps1 -Preset Linux -Release
#>
[CmdletBinding()]
param(
    [string]$Preset = 'Windows Desktop',
    [switch]$Release,
    [string]$OutputPath
)

. "$PSScriptRoot/_env.ps1"
Initialize-Godot
Set-Location $script:ProjectRoot

$version = Get-GodotVersion $script:GodotBin      # e.g. 4.7.2.stable.mono.official.<hash>
if ($version -notmatch '^(?<v>\d+\.\d+(\.\d+)?)') { throw "Could not parse Godot version from '$version'" }

# Templates live in a version+flavour-specific folder; a mismatch fails late and cryptically.
$templateDir = Join-Path $env:APPDATA "Godot\export_templates\$($Matches.v).stable.mono"
if (-not (Test-Path $templateDir)) {
    throw "Export templates missing at $templateDir`nRun: pwsh tools/install-export-templates.ps1"
}

if (-not $OutputPath) {
    $OutputPath = Get-PresetExportPath -Name $Preset
    if (-not $OutputPath) { throw "Preset '$Preset' has no export_path in export_presets.cfg" }
}

# Godot refuses to export into a directory that does not already exist.
$outDir = Split-Path -Parent $OutputPath
if ($outDir) { New-Item -ItemType Directory -Force $outDir | Out-Null }

# The C# assembly must be compiled before packing, or the export ships without it.
& "$PSScriptRoot/build.ps1" -Configuration ($Release ? 'Release' : 'Debug')

$mode = $Release ? '--export-release' : '--export-debug'
Write-Host "[export] $mode '$Preset' -> $OutputPath"
& $script:GodotBin --headless --path . $mode $Preset $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Export failed ($LASTEXITCODE)" }

$artifact = Get-Item $OutputPath -ErrorAction SilentlyContinue
if (-not $artifact -or $artifact.PSIsContainer -or $artifact.Length -eq 0) {
    throw "Export reported success but $OutputPath is missing or empty."
}
Write-Host "[ok] $($artifact.FullName)  ($([math]::Round($artifact.Length/1MB,1)) MB)"
