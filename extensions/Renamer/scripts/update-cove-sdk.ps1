<#
.SYNOPSIS
    Re-packs the @cove/extension-sdk frontend SDK into the vendored tarball.

.DESCRIPTION
    @cove/extension-sdk is not on npm. src/Renamer.Ui/package.json references it as
    `file:vendor/cove-extension-sdk-<version>.tgz`, so `npm ci` installs it offline and a clean
    checkout builds with no Cove source present. The SDK is AGPL-3.0, like this extension.

    This packs <cove>/sdk/frontend into src/Renamer.Ui/vendor/. The tarball name carries the SDK
    version, so a version change also needs the `file:vendor/...` reference in package.json updated.
    Afterwards, from src/Renamer.Ui, run `npm install` to record the new tarball and its integrity in
    package-lock.json, then `npm run verify`.

.PARAMETER CoveRepo
    The Cove checkout. Defaults to $env:COVE_REPO, then the ../cove sibling of the monorepo root.

.PARAMETER Build
    Build the SDK before packing, so its dist/ is current.
#>
[CmdletBinding()]
param(
    [string]$CoveRepo,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

$extensionRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$monorepoRoot  = Resolve-Path (Join-Path $extensionRoot '../..')
$vendorDir     = Join-Path $extensionRoot 'src/Renamer.Ui/vendor'

if (-not $CoveRepo) { $CoveRepo = $env:COVE_REPO }
if (-not $CoveRepo) { $CoveRepo = Join-Path $monorepoRoot '../cove' }

$coveRoot = Resolve-Path $CoveRepo -ErrorAction SilentlyContinue
if (-not $coveRoot) {
    throw "Cove repo not found at '$CoveRepo'. Pass -CoveRepo <path> or set `$env:COVE_REPO."
}

$sdkDir = Join-Path $coveRoot 'sdk/frontend'
if (-not (Test-Path (Join-Path $sdkDir 'package.json'))) {
    throw "No @cove/extension-sdk package at '$sdkDir'."
}

Push-Location $sdkDir
try {
    if ($Build) {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "SDK build failed with exit code $LASTEXITCODE." }
    }
    if (-not (Test-Path 'dist')) {
        throw "SDK dist/ is missing at '$sdkDir/dist'. Re-run with -Build."
    }
    if (-not (Test-Path $vendorDir)) { New-Item -ItemType Directory -Path $vendorDir | Out-Null }
    npm pack --pack-destination $vendorDir
    if ($LASTEXITCODE -ne 0) { throw "npm pack failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

Write-Host "Tarball refreshed in $vendorDir. Next, from src/Renamer.Ui: npm install, then npm run verify."
