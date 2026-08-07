<#
.SYNOPSIS
    Build -> frontend-build -> assemble -> deploy -> restart pipeline for the Renamer Cove
    extension (Windows dev loop).

.DESCRIPTION
    Contract (one atomic dev step):

      1. BUILD      Publish src/Renamer/Renamer.csproj in Release using the local Cove source
                    (-p:UseLocalCoveSource=true), so the extension is ABI-identical to the
                    running dev host. Publish (not plain build) because Cove.Sdk.targets strips
                    the host-provided closure from the *publish* set (AfterTargets=ComputeFilesToPublish).

      2. FRONTEND BUILD  Build the src/Renamer.Ui Vite library bundle to dist/index.mjs and
                    assert it exists. `npm install` runs only when node_modules is absent (keeps the
                    dev loop fast). No CSS bundle is shipped (host-Tailwind path).

      2b. ASSEMBLE  Copy the file set extensions/catalog.json declares for this extension into
                    artifacts/package, with the manifest version stamped, via the shared
                    scripts/assemble-package.mjs. That declaration is the one statement of what
                    ships: this script and CI copy the same list, so a dev deploy installs what a
                    release ships. A declared file the build did not produce fails here, before the
                    deploy target is touched.

      3. DEPLOY  Resolve the Cove data root (COVE_HOME if set, else %LOCALAPPDATA%\cove),
                    target the FIXED subdir <root>\extensions\com.alextomas955.renamer (never an
                    arbitrary/caller-supplied path), clean only that subdir's contents (never the
                    sibling host-managed .load-cache), then copy the assembled package in.

      4. RESTART    Detect the process owning port 5073 and attempt a graceful restart. The exact
                    launcher is environment-specific (dotnet run vs InstanceManager); if a reliable
                    automated restart is not possible, print clear manual instructions and exit 0.
                    Loaded DLLs are not hot-reloaded, so a restart is required to pick up the new build.

    Safety controls:
      * The assemble writes nothing unless every declared file resolved, so an incomplete package
                cannot reach the deploy step.
      * The deploy target is a fixed, validated path; no arbitrary destination argument is
                accepted; the clean step touches only the id subdir and never the sibling .load-cache;
                the restart step never kills unrelated processes.

.NOTES
    Location-independent: all paths resolve relative to $PSScriptRoot, so this is CI/GitHub-publishable.
    Property names (UseLocalCoveSource/CoveSdkVersion) must match the monorepo root's
    Directory.Build.props, since that is what selects the local-source build path.
#>

[CmdletBinding()]
param(
    # Local Cove checkout the publish references for the ABI-matched build. Falls back to
    # $env:COVE_REPO, then the conventional ../cove sibling (relative to the monorepo root).
    # Selects the build SOURCE only — the deploy TARGET is always COVE_HOME-resolved (see step 3),
    # never this value.
    [string]$CoveRepo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Fixed, validated identity (never caller-supplied) -----------------------------------------
$ExtensionId   = 'com.alextomas955.renamer'
$CovePort      = 5073

# --- 1. Resolve repo paths (location-independent) ----------------------------------------------
# $PSScriptRoot = extensions/Renamer/scripts; the extension root is one level up, and the
# monorepo root is one level above that extensions/ subfolder (two levels above ExtensionRoot).
$ExtensionRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$MonorepoRoot  = Resolve-Path (Join-Path $ExtensionRoot '../..')
$Csproj        = Join-Path $ExtensionRoot 'src/Renamer/Renamer.csproj'
$PublishDir    = Join-Path $ExtensionRoot 'artifacts/publish'
$PackageDir    = Join-Path $ExtensionRoot 'artifacts/package'
$UiDir         = Join-Path $ExtensionRoot 'src/Renamer.Ui'
$UiBundle      = Join-Path $UiDir 'dist/index.mjs'

if (-not (Test-Path $Csproj)) {
    throw "Cannot find project at $Csproj"
}

# Resolve the local-Cove SOURCE: explicit -CoveRepo, else $env:COVE_REPO, else the ../cove sibling
# relative to the MONOREPO ROOT (not this extension's own folder) — matching the root
# Directory.Build.props' own auto-detect convention.
if (-not $CoveRepo) {
    $CoveRepo = if ($env:COVE_REPO) { $env:COVE_REPO } else { Join-Path $MonorepoRoot '../cove' }
}
$CoveRepo = [System.IO.Path]::GetFullPath($CoveRepo)

Write-Host "==> Renamer deploy pipeline" -ForegroundColor Cyan
Write-Host "    Extension root : $ExtensionRoot"
Write-Host "    Project        : $Csproj"
Write-Host "    Publish        : $PublishDir"
Write-Host "    Cove repo      : $CoveRepo (build source)"

# --- 2. BUILD (publish, local source) ----------------------------------------------------------
# Best-effort pre-clean: `dotnet publish` overwrites/refreshes stale output on its own, so a
# leftover directory handle (e.g. an editor/watcher with the folder open) is not fatal here — only
# the assemble below (which reads the fresh publish output) needs to actually succeed.
if (Test-Path $PublishDir) {
    try {
        Remove-Item -Recurse -Force $PublishDir -ErrorAction Stop
        New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
    } catch {
        Write-Host "    Could not remove $PublishDir (likely a lingering directory handle from an editor/watcher) — continuing, 'dotnet publish' will refresh its contents in place." -ForegroundColor Yellow
    }
} else {
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
}

Write-Host "`n==> Publishing (Release, local Cove source)…" -ForegroundColor Cyan
dotnet publish $Csproj -c Release -p:UseLocalCoveSource=true "-p:COVE_REPO=$CoveRepo" -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE — deploy aborted."
}

# --- 3. FRONTEND BUILD — build the Renamer.Ui bundle -------------------------------------------
# index.mjs is the panel ESM bundle the manifest's jsBundle field points at, and one ordinary row of
# the catalog's declared artifacts — the assemble below resolves it out of dist/, so it needs no
# copy of its own.
$UiPackageJson = Join-Path $UiDir 'package.json'
if (Test-Path $UiPackageJson) {
    Write-Host "`n==> Building frontend bundle (src/Renamer.Ui)…" -ForegroundColor Cyan
    Push-Location $UiDir
    try {
        if (-not (Test-Path (Join-Path $UiDir 'node_modules'))) {
            Write-Host "    node_modules absent — running 'npm install'…" -ForegroundColor Yellow
            npm install
            if ($LASTEXITCODE -ne 0) {
                throw "npm install failed in $UiDir with exit code $LASTEXITCODE — deploy aborted."
            }
        } else {
            Write-Host "    node_modules present — skipping 'npm install' (keep the dev loop fast)."
        }

        Write-Host "    Running 'npm run build'…"
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed in $UiDir with exit code $LASTEXITCODE — deploy aborted."
        }
    } finally {
        Pop-Location
    }

    if (-not (Test-Path $UiBundle)) {
        throw "Frontend build completed but $UiBundle is missing — the panel would 404. Refusing to deploy. " +
              "Check src/Renamer.Ui/vite.config.ts (lib mode, fileName()=>'index.mjs')."
    }
    Write-Host "    PASS — bundle built: $UiBundle ($([math]::Round((Get-Item $UiBundle).Length / 1KB, 1)) KB)" -ForegroundColor Green
} else {
    throw "Expected $UiPackageJson but it is missing — cannot build the frontend bundle."
}

# --- 3b. ASSEMBLE — the declared package set ----------------------------------------------------
# extensions/catalog.json's artifacts array is the one declaration of what ships, and this shared
# script is what CI runs too, so the dev deploy installs the same list a release does. The version
# comes from the source manifest because the packer defaults no argument.
$SourceManifest = Join-Path $ExtensionRoot 'src/Renamer/extension.json'
$Version = (Get-Content -Raw -Path $SourceManifest | ConvertFrom-Json).version

Write-Host "`n==> Assembling the declared package (extensions/catalog.json)…" -ForegroundColor Cyan
node (Join-Path $MonorepoRoot 'scripts/assemble-package.mjs') --publish-dir $PublishDir --package-dir $PackageDir --extension $ExtensionId --version $Version
if ($LASTEXITCODE -ne 0) {
    throw "Package assemble failed with exit code $LASTEXITCODE — deploy aborted."
}

# --- 4. DEPLOY ---------------------------------------------------------------------------------
$CoveRoot = if ($env:COVE_HOME) { $env:COVE_HOME } else { Join-Path $env:LOCALAPPDATA 'cove' }
$ExtensionsDir = Join-Path $CoveRoot 'extensions'
$Target = Join-Path $ExtensionsDir $ExtensionId

Write-Host "`n==> Deploying to $Target" -ForegroundColor Cyan
Write-Host "    Cove root : $CoveRoot ($(if ($env:COVE_HOME) { 'COVE_HOME' } else { '%LOCALAPPDATA%\cove fallback' }))"

if (-not (Test-Path $ExtensionsDir)) {
    throw "Cove extensions dir not found at $ExtensionsDir — is Cove installed / has it run once? " +
          "(Set COVE_HOME if your instance uses a non-default data dir.)"
}

# Clean ONLY this extension's subdir (never the sibling host-managed .load-cache).
if (Test-Path $Target) {
    Get-ChildItem -Path $Target -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
}

Copy-Item -Path (Join-Path $PackageDir '*') -Destination $Target -Recurse -Force

Write-Host "    Deployed files:" -ForegroundColor Green
Get-ChildItem -Path $Target -File | Sort-Object Name | ForEach-Object {
    Write-Host "      $($_.Name)"
}

# --- 5. RESTART (best-effort; manual fallback) -------------------------------------------------
Write-Host "`n==> Restart Cove backend (no hot-reload — required to pick up the new DLL)…" -ForegroundColor Cyan

$conn = $null
try {
    $conn = Get-NetTCPConnection -LocalPort $CovePort -State Listen -ErrorAction Stop | Select-Object -First 1
} catch {
    $conn = $null
}

if ($null -eq $conn) {
    Write-Host "    No process is listening on port $CovePort." -ForegroundColor Yellow
    Write-Host "    The Cove dev backend does not appear to be running. Start your Cove dev host;" -ForegroundColor Yellow
    Write-Host "    on next startup it will discover the freshly deployed extension." -ForegroundColor Yellow
    exit 0
}

$proc = $null
try { $proc = Get-Process -Id $conn.OwningProcess -ErrorAction Stop } catch { $proc = $null }

if ($null -ne $proc) {
    Write-Host "    Found PID $($proc.Id) ($($proc.ProcessName)) listening on $CovePort." -ForegroundColor Yellow
}
Write-Host "    Automated graceful restart of the Cove host is environment-specific and is intentionally" -ForegroundColor Yellow
Write-Host "    NOT forced here (we never kill a process we cannot cleanly identify as the Cove dev backend)." -ForegroundColor Yellow
Write-Host "    ACTION REQUIRED: restart your Cove dev host (the process on port $CovePort)," -ForegroundColor Yellow
Write-Host "    then confirm the extension loaded:" -ForegroundColor Yellow
Write-Host "      curl -s http://localhost:$CovePort/api/extensions | findstr $ExtensionId" -ForegroundColor Yellow
Write-Host "      (expect enabled:true; check %LOCALAPPDATA%\cove\logs\cove-YYYYMMDD.log for '... initialized')" -ForegroundColor Yellow

exit 0
