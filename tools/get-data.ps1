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
  Branch, tag or commit to check out. Defaults to tools/upstream-ref.txt, the
  same reviewed upstream commit used by CI.
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

if (-not $Ref) {
    $Ref = (Get-Content -LiteralPath "$PSScriptRoot/upstream-ref.txt" -Raw).Trim()
    if ($Ref -notmatch '^[0-9a-f]{40}$') { throw 'tools/upstream-ref.txt must contain a full commit SHA.' }
}

$target = Join-Path $script:ProjectRoot 'external/endless-sky'
$repo   = 'https://github.com/endless-sky/endless-sky.git'

if (Test-Path -LiteralPath $target) {
    if (-not $Force) {
        $head = git -C $target rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Existing external/endless-sky is not a checkout; inspect it before using -Force.' }
        $wanted = git -C $target rev-parse --verify "$Ref^{commit}"
        if ($LASTEXITCODE -ne 0 -or $head -ne $wanted) {
            throw "Existing upstream checkout is at $head, not $Ref. Preserve any edits before using -Force."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $target 'data/commodities.txt'))) {
            throw 'Existing upstream checkout is missing its dataset; use -Force to re-clone.'
        }
        Write-Host "[skip] external/endless-sky already present at $head."
        exit 0
    }
    # A forced replacement may also clean up an interrupted clone. Only this
    # exact checkout under the project can be removed; never follow a junction.
    $resolvedTarget = (Get-Item -LiteralPath $target -Force)
    $expected = [IO.Path]::GetFullPath((Join-Path $script:ProjectRoot 'external/endless-sky'))
    $external = Get-Item -LiteralPath (Split-Path -Parent $target) -Force
    if ($resolvedTarget.FullName -ne $expected -or $resolvedTarget.LinkType -or $external.LinkType) {
        throw "Refusing to remove redirected checkout: $target"
    }
    Write-Host '[clean] removing existing checkout'
    Remove-Item -LiteralPath $expected -Recurse -Force
}

New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null

# Fetch a single revision, including when Ref is a commit SHA rather than a
# branch. git clone --branch cannot check out a SHA.
Write-Host "[1/3] Fetching $repo @ $Ref (blobless, shallow)..."
git init --quiet $target
if ($LASTEXITCODE -ne 0) { throw "git init failed ($LASTEXITCODE)" }
git -C $target remote add origin $repo
if ($LASTEXITCODE -ne 0) { throw "git remote add failed ($LASTEXITCODE)" }
git -C $target fetch --depth 1 --filter=blob:none origin $Ref
if ($LASTEXITCODE -ne 0) { throw "git fetch failed ($LASTEXITCODE)" }

Write-Host '[2/3] Narrowing checkout to source/ and data/...'
git -C $target sparse-checkout set --cone source data
if ($LASTEXITCODE -ne 0) { throw "sparse-checkout failed ($LASTEXITCODE)" }

Write-Host "[3/3] Checking out $Ref..."
git -C $target checkout --quiet --detach FETCH_HEAD
if ($LASTEXITCODE -ne 0) { throw "checkout of '$Ref' failed ($LASTEXITCODE)" }

$dataFiles = @(Get-ChildItem (Join-Path $target 'data') -Recurse -Filter '*.txt' -EA SilentlyContinue).Count
$head      = (git -C $target rev-parse --short HEAD)
if ($dataFiles -eq 0) { throw "Checkout produced no data files; expected external/endless-sky/data/**/*.txt" }

Write-Host "[ok] external/endless-sky @ $head -- $dataFiles data files, source/ present: $(Test-Path (Join-Path $target 'source'))"
