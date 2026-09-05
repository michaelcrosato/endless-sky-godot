#Requires -Version 7
<#
.SYNOPSIS
  Runs save/load and paid-bounty scenarios from a relocated release package.
.DESCRIPTION
  Copies the whole package outside the repository, clears the data override and
  launches from a separate working directory. Requires the bundled dataset,
  a successful exit and an explicit PASS from each scenario. Saves use temporary
  slots; logs remain in reports/. No editor or installed SDK is needed.
.PARAMETER Preset
  The preset whose package was built with tools/package.ps1.
.PARAMETER Frames
  Maximum engine iterations. Exiting before an explicit PASS fails the smoke.
.EXAMPLE
  pwsh tools/smoke-package.ps1 -Preset Linux
#>
[CmdletBinding()]
param(
    [string]$Preset = 'Windows Desktop',
    [ValidateRange(1, 1000000)][int]$Frames = 20000,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 45
)

. "$PSScriptRoot/_env.ps1"
$outputPath = Get-PresetExportPath -Name $Preset
if (-not $outputPath) { throw "Preset '$Preset' has no export_path in export_presets.cfg" }
$original = [IO.Path]::GetFullPath($outputPath, $script:ProjectRoot)
if (-not (Test-Path -LiteralPath $original -PathType Leaf)) {
    throw "Package missing at $original. Run pwsh tools/package.ps1 -Preset '$Preset'."
}

$id = [Guid]::NewGuid().ToString('N')
$tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$workspace = Join-Path $tempRoot "es-package-smoke-$id"
$relocated = Join-Path $workspace 'package'
$working = Join-Path $workspace 'working'
$reports = Join-Path $script:ProjectRoot 'reports'
$previousDataOverride = $env:ENDLESS_SKY_DATA
try {
    New-Item -ItemType Directory -Path $working -Force | Out-Null
    New-Item -ItemType Directory -Path $reports -Force | Out-Null
    Copy-Item -LiteralPath (Split-Path -Parent $original) -Destination $relocated -Recurse
    $executable = Join-Path $relocated (Split-Path -Leaf $original)
    $dataPath = Join-Path $relocated 'universe'
    if (-not (Test-Path -LiteralPath (Join-Path $dataPath 'systems.txt'))) {
        $dataPath = Join-Path $relocated 'external/endless-sky/data'
    }
    $expectedData = '(?m)^\[data\] loaded .+ from ' + [regex]::Escape($dataPath.Replace('\', '/')) + '\r?$'
    $env:ENDLESS_SKY_DATA = $null

    foreach ($scenario in 'save', 'mission') {
        $stdout = Join-Path $reports "package-$scenario-$id.log"
        $stderr = Join-Path $reports "package-$scenario-$id-error.log"
        $launch = @{
            FilePath = $executable
            WorkingDirectory = $working
            ArgumentList = @('--headless', '--quit-after', $Frames, '--', "--$scenario-smoke")
            RedirectStandardOutput = $stdout
            RedirectStandardError = $stderr
            PassThru = $true
        }
        if ($IsWindows) { $launch.WindowStyle = 'Hidden' }
        $process = Start-Process @launch
        try {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "$scenario package smoke timed out. Logs: $stdout, $stderr"
            }
            $process.WaitForExit()
            $log = (Get-Content -LiteralPath $stdout -Raw) + (Get-Content -LiteralPath $stderr -Raw)
            Write-Output $log
            if ($process.ExitCode -ne 0 -or $log -notmatch '(?m)^\[smoke\] PASS:' -or
                $log -match '(?m)^(SCRIPT )?ERROR:|^\[smoke\] FAIL:' -or
                $log.Replace('\', '/') -notmatch $expectedData) {
                throw "$scenario package smoke failed or loaded external data (exit $($process.ExitCode)). Logs: $stdout, $stderr"
            }
            Write-Host "[ok] relocated $Preset package: $scenario"
        }
        finally { $process.Dispose() }
    }
}
finally {
    $env:ENDLESS_SKY_DATA = $previousDataOverride
    $resolved = [IO.Path]::GetFullPath($workspace)
    if ((Split-Path -Parent $resolved) -ne $tempRoot) { throw "Unsafe package smoke cleanup: $resolved" }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).LinkType -or
            (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint)) {
            throw "Refusing package smoke cleanup through links: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
