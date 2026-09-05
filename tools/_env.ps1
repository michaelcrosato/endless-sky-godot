#Requires -Version 7
<#
  Shared setup for every script in tools/. Dot-source it:
      . "$PSScriptRoot/_env.ps1"

  Defines shared paths and engine helpers. Call Initialize-Godot only when an
  engine is needed; data fetching and simulation tests do not require Godot.
#>

$ErrorActionPreference = 'Stop'
$script:ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-GodotBinary {
    <#
    .SYNOPSIS
      Path to a Godot .NET console executable.
    .DESCRIPTION
      Precedence: $env:GODOT_BIN, then the newest Godot .NET build winget has
      installed -- machine scope (C:\Program Files) before user scope, matching
      how Windows resolves them on PATH.

      The *_console.exe variant is required: the plain .exe detaches from the
      terminal on Windows and swallows all stdout, which breaks both test
      runners and CI log capture.
    #>
    if ($env:GODOT_BIN) {
        if (-not (Test-Path -LiteralPath $env:GODOT_BIN -PathType Leaf)) {
            throw "GODOT_BIN does not point to a file: $env:GODOT_BIN"
        }
        return (Resolve-Path -LiteralPath $env:GODOT_BIN).Path
    }

    # These variables are absent on Linux (including CI). Do not pass null to
    # Join-Path before an explicit engine path can be supplied.
    $roots = @()
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) { $roots += Join-Path $base 'WinGet/Packages' }
    }
    if ($env:LOCALAPPDATA) { $roots += Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Packages' }
    $roots = $roots | Where-Object { Test-Path -LiteralPath $_ }

    foreach ($root in $roots) {
        $found = Get-ChildItem (Join-Path $root 'GodotEngine.GodotEngine.Mono_*') -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter 'Godot_v*_mono_win64_console.exe' -ErrorAction SilentlyContinue } |
            Sort-Object { [version]([regex]::Match($_.Name, '\d+\.\d+(?:\.\d+)?').Value) } -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw @'
No Godot .NET binary found.

Install it:      winget install --id GodotEngine.GodotEngine.Mono --exact --scope machine
Or point at one: $env:GODOT_BIN = 'C:\path\to\Godot_v..._mono_win64_console.exe'
'@
}

function Get-GodotVersion {
    param([Parameter(Mandatory)][string]$Binary)
    (& $Binary --version 2>&1 | Select-Object -Last 1).Trim()
}

function Initialize-Godot {
    # gdUnit4's C# adapter reads GODOT_BIN from the environment.
    $script:GodotBin = Resolve-GodotBinary
    $env:GODOT_BIN = $script:GodotBin
}
