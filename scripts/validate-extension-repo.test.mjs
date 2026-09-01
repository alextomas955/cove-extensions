// Drives the REAL validate-extension-repo.mjs as a child process against malformed catalog
// fixtures, asserting exit code and output text.
//
// Subprocess rather than an imported function because the validator resolves its root relative to
// where the executing file lives on disk, not process.cwd(). Each fixture therefore gets a copy of
// the real validator bytes under its own `scripts/`, so that resolution lands in the fixture tree.
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

// A valid baseline mirroring the real Renamer entry; the drift check at the end of this file
// asserts that mirror against the real one. manifestOnly:true skips the projectPath existence
// check, so a case needs no .csproj fixture. Callers override one field to create one malformation.
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

// The ATTRIBUTED form the real Directory.Build.props uses, which is what exercises
// readMsBuildProperties' optional-attribute branch. A bare-element fixture would leave the branch
// that runs in production uncovered.
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

// A catalog entry with a subject for every counter the report line carries: a floor to compare, four
// declared optional paths, two projects to find in the solution, and a registry row matching the
// version its manifest declares.
function maximalFixture() {
  const entry = validEntry("com.example.foo", "Foo", {
    name: "Foo",
    manifestOnly: false,
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    uiPath: "extensions/Foo/ui",
    e2ePath: "extensions/Foo/e2e",
    registryManifestPath: "extensions/Foo/registry.json",
  });
  return makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    solution: ["extensions/Foo/Foo.csproj", "extensions/Foo/Foo.Tests.csproj"],
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", {
        entryDll: "Foo.dll",
        minCoveVersion: "1.1.0",
      }),
      "extensions/Foo/ui/package.json": { name: "foo-ui" },
      "extensions/Foo/e2e/package.json": { name: "foo-e2e" },
      "extensions/Foo/wire/openapi.json": { openapi: "3.1.1" },
      "extensions/Foo/registry.json": { versions: [{ version: "0.1.0", minCoveVersion: "1.1.0" }] },
    },
    filesByPath: {
      "extensions/Foo/Foo.csproj": "<Project />\n",
      "extensions/Foo/Foo.Tests.csproj": "<Project />\n",
    },
  });
}

test("the summary line reports counts, and a check with no subject renders 0 rather than a sentence", () => {
  // Exit 0 alone cannot distinguish a check that passed from one that never ran, so the counts are
  // what has to be asserted. Both states are driven, because "this check ran" and "this check had no
  // subject" reading identically is the failure mode. Asserted as the whole line, since a fragment
  // matcher cannot see a counter that stopped being printed at all.
  const maximal = maximalFixture();
  try {
    const { status, stdout, stderr } = runValidator(maximal);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.equal(
      stdout.trim(),
      "Validated 1 extension catalog entries: 1 minCoveVersion floor comparison(s), " +
        "4 declared catalog path(s), 2 CoveExtensions.slnx membership(s), " +
        "1 registry row(s) compared across " +
        "1 declared registry manifest(s), " +
        "0 project file(s) scanned for ProjectReference items onto " +
        "0 declared Cove test project(s), 0 Include(s) left unjudged.",
    );
  } finally {
    rmSync(maximal, { recursive: true, force: true });
  }

  const minimal = makeFixture({
    catalog: { schemaVersion: 1, extensions: [validEntry("com.example.foo", "Foo")] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stdout, stderr } = runValidator(minimal);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.equal(
      stdout.trim(),
      "Validated 1 extension catalog entries: 0 minCoveVersion floor comparison(s), " +
        "0 declared catalog path(s), 0 CoveExtensions.slnx membership(s), " +
        "0 registry row(s) compared across " +
        "0 declared registry manifest(s), " +
        "0 project file(s) scanned for ProjectReference items onto " +
        "0 declared Cove test project(s), 0 Include(s) left unjudged.",
    );
  } finally {
    rmSync(minimal, { recursive: true, force: true });
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

test("an entry whose path does not exist fails, and reports no counts at all", () => {
  // The short-circuit that skips the floor comparison entirely. It is the reason a zero comparison
  // count cannot be an independent finding — every entry that reaches the comparison increments the
  // count, so the only way to reach zero is this error, which has already failed the run. The counts
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
    assert.equal(stdout.trim(), "");
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

test("a tagPrefix without a trailing slash fails", () => {
  // The tag a release is cut from is `<tagPrefix>v<semver>`, so a prefix missing its separator
  // produces a tag that matches no release trigger — or worse, matches another extension's. The
  // catalog is the only place that can say so.
  const entry = validEntry("com.example.foo", "Foo", { tagPrefix: "foo" });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /com\.example\.foo: tagPrefix must end with \//);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a manifest id disagreeing with its catalog entry fails, naming both ids", () => {
  // The catalog id is what CI's matrix, the release tag and the registry all key on, while the
  // manifest id is what the host loads the extension as. Nothing else compares them, and a
  // disagreement ships an extension the store and the host each know by a different name.
  const entry = validEntry("com.example.foo", "Foo");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.other"),
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: catalog id does not match extension\.json id com\.example\.other/,
    );
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

test("a manifestOnly entry that declares a uiPath fails, naming both fields", () => {
  // Each field is individually well-formed — uiPath's own existence check passes — so nothing else in
  // the catalog can call the pairing a defect. What makes it one is that several build steps read
  // uiPath and would generate, verify and bundle a frontend for an entry that ships no assembly.
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

// A C#-bearing entry, complete enough that only the membership check can speak: the project files
// exist, so the existence checks are silent, and the manifest is valid for a non-manifestOnly entry.
// `absentOnDisk` names declared paths the fixture deliberately does not plant, which is what
// separates the declared-but-missing check from the solution-membership one — both report on the
// same field, and a case that plants nothing cannot say which of the two spoke. `extraFiles` plants
// project files the catalog does not declare, which is the only way to express a project that takes a
// reference onto a declared one.
function csharpFixture({
  solution,
  projectPath,
  testProjectPath,
  coveTestProjectPath,
  absentOnDisk = [],
  extraFiles = {},
  name = "Foo",
}) {
  const entry = validEntry("com.example.foo", "Foo", {
    name,
    manifestOnly: false,
    ...(projectPath === undefined ? {} : { projectPath }),
    ...(testProjectPath === undefined ? {} : { testProjectPath }),
    ...(coveTestProjectPath === undefined ? {} : { coveTestProjectPath }),
  });
  const filesByPath = { ...extraFiles };
  for (const declared of [projectPath, testProjectPath, coveTestProjectPath]) {
    if (declared && !absentOnDisk.includes(declared)) filesByPath[declared] = "<Project />\n";
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
    // The count, not merely exit 0: a comparison that matched nothing also finds nothing to report,
    // so only a confirmed membership says the two spellings were reconciled.
    assert.match(stdout, /1 CoveExtensions\.slnx membership\(s\)/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a declared coveTestProjectPath that does not exist fails, naming the field", () => {
  // The Cove test project is consumed by the cove-present CI leg and by nothing the convention-derived
  // checks reach, so a typo in it otherwise surfaces as a dotnet restore several steps into that leg.
  // The solution declares the path under test, so only the existence check can speak here.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    coveTestProjectPath: "extensions/Foo/DoesNotExist.Cove.Tests.csproj",
    absentOnDisk: ["extensions/Foo/DoesNotExist.Cove.Tests.csproj"],
    solution: [
      "extensions/Foo/Foo.csproj",
      "extensions/Foo/Foo.Tests.csproj",
      "extensions/Foo/DoesNotExist.Cove.Tests.csproj",
    ],
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: coveTestProjectPath does not exist: extensions\/Foo\/DoesNotExist\.Cove\.Tests\.csproj/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a coveTestProjectPath absent from the solution fails, naming the entry, the field, the path and the file", () => {
  // Same silence the projectPath membership case closes, on the tier that carries it further: the
  // format and analyzer gates take their whole subject list from the solution, so a Cove test project
  // missing from it is compiled by no gate and nothing says so.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    coveTestProjectPath: "extensions/Foo/Foo.Cove.Tests.csproj",
    solution: ["extensions/Foo/Foo.csproj", "extensions/Foo/Foo.Tests.csproj"],
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: coveTestProjectPath extensions\/Foo\/Foo\.Cove\.Tests\.csproj is not declared in CoveExtensions\.slnx/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a coveTestProjectPath declared without a testProjectPath fails, naming both fields", () => {
  // Each field is individually well-formed — the path exists and the solution declares it — so only
  // the pairing is the defect. The Cove tier reaches the shared TestSupport helpers through a
  // ProjectReference onto the pure tier, so declaring one without the other names a topology that
  // does not build.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    coveTestProjectPath: "extensions/Foo/Foo.Cove.Tests.csproj",
    solution: ["extensions/Foo/Foo.csproj", "extensions/Foo/Foo.Cove.Tests.csproj"],
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: declares coveTestProjectPath without a testProjectPath/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry declaring neither test project stays valid", () => {
  // Both fields are optional to declare, and an extension with no tests at all must not be forced to
  // grow one. The membership count is asserted rather than exit 0 alone: a run that examined nothing
  // also exits 0.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    solution: ["extensions/Foo/Foo.csproj"],
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /0 declared catalog path\(s\), 1 CoveExtensions\.slnx membership\(s\)/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A fully-declared C# entry whose extra project takes one reference, so only the reference rule can
// speak: every declared path exists and the solution declares all three.
function referenceFixture(includeValue) {
  return csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    coveTestProjectPath: "extensions/Foo/Foo.Cove.Tests.csproj",
    solution: [
      "extensions/Foo/Foo.csproj",
      "extensions/Foo/Foo.Tests.csproj",
      "extensions/Foo/Foo.Cove.Tests.csproj",
    ],
    extraFiles: {
      "extensions/Bar/Bar.csproj":
        "<Project>\n  <ItemGroup>\n" +
        `    <ProjectReference Include="${includeValue}" />\n` +
        "  </ItemGroup>\n</Project>\n",
    },
  });
}

test("a ProjectReference onto a declared coveTestProjectPath fails, naming the referencing project and the field", () => {
  // The Cove test project declares that it requires a Cove source checkout, and the whole-solution
  // skip that honours that declaration is scoped by $(SolutionPath). That property separates a
  // solution build from a direct one; it does not separate an entry project from one reached through
  // a reference, so the referencing project takes the skip and then fails to resolve an output
  // nothing produced. The invariant is documented where the skip lives and nothing else asserts it.
  const root = referenceFixture("../Foo/Foo.Cove.Tests.csproj");
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(
      stderr,
      /com\.example\.foo: extensions\/Bar\/Bar\.csproj declares a ProjectReference onto coveTestProjectPath extensions\/Foo\/Foo\.Cove\.Tests\.csproj/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a backslash-separated ProjectReference onto a coveTestProjectPath is judged the same as a forward-slash one", () => {
  // The repository is authored on Windows and built on Linux, so a Windows-spelled Include names the
  // same file. Case is deliberately not folded here either, for the reason the solution comparison
  // states.
  const root = referenceFixture("..\\Foo\\Foo.Cove.Tests.csproj");
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /coveTestProjectPath extensions\/Foo\/Foo\.Cove\.Tests\.csproj/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a semicolon-delimited Include naming the coveTestProjectPath among others still fails", () => {
  // An Include is an MSBuild item list, so one attribute can name several projects. Reading the whole
  // attribute as a single path makes a forbidden reference sitting beside another one invisible.
  const root = referenceFixture("../Foo/Foo.Tests.csproj;../Foo/Foo.Cove.Tests.csproj");
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /coveTestProjectPath extensions\/Foo\/Foo\.Cove\.Tests\.csproj/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a single-quoted Include onto the coveTestProjectPath is judged the same as a double-quoted one", () => {
  // XML admits either quote character as an attribute delimiter, and MSBuild reads both.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    testProjectPath: "extensions/Foo/Foo.Tests.csproj",
    coveTestProjectPath: "extensions/Foo/Foo.Cove.Tests.csproj",
    solution: [
      "extensions/Foo/Foo.csproj",
      "extensions/Foo/Foo.Tests.csproj",
      "extensions/Foo/Foo.Cove.Tests.csproj",
    ],
    extraFiles: {
      "extensions/Bar/Bar.csproj":
        "<Project>\n  <ItemGroup>\n" +
        "    <ProjectReference Include='../Foo/Foo.Cove.Tests.csproj' />\n" +
        "  </ItemGroup>\n</Project>\n",
    },
  });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /coveTestProjectPath extensions\/Foo\/Foo\.Cove\.Tests\.csproj/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a property-expanded Include is named as unjudged rather than counted clean", () => {
  // Expanding $(…) needs MSBuild's own evaluation, which this script does not have. Passing such an
  // Include silently would report success over a reference the rule never read, so it is disclosed by
  // name and by count instead.
  const root = referenceFixture("$(CoveTestProject)");
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /NOTICE: extensions\/Bar\/Bar\.csproj: \$\(CoveTestProject\)/);
    assert.match(stdout, /1 Include\(s\) left unjudged\./);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a ProjectReference onto a project that is not the coveTestProjectPath stays valid", () => {
  // Without this the rule could be satisfied by refusing every reference, which would refuse the real
  // repository's own topology. The scanned count is asserted rather than exit 0 alone: a walk that
  // reached no project file also finds nothing to report.
  const root = referenceFixture("../Foo/Foo.Tests.csproj");
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(
      stdout,
      /4 project file\(s\) scanned for ProjectReference items onto 1 declared Cove test project\(s\)/,
    );
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
    // ONE of the three rows was compared. The count is what says so: an implementation comparing
    // every row also exits 0 on this fixture, because the two older rows agree with themselves.
    assert.match(stdout, /1 registry row\(s\) compared across 1 declared registry manifest\(s\)/);
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
// drift is silent in the worst way: when a field leaves the real shape, every case above keeps passing
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
