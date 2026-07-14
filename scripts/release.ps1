<#
.SYNOPSIS
  Cut a versioned release of the migration platform (Windows equivalent of release.sh).

.DESCRIPTION
  Bumps VERSION, rolls CHANGELOG's [Unreleased] into a dated section, commits +
  annotated-tags, and builds the api/web Docker images tagged with the version
  and `latest`. Does NOT push git or images automatically — prints the push
  commands so the operator stays in control.

.EXAMPLE
  .\scripts\release.ps1 0.9.1
  .\scripts\release.ps1 -Patch
  $env:REGISTRY = "myacr.azurecr.io"; .\scripts\release.ps1 -Minor -Push

.NOTES
  Registry push needs `az acr login -n <acr>` or `docker login` first.
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)] [string] $Version,
  [switch] $Patch, [switch] $Minor, [switch] $Major,
  [switch] $Push, [switch] $NoBuild, [switch] $AllowDirty, [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$VersionFile = Join-Path $Root 'VERSION'
$Changelog   = Join-Path $Root 'CHANGELOG.md'
$Registry    = $env:REGISTRY

function Info($m) { Write-Host "->  $m" }
function Do($cmd) { if ($DryRun) { Write-Host "   [dry-run] $cmd" } else { Invoke-Expression $cmd } }

# ── Determine new version ────────────────────────────────────────────────────
$bumps = @($Patch, $Minor, $Major | Where-Object { $_ }).Count
if ($Version -and $bumps) { throw "Give either a version OR one of -Patch/-Minor/-Major, not both." }
if (-not $Version -and -not $bumps) { throw "Specify a version (e.g. 0.9.1) or -Patch/-Minor/-Major." }

$current = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '0.9.0' }
if ($Version) {
  $new = $Version
} else {
  $p = $current.Split('.'); $ma = [int]$p[0]; $mi = [int]$p[1]; $pa = [int]$p[2]
  if ($Patch) { $pa++ } elseif ($Minor) { $mi++; $pa = 0 } elseif ($Major) { $ma++; $mi = 0; $pa = 0 }
  $new = "$ma.$mi.$pa"
}
if ($new -notmatch '^\d+\.\d+\.\d+$') { throw "'$new' is not valid semver X.Y.Z" }
$tag = "v$new"
Info "Current: $current   ->   New: $new   (tag $tag)"

# ── Guard rails ──────────────────────────────────────────────────────────────
$branch = (git rev-parse --abbrev-ref HEAD)
if ($branch -ne 'main') { Write-Warning "not on 'main' (on '$branch') - continuing anyway." }
if (-not $AllowDirty -and (git status --porcelain)) { throw "Working tree is dirty. Commit/stash first, or pass -AllowDirty." }
if (git rev-parse $tag 2>$null) { throw "Tag $tag already exists." }

# ── VERSION ──────────────────────────────────────────────────────────────────
Info "Writing VERSION = $new"
if (-not $DryRun) { Set-Content -Path $VersionFile -Value $new -NoNewline; Add-Content -Path $VersionFile -Value "" }

# ── CHANGELOG roll ───────────────────────────────────────────────────────────
if (Test-Path $Changelog) {
  $date = Get-Date -Format 'yyyy-MM-dd'
  Info "Rolling CHANGELOG [Unreleased] -> [$new] - $date"
  if (-not $DryRun) {
    $lines = Get-Content $Changelog
    $out = New-Object System.Collections.Generic.List[string]
    $done = $false
    foreach ($l in $lines) {
      if (-not $done -and $l -match '^## \[Unreleased\]') {
        $out.Add('## [Unreleased]'); $out.Add(''); $out.Add("## [$new] - $date"); $done = $true
      } else { $out.Add($l) }
    }
    Set-Content -Path $Changelog -Value $out
  }
} else { Write-Warning "no CHANGELOG.md - skipping changelog roll." }

# ── Commit + tag ─────────────────────────────────────────────────────────────
Info "Committing + tagging $tag"
Do "git add `"$VersionFile`" `"$Changelog`""
Do "git commit -m 'release: $tag'"
Do "git tag -a $tag -m 'Release $tag'"

# ── Build images ─────────────────────────────────────────────────────────────
function Build-Image($name, $ctx) {
  Info "Building ${name}:$new (+ latest)"
  Do "docker build -t ${name}:$new -t ${name}:latest `"$ctx`""
  if ($Registry) {
    Do "docker tag ${name}:$new $Registry/${name}:$new"
    Do "docker tag ${name}:latest $Registry/${name}:latest"
    if ($Push) {
      Info "Pushing $Registry/${name}:$new (+ latest)"
      Do "docker push $Registry/${name}:$new"
      Do "docker push $Registry/${name}:latest"
    }
  }
}
if (-not $NoBuild) {
  Build-Image 'migration-api' (Join-Path $Root 'apps/api')
  Build-Image 'migration-web' (Join-Path $Root 'apps/web')
} else { Info "Skipping image build (-NoBuild)." }
if ($Push -and -not $Registry) { Write-Warning "-Push ignored: set `$env:REGISTRY and run 'az acr login'/'docker login' first." }

Write-Host ""
Write-Host "OK Release $tag prepared locally."
Write-Host "Next (manual): git push; git push --tags"
if (-not $Registry) { Write-Host "  # images are local only. Set `$env:REGISTRY and -Push to publish." }
