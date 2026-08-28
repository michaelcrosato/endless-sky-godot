#Requires -Version 7
<#
.SYNOPSIS
  Runs the game.
.PARAMETER Headless
  No window -- boots the main scene, prints its report, exits. Used as a smoke test.
.PARAMETER Scene
  Run a specific scene instead of the configured main scene.
.PARAMETER Frames
  Headless only: quit after this many frames (default 5).
#>
[CmdletBinding()]
param(
    [switch]$Headless,
    [string]$Scene,
    [int]$Frames = 5
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

$godotArgs = @('--path', '.')
if ($Headless) { $godotArgs += @('--headless', '--quit-after', $Frames) }
if ($Scene)    { $godotArgs += $Scene }

Write-Host "[run] $script:GodotBin $($godotArgs -join ' ')"
& $script:GodotBin @godotArgs
exit $LASTEXITCODE
