// Behavior coverage for the shared, catalog-driven package assembler. Every case is driven from a
// fixture tree in a temp dir — a fake extensions/catalog.json plus a fake publish output — so no
// dotnet build, no npm build and no sibling Cove checkout is needed, and the happy path is green or
// red on the packer rather than on whether this machine happens to have built anything.
//
// The fixture entry's file names deliberately differ from any real extension's, so a case that
// passes proves the resolution came from the catalog rather than from a name baked into the packer.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { assemblePackage } from "./assemble-package.mjs";

const scriptPath = fileURLToPath(new URL("./assemble-package.mjs", import.meta.url));

const ID = "com.example.fixture";
const NAME = "Fixture";
const VERSION = "4.5.6";

const DECLARED = [
  "extension.json",
  "bundle.mjs",
  "Fixture.dll",
  "Fixture.Extra.dll",
  "Fixture.deps.json",
  "README.md",
  "LICENSE",
];

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "assemble-package-"));
}

function write(dir, name, content = "") {
  fs.mkdirSync(path.dirname(path.join(dir, name)), { recursive: true });
  fs.writeFileSync(path.join(dir, name), content);
}

/**
 * Builds a fixture repo root whose shape matches this repo's: extensions/catalog.json beside one
 * directory per extension, with the manifest and UI bundle nested under src/.
 *
 * `artifacts` overrides the declared set, and `null` omits the field entirely; `publishFiles`
 * overrides what the fake build emitted; `entry` and `manifest` merge into the catalog entry and the
 * source manifest.
 */
function fixtureRoot({ artifacts = DECLARED, publishFiles = null, entry = {}, manifest = {} } = {}) {
  const root = tmpDir();
  const extPath = "extensions/" + NAME;

  const catalogEntry = {
    name: NAME,
    id: ID,
    path: extPath,
    tagPrefix: "fixture/",
    manifestPath: extPath + "/src/" + NAME + "/extension.json",
    uiPath: extPath + "/src/" + NAME + ".Ui",
    ...(artifacts === null ? {} : { artifacts }),
    ...entry,
  };
  write(root, "extensions/catalog.json", JSON.stringify({ schemaVersion: 1, extensions: [catalogEntry] }, null, 2) + "\n");

  const sourceManifest = { id: ID, name: NAME, version: "0.1.0", entryDll: "Fixture.dll", jsBundle: "bundle.mjs", ...manifest };
  write(root, catalogEntry.manifestPath, JSON.stringify(sourceManifest, null, 2) + "\n");
  write(root, catalogEntry.uiPath + "/dist/bundle.mjs", "export const fixture = 1;\n");
  write(root, extPath + "/README.md", "# Fixture\n");
  write(root, "LICENSE", "AGPL-3.0\n");

  const publishDir = path.join(root, extPath, "artifacts", "publish");
  const emitted = publishFiles ?? {
    "Fixture.dll": "MZ",
    "Fixture.Extra.dll": "MZ",
    "Fixture.deps.json": '{ "runtimeTarget": { "name": "net10.0" } }\n',
  };
  for (const [name, content] of Object.entries(emitted)) write(publishDir, name, content);

  return { root, publishDir, packageDir: path.join(root, extPath, "artifacts", "package") };
}

function assemble({ root, publishDir, packageDir }, overrides = {}) {
  return assemblePackage({ root, publishDir, packageDir, idOrName: ID, version: VERSION, ...overrides });
}

test("copies exactly the declared set, resolved from the catalog rather than a built-in list", () => {
  const fixture = fixtureRoot();
  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...DECLARED].sort());
  assert.equal(r.copied.length, DECLARED.length);
  assert.deepEqual(
    r.copied.map((f) => f.name),
    DECLARED,
    "copied order must follow the declaration order",
  );
});

test("fails: one declared artifact absent from every source root, named with the roots searched", () => {
  const fixture = fixtureRoot({
    publishFiles: { "Fixture.dll": "MZ", "Fixture.deps.json": "{}\n" },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "expected a MISSING failure for the artifact the fake build did not emit");
  const missing = r.failures.find((f) => f.startsWith("MISSING:") && f.includes("Fixture.Extra.dll"));
  assert.ok(missing, "expected a MISSING failure naming Fixture.Extra.dll, got: " + r.failures.join("; "));
  for (const searchedRoot of ["artifacts", "extensions", "Fixture.Extra.dll"]) {
    assert.ok(missing.includes(searchedRoot), "the MISSING message must list the roots searched, got: " + missing);
  }
  assert.equal(fs.existsSync(fixture.packageDir), false, "a failed assemble must leave no package directory behind");
});

test("re-assembling into a dirty package directory leaves no leftover from the earlier run", () => {
  const fixture = fixtureRoot();
  assert.equal(assemble(fixture).ok, true);
  write(fixture.packageDir, "Leftover.dll", "MZ");

  const r = assemble(fixture);
  assert.equal(r.ok, true, r.failures.join("; "));
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...DECLARED].sort());
});

test("fails HARD: an empty artifacts array, rather than reporting a green zero-file copy", () => {
  const fixture = fixtureRoot({ artifacts: [] });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "an assemble that copies nothing inspected nothing — it must not exit green");
  assert.equal(r.copied.length, 0);
  assert.ok(r.failures.some((f) => f.startsWith("INVALID:")), r.failures.join("; "));
});

// The contract test for the promote decision: the shipped set is declared in exactly one place, so a
// catalog entry carrying only the older narrow field must fail rather than quietly resolve from it.
test("fails HARD: an entry declaring requiredBundledDlls but no artifacts — never a fallback", () => {
  const fixture = fixtureRoot({ artifacts: null, entry: { requiredBundledDlls: ["Fixture.Extra"] } });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "requiredBundledDlls must not be honoured as an alternative declaration");
  assert.equal(r.copied.length, 0);
  assert.ok(
    r.failures.some((f) => f.startsWith("MISSING:") && f.includes("declares no artifacts array")),
    r.failures.join("; "),
  );
});

test("fails: a name repeated in artifacts, rather than collapsing into one copy counted twice", () => {
  const fixture = fixtureRoot({ artifacts: ["Fixture.dll", "Fixture.Extra.dll", "Fixture.dll"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false);
  assert.ok(
    r.failures.some((f) => f.startsWith("DUPLICATE:") && f.includes("Fixture.dll")),
    r.failures.join("; "),
  );
  assert.equal(r.copied.length, 0, "the reported count must never exceed what was written");
});

test("two distinct failures are emitted in declaration order", () => {
  const fixture = fixtureRoot({ artifacts: ["Aardvark.dll", "Fixture.dll", "Zebra.dll"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false);
  assert.equal(r.failures.length, 2, r.failures.join("; "));
  assert.ok(r.failures[0].includes("Aardvark.dll"), r.failures.join("; "));
  assert.ok(r.failures[1].includes("Zebra.dll"), r.failures.join("; "));
});

test("refuses to write a shipped json carrying a Windows drive-root path, naming file and line", () => {
  // The drive letter, colon and separator are assembled from parts so this file's own source does not
  // read as a leak to the very scan it is exercising.
  const driveRoot = "C:" + String.fromCodePoint(92) + String.fromCodePoint(92) + "build" + String.fromCodePoint(92) + String.fromCodePoint(92) + "out";
  const fixture = fixtureRoot({
    artifacts: ["Fixture.dll", "Leaky.json"],
    publishFiles: { "Fixture.dll": "MZ", "Leaky.json": '{\n  "target": "' + driveRoot + '"\n}\n' },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a drive-root path in a shipped json must be refused");
  assert.ok(
    r.failures.some((f) => f.startsWith("LEAK:") && f.includes("Leaky.json") && f.includes(":2:")),
    "expected a LEAK failure naming Leaky.json line 2, got: " + r.failures.join("; "),
  );
  assert.equal(fs.existsSync(fixture.packageDir), false, "the refused json must not reach the package");
});

test("refuses to write a shipped json carrying a unix home path prefix, naming file and line", () => {
  const unixHome = "/" + "home" + "/" + "runner/work/out";
  const fixture = fixtureRoot({
    artifacts: ["Fixture.dll", "Leaky.json"],
    publishFiles: { "Fixture.dll": "MZ", "Leaky.json": '{\n  "target": "' + unixHome + '"\n}\n' },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a unix home path in a shipped json must be refused");
  assert.ok(
    r.failures.some((f) => f.startsWith("LEAK:") && f.includes("Leaky.json") && f.includes(":2:")),
    "expected a LEAK failure naming Leaky.json line 2, got: " + r.failures.join("; "),
  );
  assert.equal(fs.existsSync(fixture.packageDir), false, "the refused json must not reach the package");
});

test("rejects a declared name that escapes the package, before any source is read", () => {
  const escapes = [
    "/" + "etc/passwd",
    "C:" + String.fromCodePoint(92) + "windows" + String.fromCodePoint(92) + "evil.dll",
    ".." + "/outside.dll",
    "nested/Fixture.dll",
    String.fromCodePoint(92) + "server" + String.fromCodePoint(92) + "share.dll",
  ];

  for (const value of escapes) {
    const fixture = fixtureRoot({ artifacts: [value] });
    // Pointing at a publish dir that does not exist proves the rejection is on shape: a check that
    // needed to look for the file would fail differently, or not at all.
    const r = assemble(fixture, { publishDir: path.join(fixture.root, "no-such-publish-dir") });
    assert.equal(r.ok, false, "expected a rejection for " + value);
    assert.ok(
      r.failures.some((f) => f.startsWith("ESCAPE:")),
      "expected an ESCAPE failure for " + value + ", got: " + r.failures.join("; "),
    );
    assert.equal(fs.existsSync(fixture.packageDir), false);
  }
});

test("rejects a declared name that is not a non-empty string, before any source is read", () => {
  for (const value of ["", 42, null, {}]) {
    const fixture = fixtureRoot({ artifacts: [value] });
    const r = assemble(fixture, { publishDir: path.join(fixture.root, "no-such-publish-dir") });
    assert.equal(r.ok, false, "expected a rejection for " + JSON.stringify(value));
    assert.ok(
      r.failures.some((f) => f.startsWith("INVALID:")),
      "expected an INVALID failure for " + JSON.stringify(value) + ", got: " + r.failures.join("; "),
    );
  }
});

test("a .pdb and a .xml sitting in the publish output cannot reach the package", () => {
  const fixture = fixtureRoot({
    artifacts: ["Fixture.dll", "Fixture.deps.json"],
    publishFiles: {
      "Fixture.dll": "MZ",
      "Fixture.deps.json": "{}\n",
      "Fixture.pdb": "symbols",
      "Fixture.xml": "<doc/>",
    },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), ["Fixture.deps.json", "Fixture.dll"]);
});

test("stamps the packaged manifest with the version passed in, byte-for-byte and only there", () => {
  for (const version of ["0.0.0", "1.02.3"]) {
    const fixture = fixtureRoot();
    const r = assemble(fixture, { version });
    assert.equal(r.ok, true, r.failures.join("; "));

    const packaged = JSON.parse(fs.readFileSync(path.join(fixture.packageDir, "extension.json"), "utf8"));
    assert.equal(packaged.version, version, "the version must be written exactly as given, with no semver coercion");

    const source = JSON.parse(fs.readFileSync(path.join(fixture.root, "extensions", NAME, "src", NAME, "extension.json"), "utf8"));
    assert.equal(source.version, "0.1.0", "the source manifest must be left untouched");
  }
});

test("refuses a packageDir that is the repo root, the publish dir, or an ancestor of the publish dir", () => {
  const cases = [
    (f) => f.root,
    (f) => f.publishDir,
    (f) => path.dirname(f.publishDir),
  ];

  for (const pick of cases) {
    const fixture = fixtureRoot();
    const packageDir = pick(fixture);
    const r = assemble(fixture, { packageDir });

    assert.equal(r.ok, false, "expected a refusal for packageDir " + packageDir);
    assert.ok(r.failures.some((f) => f.startsWith("INVALID:")), r.failures.join("; "));
    // Nothing may have been removed on the way to that refusal.
    assert.equal(fs.existsSync(path.join(fixture.root, "LICENSE")), true);
    assert.equal(fs.existsSync(path.join(fixture.publishDir, "Fixture.dll")), true);
  }
});

// ── CLI leg ─────────────────────────────────────────────────────────────────────────────────────

function runCli(fixture, argv) {
  return spawnSync(process.execPath, [scriptPath, "--root", fixture.root, ...argv], { encoding: "utf8" });
}

function fullArgv(fixture, overrides = {}) {
  const values = {
    "--publish-dir": fixture.publishDir,
    "--package-dir": fixture.packageDir,
    "--extension": ID,
    "--version": VERSION,
    ...overrides,
  };
  return Object.entries(values).flat();
}

test("CLI: reports the entry, the version and a count equal to the files written", () => {
  const fixture = fixtureRoot();
  const run = runCli(fixture, fullArgv(fixture));

  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.ok(run.stdout.includes(ID), run.stdout);
  assert.ok(run.stdout.includes(VERSION), run.stdout);
  assert.ok(run.stdout.includes(String(DECLARED.length) + " file(s)"), run.stdout);
  assert.equal(fs.readdirSync(fixture.packageDir).length, DECLARED.length);
  for (const name of DECLARED) {
    assert.match(run.stdout, new RegExp("^ +" + name.replace(".", "\\.") + " ", "m"), "expected an indented line for " + name);
  }
});

test("CLI: two runs over identical input print byte-identical output", () => {
  const fixture = fixtureRoot();
  const first = runCli(fixture, fullArgv(fixture));
  const second = runCli(fixture, fullArgv(fixture));

  assert.equal(first.status, 0, first.stdout + first.stderr);
  assert.equal(second.stdout, first.stdout);
});

test("CLI: a missing declared artifact exits non-zero and writes no package", () => {
  const fixture = fixtureRoot({ publishFiles: { "Fixture.dll": "MZ", "Fixture.deps.json": "{}\n" } });
  const run = runCli(fixture, fullArgv(fixture));

  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /MISSING:/);
  assert.equal(fs.existsSync(fixture.packageDir), false);
});

test("CLI: each required flag omitted in turn prints usage and exits non-zero", () => {
  const fixture = fixtureRoot();
  for (const omitted of ["--publish-dir", "--package-dir", "--extension", "--version"]) {
    const argv = Object.entries({
      "--publish-dir": fixture.publishDir,
      "--package-dir": fixture.packageDir,
      "--extension": ID,
      "--version": VERSION,
    })
      .filter(([flag]) => flag !== omitted)
      .flat();

    const run = runCli(fixture, argv);
    assert.notEqual(run.status, 0, "omitting " + omitted + " must not exit 0");
    assert.match(run.stderr, /Usage:/, "omitting " + omitted + " must print usage");
  }
});
