#Requires -Version 7
<#
.SYNOPSIS
  Runs the test suites. All of them by default; exits non-zero if any fails.
.DESCRIPTION
  Two tiers, matching the directive's separation of simulation from rendering:

    sim    NUnit over libs/EndlessSky.{Data,Sim}. Those projects cannot see
           GodotSharp, so these run on the bare .NET host without an engine.
           This is where behavioural parity with upstream is pinned down.

    godot  gdUnit4 over the presentation layer, which needs a real engine
           process. Skipped cleanly while no such suites exist yet.
.PARAMETER Suite
  'all' (default), 'sim', or 'godot'.
.PARAMETER Filter
  Passed through to `dotnet test --filter` (sim) / gdUnit4 `-a` path (godot).
.EXAMPLE
  pwsh tools/test.ps1
  pwsh tools/test.ps1 -Suite sim -Filter "FullyQualifiedName~ShipPhysics"
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'sim', 'godot')] [string]$Suite = 'all',
    [string]$Filter
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

$failures = [System.Collections.Generic.List[string]]::new()

if ($Suite -in 'all', 'sim') {
    Write-Host '=== Simulation + data (NUnit, engine-free) ===' -ForegroundColor Cyan
    # .runsettings carries the guardrails this suite is supposed to run under --
    # notably TreatNoTestsAsError, so a filter that matches nothing fails loudly
    # instead of reporting a green run of zero tests. Nothing passed it to dotnet
    # before, which made the whole file inert configuration.
    $simArgs = @('test', 'tests/sim/EndlessSky.SimTests.csproj', '--nologo',
                 '--settings', "$script:ProjectRoot/.runsettings")
    if ($Filter) { $simArgs += @('--filter', $Filter) }
    dotnet @simArgs
    if ($LASTEXITCODE -ne 0) { $failures.Add("sim (exit $LASTEXITCODE)") }
    Write-Host ''
}

if ($Suite -in 'all', 'godot') {
    Write-Host '=== Presentation (gdUnit4, in-engine) ===' -ForegroundColor Cyan

    # gdUnit4 exits non-zero when it finds nothing to run, which would make a
    # green build look broken while the presentation layer has no suites yet.
    $gdSuites = @(Get-ChildItem 'tests/godot' -Recurse -Include '*_test.gd', '*Test.cs' -EA SilentlyContinue)
    if ($gdSuites.Count -eq 0) {
        Write-Host '[skip] no presentation suites in tests/godot yet.'
        Write-Host ''
    }
    else {
        Initialize-Godot
        Write-Host "[godot] $(Get-GodotVersion $script:GodotBin)"
        # --ignoreHeadlessMode: headless Godot delivers no InputEvents, so gdUnit4
        #   refuses to start without it. Safe unless a suite drives simulated input.
        # Do not enable the interactive local debugger (-d) in automation.
        # Parse failures exit nonzero; error breaks must never wait for input.
        $path = if ($Filter) { $Filter } else { 'tests/godot' }
        & $script:GodotBin --headless --path . --ignore-error-breaks `
            --script res://addons/gdUnit4/bin/GdUnitCmdTool.gd -a $path --ignoreHeadlessMode
        if ($LASTEXITCODE -ne 0) { $failures.Add("godot (exit $LASTEXITCODE)") }
        Write-Host ''
    }
}

if ($failures.Count -gt 0) {
    Write-Host "[FAIL] $($failures -join '; ')" -ForegroundColor Red
    exit 1
}
Write-Host '[ok] all suites passed' -ForegroundColor Green
