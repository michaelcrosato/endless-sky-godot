#Requires -Version 7
<#
.SYNOPSIS
  Opens this project in the Godot .NET editor.
.DESCRIPTION
  Launches detached so the terminal stays usable. The editor's GDScript language
  server listens on tcp://127.0.0.1:6005 while it is open.
#>
[CmdletBinding()]
param()

. "$PSScriptRoot/_env.ps1"
Initialize-Godot
Set-Location $script:ProjectRoot

Write-Host "[editor] $(Get-GodotVersion $script:GodotBin)"
Write-Host '[lsp]    GDScript language server: tcp://127.0.0.1:6005 (while the editor is open)'
Start-Process -FilePath $script:GodotBin -ArgumentList @('--editor', '--path', $script:ProjectRoot)
