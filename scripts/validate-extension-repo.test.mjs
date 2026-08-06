// Subprocess-fixture test suite for scripts/validate-extension-repo.mjs.
//
// This suite does NOT import or refactor the validator. It drives the REAL, unmodified
// validate-extension-repo.mjs as a child process against deliberately-malformed catalog
// fixtures, asserting exit code + output text per case. Driving it as a subprocess rather
// than refactoring it to export a function is forced by the subject: it resolves
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
// is true. Callers override individual fields to create exactly one malformation.
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

// The floor-bearing props shape, written in the ATTRIBUTED form the real Directory.Build.props
// uses. That form is what exercises readMsBuildProperties' optional-attribute branch; a fixture
// using the bare `<CoveMinVersion>1.1.0</CoveMinVersion>` form would leave the branch that
// actually runs in production uncovered, and a regex that stopped matching it would read the
// floor as undefined while every test still passed.
function buildPropsWithFloor(floor = "1.1.0") {
  return [
    "<Project>",
    "  <PropertyGroup>",
    `    <CoveMinVersion Condition="'$(CoveMinVersion)' == ''">${floor}</CoveMinVersion>`,
    `    <CoveSdkVersion Condition="'$(CoveSdkVersion)' == ''">$(CoveMinVersion)</CoveSdkVersion>`,
    "  </PropertyGroup>",
    "</Project>",
    "",
  ].join("\n");
}

// Builds a temp fixture tree:
//   <root>/scripts/validate-extension-repo.mjs   (real validator bytes, copied at run time)
//   <root>/extensions/catalog.json                (the catalog under test)
//   <root>/Directory.Build.props                  (defaults to "" — declares no floor, so the
//                                                    per-entry floor comparison no-ops)
//   <root>/<relPath> for each [relPath, manifest] in extensionJsonByPath (a real extension.json
//   on disk for each catalog entry that must NOT short-circuit on path-existence)
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

test("happy path: a fully-valid single-entry catalog exits 0 and says no floor was declared", () => {
  // With no CoveMinVersion in Directory.Build.props the per-entry comparison deliberately no-ops
  // (the documented fork deviation from upstream, which errors instead). The report line must say
  // so outright, or a repo enforcing no floor at all is indistinguishable from one that passed.
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /no CoveMinVersion declared in Directory\.Build\.props, so no floor comparison ran/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a declared floor is compared, and the report line names the count and the floor value", () => {
  // The "prove it ran" half of the gate contract: exit 0 alone cannot distinguish a comparison
  // that passed from one that never happened, which is precisely how the deleted self-comparing
  // checks stayed invisible. The count and the floor value in stdout are the distinguisher.
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { minCoveVersion: "1.1.0" }),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /compared 1 extension\.json minCoveVersion declaration\(s\) against CoveMinVersion 1\.1\.0/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a manifest floor below the repo floor fails, naming the entry and the floor it fell below", () => {
  // The surviving floor comparison, driven to failure. It is the only one left with a real
  // subject — a per-entry manifest value against the repo floor — so if this case cannot fail,
  // nothing in the file compares versions at all.
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { minCoveVersion: "1.0.0" }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /com\.example\.foo: extension\.json minCoveVersion 1\.0\.0 is below repo CoveMinVersion 1\.1\.0/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry whose path does not exist fails, and reports nothing about the floor", () => {
  // The short-circuit that skips the floor comparison entirely. It is the reason a zero comparison
  // count cannot be an independent finding — every entry that reaches the comparison increments the
  // count, so the only way to reach zero is this error, which has already failed the run. The report
  // line must not appear at all on a failed run: a count is a claim of coverage.
  const entry = validEntry("com.example.foo", "Missing");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /com\.example\.foo: path does not exist/);
    assert.doesNotMatch(stdout, /compared \d+ extension\.json minCoveVersion/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a catalog with zero entries is a finding, not a clean pass", () => {
  // The repo's worked example of "empty input is a hard failure" — validating nothing must never
  // read as validating everything.
  const root = makeFixture({ catalog: { schemaVersion: 1, extensions: [] } });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /extensions\/catalog\.json has no extensions/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("missing required field (id) produces a non-zero exit and the expected error", () => {
  // The label falls back through `entry.id ?? entry.name ?? "catalog entry"`, and id is the field
  // omitted here, so the error is reported against entry.name ("Foo").
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
  // The projectPath check is guarded by `!isManifestOnly`, so this case overrides the
  // validEntry() baseline's manifestOnly:true and supplies a manifest with kind="module" plus a
  // real entryDll, so no OTHER error fires alongside the one under test. manifestPath stays valid
  // so the check reaches the project-path branch instead of short-circuiting on an earlier
  // `continue`.
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
  // entry.path (extensionDir) must exist on disk, or the earlier existsSync(extensionDir) check
  // short-circuits via `continue` before ever reaching the manifestPath check — so a real,
  // unrelated placeholder file is planted under extensions/Foo/ to satisfy the extensionDir
  // check, while manifestPath itself stays absent.
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
  // Both entries need real, distinct, fully-valid fixture dirs so neither short-circuits on the
  // path-existence `continue` before the dedup check on the SECOND entry runs.
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
  // Distinct ids, shared tagPrefix; both entries fully valid otherwise so the dedup check is
  // isolated.
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
  // The per-entry comparison only runs when CoveMinVersion is set, so the fixture declares a
  // floor to activate that path; only the manifest's minCoveVersion is malformed, isolating the
  // "must be a semantic version" branch.
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("0.1.0"),
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
