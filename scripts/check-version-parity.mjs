#!/usr/bin/env node
/**
 * Version-parity gate.
 *
 * The extension declares its version and its minimum host version in more than one place. A registry
 * or release consumer reads extension.json; the loaded assembly advertises the C# source overrides;
 * the frontend package carries its own version. These must agree, or a release ships a manifest that
 * disagrees with the binary (exactly the drift where extension.json was bumped to 0.7.1 but the
 * C# source MinCoveVersion override stayed 0.6.2). This guard reads the real sources and fails CI on
 * any disagreement so the drift cannot merge silently.
 *
 * It reconciles two axes only:
 *   - the artifact `version`, across the manifest / the C# version-source file / the UI package.json
 *     / the optional catalog registry manifest (up to four sources);
 *   - `minCoveVersion`, across the manifest / the C# version-source file / the optional catalog
 *     registry manifest (up to three sources).
 * It deliberately does NOT touch the CHANGELOG — release notes are a separate, prose-owned axis.
 *
 * Generalized (2026-07-01) from the single-extension-repo `check-version-parity.cjs`, which
 * hardcoded src/Rename/... paths resolved relative to the script's own directory. This version
 * accepts three CLI path arguments instead, resolved relative to process.cwd() (the CI/invoker's
 * working directory), so a catalog-driven monorepo workflow can invoke it once per extension entry
 * with that entry's own manifestPath/versionSourcePath, and a path to the UI package.json. An
 * optional fourth argument, the catalog registry manifest path (e.g.
 * com.alextomas955.renamer.json), adds that manifest's `versions[0].version` and
 * `versions[0].minCoveVersion` into the same two axes; when omitted, behavior is unchanged:
 *
 *   node scripts/check-version-parity.mjs <manifestPath> <versionSourcePath> <uiPackageJsonPath> [catalogManifestPath]
 */
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const [, , manifestArg, versionSourceArg, uiPackageJsonArg, catalogManifestArg] = process.argv;

const argCount = process.argv.length - 2;
if (argCount < 3 || argCount > 4) {
  console.error(
    "Usage: node scripts/check-version-parity.mjs <manifestPath> <versionSourcePath> <uiPackageJsonPath> [catalogManifestPath]",
  );
  process.exit(1);
}

function read(argPath) {
  return fs.readFileSync(path.resolve(process.cwd(), argPath), "utf8");
}

// The two C#-source overrides are matched by PROPERTY NAME so the artifact Version and the
// MinCoveVersion (which carry different values) are never confused for one another.
function csharpOverride(source, property) {
  const m = source.match(new RegExp(`${property}\\s*=>\\s*"([^"]+)"`));
  return m ? m[1] : null;
}

const manifest = JSON.parse(read(manifestArg));
const versionSource = read(versionSourceArg);
const uiPkg = JSON.parse(read(uiPackageJsonArg));

const versionSources = [
  { label: "extension.json version", file: manifestArg, value: manifest.version },
  { label: "C# source Version", file: versionSourceArg, value: csharpOverride(versionSource, "Version") },
  { label: "package.json version", file: uiPackageJsonArg, value: uiPkg.version },
];

const minCoveSources = [
  { label: "extension.json minCoveVersion", file: manifestArg, value: manifest.minCoveVersion },
  { label: "C# source MinCoveVersion", file: versionSourceArg, value: csharpOverride(versionSource, "MinCoveVersion") },
];

const failures = [];

// Optional fourth source: the catalog registry manifest (e.g. com.alextomas955.renamer.json).
// It carries a `versions` ARRAY; the release entry under comparison is `versions[0]` (the
// newest/first). Guard the nested access so a missing/empty array is a clean gate failure, not an
// uncaught TypeError.
if (catalogManifestArg) {
  const catalogManifest = JSON.parse(read(catalogManifestArg));
  const latestVersion = Array.isArray(catalogManifest.versions) ? catalogManifest.versions[0] : null;

  if (latestVersion == null) {
    failures.push(`catalog manifest (${catalogManifestArg}) has no versions[0] entry to compare`);
  } else {
    versionSources.push({
      label: "catalog manifest versions[0].version",
      file: catalogManifestArg,
      value: latestVersion.version,
    });
    minCoveSources.push({
      label: "catalog manifest versions[0].minCoveVersion",
      file: catalogManifestArg,
      value: latestVersion.minCoveVersion,
    });
  }
}

function assertAgreement(axis, sources) {
  const expected = sources[0].value;
  for (const s of sources) {
    if (s.value == null) {
      failures.push(`${axis}: could not read ${s.label} (${s.file})`);
    } else if (s.value !== expected) {
      failures.push(
        `${axis} drift: ${s.label} (${s.file}) is "${s.value}", expected "${expected}" (from ${sources[0].label})`,
      );
    }
  }
}

assertAgreement("artifact version", versionSources);
assertAgreement("minCoveVersion", minCoveSources);

if (failures.length > 0) {
  console.error("check-version-parity: FAILED");
  for (const f of failures) {
    console.error(`  ${f}`);
  }
  process.exit(1);
}

console.log(
  `check-version-parity: OK (artifact version ${versionSources[0].value}, minCoveVersion ${minCoveSources[0].value})`,
);
