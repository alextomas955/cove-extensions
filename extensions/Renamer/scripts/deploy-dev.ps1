<#
.SYNOPSIS
    Builds the Renamer extension against the local Cove checkout and installs it into a dev Cove.

.DESCRIPTION
    1. Publishes src/Renamer/Renamer.csproj in Release from local Cove source, so the extension is
       ABI-identical to the dev host. Publish, not build, because Cove.Sdk.targets strips the
       host-provided assemblies from the publish set.
    2. Builds the UI bundle to src/Renamer.Ui/dist/index.mjs, running `npm ci` first when
       node_modules is absent.
    3. Assembles the file set extensions/catalog.json declares into artifacts/package with the shared
       scripts/assemble-package.mjs, the same step CI runs.
    4. Replaces the contents of <COVE_HOME>/extensions/com.alextomas955.renamer with that package.
       COVE_HOME defaults to the per-user local application-data 'cove' folder on Windows and is
       required elsewhere.

    Cove loads extension assemblies once, so restart the host afterwards.

.NOTES
    Run with pwsh. It reads $IsWindows, which Windows PowerShell 5.1 does not define.
#>

[CmdletBinding()]
param(
    # The Cove checkout to build against. Defaults to $env:COVE_REPO, then the ../cove sibling of the
    # monorepo root. Selects the build source only, never the deploy target.
    [string]$CoveRepo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ExtensionId   = 'com.alextomas955.renamer'
$ExtensionRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$MonorepoRoot  = Resolve-Path (Join-Path $ExtensionRoot '../..')
$Csproj        = Join-Path $ExtensionRoot 'src/Renamer/Renamer.csproj'
$PublishDir    = Join-Path $ExtensionRoot 'artifacts/publish'
$PackageDir    = Join-Path $ExtensionRoot 'artifacts/package'
$UiDir         = Join-Path $ExtensionRoot 'src/Renamer.Ui'

if (-not $CoveRepo) {
    $CoveRepo = if ($env:COVE_REPO) { $env:COVE_REPO } else { Join-Path $MonorepoRoot '../cove' }
}
$CoveRepo = [System.IO.Path]::GetFullPath($CoveRepo)

# The deploy step deletes the target's contents, so the target is only ever the declared COVE_HOME or
# the one default known to be Cove's. A guessed path could delete a directory that is not Cove's.
$CoveRoot = if ($env:COVE_HOME) {
    $env:COVE_HOME
} elseif ($IsWindows) {
    Join-Path $env:LOCALAPPDATA 'cove'
} else {
    throw "Set COVE_HOME to your Cove data directory (the folder holding 'extensions') and re-run."
}
$ExtensionsDir = Join-Path $CoveRoot 'extensions'
if (-not (Test-Path $ExtensionsDir)) {
    throw "No extensions folder at $ExtensionsDir. Has this Cove run once? Is COVE_HOME right?"
}
$Target = Join-Path $ExtensionsDir $ExtensionId

Write-Host "==> Publishing against $CoveRepo" -ForegroundColor Cyan
dotnet publish $Csproj -c Release -p:CoveSourceMode=source "-p:CoveRepoRoot=$CoveRepo" -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host "==> Building the UI bundle" -ForegroundColor Cyan
Push-Location $UiDir
try {
    if (-not (Test-Path 'node_modules')) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

Write-Host "==> Assembling the declared package" -ForegroundColor Cyan
# The packer refuses a directory that is not empty. $PackageDir comes from $PSScriptRoot, never from a
# caller, which is what makes the recursive remove safe.
if (Test-Path $PackageDir) { Remove-Item -Recurse -Force $PackageDir }
New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null
$Version = (Get-Content -Raw -Path (Join-Path $ExtensionRoot 'src/Renamer/extension.json') | ConvertFrom-Json).version
node (Join-Path $MonorepoRoot 'scripts/assemble-package.mjs') --publish-dir $PublishDir --package-dir $PackageDir --extension $ExtensionId --version $Version
if ($LASTEXITCODE -ne 0) { throw "Package assemble failed with exit code $LASTEXITCODE." }

Write-Host "==> Installing into $Target" -ForegroundColor Cyan
# Only this extension's folder is cleared, never the host-managed .load-cache beside it.
if (Test-Path $Target) {
    Get-ChildItem -Path $Target -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
}
Copy-Item -Path (Join-Path $PackageDir '*') -Destination $Target -Recurse -Force
Get-ChildItem -Path $Target -File | Sort-Object Name | ForEach-Object { Write-Host "    $($_.Name)" }

Write-Host "`nInstalled. Restart Cove to load it." -ForegroundColor Green
