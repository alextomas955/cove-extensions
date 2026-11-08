// Subprocess rather than an imported function: the validator resolves its root relative to where the
// executing file lives on disk, not process.cwd(). Each fixture therefore gets a copy of the real
// validator bytes under its own `scripts/`, so that resolution lands in the fixture tree.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

// A fully-valid extension.json baseline. Callers override one field to create one malformation.
function validManifest(id, overrides = {}) {
  return {
    id,
    version: "0.1.0",
    url: "https://example.invalid/" + id,
    categories: ["utility"],
    entryDll: "Fixture.dll",
    ...overrides,
  };
}

// The smallest entry the validator accepts. Every optional path field is left out so a case can
// drive the zero-subject state of each check. makeFixture plants its project and solution unless a
// case opts out.
function validEntry(id, dirName, overrides = {}) {
  return {
    name: id,
    id,
    path: "extensions/" + dirName,
    tagPrefix: dirName.toLowerCase() + "/",
    manifestPath: "extensions/" + dirName + "/extension.json",
    projectPath: "extensions/" + dirName + "/" + dirName + ".csproj",
    ...overrides,
  };
}

// The attributed form the real Directory.Build.props uses. A bare-element fixture would miss
// readMsBuildProperties' optional-attribute branch, which is the one production takes.
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

// Renders a solution file from a list of project paths. A case passes raw text instead to express a
// solution the path-attribute match cannot read.
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

// Builds a temp fixture tree. `buildProps` defaults to "", which declares no floor and so no-ops the
// per-entry floor comparison. `solution` defaults to one listing every planted project, and null
// leaves the file absent. `filesByPath` is raw bytes, for fixtures that are not JSON.
function makeFixture({
  catalog,
  buildProps = "",
  extensionJsonByPath = {},
  filesByPath = {},
  solution,
  plantProjects = true,
}) {
  const root = mkdtempSync(path.join(tmpdir(), "validate-fixture-"));
  mkdirSync(path.join(root, "scripts"), { recursive: true });
  for (const file of ["validate-extension-repo.mjs", "cove-versions.mjs", "repo-files.mjs"]) {
    copyFileSync(path.join(here, file), path.join(root, "scripts", file));
  }
  mkdirSync(path.join(root, "extensions"), { recursive: true });
  writeFileSync(path.join(root, "extensions", "catalog.json"), JSON.stringify(catalog, null, 2));
  writeFileSync(path.join(root, "Directory.Build.props"), buildProps);
  const planted = plantProjects
    ? catalog.extensions.flatMap((entry) =>
        [entry.projectPath, entry.testProjectPath].filter(Boolean),
      )
    : [];
  for (const relPath of planted) {
    mkdirSync(path.dirname(path.join(root, relPath)), { recursive: true });
    writeFileSync(path.join(root, relPath), "<Project />\n");
  }
  const solutionContent = solution === undefined ? planted : solution;
  if (solutionContent !== null) {
    writeFileSync(
      path.join(root, "CoveExtensions.slnx"),
      typeof solutionContent === "string" ? solutionContent : solutionXml(solutionContent),
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

// One valid entry and its manifest, each carrying the overrides a case supplies.
function runWith({ entry = {}, manifest = {} } = {}) {
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [validEntry("com.example.foo", "Foo", entry)] },
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", manifest),
    },
  });
  try {
    return runValidator(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// Two valid entries differing only in the field a case duplicates. Both need real, distinct fixture
// dirs so neither short-circuits on the path-existence `continue` before the dedup check runs on the
// second entry.
function runWithTwoEntries(first, second) {
  const root = makeFixture({
    catalog: {
      schemaVersion: 1,
      extensions: [first, second].map(({ id, dir, overrides }) => validEntry(id, dir, overrides)),
    },
    extensionJsonByPath: Object.fromEntries(
      [first, second].map(({ id, dir }) => [`extensions/${dir}/extension.json`, validManifest(id)]),
    ),
  });
  try {
    return runValidator(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// A catalog entry with a subject for every counter the report line carries.
function maximalFixture() {
  const entry = validEntry("com.example.foo", "Foo", {
    name: "Foo",
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
      "extensions/Foo/registry.json": { versions: [{ version: "0.1.0", minCoveVersion: "1.1.0" }] },
    },
    filesByPath: {
      "extensions/Foo/Foo.csproj": "<Project />\n",
      "extensions/Foo/Foo.Tests.csproj": "<Project />\n",
    },
  });
}

test("the summary line reports counts, and a check with no subject renders 0 rather than a sentence", () => {
  // Asserted as the whole line: a fragment matcher cannot see a counter that stopped being printed.
  const maximal = maximalFixture();
  try {
    const { status, stdout, stderr } = runValidator(maximal);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.equal(
      stdout.trim(),
      "Validated 1 extension catalog entries: 1 minCoveVersion floor comparison(s), " +
        "4 declared catalog path(s), 2 CoveExtensions.slnx membership(s), " +
        "1 registry row(s) compared across " +
        "1 declared registry manifest(s).",
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
        "0 declared catalog path(s), 1 CoveExtensions.slnx membership(s), " +
        "0 registry row(s) compared across " +
        "0 declared registry manifest(s).",
    );
  } finally {
    rmSync(minimal, { recursive: true, force: true });
  }
});

test("a manifest floor below the repo floor fails, naming the entry and the floor it fell below", () => {
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
  const entry = validEntry("com.example.foo", "Missing");
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    buildProps: buildPropsWithFloor("1.1.0"),
    // A planted project would create the directory whose absence is under test.
    plantProjects: false,
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
  const root = makeFixture({ catalog: { schemaVersion: 1, extensions: [] } });
  try {
    const { status, stderr } = runValidator(root);
    assert.notEqual(status, 0);
    assert.match(stderr, /extensions\/catalog\.json has no extensions/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("each entry-level malformation is refused with its own reason", () => {
  for (const [overrides, expected] of [
    [{ entry: { id: undefined } }, /missing id/],
    // A release tag is `<tagPrefix>v<semver>`, so a prefix missing its separator produces a tag that
    // matches no release trigger, or matches another extension's.
    [{ entry: { tagPrefix: "foo" } }, /com\.example\.foo: tagPrefix must end with \//],
    // The catalog id is what CI's matrix, the release tag and the registry key on; the manifest id is
    // what the host loads the extension as. Nothing else compares them.
    [
      { manifest: { id: "com.example.other" } },
      /com\.example\.foo: catalog id does not match extension\.json id com\.example\.other/,
    ],
    // uiPath/testProjectPath/e2ePath are consumed by the CI build matrix but by none of the
    // convention-derived checks, so without this a typo in one surfaces only inside a matrix leg.
    [
      { entry: { uiPath: "extensions/Foo/DoesNotExist.Ui" } },
      /com\.example\.foo: uiPath does not exist: extensions\/Foo\/DoesNotExist\.Ui/,
    ],
  ]) {
    const { status, stderr } = runWith(overrides);
    assert.notEqual(status, 0, `${JSON.stringify(overrides)} passed`);
    assert.match(stderr, expected);
  }
});

test("nonexistent projectPath produces a non-zero exit and the expected error", () => {
  // manifestPath stays valid so the check reaches the project-path branch instead of
  // short-circuiting on an earlier `continue`.
  const entry = validEntry("com.example.foo", "Foo", {
    projectPath: "extensions/Foo/DoesNotExist.csproj",
  });
  const root = makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    plantProjects: false,
    // The solution declares the very path under test, so the membership check has nothing to say.
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
  // entry.path must exist on disk, or the earlier existsSync check short-circuits via `continue`
  // before reaching the manifestPath check. The placeholder satisfies it while manifestPath stays
  // absent.
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

test("a field two entries share where it must be unique is refused, naming that field", () => {
  for (const [entries, expected] of [
    [
      [
        { id: "com.example.dup", dir: "DupA" },
        { id: "com.example.dup", dir: "DupB" },
      ],
      /duplicate extension id/,
    ],
    // Distinct ids, shared tagPrefix, so the tagPrefix dedup check is the only one that can speak.
    [
      [
        { id: "com.example.taga", dir: "TagA", overrides: { tagPrefix: "shared/" } },
        { id: "com.example.tagb", dir: "TagB", overrides: { tagPrefix: "shared/" } },
      ],
      /duplicate tagPrefix/,
    ],
  ]) {
    const { status, stderr } = runWithTwoEntries(entries[0], entries[1]);
    assert.notEqual(status, 0, `${JSON.stringify(entries)} passed`);
    assert.match(stderr, expected);
  }
});

// A C#-bearing entry complete enough that only the membership check can speak. `absentOnDisk` names
// declared paths the fixture does not plant, which is what separates the declared-but-missing check
// from the solution-membership one: both report on the same field.
function csharpFixture({
  solution,
  projectPath,
  testProjectPath,
  absentOnDisk = [],
  name = "Foo",
}) {
  const entry = validEntry("com.example.foo", "Foo", {
    name,
    projectPath,
    ...(testProjectPath === undefined ? {} : { testProjectPath }),
  });
  const filesByPath = {};
  for (const declared of [projectPath, testProjectPath]) {
    if (declared && !absentOnDisk.includes(declared)) filesByPath[declared] = "<Project />\n";
  }
  if (projectPath === undefined) filesByPath[`extensions/Foo/${name}.csproj`] = "<Project />\n";
  return makeFixture({
    catalog: { schemaVersion: 1, extensions: [entry] },
    solution,
    plantProjects: false,
    filesByPath,
    extensionJsonByPath: {
      "extensions/Foo/extension.json": validManifest("com.example.foo", { entryDll: "Foo.dll" }),
    },
  });
}

test("a projectPath absent from the solution fails, naming the entry, the field, the path and the file", () => {
  // The format and analyzer gates take their whole subject list from the solution, so a project
  // missing from it is never compiled and nothing else says so.
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

test("an entry declaring no projectPath has its convention-derived path checked", () => {
  // The error says the path was derived, or a reader greps the catalog for it and finds nothing.
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
  // A match that stops finding path attributes yields an empty set, and an empty set makes every
  // membership look confirmed.
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
  // must agree. Case is not folded: a case mismatch breaks the Linux build.
  const root = csharpFixture({
    projectPath: "extensions/Foo/Foo.csproj",
    solution: ["extensions\\Foo\\Foo.csproj"],
  });
  try {
    const { status, stdout, stderr } = runValidator(root);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    // The count, not merely exit 0: a comparison that matched nothing also finds nothing to report.
    assert.match(stdout, /1 CoveExtensions\.slnx membership\(s\)/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an entry declaring no test project stays valid", () => {
  // The membership count is asserted rather than exit 0 alone: a run that examined nothing also
  // exits 0.
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

test("a registry versions[] row whose floor disagrees with extension.json fails, naming both floors", () => {
  // Both files declare a minCoveVersion. A disagreement is invisible until a user on the version
  // between the two floors is either locked out or offered a zip their host cannot run.
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
  // Each registry row describes an immutable published zip whose floor is the floor that zip needs,
  // so the two older rows here are correct precisely by disagreeing with the current one.
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
    // The count, not exit 0: an implementation comparing every row also exits 0 on this fixture,
    // because the two older rows agree with themselves.
    assert.match(stdout, /1 registry row\(s\) compared across 1 declared registry manifest\(s\)/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("two versions[] rows carrying the same version fail, naming the duplicated version", () => {
  // With two rows claiming one version, "the row describing the current version" is not well
  // defined, and the comparison would take whichever came first.
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

test("the unmodified manifest baseline passes, so each refusal below is its own field's", () => {
  const { status, stderr } = runWith();
  assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
});

test("a manifest missing entryDll, url or categories fails, naming the missing field", () => {
  for (const [overrides, expected] of [
    [{ entryDll: undefined }, /com\.example\.foo: extension\.json missing entryDll/],
    [{ url: undefined }, /com\.example\.foo: extension\.json missing url/],
    [{ categories: [] }, /com\.example\.foo: extension\.json missing categories/],
    [{ categories: undefined }, /com\.example\.foo: extension\.json missing categories/],
  ]) {
    const { status, stderr } = runWith({ manifest: overrides });
    assert.notEqual(status, 0, `${JSON.stringify(overrides)} passed`);
    assert.match(stderr, expected);
  }
});

test("a category that is not lowercase kebab-case fails, naming the category", () => {
  for (const category of ["Utility", "file tools"]) {
    const { status, stderr } = runWith({ manifest: { categories: ["utility", category] } });
    assert.notEqual(status, 0, `category "${category}" passed`);
    assert.match(stderr, new RegExp(`category must be lowercase kebab-case: ${category}$`, "m"));
  }
});

test("each malformed external dependency is refused with its own reason", () => {
  for (const [externalDependencies, expected] of [
    [{ id: "dep" }, /externalDependencies must be an array/],
    [[{ name: "Dep" }], /external dependency missing id/],
    [[{ id: "dep" }], /external dependency missing name/],
    [[{ id: "dep", name: "Dep", configurationKeys: "k" }], /configurationKeys must be an array/],
  ]) {
    const { status, stderr } = runWith({ manifest: { externalDependencies } });
    assert.notEqual(status, 0, `${JSON.stringify(externalDependencies)} passed`);
    assert.match(stderr, expected);
  }

  const wellFormed = runWith({
    manifest: {
      externalDependencies: [{ id: "dep", name: "Dep", required: false, configurationKeys: ["k"] }],
    },
  });
  assert.equal(wellFormed.status, 0, "a well-formed dependency was refused: " + wellFormed.stderr);
});

test("each malformed setting is refused with its own reason", () => {
  for (const [settings, expected] of [
    [{ name: "s" }, /extension\.json settings must be an array/],
    [[{ displayName: "S" }], /setting missing name/],
  ]) {
    const { status, stderr } = runWith({ manifest: { settings } });
    assert.notEqual(status, 0, `${JSON.stringify(settings)} passed`);
    assert.match(stderr, expected);
  }

  const wellFormed = runWith({ manifest: { settings: [{ name: "s", displayName: "S" }] } });
  assert.equal(wellFormed.status, 0, "a well-formed setting was refused: " + wellFormed.stderr);
});
