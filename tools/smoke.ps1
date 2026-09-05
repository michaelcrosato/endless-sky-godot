#Requires -Version 7
<#
.SYNOPSIS
  Runs real engine scenarios, requiring both a successful exit and completion.
.DESCRIPTION
  Covers startup, landing, tutorial delivery, save/load, a paid bounty and owned escorts.
  Mission uses a stock combat flagship, reloads during combat and after victory,
  and positions its travel legs; tutorial exercises flight and delivery.
  Save restores the pilot, commodity costs and changed markets through flight and port menus,
  rejects an invalid save without changing the active game, then changes flagship
  and departs without altering its stock outfits. It also reloads overfull port cargo,
  cancels departure, then confirms an excess sale and launch.
  Fleet uses stock ships and a freight fixture: an escort fights, holds while its
  flagship jumps, follows on its own fuel, reloads mid-jump and delivers its cargo.
  Only its landing approaches are positioned. All save checks use temporary slots.
.PARAMETER Frames
  Maximum engine iterations per scenario. Reaching the limit without a PASS fails.
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'startup', 'land', 'tutorial', 'save', 'mission', 'fleet')]
    [string]$Scenario = 'all',
    [ValidateRange(1, 1000000)] [int]$Frames = 20000,
    [switch]$NoBuild
)

. "$PSScriptRoot/_env.ps1"
Initialize-Godot
Set-Location $script:ProjectRoot
if (-not $NoBuild) { & "$PSScriptRoot/build.ps1" }

$scenarios = if ($Scenario -eq 'all') { @('startup', 'land', 'tutorial', 'save', 'mission', 'fleet') } else { @($Scenario) }
foreach ($name in $scenarios) {
    $limit = if ($name -eq 'startup') { [Math]::Min($Frames, 60) } else { $Frames }
    $engineArgs = @('--headless', '--path', '.', '--ignore-error-breaks', '--quit-after', $limit)
    if ($name -ne 'startup') { $engineArgs += @('--', "--$name-smoke") }
    $output = @(& $script:GodotBin @engineArgs 2>&1)
    $engineExit = $LASTEXITCODE
    $output | Write-Output
    $log = $output -join "`n"
    $completed = if ($name -eq 'startup') {
        $log -match '(?m)^\[flight\] data=.+ system=.+' -and
        $log -match '(?m)^\[flight\] exit: simFrames=[1-9][0-9]* '
    } else { $log -match '(?m)^\[smoke\] PASS:' }
    if ($engineExit -ne 0 -or -not $completed -or
        $log -match '(?m)^(SCRIPT )?ERROR:|^\[smoke\] FAIL:') {
        throw "$name smoke failed or did not complete (engine exit $engineExit)."
    }
    Write-Host "[ok] $name smoke completed"
}
