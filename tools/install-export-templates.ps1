#Requires -Version 7
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
. "$PSScriptRoot/_env.ps1"

$suffix  = if ($Flavor -eq 'mono') { '.mono' } else { '' }
$target  = Get-GodotTemplateDirectory -Version $Version -Flavor $Flavor
$asset   = if ($Flavor -eq 'mono') { "Godot_v$Version-stable_mono_export_templates.tpz" }
           else                    { "Godot_v$Version-stable_export_templates.tpz" }
$url     = "https://github.com/godotengine/godot/releases/download/$Version-stable/$asset"

if ((Test-Path -LiteralPath (Join-Path $target 'version.txt')) -and -not $Force) {
  Write-Host "[skip] Export templates already installed at $target (use -Force to reinstall)."
  exit 0
}

# Staging dir is GUID-named so repeated runs never collide.
$stage = Join-Path ([IO.Path]::GetTempPath()) "godot-tpl-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force $stage | Out-Null
$tpz = Join-Path $stage $asset

try {
  Write-Host "[1/3] Downloading $asset..."
  # Use the native curl application on each host, never a PowerShell alias.
  $curl = Get-Command ($IsWindows ? 'curl.exe' : 'curl') -CommandType Application | Select-Object -First 1
  & $curl.Source -L --fail --retry 3 --retry-delay 5 -o $tpz $url
  if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $url" }

  Write-Host "[2/3] Extracting..."
  # A .tpz is a plain zip whose payload lives under templates/.
  $zip = [IO.Path]::ChangeExtension($tpz, '.zip')
  Move-Item -LiteralPath $tpz -Destination $zip
  Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
  $payload = Join-Path $stage 'templates'
  $installed = (Get-Content -LiteralPath (Join-Path $payload 'version.txt') -Raw).Trim()
  if ($installed -ne "$Version.stable$suffix") { throw "Unexpected template version: $installed" }

  Write-Host "[3/3] Installing to $target"
  New-Item -ItemType Directory -Force $target | Out-Null
  Get-ChildItem -LiteralPath $payload | Copy-Item -Destination $target -Recurse -Force
  Write-Host "[ok] Installed export templates: $installed"
}
finally {
  # Only the GUID-named directory created by this invocation may be removed.
  $resolved = [IO.Path]::GetFullPath($stage)
  $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
  if ((Split-Path -Parent $resolved) -ne $tempRoot) { throw "Unsafe template cleanup: $resolved" }
  if ((Get-Item -LiteralPath $resolved -Force).LinkType -or
      (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint)) {
    throw "Refusing template cleanup through links: $resolved"
  }
  Remove-Item -LiteralPath $resolved -Recurse -Force
}
