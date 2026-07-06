// Subprocess-fixture test suite for scripts/validate-extension-repo.mjs (INFRA-03).
//
// This suite does NOT import or refactor the validator. It drives the REAL, unmodified
// validate-extension-repo.mjs as a child process against deliberately-malformed catalog
// fixtures, asserting exit code + stderr text per case. This is the "subprocess, not
// refactor-to-export" pattern from 17-RESEARCH.md Pattern 1: the validator resolves
// `root = path.resolve(import.meta.dirname, "..")`, i.e. relative to wherever the EXECUTING
// file physically lives on disk, not process.cwd(). So each fixture gets its own copy of the
// real validator bytes (copyFileSync'd at run time, never hand-written) inside a
// `<fixture>/scripts/` subfolder, and we spawn THAT copy so its relative-path math resolves
// against the fixture tree instead of the real repo.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const realValidatorPath = path.join(here, "validate-extension-repo.mjs");

// A fully-valid extension.json baseline (mirrors extensions/catalog.json's real Renamer entry
// shape: id matching the catalog entry, semver version, entryDll, url, non-empty lowercase-kebab
// categories). Callers override individual fields to create exactly one malformation.
function validManifest(id, overrides = {}) {
  return {
    id,
    version: "0.1.0",
    url: "https://example.invalid/" + id,
    categories: ["utility"],
    kind: "bundle",
    ...overrides,
  };
}

// A fully-valid catalog entry baseline (mirrors extensions/catalog.json's real Renamer entry:
// name, id, path, tagPrefix ending in "/", manifestPath, projectPath). manifestOnly:true (with
// a valid manifest kind, set in validManifest) avoids needing a real .csproj fixture file for
// every case — the validator skips the projectPath existence check entirely when manifestOnly
// is true (line 179: `if (!isManifestOnly && !fs.existsSync(projectPath))`). Callers override
// individual fields to create exactly one malformation.
function validEntry(id, dirName, overrides = {}) {
  return {
    name: id,
    id,
    path: "extensions/" + dirName,
    tagPrefix: dirName.toLowerCase() + "/",
    manifestPath: "extensions/" + dirName + "/extension.json",
    manifestOnly: true,
    ...overrides,
  };
}

// Builds a temp fixture tree:
//   <root>/scripts/validate-extension-repo.mjs   (real validator bytes, copied at run time)
//   <root>/extensions/catalog.json                (the catalog under test)
//   <root>/Directory.Build.props                  (defaults to "" — no-ops version-floor checks
//                                                    per validator lines 137-146/143)
//   <root>/<relPath> for each [relPath, manifest] in extensionJsonByPath (a real extension.json
//   on disk for each catalog entry that must NOT short-circuit on path-existence — see
//   17-RESEARCH.md Pitfall 4)
function makeFixture({ catalog, buildProps = "", extensionJsonByPath = {} }) {
  const root = mkdtempSync(path.join(tmpdir(), "validate-fixture-"));
  mkdirSync(path.join(root, "scripts"), { recursive: true });
  copyFileSync(realValidatorPath, path.join(root, "scripts", "validate-extension-repo.mjs"));
  mkdirSync(path.join(root, "extensions"), { recursive: true });
  writeFileSync(path.join(root, "extensions", "catalog.json"), JSON.stringify(catalog, null, 2));
  writeFileSync(path.join(root, "Directory.Build.props"), buildProps);
  for (const [relPath, manifest] of Object.entries(extensionJsonByPath)) {
    const full = path.join(root, relPath);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, JSON.stringify(manifest, null, 2));
  }
  return root;
}

function runValidator(fixtureRoot) {
  const result = spawnSync(process.execPath, [path.join(fixtureRoot, "scripts", "validate-extension-repo.mjs")], {
    encoding: "utf8",
  });
  return { status: result.status, stderr: result.stderr, stdout: result.stdout };
}

test("happy path: a fully-valid single-entry catalog exits 0", () => {
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("missing required field (id) produces a non-zero exit and the expected error", () => {
  // Validator line 152: `(entry.id ?? entry.name ?? "catalog entry") + ": missing " + field`.
  // Since id is the field omitted, the label falls back to entry.name ("Foo").
  const entry = validEntry("com.example.foo", "Foo");
  delete entry.id;
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /missing id/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("nonexistent projectPath produces a non-zero exit and the expected error", () => {
  // Validator line 179: `if (!isManifestOnly && !fs.existsSync(projectPath))` — the projectPath
  // check only runs when manifestOnly is NOT true, so this case overrides the validEntry()
  // baseline's manifestOnly:true and supplies a manifest with kind="module" (a non-bundle/
  // scraper-pack kind is irrelevant here since isManifestOnly is false, so the manifestOnly
  // kind-allowlist check at line 189 doesn't apply) plus a real entryDll so no OTHER error
  // fires alongside the one under test. manifestPath stays valid so the check reaches the
  // project-path branch without short-circuiting (Pitfall 4).
  const entry = validEntry("com.example.foo", "Foo", {
    manifestOnly: false,
    projectPath: "extensions/Foo/DoesNotExist.csproj",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { entryDll: "Foo.dll" }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /missing project/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("nonexistent manifestPath produces a non-zero exit and the expected error", () => {
  // Validator line 176: entry.id + ": missing extension.json at " + (entry.manifestPath ?? ...).
  // entry.path (extensionDir) must exist on disk, or the earlier existsSync(extensionDir) check
  // at line 171 short-circuits via `continue` before ever reaching the manifestPath check
  // (17-RESEARCH.md Pitfall 4) — so a real, unrelated placeholder file is planted under
  // extensions/Foo/ to satisfy the extensionDir check, while manifestPath itself stays absent.
  const entry = validEntry("com.example.foo", "Foo", {
    manifestPath: "extensions/Foo/does-not-exist.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/.placeholder": { note: "extensionDir must exist; manifestPath must not" },
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /missing extension\.json at/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("duplicate extension id produces a non-zero exit and the expected error", () => {
  // Validator line 155: entry.id + ": duplicate extension id". Both entries need real,
  // distinct, fully-valid fixture dirs so neither short-circuits via the path-existence
  // `continue` at lines 171-178 (Pitfall 4) before the dedup check on the SECOND entry runs.
  const entryA = validEntry("com.example.dup", "DupA");
  const entryB = validEntry("com.example.dup", "DupB");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entryA, entryB] },
    extensionJsonByPath: {
      "extensions/DupA/extension.json": validManifest("com.example.dup"),
      "extensions/DupB/extension.json": validManifest("com.example.dup"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /duplicate extension id/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("duplicate tagPrefix produces a non-zero exit and the expected error", () => {
  // Validator line 158: entry.id + ": duplicate tagPrefix " + entry.tagPrefix. Distinct ids,
  // shared tagPrefix; both entries fully valid otherwise so the dedup check is isolated.
  const entryA = validEntry("com.example.taga", "TagA", { tagPrefix: "shared/" });
  const entryB = validEntry("com.example.tagb", "TagB", { tagPrefix: "shared/" });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entryA, entryB] },
    extensionJsonByPath: {
      "extensions/TagA/extension.json": validManifest("com.example.taga"),
      "extensions/TagB/extension.json": validManifest("com.example.tagb"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /duplicate tagPrefix/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("non-semver extension.json minCoveVersion produces a non-zero exit and the expected error", () => {
  // Validator only runs the version-floor check (validateVersionFloor, line 78's "must be a
  // semantic version" message) when CoveMinVersion is set in Directory.Build.props (line 143:
  // `if (coveMinVersion) { ... }`). Fixture Directory.Build.props defines CoveMinVersion to
  // activate that path, and CoveSdkVersion/CoveCoreVersion are given valid semver so ONLY the
  // manifest's minCoveVersion (validator line 186:
  // `validateVersionFloor(entry.id, "extension.json minCoveVersion", manifest.minCoveVersion, coveMinVersion)`)
  // trips the "must be a semantic version" branch — isolating the case under test.
  const buildProps = [
    "<Project>",
    "  <PropertyGroup>",
    "    <CoveMinVersion>0.1.0</CoveMinVersion>",
    "    <CoveSdkVersion>0.7.1</CoveSdkVersion>",
    "    <CoveCoreVersion>0.7.1</CoveCoreVersion>",
    "  </PropertyGroup>",
    "</Project>",
    "",
  ].join("\n");
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps,
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { minCoveVersion: "not-a-version" }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /must be a semantic version/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
