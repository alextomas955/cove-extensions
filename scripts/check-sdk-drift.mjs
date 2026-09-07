#!/usr/bin/env node
/**
 * Local (NOT CI) drift check: for EVERY extension in the catalog, compares the committed vendored
 * `cove-extension-sdk-<version>.tgz` against the sibling Cove checkout's frontend SDK version
 * (`<cove>/sdk/frontend/package.json`).
 *
 * The subject list comes from `extensions/catalog.json`'s `uiPath`, so an extension that vendors a
 * tarball is checked by virtue of being registered. A hardcoded subject is how this gate previously
 * inspected one extension while reporting OK for the repo.
 *
 * Why local-only: CI has no access to the sibling `../cove` checkout, so this can never run there —
 * it always no-ops (exit 0) when the sibling is absent. See lefthook.yml for the wiring.
 *
 * Resolution order for the sibling Cove repo (mirrors update-cove-sdk.ps1):
 *   1. $COVE_REPO env var, if set.
 *   2. `../cove` relative to the monorepo root.
 *
 * Usage:
 *   node scripts/check-sdk-drift.mjs          # drift exits non-zero (fail mode)
 *   node scripts/check-sdk-drift.mjs --warn   # drift still prints the mismatch but exits 0
 */
import { existsSync, readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const WARN_MODE = process.argv.includes("--warn");

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const monorepoRoot = path.resolve(__dirname, "..");
const TARBALL_RE = /^cove-extension-sdk-(.+)\.tgz$/;

console.log(`[check-sdk-drift] monorepo root: ${monorepoRoot}`);

// --- 1. Resolve the sibling Cove SDK version once; without it there is nothing to compare against. ---
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
  siblingVersion = JSON.parse(readFileSync(siblingPackageJson, "utf8")).version;
} catch (err) {
  console.error(`[check-sdk-drift] FAIL: could not read/parse ${siblingPackageJson}: ${err.message}`);
  process.exit(1);
}

if (!siblingVersion) {
  console.error(`[check-sdk-drift] FAIL: ${siblingPackageJson} has no "version" field`);
  process.exit(1);
}
console.log(`[check-sdk-drift] sibling Cove SDK version: ${siblingVersion}`);

// --- 2. Every catalog entry that has a UI is a subject. ---
const catalogPath = path.join(monorepoRoot, "extensions", "catalog.json");
let catalog;
try {
  catalog = JSON.parse(readFileSync(catalogPath, "utf8"));
} catch (err) {
  console.error(`[check-sdk-drift] FAIL: could not read/parse ${catalogPath}: ${err.message}`);
  process.exit(1);
}

const subjects = (catalog.extensions ?? catalog).filter((entry) => entry.uiPath);
if (subjects.length === 0) {
  console.error(`[check-sdk-drift] FAIL: no catalog entry declares a uiPath — nothing would be checked`);
  process.exit(1);
}

// --- 3. Compare each subject's vendored tarball. ---
const drifted = [];
for (const entry of subjects) {
  const vendorDir = path.join(monorepoRoot, entry.uiPath, "vendor");
  if (!existsSync(vendorDir)) {
    console.log(`[check-sdk-drift] ${entry.name}: no vendor/ directory — nothing vendored, skipped`);
    continue;
  }

  const match = readdirSync(vendorDir)
    .map((name) => ({ name, m: name.match(TARBALL_RE) }))
    .find((candidate) => candidate.m);

  if (!match) {
    console.error(
      `[check-sdk-drift] FAIL: ${entry.name} has a vendor/ directory but no cove-extension-sdk-*.tgz in ${vendorDir}`,
    );
    process.exit(1);
  }

  const vendoredVersion = match.m[1];
  if (vendoredVersion === siblingVersion) {
    console.log(`[check-sdk-drift] OK: ${entry.name} vendored ${vendoredVersion} matches sibling ${siblingVersion}`);
    continue;
  }

  drifted.push(
    `[check-sdk-drift] DRIFT: ${entry.name} vendored tarball (${vendoredVersion}) != sibling Cove SDK (${siblingVersion}). Refresh it with that extension's update-cove-sdk script.`,
  );
}

if (drifted.length === 0) {
  console.log(`[check-sdk-drift] checked ${subjects.length} extension(s) — no drift`);
  process.exit(0);
}

for (const message of drifted) {
  if (WARN_MODE) {
    console.warn(message);
  } else {
    console.error(message);
  }
}

process.exit(WARN_MODE ? 0 : 1);
