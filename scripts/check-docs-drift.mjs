#!/usr/bin/env node
/**
 * Docs-drift WARN check: for each extension in catalog.json, warns when public-surface source
 * changed but that extension's docs did not (docs/, README.md, CHANGELOG.md, or the site page).
 *
 * Heuristic, so it WARNS only (exit 0) — a source change may legitimately need no docs update.
 * Reminder, not a gate; the PR checklist is where the author confirms intent. Drop --warn to make
 * drift block once the maintainer wants it enforced hard (mirrors check-sdk-drift.mjs).
 *
 * Changed-file source (in order):
 *   1. Explicit paths passed as CLI args after --warn (lefthook passes {staged_files}).
 *   2. Fallback: `git diff --name-only origin/main...HEAD` (CI).
 *   3. If neither yields a list (no git / no base ref), no-op exit 0 (same defensive posture as
 *      check-sdk-drift.mjs when the sibling checkout is absent).
 *
 * Usage:
 *   node scripts/check-docs-drift.mjs --warn <file>...   # warn on drift, exit 0
 *   node scripts/check-docs-drift.mjs <file>...           # drift exits non-zero (block mode)
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import process from "node:process";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const WARN_MODE = process.argv.includes("--warn");

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// scripts/ -> repo root
const repoRoot = path.resolve(__dirname, "..");

// --- 1. Determine changed files. ---
// CLI args after the script name, minus the --warn flag, are the staged-file list.
let changed = process.argv.slice(2).filter((a) => a !== "--warn");

if (changed.length === 0) {
  // Fallback to the PR diff against origin/main (CI). If git or the ref is unavailable, no-op.
  try {
    // Fixed argument vector, no shell — no interpolation of any input.
    const out = execFileSync(
      "git",
      ["diff", "--name-only", "origin/main...HEAD"],
      { cwd: repoRoot, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] },
    );
    changed = out.split("\n").map((s) => s.trim()).filter(Boolean);
  } catch {
    console.log(
      "[check-docs-drift] skipped: no staged files passed and no git diff available (origin/main missing?)",
    );
    process.exit(0);
  }
}

if (changed.length === 0) {
  console.log("[check-docs-drift] OK: no changed files to check");
  process.exit(0);
}

// Normalize to forward-slash, repo-relative paths.
const norm = (p) => p.replace(/\\/g, "/");
changed = changed.map(norm);

// --- 2. Read the catalog. ---
const catalogPath = path.join(repoRoot, "extensions", "catalog.json");
if (!existsSync(catalogPath)) {
  console.log(`[check-docs-drift] skipped: no catalog at ${catalogPath}`);
  process.exit(0);
}
let catalog;
try {
  catalog = JSON.parse(readFileSync(catalogPath, "utf8"));
} catch (err) {
  console.error(`[check-docs-drift] FAIL: could not parse ${catalogPath}: ${err.message}`);
  process.exit(WARN_MODE ? 0 : 1);
}

// --- 3. Per-extension drift heuristic. ---
// Public-surface source globs (NARROW — settings/options/SDK/manifest/UI-settings; per
// DOC-GOVERNANCE.md Q2, keeping it narrow minimizes false-positive fatigue).
const isPublicSurface = (file, extPath) => {
  if (!file.startsWith(extPath + "/src/")) {
    // The manifest is public surface even though it's not under src/.
    return file === `${extPath}/src/Renamer/extension.json` || /\/extension\.json$/.test(file) && file.startsWith(extPath + "/");
  }
  return (
    /Settings[^/]*\.cs$/.test(file) ||
    /Options[^/]*\.cs$/.test(file) ||
    /\.Sdk/.test(file) ||
    /\/src\/.*Settings[^/]*\.tsx$/.test(file) ||
    /\/extension\.json$/.test(file)
  );
};

const idLastSegment = (id) => String(id).split(".").pop();

const isDocsChange = (file, extPath, extId) => {
  const siteFolder = `website/docs/extensions/${idLastSegment(extId)}`;
  return (
    file.startsWith(`${extPath}/docs/`) ||
    file === `${extPath}/README.md` ||
    file === `${extPath}/CHANGELOG.md` ||
    file.startsWith(siteFolder + "/") ||
    file === `${siteFolder}.md`
  );
};

let drift = false;
for (const ext of catalog.extensions || []) {
  const extPath = norm(ext.path);
  const surfaceChanges = changed.filter((f) => isPublicSurface(f, extPath));
  const docsChanges = changed.filter((f) => isDocsChange(f, extPath, ext.id));

  if (surfaceChanges.length > 0 && docsChanges.length === 0) {
    drift = true;
    console.warn(
      `[check-docs-drift] DRIFT: ${ext.name} (${extPath}) — public-surface source changed with no docs change:`,
    );
    for (const f of surfaceChanges) console.warn(`  - ${f}`);
    console.warn(
      `  Update ${extPath}/docs/, its README.md/CHANGELOG.md, or website/docs/extensions/${idLastSegment(ext.id)}/ in the same change (or note "no docs needed" on the PR).`,
    );
  }
}

if (!drift) {
  console.log("[check-docs-drift] OK: no docs drift in the changed files");
  process.exit(0);
}

if (WARN_MODE) {
  console.warn("[check-docs-drift] (warning only — not blocking; drop --warn to enforce)");
  process.exit(0);
}
process.exit(1);
