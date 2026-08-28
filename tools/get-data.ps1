#Requires -Version 7
<#
.SYNOPSIS
  Fetches the Endless Sky upstream reference into external/endless-sky.
.DESCRIPTION
  Upstream is the source of truth for behaviour and content, but it is a ~600 MB
  repository and external/ is gitignored, so a fresh clone of this project has no
  copy. This pulls only what we actually read -- `source/` (behavioural reference)
  and `data/` (content) -- via a blobless sparse checkout, which keeps it to a
  fraction of the full history and working tree.

  Safe to re-run: an existing checkout is left alone unless -Force is passed.
.PARAMETER Ref
  Branch, tag or commit to check out. Defaults to the upstream default branch.
.PARAMETER Force
  Discard and re-clone an existing external/endless-sky.
.EXAMPLE
  pwsh tools/get-data.ps1
  pwsh tools/get-data.ps1 -Ref v0.10.14
  pwsh tools/get-data.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$Ref,
    [switch]$Force
)

. "$PSScriptRoot/_env.ps1"
Set-Location $script:ProjectRoot

$target = Join-Path $script:ProjectRoot 'external/endless-sky'
$repo   = 'https://github.com/endless-sky/endless-sky.git'

if (Test-Path (Join-Path $target 'data')) {
    if (-not $Force) {
        $head = (git -C $target rev-parse --short HEAD 2>$null)
        Write-Host "[skip] external/endless-sky already present at $head (use -Force to re-clone)."
        exit 0
    }
    Write-Host '[clean] removing existing checkout'
    Remove-Item $target -Recurse -Force
}

New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null

# --filter=blob:none fetches file contents lazily, --sparse limits the working
# tree; together they turn a ~600 MB clone into a small one.
Write-Host "[1/3] Cloning $repo (blobless, sparse)..."
$cloneArgs = @('clone', '--filter=blob:none', '--sparse', $repo, $target)
if (-not $Ref) { $cloneArgs = @('clone', '--depth', '1', '--filter=blob:none', '--sparse', $repo, $target) }
git @cloneArgs
if ($LASTEXITCODE -ne 0) { throw "git clone failed ($LASTEXITCODE)" }

Write-Host '[2/3] Narrowing checkout to source/ and data/...'
git -C $target sparse-checkout set source data
if ($LASTEXITCODE -ne 0) { throw "sparse-checkout failed ($LASTEXITCODE)" }

if ($Ref) {
    Write-Host "[3/3] Checking out $Ref..."
    git -C $target checkout --quiet $Ref
    if ($LASTEXITCODE -ne 0) { throw "checkout of '$Ref' failed ($LASTEXITCODE)" }
}
else {
    Write-Host '[3/3] Staying on the default branch tip.'
}

$dataFiles = @(Get-ChildItem (Join-Path $target 'data') -Recurse -Filter '*.txt' -EA SilentlyContinue).Count
$head      = (git -C $target rev-parse --short HEAD)
if ($dataFiles -eq 0) { throw "Checkout produced no data files; expected external/endless-sky/data/**/*.txt" }

Write-Host "[ok] external/endless-sky @ $head -- $dataFiles data files, source/ present: $(Test-Path (Join-Path $target 'source'))"
