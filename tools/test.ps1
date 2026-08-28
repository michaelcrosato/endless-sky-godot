#Requires -Version 7
<#
.SYNOPSIS
  Runs the test suites. Both by default; exits non-zero if any suite fails.
.PARAMETER Suite
  'all' (default), 'gd' (GDScript via gdUnit4 CLI), or 'cs' (C# via dotnet test).
.PARAMETER Path
  GDScript only: restrict the scan to a directory or single test suite.
.PARAMETER Filter
  C# only: passed through to `dotnet test --filter`.
.EXAMPLE
  pwsh tools/test.ps1
  pwsh tools/test.ps1 -Suite gd -Path tests/gd/health_test.gd
  pwsh tools/test.ps1 -Suite cs -Filter "FullyQualifiedName~Inventory"
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'gd', 'cs')] [string]$Suite = 'all',
    [string]$Path = 'tests/gd',
    [string]$Filter
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

Write-Host "[godot] $(Get-GodotVersion $script:GodotBin)`n"
$failures = [System.Collections.Generic.List[string]]::new()

if ($Suite -in 'all', 'gd') {
    Write-Host '=== GDScript (gdUnit4) ==='
    # --ignoreHeadlessMode: headless Godot does not deliver InputEvents, so gdUnit4
    #   refuses to start without it. Safe here -- no suite drives simulated input.
    # --remote-debug on port 0 is never bound, so the connection is refused instantly;
    #   that keeps a parse error from dropping Godot into its interactive `debug>` prompt.
    & $script:GodotBin --headless --path . -s -d --remote-debug tcp://127.0.0.1:0 `
        res://addons/gdUnit4/bin/GdUnitCmdTool.gd -a $Path --ignoreHeadlessMode
    if ($LASTEXITCODE -ne 0) { $failures.Add("GDScript (exit $LASTEXITCODE)") }
    Write-Host ''
}

if ($Suite -in 'all', 'cs') {
    Write-Host '=== C# (gdUnit4Net / VSTest) ==='
    $dotnetArgs = @('test', 'GdCcT.csproj', '--settings', '.runsettings', '--nologo')
    if ($Filter) { $dotnetArgs += @('--filter', $Filter) }
    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { $failures.Add("C# (exit $LASTEXITCODE)") }
    Write-Host ''
}

if ($failures.Count -gt 0) {
    Write-Host "[FAIL] $($failures -join '; ')" -ForegroundColor Red
    exit 1
}
Write-Host '[ok] all suites passed' -ForegroundColor Green
