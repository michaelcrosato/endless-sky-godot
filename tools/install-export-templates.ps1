<#
.SYNOPSIS
  Downloads and installs Godot export templates matching the engine in use.
.PARAMETER Flavor
  'mono' (.NET/C#, default -- matches godot4-mono) or 'standard' (GDScript-only builds).
.EXAMPLE
  pwsh tools/install-export-templates.ps1
  pwsh tools/install-export-templates.ps1 -Flavor standard -Force
#>
[CmdletBinding()]
param(
  [ValidateSet('mono', 'standard')] [string]$Flavor = 'mono',
  [string]$Version = '4.7.2',
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

$suffix  = if ($Flavor -eq 'mono') { '.mono' } else { '' }
$target  = Join-Path $env:APPDATA "Godot\export_templates\$Version.stable$suffix"
$asset   = if ($Flavor -eq 'mono') { "Godot_v$Version-stable_mono_export_templates.tpz" }
           else                    { "Godot_v$Version-stable_export_templates.tpz" }
$url     = "https://github.com/godotengine/godot/releases/download/$Version-stable/$asset"

if ((Test-Path "$target\version.txt") -and -not $Force) {
  Write-Host "[skip] Export templates already installed at $target (use -Force to reinstall)."
  exit 0
}

# Staging dir is GUID-named so repeated runs never collide.
$stage = Join-Path $env:TEMP "godot-tpl-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force $stage | Out-Null
$tpz = Join-Path $stage $asset

Write-Host "[1/3] Downloading $asset (~1.2 GB)..."
# curl.exe handles multi-GB downloads far better than Invoke-WebRequest.
& curl.exe -L --fail --retry 3 --retry-delay 5 -o $tpz $url
if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $url" }

Write-Host "[2/3] Extracting..."
# A .tpz is a plain zip whose payload lives under templates/
$zip = [IO.Path]::ChangeExtension($tpz, '.zip')
Move-Item $tpz $zip
Expand-Archive -Path $zip -DestinationPath $stage -Force

Write-Host "[3/3] Installing to $target"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$stage\templates\*" $target -Recurse -Force

$installed = (Get-Content "$target\version.txt" -ErrorAction SilentlyContinue)
Write-Host "[ok] Installed export templates: $installed"
