<#
.SYNOPSIS
  Builds a double-clickable game folder: exe, assemblies and the dataset it reads.

.DESCRIPTION
  Export alone does not produce a runnable game. The Endless Sky dataset is read
  from disk with System.IO at runtime, not loaded through res://, so it can never
  come out of the .pck however the export is configured — an exported build with
  no data beside it boots to "Endless Sky data not found" and idles.

  This does the whole job: build, export, and place the dataset where EsData
  looks for it, which in an exported build is the executable's own directory.

  The output folder is self-contained and movable. The exe alone is not: it needs
  its sibling assembly folder and external/ next to it.

.PARAMETER Preset
  Export preset name from export_presets.cfg. Defaults to Windows Desktop.

.PARAMETER DebugBuild
  Export a debug build instead of release. Debug builds carry a console wrapper,
  so they print to the terminal; release builds are silent. Named DebugBuild
  rather than Debug because -Debug is one of PowerShell's common parameters.

.EXAMPLE
  pwsh tools/package.ps1
  Builds C:\dev\gd-cc-t\build\windows\endless-sky-3d.exe, ready to double-click.
#>
param(
    [string]$Preset = 'Windows Desktop',
    [switch]$DebugBuild
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

# A hashtable, not an array: array splatting binds positionally, so the switch
# arrives as a stray positional argument and the call fails.
$exportArgs = @{ Preset = $Preset }
if (-not $DebugBuild) { $exportArgs['Release'] = $true }

& "$PSScriptRoot/export.ps1" @exportArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[package] export failed"
    exit 1
}

# Where the export landed, read from the preset rather than assumed.
$presetBlock = Get-Content 'export_presets.cfg' -Raw
$escaped = [regex]::Escape($Preset)
$match = [regex]::Match($presetBlock, "name=`"$escaped`"[\s\S]*?export_path=`"([^`"]+)`"")
if (-not $match.Success) {
    Write-Error "[package] no export_path for preset `"$Preset`" in export_presets.cfg"
    exit 1
}

$exePath = Join-Path $script:ProjectRoot $match.Groups[1].Value
$outDir = Split-Path -Parent $exePath

# The dataset, beside the exe. EsData resolves its data directories relative to
# res://, which in an exported build globalizes to the executable's directory, so
# the game's own universe has to be laid down there under the same name.
$source = Join-Path $script:ProjectRoot 'universe'
$relative = 'universe'
if (-not (Test-Path (Join-Path $source 'systems.txt'))) {
    # Fall back to the upstream reference clone, for a build of the parity content.
    $source = Join-Path $script:ProjectRoot 'external/endless-sky/data'
    $relative = 'external/endless-sky/data'
}

if (-not (Test-Path $source)) {
    Write-Error "[package] no dataset — run python tools/worldgen/worldgen.py"
    exit 1
}

$dest = Join-Path $outDir $relative
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Recurse -Force -Path (Join-Path $source '*') -Destination $dest

$files = Get-ChildItem -Recurse -File $dest
$dataMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$exeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

Write-Host ""
Write-Host "[ok] $exePath"
Write-Host "     exe $exeMb MB + dataset $($files.Count) files, $dataMb MB"
Write-Host "     double-click the exe, or: & `"$exePath`""
Write-Host "     the folder is self-contained; the exe alone is not."
