#Requires -Version 7
<#
.SYNOPSIS
  Builds a self-contained game folder: executable, assemblies, PCK and dataset.
.DESCRIPTION
  The dataset is read from disk with System.IO, not from the PCK. This script
  exports and copies data into a fresh staging folder, then replaces the output
  folder only after both succeed. Removed data and assemblies cannot survive a
  repeated package. A failed build leaves the previous package available.

  Output folders must be below the repository's build/ directory. The entire
  output folder is generated and replaced; keep personal files elsewhere.
.PARAMETER Preset
  Export preset name from export_presets.cfg. Defaults to Windows Desktop.
.PARAMETER DebugBuild
  Export a debug build instead of release.
.PARAMETER OutputPath
  Override the executable's export path, within a dedicated folder under build/.
.EXAMPLE
  pwsh tools/package.ps1
  pwsh tools/package.ps1 -OutputPath build/portable/endless-sky-3d.exe
#>
[CmdletBinding()]
param(
    [string]$Preset = 'Windows Desktop',
    [switch]$DebugBuild,
    [string]$OutputPath
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

function Assert-PackageDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path, $script:ProjectRoot)
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $script:ProjectRoot 'build'))
    $comparison = $IsWindows ? [StringComparison]::OrdinalIgnoreCase : [StringComparison]::Ordinal
    if (-not $full.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "Package output must be a dedicated directory below $buildRoot, not $full."
    }

    # Check the resolved absolute target and each ancestor before any directory
    # move/removal. Never redirect package replacement through a junction/link.
    for ($cursor = $full; ; $cursor = Split-Path -Parent $cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing redirected or non-directory package path: $cursor"
            }
        }
        if ($cursor -eq $script:ProjectRoot) { break }
    }
    if (Test-Path -LiteralPath $full) {
        $links = Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint
        if ($links) { throw "Refusing a package directory containing links: $full" }
    }
    return $full
}

if (-not $OutputPath) { $OutputPath = Get-PresetExportPath -Name $Preset }
if (-not $OutputPath) { throw "Preset '$Preset' has no export_path in export_presets.cfg" }
$exePath = [IO.Path]::GetFullPath($OutputPath, $script:ProjectRoot)
$outDir = Assert-PackageDirectory (Split-Path -Parent $exePath)

$source = Join-Path $script:ProjectRoot 'universe'
$relative = 'universe'
if (-not (Test-Path -LiteralPath (Join-Path $source 'systems.txt') -PathType Leaf)) {
    $source = Join-Path $script:ProjectRoot 'external/endless-sky/data'
    $relative = 'external/endless-sky/data'
}
if (-not (Test-Path -LiteralPath $source -PathType Container) -or
    -not (Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.txt')) {
    throw 'No dataset to package. Run python tools/worldgen/worldgen.py or pwsh tools/get-data.ps1.'
}

$parent = Split-Path -Parent $outDir
$id = [Guid]::NewGuid().ToString('N')
$staging = Assert-PackageDirectory (Join-Path $parent ".package-$id")
$backup = Assert-PackageDirectory (Join-Path $parent ".package-old-$id")
try {
    $exportArgs = @{
        Preset = $Preset
        OutputPath = Join-Path $staging (Split-Path -Leaf $exePath)
        Release = -not $DebugBuild
    }
    & "$PSScriptRoot/export.ps1" @exportArgs

    $dest = Join-Path $staging $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    Copy-Item -LiteralPath $source -Destination $dest -Recurse -Force

    # Recheck after export and copy, immediately before replacing the package.
    $null = Assert-PackageDirectory $outDir
    $null = Assert-PackageDirectory $staging
    $null = Assert-PackageDirectory $backup
    if (Test-Path -LiteralPath $outDir) {
        Move-Item -LiteralPath $outDir -Destination $backup
    }
    try {
        Move-Item -LiteralPath $staging -Destination $outDir
    }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $outDir)) {
            Move-Item -LiteralPath $backup -Destination $outDir
        }
        throw
    }
    if (Test-Path -LiteralPath $backup) {
        $null = Assert-PackageDirectory $backup
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $null = Assert-PackageDirectory $staging
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

$files = @(Get-ChildItem -LiteralPath (Join-Path $outDir $relative) -Recurse -File)
$dataMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$exeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)
Write-Host "[ok] $exePath"
Write-Host "     exe $exeMb MB + dataset $($files.Count) files, $dataMb MB"
Write-Host '     Move the whole folder, including the PCK, assemblies and dataset.'
