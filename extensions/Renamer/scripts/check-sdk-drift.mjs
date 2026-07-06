#!/usr/bin/env node
/**
 * Local (NOT CI) drift check: compares the committed vendored
 * `cove-extension-sdk-<version>.tgz` tarball's version against the sibling Cove checkout's
 * frontend SDK version (`<cove>/sdk/frontend/package.json`).
 *
 * Why local-only: CI has no access to the sibling `../cove` checkout, so this can never run
 * there — it always no-ops (exit 0) when the sibling is absent. See lefthook.yml for the wiring.
 *
 * Resolution order for the sibling Cove repo (mirrors update-cove-sdk.ps1):
 *   1. $COVE_REPO env var, if set.
 *   2. `../cove` relative to the MONOREPO ROOT (this repo's own root — the parent of the
 *      `extensions/` subfolder). This script lives at extensions/Renamer/scripts/, so the
 *      monorepo root is three levels up: scripts/ -> Renamer/ -> extensions/ -> repo root.
 *
 * Usage:
 *   node check-sdk-drift.mjs          # drift exits non-zero (fail mode)
 *   node check-sdk-drift.mjs --warn   # drift still prints the mismatch but exits 0 (warn mode)
 */
import { existsSync, readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const WARN_MODE = process.argv.includes("--warn");

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// scripts/ -> extensions/Renamer/scripts/ ; extensionRoot = extensions/Renamer/
const extensionRoot = path.resolve(__dirname, "..");
// monorepoRoot = repo root (parent of the extensions/ subfolder holding Renamer/)
const monorepoRoot = path.resolve(extensionRoot, "..", "..");

console.log(`[check-sdk-drift] extension root: ${extensionRoot}`);
console.log(`[check-sdk-drift] monorepo root:   ${monorepoRoot}`);

// --- 1. Resolve the vendored tarball version from its filename. ---
const vendorDir = path.join(extensionRoot, "src", "Renamer.Ui", "vendor");
const TARBALL_RE = /^cove-extension-sdk-(.+)\.tgz$/;

let vendoredVersion = null;
if (existsSync(vendorDir)) {
  const match = readdirSync(vendorDir)
    .map((name) => ({ name, m: name.match(TARBALL_RE) }))
    .find((entry) => entry.m);
  if (match) vendoredVersion = match.m[1];
}

if (!vendoredVersion) {
  console.error(
    `[check-sdk-drift] FAIL: vendored SDK tarball missing — no cove-extension-sdk-*.tgz found under ${vendorDir}`,
  );
  process.exit(1);
}
console.log(`[check-sdk-drift] vendored tarball version: ${vendoredVersion}`);

// --- 2. Resolve the sibling Cove SDK version. ---
const coveRepo = process.env.COVE_REPO || path.join(monorepoRoot, "..", "cove");
const siblingPackageJson = path.join(coveRepo, "sdk", "frontend", "package.json");

if (!existsSync(siblingPackageJson)) {
  console.log(
    `[check-sdk-drift] skipped: no sibling Cove checkout found at ${siblingPackageJson} (set $COVE_REPO or place a checkout at ${path.join(monorepoRoot, "..", "cove")})`,
  );
  process.exit(0);
}

let siblingVersion;
try {
  const pkg = JSON.parse(readFileSync(siblingPackageJson, "utf8"));
  siblingVersion = pkg.version;
} catch (err) {
  console.error(
    `[check-sdk-drift] FAIL: could not read/parse ${siblingPackageJson}: ${err.message}`,
  );
  process.exit(1);
}

if (!siblingVersion) {
  console.error(
    `[check-sdk-drift] FAIL: ${siblingPackageJson} has no "version" field`,
  );
  process.exit(1);
}
console.log(`[check-sdk-drift] sibling Cove SDK version:  ${siblingVersion}`);

// --- 3. Compare. ---
if (vendoredVersion === siblingVersion) {
  console.log(
    `[check-sdk-drift] OK: vendored tarball (${vendoredVersion}) matches sibling Cove SDK (${siblingVersion})`,
  );
  process.exit(0);
}

const mismatchMsg = `[check-sdk-drift] DRIFT: vendored tarball version (${vendoredVersion}) != sibling Cove SDK version (${siblingVersion}). Run extensions/Renamer/scripts/update-cove-sdk.ps1 to refresh the vendored tarball.`;

if (WARN_MODE) {
  console.warn(mismatchMsg);
  process.exit(0);
}

console.error(mismatchMsg);
process.exit(1);
