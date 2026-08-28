#Requires -Version 7
<#
  Shared setup for every script in tools/. Dot-source it:
      . "$PSScriptRoot/_env.ps1"

  Resolves a C#-capable Godot binary once so the rest of the tooling never
  hard-codes a version or install path.
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
    if ($env:GODOT_BIN -and (Test-Path $env:GODOT_BIN)) {
        return (Resolve-Path $env:GODOT_BIN).Path
    }

    $roots = @(
        (Join-Path $env:ProgramFiles   'WinGet\Packages')
        (Join-Path ${env:ProgramFiles(x86)} 'WinGet\Packages')
        (Join-Path $env:LOCALAPPDATA   'Microsoft\WinGet\Packages')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $found = Get-ChildItem (Join-Path $root 'GodotEngine.GodotEngine.Mono_*') -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter 'Godot_v*_mono_win64_console.exe' -ErrorAction SilentlyContinue } |
            Sort-Object Name -Descending |
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

# gdUnit4's C# adapter reads GODOT_BIN from the environment, so export it here
# rather than making every caller remember to.
$script:GodotBin = Resolve-GodotBinary
$env:GODOT_BIN = $script:GodotBin
