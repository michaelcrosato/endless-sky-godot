#Requires -Version 7
<#
.SYNOPSIS
  Imports Godot assets and compiles the C# assembly.
.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER Clean
  Wipe .godot/ and obj/ first, forcing a full reimport and rebuild.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$Clean
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

if ($Clean) {
    Write-Host '[clean] removing .godot/ and obj/'
    foreach ($d in '.godot', 'obj', 'bin') {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
}

Write-Host "[godot] $(Get-GodotVersion $script:GodotBin)"
Write-Host '[1/2] Importing assets...'
& $script:GodotBin --headless --path . --import
if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)" }

Write-Host "[2/2] Building C# ($Configuration)..."
dotnet build EndlessSky.csproj --configuration $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

Write-Host '[ok] build complete'
