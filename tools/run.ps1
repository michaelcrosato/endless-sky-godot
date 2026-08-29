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
.PARAMETER NoBuild
  Skip the build. Faster, but a stale assembly fails at runtime with a cryptic
  "Cannot instantiate C# script ... class could not be found", so only use it
  when nothing has changed since the last build.
#>
[CmdletBinding()]
param(
    [switch]$Headless,
    # Passed through to the game after `--`, e.g. -UserArgs '--mission-smoke'.
    [string[]]$UserArgs = @(),
    [string]$Scene,
    [int]$Frames = 5,
    [switch]$NoBuild
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

# Godot loads the compiled assembly, not the .cs files. Running against a stale
# one fails as "Cannot instantiate C# script ... class could not be found",
# which reads like a broken scene rather than a missed build.
if (-not $NoBuild) {
    & "$PSScriptRoot/build.ps1" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed; not launching." }
}

$godotArgs = @('--path', '.')
if ($Headless) { $godotArgs += @('--headless', '--quit-after', $Frames) }
if ($Scene)    { $godotArgs += $Scene }
# Everything after `--` reaches the game as OS.GetCmdlineUserArgs().
if ($UserArgs.Count) { $godotArgs += @('--') + $UserArgs }

Write-Host "[run] $script:GodotBin $($godotArgs -join ' ')"
& $script:GodotBin @godotArgs
exit $LASTEXITCODE
