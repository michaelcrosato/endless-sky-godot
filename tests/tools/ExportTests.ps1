#Requires -Version 7
<# Exercises the real export preflight with a fake engine; no downloads or SDK needed. #>
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = Join-Path $repo "build/export-tests-$([Guid]::NewGuid().ToString('N'))"
$savedEnvironment = @{}
foreach ($name in 'APPDATA', 'XDG_DATA_HOME', 'GODOT_BIN', 'ENDLESS_SKY_EXPORT_TEST_MODE') {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $failure = ''
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match $Pattern) "$Message Actual error: $failure"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'tools') -Force | Out-Null
    foreach ($script in '_env.ps1', 'export.ps1', 'install-export-templates.ps1') {
        Copy-Item -LiteralPath (Join-Path $repo "tools/$script") -Destination (Join-Path $fixture 'tools')
    }
    @'
param([string]$Configuration)
Set-Content -LiteralPath (Join-Path $PSScriptRoot '../build-configuration.txt') -Value $Configuration
'@ | Set-Content -LiteralPath (Join-Path $fixture 'tools/build.ps1')
    @'
if ($args -contains '--version') { Write-Output '9.8.7.stable.mono.official.fixture'; exit 0 }
if ($env:ENDLESS_SKY_EXPORT_TEST_MODE -eq 'fail') { exit 23 }
if ($env:ENDLESS_SKY_EXPORT_TEST_MODE -eq 'missing') { exit 0 }
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($args[-1])
if ($env:ENDLESS_SKY_EXPORT_TEST_MODE -eq 'empty') {
    [IO.File]::WriteAllText($outputPath, '')
} else { Set-Content -LiteralPath $outputPath -Value 'exported game' }
exit 0
'@ | Set-Content -LiteralPath (Join-Path $fixture 'engine.ps1')
    @'
[preset.0]
name="Linux"
export_path="build/linux/game.x86_64"
'@ | Set-Content -LiteralPath (Join-Path $fixture 'export_presets.cfg')

    $env:GODOT_BIN = Join-Path $fixture 'engine.ps1'
    $env:ENDLESS_SKY_EXPORT_TEST_MODE = $null
    $env:APPDATA = $IsWindows ? (Join-Path $fixture 'roaming') : $null
    $env:XDG_DATA_HOME = Join-Path $fixture 'xdg'
    $editorData = $IsWindows ? (Join-Path $env:APPDATA 'Godot') : (Join-Path $env:XDG_DATA_HOME 'godot')
    $templates = Join-Path $editorData 'export_templates/9.8.7.stable.mono'
    $export = Join-Path $fixture 'tools/export.ps1'
    Assert-Fails { & $export -Preset Linux -Release } 'Export templates missing' 'Missing templates produced the wrong diagnostic.'

    # Isolate installed templates under the fixture. These cases run on Windows and
    # Linux; macOS uses a fixed Application Support location, which we never modify.
    if (-not $IsMacOS) {
        New-Item -ItemType Directory -Path $templates -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $templates 'version.txt') -Value '9.8.7.stable.mono'
        & (Join-Path $fixture 'tools/install-export-templates.ps1') -Version '9.8.7'
        Assert-True ($LASTEXITCODE -eq 0) 'The installer did not find the existing templates.'

        & $export -Preset Linux -Release
        $artifact = Join-Path $fixture 'build/linux/game.x86_64'
        Assert-True ((Get-Content -LiteralPath $artifact) -eq 'exported game') 'The real export path did not produce an artifact.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $fixture 'build-configuration.txt')) -eq 'Release') 'Release did not reach the build.'

        & $export -Preset Linux -OutputPath 'build/custom/game.x86_64'
        Assert-True (Test-Path -LiteralPath (Join-Path $fixture 'build/custom/game.x86_64')) 'The output override was ignored.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $fixture 'build-configuration.txt')) -eq 'Debug') 'The default build was not Debug.'
        Assert-Fails { & $export -Preset Unknown } 'no export_path' 'An unknown preset was accepted.'

        foreach ($mode in 'missing', 'empty', 'fail') {
            $env:ENDLESS_SKY_EXPORT_TEST_MODE = $mode
            $pattern = $mode -eq 'fail' ? 'Export failed \(23\)' : 'missing or empty'
            Assert-Fails { & $export -Preset Linux -OutputPath "build/$mode/game.x86_64" } $pattern "An invalid $mode export was accepted."
        }
    }
    Write-Host '[ok] native template paths, installed-template discovery, export arguments and artifact failures'
}
finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    Set-Location $repo
    $resolved = [IO.Path]::GetFullPath($fixture)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
    if ((Split-Path -Parent $resolved) -ne $expectedParent) { throw "Unsafe fixture cleanup: $resolved" }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).LinkType -or
            (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint)) {
            throw "Refusing fixture cleanup through links: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
