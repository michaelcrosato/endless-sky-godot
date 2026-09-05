#Requires -Version 7
<# Exercises packaging with a fake exporter; no engine or export templates needed. #>
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = Join-Path $repo "build/package-tests-$([Guid]::NewGuid().ToString('N'))"
$package = Join-Path $fixture 'tools/package.ps1'

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
    New-Item -ItemType Directory -Path (Join-Path $fixture 'universe') -Force | Out-Null
    foreach ($script in '_env.ps1', 'package.ps1') {
        Copy-Item -LiteralPath (Join-Path $repo "tools/$script") -Destination (Join-Path $fixture 'tools')
    }
    @'
param([string]$Preset, [switch]$Release, [string]$OutputPath)
if ($Preset -eq 'Fail') { throw 'Expected export failure' }
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
Set-Content -LiteralPath $OutputPath -Value 'executable'
Set-Content -LiteralPath ([IO.Path]::ChangeExtension($OutputPath, 'pck')) -Value 'resources'
'@ | Set-Content -LiteralPath (Join-Path $fixture 'tools/export.ps1')
    @'
[preset.0]
name="Windows Desktop"
export_path="build/release/game.exe"
[preset.1]
name="Linux"
export_path="build/linux/game.x86_64"
'@ | Set-Content -LiteralPath (Join-Path $fixture 'export_presets.cfg')
    Set-Content -LiteralPath (Join-Path $fixture 'universe/systems.txt') -Value 'original data'

    & $package
    $output = Join-Path $fixture 'build/release'
    Assert-True (Test-Path -LiteralPath "$output/game.exe") 'The executable is missing.'
    Assert-True (Test-Path -LiteralPath "$output/game.pck") 'The resource pack is missing.'
    Assert-True (Test-Path -LiteralPath "$output/universe/systems.txt") 'The dataset is missing.'

    # Old content and assemblies must disappear on the next successful package.
    Set-Content -LiteralPath "$output/universe/obsolete.txt" -Value 'obsolete data'
    Set-Content -LiteralPath "$output/obsolete.dll" -Value 'obsolete assembly'
    New-Item -ItemType Directory -Path "$output/external/endless-sky/data" -Force | Out-Null
    Set-Content -LiteralPath "$output/external/endless-sky/data/obsolete.txt" -Value 'alternate data'
    Set-Content -LiteralPath (Join-Path $fixture 'universe/systems.txt') -Value 'updated data'
    & $package
    Assert-True (-not (Test-Path -LiteralPath "$output/universe/obsolete.txt")) 'Obsolete data survived.'
    Assert-True (-not (Test-Path -LiteralPath "$output/obsolete.dll")) 'Obsolete assemblies survived.'
    Assert-True (-not (Test-Path -LiteralPath "$output/external")) 'An old alternate dataset survived.'
    Assert-True ((Get-Content -LiteralPath "$output/universe/systems.txt") -eq 'updated data') 'Data was not updated.'

    $before = (Get-FileHash -LiteralPath "$output/game.exe").Hash
    Assert-Fails { & $package -Preset Fail -OutputPath "$output/game.exe" } 'Expected export failure' 'A failed export was accepted.'
    Assert-True ((Get-FileHash -LiteralPath "$output/game.exe").Hash -eq $before) 'A failed export damaged the old package.'
    Assert-True (-not (Get-ChildItem -LiteralPath (Join-Path $fixture 'build') -Filter '.package-*' -Force)) 'Staging folders were left behind.'

    # A fallback package must not retain a universe/ directory that shadows it.
    Remove-Item -LiteralPath (Join-Path $fixture 'universe/systems.txt')
    New-Item -ItemType Directory -Path (Join-Path $fixture 'external/endless-sky/data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'external/endless-sky/data/systems.txt') -Value 'upstream data'
    & $package
    Assert-True (-not (Test-Path -LiteralPath "$output/universe")) 'Stale universe shadows the fallback data.'
    Assert-True (Test-Path -LiteralPath "$output/external/endless-sky/data/systems.txt") 'Fallback data is missing.'

    & $package -Preset Linux
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture 'build/linux/game.x86_64')) 'The named preset path was ignored.'

    Assert-Fails { & $package -OutputPath (Join-Path $fixture 'game.exe') } 'dedicated directory below' 'Packaging accepted an output outside build/.'
    Assert-Fails { & $package -OutputPath (Join-Path $fixture 'build/game.exe') } 'dedicated directory below' 'Packaging accepted replacing build/ itself.'
    Assert-Fails { & $package -OutputPath (Join-Path $fixture 'build/../universe/game.exe') } 'dedicated directory below' 'Parent traversal bypassed the output check.'

    $kind = $IsWindows ? 'Junction' : 'SymbolicLink'
    $source = Join-Path $fixture 'external/endless-sky/data'
    $link = Join-Path $fixture 'build/redirected'
    New-Item -ItemType $kind -Path $link -Target $source | Out-Null
    try {
        Assert-Fails { & $package -OutputPath "$link/game.exe" } 'redirected or non-directory' 'A redirected output was accepted.'
        Assert-True (Test-Path -LiteralPath "$source/systems.txt") 'Redirected output damaged source data.'
    }
    finally { Remove-Item -LiteralPath $link -Force }

    $nestedLink = Join-Path $output 'linked-data'
    New-Item -ItemType $kind -Path $nestedLink -Target $source | Out-Null
    try {
        Assert-Fails { & $package } 'containing links' 'An output containing a link was accepted.'
        Assert-True (Test-Path -LiteralPath "$source/systems.txt") 'Nested link damaged source data.'
    }
    finally { Remove-Item -LiteralPath $nestedLink -Force }

    Write-Host '[ok] package creation, replacement, fallback, failure recovery and path guards'
}
finally {
    Set-Location $repo
    # This exact, uniquely created fixture is the only recursive cleanup target.
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
