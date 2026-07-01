<#
.SYNOPSIS
    Re-pack the @cove/extension-sdk frontend SDK into the committed vendor tarball.

.DESCRIPTION
    The Renamer frontend depends on @cove/extension-sdk, which is not published to npm. It is
    vendored into this extension as a committed tarball at
    src/Renamer.Ui/vendor/cove-extension-sdk-0.1.0.tgz and referenced from
    src/Renamer.Ui/package.json as `file:vendor/cove-extension-sdk-0.1.0.tgz`.
    `npm ci` resolves that tarball offline with an integrity hash, so a clean checkout (and CI) can
    build the frontend with no sibling Cove monorepo present. The vendored SDK is AGPL-3.0, the same
    license as this extension, so vendoring it is license-compatible.

    This script refreshes that tarball from a local Cove checkout when the SDK changes:

      1. Resolve the Cove repo root from -CoveRepo, else $env:COVE_REPO, else ../cove relative
         to the monorepo root (the standard cove-dev layout: i:\cove-dev\extensions\ and
         i:\cove-dev\cove\ are siblings). The SDK package lives at <cove>/sdk/frontend.
      2. Optionally build the SDK (`npm run build`, tsc) so dist/ is current before packing.
      3. `npm pack` the SDK into src/Renamer.Ui/vendor/ (the SDK's `files: ["dist"]` field keeps the
         tarball to dist/ + package.json only).

    The tarball name encodes the SDK version (cove-extension-sdk-<version>.tgz). When the SDK version
    changes, the package.json `file:vendor/...` ref and CONTRIBUTING must be updated to match.

    After running this, the lock must be regenerated and the frontend re-verified:
        cd src/Renamer.Ui
        npm install      # records the new tarball + integrity in package-lock.json
        npm run verify
        npm run build    # rebuilds dist/index.mjs; commit it if it changed

.PARAMETER CoveRepo
    Path to the Cove monorepo root. Overrides $env:COVE_REPO. Defaults to ../cove relative to
    the extensions monorepo root (two levels up from this script).

.PARAMETER Build
    Build the SDK (npm run build in the SDK dir) before packing, so dist/ is fresh.

.NOTES
    Ported from the retired single-repo extensions/rename/scripts/update-cove-sdk.ps1 during the
    multi-extension monorepo migration (v1.3) — the sibling-Cove default path is now ../cove
    relative to the MONOREPO root (extensions/), not ../../cove relative to this extension's own
    folder, since extensions/Renamer/ is one level deeper than the old standalone rename/ repo was.
#>
[CmdletBinding()]
param(
    [string]$CoveRepo,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot = extensions/Renamer/scripts; the monorepo root is two levels up.
$extensionRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$monorepoRoot  = Resolve-Path (Join-Path $extensionRoot '..')
$vendorDir     = Join-Path $extensionRoot 'src/Renamer.Ui/vendor'

if (-not $CoveRepo) { $CoveRepo = $env:COVE_REPO }
if (-not $CoveRepo) { $CoveRepo = Join-Path $monorepoRoot '../cove' }

$coveRoot = Resolve-Path $CoveRepo -ErrorAction SilentlyContinue
if (-not $coveRoot) {
    throw "Cove repo not found at '$CoveRepo'. Pass -CoveRepo <path> or set `$env:COVE_REPO."
}

$sdkDir = Join-Path $coveRoot 'sdk/frontend'
if (-not (Test-Path (Join-Path $sdkDir 'package.json'))) {
    throw "No @cove/extension-sdk package at '$sdkDir' (expected <cove>/sdk/frontend)."
}

if ($Build) {
    Write-Host "Building SDK (tsc) in $sdkDir"
    Push-Location $sdkDir
    try { npm run build } finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $sdkDir 'dist'))) {
    throw "SDK dist/ is missing at '$sdkDir/dist'. Re-run with -Build to compile it first."
}

if (-not (Test-Path $vendorDir)) { New-Item -ItemType Directory -Path $vendorDir | Out-Null }

Write-Host "Packing $sdkDir -> $vendorDir"
Push-Location $sdkDir
try { npm pack --pack-destination $vendorDir } finally { Pop-Location }

Write-Host ''
Write-Host 'Tarball refreshed. Next:'
Write-Host '  cd src/Renamer.Ui'
Write-Host '  npm install      # update package-lock.json (tarball + integrity)'
Write-Host '  npm run verify'
Write-Host '  npm run build    # commit dist/index.mjs if it changed'
