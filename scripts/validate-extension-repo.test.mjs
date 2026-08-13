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
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, readFileSync, rmSync } from "node:fs";
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
// name, id, path, tagPrefix ending in "/", manifestPath). That mirrored list is not prose to be
// trusted — the drift check at the end of this file asserts it against the real entry.
//
// manifestOnly:true (with a valid manifest kind, set in validManifest) avoids needing a real .csproj
// fixture file for every case — the validator skips the projectPath existence check entirely when
// manifestOnly is true. Callers override individual fields to create exactly one malformation.
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

// Renders a solution file from a list of project paths. Passing raw text instead lets a case
// express a solution the path-attribute match cannot read, which must fail loudly rather than
// report an empty set — an empty set would read as every membership passing.
function solutionXml(projectPaths) {
  return [
    "<Solution>",
    '  <Folder Name="/extensions/">',
    ...projectPaths.map((p) => `    <Project Path="${p}" />`),
    "  </Folder>",
    "</Solution>",
    "",
  ].join("\n");
}

// Builds a temp fixture tree:
//   <root>/scripts/validate-extension-repo.mjs   (real validator bytes, copied at run time)
//   <root>/extensions/catalog.json                (the catalog under test)
//   <root>/Directory.Build.props                  (defaults to "" — declares no floor, so the
//                                                    per-entry floor comparison no-ops)
//   <root>/CoveExtensions.slnx                    (only when `solution` is supplied — omitting it
//                                                    is how a case expresses an absent solution)
//   <root>/<relPath> for each [relPath, manifest] in extensionJsonByPath (a real extension.json
//   on disk for each catalog entry that must NOT short-circuit on path-existence)
//   <root>/<relPath> for each [relPath, text] in filesByPath (raw bytes — a .csproj fixture is not
//   JSON, and only has to exist for the checks that consume it)
function makeFixture({
  catalog,
  buildProps = "",
  extensionJsonByPath = {},
  filesByPath = {},
  solution,
}) {
  const root = mkdtempSync(path.join(tmpdir(), "validate-fixture-"));
  mkdirSync(path.join(root, "scripts"), { recursive: true });
  copyFileSync(realValidatorPath, path.join(root, "scripts", "validate-extension-repo.mjs"));
  mkdirSync(path.join(root, "extensions"), { recursive: true });
  writeFileSync(path.join(root, "extensions", "catalog.json"), JSON.stringify(catalog, null, 2));
  writeFileSync(path.join(root, "Directory.Build.props"), buildProps);
  if (solution !== undefined) {
    writeFileSync(
      path.join(root, "CoveExtensions.slnx"),
      typeof solution === "string" ? solution : solutionXml(solution),
    );
  }
  for (const [relPath, manifest] of Object.entries(extensionJsonByPath)) {
    const full = path.join(root, relPath);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, JSON.stringify(manifest, null, 2));
  }
  for (const [relPath, text] of Object.entries(filesByPath)) {
    const full = path.join(root, relPath);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
  return root;
}

function runValidator(fixtureRoot) {
  const result = spawnSync(
    process.execPath,
    [path.join(fixtureRoot, "scripts", "validate-extension-repo.mjs")],
    {
      encoding: "utf8",
    },
  );
  return { status: result.status, stderr: result.stderr, stdout: result.stdout };
}

test("happy path: a fully-valid single-entry catalog exits 0 and says no floor was declared", () => {
  // With no CoveMinVersion in Directory.Build.props the per-entry comparison no-ops, exactly as it
  // does upstream — both guard it on the same `if (coveMinVersion)`. What the fork adds is saying so
  // outright, or a repo enforcing no floor at all is indistinguishable from one that passed.
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
    assert.match(
      stdout,
      /no CoveMinVersion declared in Directory\.Build\.props, so no floor comparison ran/,
    );
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
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        minCoveVersion: "1.1.0",
      }),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(
      stdout,
      /compared 1 extension\.json minCoveVersion declaration\(s\) against CoveMinVersion 1\.1\.0/,
    );
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
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        minCoveVersion: "1.0.0",
      }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: extension\.json minCoveVersion 1\.0\.0 is below repo CoveMinVersion 1\.1\.0/,
    );
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
    // The solution declares the very path under test, so the membership check has nothing to say
    // here and the promise above — that only one error fires — survives.
    solution: ["extensions/Foo/DoesNotExist.csproj"],
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

test("a declared catalog path that does not exist fails, naming the field", () => {
  // uiPath/testProjectPath/e2ePath/e2eNodeTestsPath are consumed by the CI build matrix but by none
  // of the convention-derived checks, so a typo in one used to surface only inside a matrix leg — an
  // `npm ci` in a directory that is not there, or a dotnet restore several steps in. The error must
  // name the FIELD: the path value alone does not say which CI step is about to break.
  const entry = validEntry("com.example.foo", "Foo", { uiPath: "extensions/Foo/DoesNotExist.Ui" });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: uiPath does not exist: extensions\/Foo\/DoesNotExist\.Ui/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("declared catalog paths that all exist pass, and the report line names how many were checked", () => {
  // The "prove it ran" half, for this check. Two fields are declared and both resolve, so the run is
  // clean — and the count is what distinguishes that from a check that silently examined nothing.
  //
  // Which two fields is immaterial: the counter walks matrixPathFields and does not read their names.
  // uiPath is deliberately not one of them, because it cannot coexist with the manifestOnly baseline —
  // that pairing is its own refusal, and borrowing uiPath here would make this case fail for a reason
  // it says nothing about.
  const entry = validEntry("com.example.foo", "Foo", {
    e2ePath: "extensions/Foo/e2e",
    e2eNodeTestsPath: "extensions/Foo/e2e/node",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
      // Each planted file is only a way to make its parent directory exist on disk; the validator
      // checks the declared directory, never these.
      "extensions/Foo/e2e/package.json": { name: "foo-e2e" },
      "extensions/Foo/e2e/node/package.json": { name: "foo-e2e-node" },
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /confirmed 2 declared catalog path\(s\) exist/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a declared wireDocumentPath that does not exist fails, naming the field", () => {
  // The wire document is written by a test and diffed by CI against the committed bytes. A path that
  // points nowhere makes that diff step read a file that is not there, several steps into a matrix
  // leg — and the error there names a filename, not the catalog field that produced it.
  const entry = validEntry("com.example.foo", "Foo", {
    wireDocumentPath: "extensions/Foo/wire/openapi.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: wireDocumentPath does not exist: extensions\/Foo\/wire\/openapi\.json/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry with a UI and a test project but no wireDocumentPath fails, naming the missing field", () => {
  // The silent-loss case, and the one that only appears with a SECOND extension: everything else about
  // such an entry is well-formed, so nothing else in this file can speak for it. Without this check the
  // CI step keyed on the absent field simply never runs and the leg is green, which reads as coverage.
  const entry = validEntry("com.example.foo", "Foo", {
    uiPath: "extensions/Foo/ui",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    // Declared so the membership check is silent and this case has exactly one malformation.
    solution: ["extensions/Foo/Foo.Tests.csproj"],
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
      "extensions/Foo/ui/package.json": { name: "foo-ui" },
    },
    filesByPath: { "extensions/Foo/Foo.Tests.csproj": "<Project />\n" },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: declares uiPath and testProjectPath but no wireDocumentPath/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a manifestOnly entry that declares a uiPath fails, naming both fields", () => {
  // The two fields contradict each other and nothing else in the catalog can say so: each is
  // individually well-formed, and uiPath's own existence check passes here because the directory is
  // real. What makes the pairing a defect is what CI does with it — several build steps read uiPath
  // and would generate, verify and bundle a frontend for an entry that ships no assembly to load it.
  // The refusal is read from the entry alone, so it must also speak for an entry whose directory or
  // manifest is broken; the case below plants both so this one has exactly one malformation.
  const entry = validEntry("com.example.foo", "Foo", { uiPath: "extensions/Foo/ui" });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
      // Planted so the matrixPathFields existence check is silent — without it this case passes on
      // "uiPath does not exist", which proves nothing about the pairing.
      "extensions/Foo/ui/package.json": { name: "foo-ui" },
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /com\.example\.foo: declares both manifestOnly and uiPath/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a manifestOnly entry declaring no uiPath is untouched by the pairing refusal", () => {
  // The arm that proves the refusal reads both operands rather than firing on manifestOnly alone.
  // Every entry in this suite is manifestOnly by baseline, so a one-operand condition would redden
  // most of the file — but it would redden it for reasons each of those cases is silent about, and
  // this case is the one that names the operand. The fixture is deliberately the happy path's: what
  // differs is the claim, which is that adding the refusal changed nothing here.
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

test("a uiPath on an entry that is not manifestOnly is untouched by the pairing refusal", () => {
  // The opposite arm, and the shape the repository's own catalog has: a UI on an assembly-bearing
  // entry is ordinary, so manifestOnly:false must disarm the refusal. Dropping manifestOnly puts the
  // entry back on the C# path, which is why the convention-derived .csproj, its solution membership
  // and an entryDll are all supplied — each answers a check the entry only now reaches, so exit 0
  // means the refusal stayed silent rather than that some earlier error short-circuited past it.
  const entry = validEntry("com.example.foo", "Foo", {
    name: "Foo",
    manifestOnly: false,
    uiPath: "extensions/Foo/ui",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    solution: ["extensions/Foo/Foo.csproj"],
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { entryDll: "Foo.dll" }),
      "extensions/Foo/ui/package.json": { name: "foo-ui" },
    },
    filesByPath: { "extensions/Foo/Foo.csproj": "<Project />\n" },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry declaring no optional catalog path says so, rather than reporting nothing", () => {
  // Zero checked paths is the case that must not be invisible: an entry set that declares none is
  // legitimate, but it is NOT the same as one whose paths were all confirmed, and a report line that
  // simply omitted the clause would read identically to both.
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
    assert.match(stdout, /no entry declared an optional catalog path, so none were checked/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A C#-bearing entry, complete enough that only the membership check can speak: the project files
// exist, so the existence checks are silent, and the manifest is valid for a non-manifestOnly entry.
function csharpFixture({ solution, projectPath, testProjectPath, name = "Foo" }) {
  const entry = validEntry("com.example.foo", "Foo", {
    name,
    manifestOnly: false,
    ...(projectPath === undefined ? {} : { projectPath }),
    ...(testProjectPath === undefined ? {} : { testProjectPath }),
  });
  const filesByPath = {};
  for (const declared of [projectPath, testProjectPath]) {
    if (declared) filesByPath[declared] = "<Project />\n";
  }
  if (projectPath === undefined) filesByPath[`extensions/Foo/${name}.csproj`] = "<Project />\n";
  return makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    solution,
    filesByPath,
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { entryDll: "Foo.dll" }),
    },
  });
}

test("a projectPath absent from the solution fails, naming the entry, the field, the path and the file", () => {
  // The gap this closes is silent by nature: the format and analyzer gates take their whole subject
  // list from the solution, so a project missing from it is simply never compiled and nothing says
  // so. The error therefore has to name which CI step is about to under-cover, not only which string
  // was absent — hence the field and the solution file alongside the path.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    solution: ["extensions/Bar/Bar.csproj"],
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: projectPath extensions\/Foo\/Foo\.csproj is not declared in CoveExtensions\.slnx/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a testProjectPath absent from the solution fails independently of a projectPath that is present", () => {
  // The two fields reach different gates — the format and analyzer jobs compile the solution, while
  // the Windows unit job runs each catalog testProjectPath — so one being declared cannot excuse the
  // other. The present projectPath is what makes this case about independence rather than about
  // whether the check runs at all.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    solution: ["extensions/Foo/Foo.csproj"],
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: testProjectPath extensions\/Foo\/Foo\.Tests\.csproj is not declared in CoveExtensions\.slnx/,
    );
    assert.doesNotMatch(stderr, /projectPath extensions\/Foo\/Foo\.csproj is not declared/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a non-manifestOnly entry declaring no projectPath has its convention-derived path checked", () => {
  // The upstream convention path is a real entry shape, not a legacy one, so an entry that omits
  // projectPath still compiles something and can still be missing from the solution. The error says
  // the path was derived, or a reader greps the catalog for it and finds nothing.
  const root = csharpFixture({ solution: ["extensions/Bar/Bar.csproj"] });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: projectPath \(by convention\) extensions\/Foo\/Foo\.csproj is not declared in CoveExtensions\.slnx/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("every catalog-implied project present exits 0, and the report line names the confirmed count", () => {
  // The "prove it ran" half for this check. Exit 0 alone cannot distinguish two confirmed
  // memberships from a parse that read nothing and therefore found nothing to complain about.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    solution: ["extensions/Foo/Foo.csproj", "extensions/Foo/Foo.Tests.csproj"],
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /confirmed 2 project membership\(s\) in CoveExtensions\.slnx/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a catalog implying no C# project says so, rather than omitting the clause", () => {
  // A catalog of manifestOnly entries implies nothing to compile, which is legitimate — but it is
  // not the same as a catalog whose projects were all confirmed, and a report line that dropped the
  // clause would read identically to both.
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
    assert.match(
      stdout,
      /no catalog entry implied a C# project, so no solution membership was checked/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a solution whose project paths cannot be read is a named failure, not a silent pass", () => {
  // The failure mode that would make this whole check decorative: a match that stops finding path
  // attributes yields an empty set, and an empty set makes every membership look confirmed. The
  // element count and the extracted count are compared so that divergence speaks.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    solution: '<Solution>\n  <Project Include="extensions/Foo/Foo.csproj" />\n</Solution>\n',
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /CoveExtensions\.slnx: found 1 project element\(s\) but read 0 path/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a solution declaring backslash separators matches a catalog declaring forward slashes", () => {
  // The repository is authored on Windows and built on Linux, so the two spellings of the same path
  // must agree. Case is deliberately not folded: a case mismatch breaks the Linux build, so failing
  // on it is the correct outcome on both platforms.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    solution: ["extensions\\Foo\\Foo.csproj"],
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /confirmed 1 project membership\(s\) in CoveExtensions\.slnx/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a catalog implying a project while the solution file is absent is a named failure", () => {
  // Absence is the one case a set-membership comparison cannot express: with no file there is no set
  // to be missing from, so the miss has to be reported against the file itself.
  const root = csharpFixture({ projectPath: "extensions/Foo/Foo.csproj" });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /CoveExtensions\.slnx is missing, so 1 catalog-implied C# project/);
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
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        minCoveVersion: "not-a-version",
      }),
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

test("a registry versions[] row whose floor disagrees with extension.json fails, naming both floors", () => {
  // The drift that has already shipped here once and was corrected by hand. Both files declare a
  // minCoveVersion, nothing compares them, and the disagreement is invisible until a user on the
  // version between the two floors is either locked out or offered a zip their host cannot run.
  const entry = validEntry("com.example.foo", "Foo", {
    registryManifestPath: "extensions/Foo/extensions/com.example.foo.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        version: "0.3.0",
        minCoveVersion: "1.1.0",
      }),
    },
    filesByPath: {
      "extensions/Foo/extensions/com.example.foo.json": JSON.stringify({
        versions: [{ version: "0.3.0", minCoveVersion: "1.0.0" }],
      }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: registry manifest extensions\/Foo\/extensions\/com\.example\.foo\.json versions\[\] row 0\.3\.0 declares minCoveVersion 1\.0\.0, but extension\.json declares 1\.1\.0/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a registry versions[] row agreeing with extension.json passes, and the report names the count", () => {
  // The "prove it ran" half for this guard. Exit 0 alone cannot distinguish a floor that matched from
  // a manifest that was never opened — and the second is what every version of this check looked like
  // before it existed.
  const entry = validEntry("com.example.foo", "Foo", {
    registryManifestPath: "extensions/Foo/extensions/com.example.foo.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        version: "0.3.0",
        minCoveVersion: "1.1.0",
      }),
    },
    filesByPath: {
      "extensions/Foo/extensions/com.example.foo.json": JSON.stringify({
        versions: [{ version: "0.3.0", minCoveVersion: "1.1.0" }],
      }),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(
      stdout,
      /compared 1 registry versions\[\] row\(s\) against the extension\.json floor/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a registry row carrying no minCoveVersion fails, rather than comparing against nothing", () => {
  // The floor is asserted present BEFORE it is compared. Without that order an absent floor compares
  // undefined against a real version string, which is unequal, so the run would fail with a message
  // describing a mismatch that is really an omission — and a deleted field is exactly half of the
  // injection this guard exists to catch.
  const entry = validEntry("com.example.foo", "Foo", {
    registryManifestPath: "extensions/Foo/extensions/com.example.foo.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        version: "0.3.0",
        minCoveVersion: "1.1.0",
      }),
    },
    filesByPath: {
      "extensions/Foo/extensions/com.example.foo.json": JSON.stringify({
        versions: [{ version: "0.3.0" }],
      }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /versions\[\] row 0\.3\.0 declares no minCoveVersion, so its floor cannot be compared/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("older versions[] rows with lower floors are not compared, so history stays immutable", () => {
  // The case a naive implementation fails, and the only one that can speak for it. An implementation
  // comparing EVERY row passes every other case in this file while demanding the historical-row edit
  // releasing.md forbids: each row describes an immutable published zip whose floor is the floor that
  // zip needs, so the two older rows here are correct precisely by disagreeing with the current one.
  // The three rows mirror the real manifest's shape (1.1.0 / 1.0.0 / 0.7.1, newest first).
  const entry = validEntry("com.example.foo", "Foo", {
    registryManifestPath: "extensions/Foo/extensions/com.example.foo.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        version: "0.3.0",
        minCoveVersion: "1.1.0",
      }),
    },
    filesByPath: {
      "extensions/Foo/extensions/com.example.foo.json": JSON.stringify({
        versions: [
          { version: "0.3.0", minCoveVersion: "1.1.0" },
          { version: "0.2.0", minCoveVersion: "1.0.0" },
          { version: "0.1.0", minCoveVersion: "0.7.1" },
        ],
      }),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(
      stdout,
      /compared 1 registry versions\[\] row\(s\) against the extension\.json floor/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry declaring no registryManifestPath says so, rather than reporting nothing", () => {
  // The "prove it ran" half for the absent-declaration state. An entry set declaring none is
  // legitimate — an extension not yet published to the store has no registry manifest — but it is NOT
  // the same as one whose rows were all compared, and a clause dropped on absence reads as both.
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
    assert.match(
      stdout,
      /no entry declared a registryManifestPath, so no registry floor was compared/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("two versions[] rows carrying the same version fail, naming the duplicated version", () => {
  // A precondition of the guard above rather than a separate feature: with two rows claiming one
  // version, "the row describing the current version" is not well defined, and the comparison would
  // silently take whichever came first — passing or failing on row order alone.
  const entry = validEntry("com.example.foo", "Foo", {
    registryManifestPath: "extensions/Foo/extensions/com.example.foo.json",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        version: "0.3.0",
        minCoveVersion: "1.1.0",
      }),
    },
    filesByPath: {
      "extensions/Foo/extensions/com.example.foo.json": JSON.stringify({
        versions: [
          { version: "0.3.0", minCoveVersion: "1.1.0" },
          { version: "0.3.0", minCoveVersion: "1.1.0" },
        ],
      }),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /declares versions\[\] row 0\.3\.0 more than once/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ── The fixtures themselves, checked against reality ─────────────────────────────────────────────
//
// Every case above is written against `validEntry`/`validManifest` — hand-written mirrors of the real
// Renamer catalog entry and manifest. A hand-mirrored value with no mechanical check drifts, and the
// drift is silent in the worst way: when a field leaves the real shape, all 24 cases keep passing
// while exercising a shape that no longer exists.
//
// This does NOT re-run the validator against the real repo; CI already does that
// (.github/workflows/build.yml, the required `validate` job), and a second copy of an existing gate
// would rot rather than protect. What CI cannot say is whether these fixtures still describe what it
// validates. That is the gap here.
//
// Catalog-driven, so a second extension needs no edit: every entry is checked, and an empty catalog
// is a hard failure rather than a vacuous pass.

// Fields the fixtures add deliberately, which are absent from the real shape by design. `manifestOnly`
// makes the validator skip the projectPath existence check so a case needs no .csproj on disk, and
// `kind` is what it pairs with. Both are documented at their baseline above. Anything else appearing
// here means a fixture is modelling a field reality does not have.
const FIXTURE_ONLY_ENTRY_FIELDS = new Set(["manifestOnly"]);
const FIXTURE_ONLY_MANIFEST_FIELDS = new Set(["kind"]);

test("the hand-mirrored fixture baselines still describe the real catalog and manifest shape", () => {
  const repoRoot = path.join(here, "..");
  const catalog = JSON.parse(
    readFileSync(path.join(repoRoot, "extensions", "catalog.json"), "utf8"),
  );
  const entries = catalog.extensions ?? [];

  assert.ok(
    entries.length > 0,
    "extensions/catalog.json declares no extensions — this check inspected nothing, which is a failure, not a pass",
  );

  const baselineEntryFields = Object.keys(validEntry("com.example.foo", "Foo")).filter(
    (field) => !FIXTURE_ONLY_ENTRY_FIELDS.has(field),
  );
  const baselineManifestFields = Object.keys(validManifest("com.example.foo")).filter(
    (field) => !FIXTURE_ONLY_MANIFEST_FIELDS.has(field),
  );

  for (const entry of entries) {
    for (const field of baselineEntryFields) {
      assert.ok(
        field in entry,
        `validEntry models catalog field "${field}", which the real entry "${entry.id}" does not have — ` +
          `the fixtures describe a shape that no longer exists, so the cases above prove nothing about it`,
      );
    }

    // The manifest is reached through the entry's own manifestPath rather than a second hardcoded
    // path, so this check cannot itself become a stale mirror of where the manifest lives.
    const manifest = JSON.parse(readFileSync(path.join(repoRoot, entry.manifestPath), "utf8"));
    for (const field of baselineManifestFields) {
      assert.ok(
        field in manifest,
        `validManifest models manifest field "${field}", which ${entry.manifestPath} does not have — ` +
          `same drift, on the manifest side`,
      );
    }
  }
});
