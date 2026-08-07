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

// The core is imported directly, while the CLI cases below spawn ./assemble-package.mjs — the entry
// point a caller names — so the command line is exercised as a caller actually reaches it.
import { assemblePackage } from "./assemble-package-core.mjs";

const scriptPath = fileURLToPath(new URL("./assemble-package.mjs", import.meta.url));
const scriptDir = fileURLToPath(new URL("./", import.meta.url));

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

// Restages the fake build output under the CI layout — artifacts/publish/<Name> at the repo root
// rather than under the extension — because the descendant bypass below is only reachable with a
// publish directory that sits outside extensions/, which is exactly the shape build.yml passes.
function ciShapedPublish(fixture) {
  const publishDir = path.join(fixture.root, "artifacts", "publish", NAME);
  fs.mkdirSync(publishDir, { recursive: true });
  for (const name of fs.readdirSync(fixture.publishDir)) {
    fs.copyFileSync(path.join(fixture.publishDir, name), path.join(publishDir, name));
  }
  return publishDir;
}

function linkToDir(target, linkName) {
  // A junction needs no elevation on Windows, where a directory symlink does; elsewhere a directory
  // symlink is the same identity-through-an-alias. Picking per platform is what makes this portable.
  const linkType = process.platform === "win32" ? "junction" : "dir";
  const link = path.join(tmpDir(), linkName);
  fs.symlinkSync(target, link, linkType);
  return { link, linkType };
}

/**
 * The three spellings of a destructive packageDir that phase 22's verifier reproduced, each building
 * its own fixture. Shared between the per-spelling refusal cases and the "removes nothing" case so a
 * spelling cannot be closed in one and forgotten in the other.
 *
 * Every fixture root here is a fresh OS temp directory. These cases are destructive by construction
 * against an unfixed packer — that discipline is the only thing between them and a real tree, so no
 * packageDir in this file may ever point outside tmpDir().
 */
const DESTRUCTIVE_SPELLINGS = [
  // The same directory, a different string: on Windows a drive letter is case-insensitive to the
  // filesystem and case-sensitive to `===`, and this workspace's own docs spell its root both ways.
  // A platform with no drive letters has no such spelling, so it exercises the equivalent alias
  // through a symlink — reported, never skipped.
  function driveCaseAlias() {
    const fixture = fixtureRoot();
    const drive = /^([A-Za-z]):/.exec(fixture.root);
    if (drive) {
      const flipped = drive[1] === drive[1].toLowerCase() ? drive[1].toUpperCase() : drive[1].toLowerCase();
      return {
        fixture,
        form: "drive-letter case alias (" + flipped + ": for " + drive[1] + ":)",
        overrides: { packageDir: flipped + fixture.root.slice(1) },
      };
    }
    const { link } = linkToDir(fixture.root, "root-alias");
    return {
      fixture,
      form: "directory symlink alias (this platform spells no drive letter)",
      overrides: { packageDir: link },
    };
  },
  function linkAlias() {
    const fixture = fixtureRoot();
    const { link, linkType } = linkToDir(fixture.root, "link-to-root");
    return { fixture, form: linkType + " to the repo root", overrides: { packageDir: link } };
  },
  // The upward-only ancestor test had nothing to say about this one: <root>/extensions is neither the
  // root nor an ancestor of it, so a green run replaced the catalog and the whole source tree with
  // the packaged files.
  function sourceBearingDescendant() {
    const fixture = fixtureRoot();
    return {
      fixture,
      form: "<root>/extensions with a CI-shaped publish dir",
      overrides: { packageDir: path.join(fixture.root, "extensions"), publishDir: ciShapedPublish(fixture) },
    };
  },
];

function snapshotTree(root) {
  const sizes = new Map();
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else sizes.set(path.relative(root, full), fs.statSync(full).size);
    }
  };
  walk(root);
  return sizes;
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

// A declaration can name only files that exist and still describe a package the host cannot load.
// These are the replacement for the two checks that went with the deleted strip-verification gate, so
// each one names the property rather than the gate.

test("fails: a declaration with no source manifest — the host cannot load a package without one", () => {
  const fixture = fixtureRoot({ artifacts: ["Fixture.deps.json"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a package with no load manifest is one the host cannot install");
  assert.ok(
    r.failures.some((f) => f.startsWith("UNLOADABLE:") && f.includes("extension.json")),
    "expected an UNLOADABLE failure naming the manifest, got: " + r.failures.join("; "),
  );
  assert.equal(fs.existsSync(fixture.packageDir), false, "nothing may be written for an unloadable declaration");
});

test("fails: a manifest whose entry assembly is not in the declared set", () => {
  const fixture = fixtureRoot({ artifacts: ["extension.json", "Fixture.deps.json"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a manifest naming an assembly the package does not carry cannot load");
  assert.ok(
    r.failures.some((f) => f.startsWith("UNLOADABLE:") && f.includes("entryDll") && f.includes("Fixture.dll")),
    "expected an UNLOADABLE failure naming both the field and its value, got: " + r.failures.join("; "),
  );
});

test("fails: a manifest whose script bundle is not in the declared set", () => {
  const fixture = fixtureRoot({ artifacts: ["extension.json", "Fixture.dll"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false);
  assert.ok(
    r.failures.some((f) => f.startsWith("UNLOADABLE:") && f.includes("jsBundle") && f.includes("bundle.mjs")),
    "expected an UNLOADABLE failure naming both the field and its value, got: " + r.failures.join("; "),
  );
});

test("fails: a manifest whose stylesheet bundle is not in the declared set", () => {
  const fixture = fixtureRoot({ manifest: { cssBundle: "styles.css" } });
  const r = assemble(fixture);

  assert.equal(r.ok, false);
  assert.ok(
    r.failures.some((f) => f.startsWith("UNLOADABLE:") && f.includes("cssBundle") && f.includes("styles.css")),
    "expected an UNLOADABLE failure naming both the field and its value, got: " + r.failures.join("; "),
  );
});

// The other half of the same check: a field the manifest does not carry makes no claim, so it must not
// be failed for. Without this the branch above could pass by refusing everything.
test("a manifest declaring no stylesheet bundle is not failed for the field it does not have", () => {
  const fixture = fixtureRoot();
  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  assert.equal(
    r.failures.filter((f) => f.includes("cssBundle")).length,
    0,
    "an absent optional field must not be treated as an unsatisfied claim",
  );
});

// The loadability failures are pushed rather than returned, so one run still reports everything wrong
// with a declaration. The two LEAK cases above are the other half of that cover: they declare a subset
// carrying no manifest, so a short-circuiting refusal would fire before the scan they exist to exercise.
test("an unloadable declaration and an absolute path in a shipped json are reported in one result", () => {
  const driveRoot = "C:" + String.fromCodePoint(92) + String.fromCodePoint(92) + "build" + String.fromCodePoint(92) + String.fromCodePoint(92) + "out";
  const fixture = fixtureRoot({
    artifacts: ["Leaky.json"],
    publishFiles: { "Leaky.json": '{\n  "target": "' + driveRoot + '"\n}\n' },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, false);
  assert.ok(
    r.failures.some((f) => f.startsWith("UNLOADABLE:")),
    "expected the unloadable declaration to be reported, got: " + r.failures.join("; "),
  );
  assert.ok(
    r.failures.some((f) => f.startsWith("LEAK:")),
    "an unloadable declaration must not short-circuit the absolute-path scan, got: " + r.failures.join("; "),
  );
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

test("refuses a packageDir that is the repo root spelled with a different drive-letter case", (t) => {
  const { fixture, form, overrides } = DESTRUCTIVE_SPELLINGS[0]();
  // Emitted so the passing run itself names which spelling ran on this platform: a case whose value
  // depends on having been exercised must report which form it exercised, never skip quietly.
  t.diagnostic("exercised form: " + form);

  const r = assemble(fixture, overrides);

  assert.equal(r.ok, false, "exercised form: " + form + " — " + overrides.packageDir + " is the fixture root and must be refused");
  assert.ok(
    r.failures.some((f) => f.startsWith("INVALID:")),
    "exercised form: " + form + " — expected an INVALID refusal, got: " + r.failures.join("; "),
  );
});

test("refuses a packageDir reached through a junction or symlink to a protected path", (t) => {
  const { fixture, form, overrides } = DESTRUCTIVE_SPELLINGS[1]();
  t.diagnostic("exercised form: " + form);

  const r = assemble(fixture, overrides);

  assert.equal(r.ok, false, "exercised form: " + form + " — a link resolving onto the repo root must be refused");
  assert.ok(
    r.failures.some((f) => f.startsWith("INVALID:")),
    "exercised form: " + form + " — expected an INVALID refusal, got: " + r.failures.join("; "),
  );
});

test("refuses a packageDir that contains the catalog, the extension source tree, the manifest or the UI directory", () => {
  const { fixture, form, overrides } = DESTRUCTIVE_SPELLINGS[2]();

  const r = assemble(fixture, overrides);

  assert.equal(r.ok, false, form + " — packageDir holds the catalog and the whole extension source tree");
  assert.ok(
    r.failures.some((f) => f.startsWith("INVALID:")),
    form + " — expected an INVALID refusal, got: " + r.failures.join("; "),
  );
  assert.deepEqual(
    fs.readdirSync(path.join(fixture.root, "extensions")).sort(),
    ["Fixture", "catalog.json"],
    form + " — the catalog and the extension source directory must both survive the refusal",
  );
});

test("a refused packageDir removes nothing — every fixture file still exists afterwards", () => {
  for (const spelling of DESTRUCTIVE_SPELLINGS) {
    const { fixture, form, overrides } = spelling();
    const before = snapshotTree(fixture.root);

    const r = assemble(fixture, overrides);
    assert.equal(r.ok, false, form + " — expected a refusal, got ok:true");

    for (const [relative, size] of before) {
      const full = path.join(fixture.root, relative);
      assert.equal(fs.existsSync(full), true, form + " — a refusal removed " + relative);
      assert.equal(fs.statSync(full).size, size, form + " — a refusal rewrote " + relative);
    }
    // Named as well as swept, because these are what each reproduction was measured destroying.
    for (const survivor of ["extensions/catalog.json", "extensions/Fixture/README.md", "LICENSE"]) {
      assert.equal(fs.existsSync(path.join(fixture.root, survivor)), true, form + " — " + survivor + " was removed");
    }
    const publishDir = overrides.publishDir ?? fixture.publishDir;
    assert.ok(fs.readdirSync(publishDir).length > 0, form + " — the publish directory was emptied by a refused run");
  }
});

test("the three real caller shapes still assemble", () => {
  // The widened refusal only holds if no live caller's package directory collides with the protected
  // set. Each shape is exercised against a fixture rather than assumed safe.
  const shapes = [
    {
      name: "CI: artifacts/publish/<Name> -> artifacts/<Name>",
      build: (f) => ({ publishDir: ciShapedPublish(f), packageDir: path.join(f.root, "artifacts", NAME) }),
    },
    {
      name: "dev deploy: <ext>/artifacts/publish -> <ext>/artifacts/package",
      build: (f) => ({ publishDir: f.publishDir, packageDir: f.packageDir }),
    },
    {
      name: "E2E harness: a per-run temp directory",
      build: (f) => ({ publishDir: f.publishDir, packageDir: path.join(tmpDir(), ID) }),
    },
  ];

  for (const shape of shapes) {
    const fixture = fixtureRoot();
    const overrides = shape.build(fixture);
    const r = assemble(fixture, overrides);

    assert.equal(r.ok, true, shape.name + " — " + r.failures.join("; "));
    assert.deepEqual(fs.readdirSync(overrides.packageDir).sort(), [...DECLARED].sort(), shape.name);
  }
});

test("refuses to empty a package directory that holds anything other than regular files", (t) => {
  const linkType = process.platform === "win32" ? "junction" : "dir";
  const cases = [
    {
      form: "a subdirectory",
      plant: (dir) => {
        fs.mkdirSync(path.join(dir, "nested"), { recursive: true });
        fs.writeFileSync(path.join(dir, "nested", "someone-elses-data.txt"), "keep me");
        return "nested";
      },
    },
    {
      form: "a " + linkType + " entry",
      plant: (dir) => {
        const target = tmpDir();
        fs.writeFileSync(path.join(target, "someone-elses-data.txt"), "keep me");
        fs.symlinkSync(target, path.join(dir, "linked"), linkType);
        return "linked";
      },
    },
  ];

  for (const { form, plant } of cases) {
    t.diagnostic("exercised form: " + form);
    const fixture = fixtureRoot();
    fs.mkdirSync(fixture.packageDir, { recursive: true });
    const planted = plant(fixture.packageDir);
    // Sorts before both planted names, so a refusal raised partway through the removal loop rather
    // than before it would already have taken this file.
    write(fixture.packageDir, "Leftover.dll", "MZ");

    const r = assemble(fixture);

    assert.equal(r.ok, false, form + " — a package directory holding a non-file entry must be refused");
    assert.ok(
      r.failures.some((f) => f.startsWith("INVALID:") && f.includes(planted)),
      form + " — expected an INVALID failure naming " + planted + ", got: " + r.failures.join("; "),
    );
    assert.equal(fs.existsSync(path.join(fixture.packageDir, planted)), true, form + " — " + planted + " was removed");
    assert.equal(
      fs.existsSync(path.join(fixture.packageDir, planted, "someone-elses-data.txt")),
      true,
      form + " — data under " + planted + " was removed",
    );
    assert.equal(
      fs.existsSync(path.join(fixture.packageDir, "Leftover.dll")),
      true,
      form + " — a sibling file was removed before the refusal fired",
    );
  }
});

// ── CLI leg ─────────────────────────────────────────────────────────────────────────────────────

function runCli(fixture, argv, script = scriptPath) {
  return spawnSync(process.execPath, [script, "--root", fixture.root, ...argv], { encoding: "utf8" });
}

/**
 * The entry script reached through an alias of its own directory — the same mechanism this
 * repository's documented worktree workflow uses, which is why an invocation path that changes
 * behaviour is a defect here rather than a hypothetical.
 *
 * A failure to create the link is asserted, never skipped: a case that quietly does not run restores
 * exactly the zero-input-green shape these two cases exist to remove.
 */
function aliasedEntryScript() {
  const linkType = process.platform === "win32" ? "junction" : "dir";
  const link = path.join(tmpDir(), "scripts-alias");
  try {
    fs.symlinkSync(scriptDir, link, linkType);
  } catch (error) {
    assert.fail(
      "could not create a " + linkType + " alias of the scripts directory in this environment, so the " +
        "aliased-invocation case could not run: " + error.message,
    );
  }
  return { aliased: path.join(link, path.basename(scriptPath)), form: linkType + " alias of the scripts directory" };
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

// One directory has many spellings, and which one a caller happens to use must not decide whether the
// script does anything at all. These two cases are the whole cover for that: run with no arguments the
// entry point must refuse, and run with a full set it must assemble — identically through an alias and
// through the real path.
test("CLI: invoked through an aliased path, no arguments still prints usage and exits non-zero", (t) => {
  const { aliased, form } = aliasedEntryScript();
  t.diagnostic("exercised form: " + form);

  const run = spawnSync(process.execPath, [aliased], { encoding: "utf8" });

  assert.notEqual(
    run.status,
    0,
    form + " — a run that assembled nothing must not exit 0; stdout: " + JSON.stringify(run.stdout) +
      " stderr: " + JSON.stringify(run.stderr),
  );
  assert.match(run.stderr, /Usage:/, form + " — expected usage on stderr, got: " + JSON.stringify(run.stderr));
});

test("CLI: invoked through an aliased path, a full invocation still assembles the declared set", (t) => {
  const fixture = fixtureRoot();
  const { aliased, form } = aliasedEntryScript();
  t.diagnostic("exercised form: " + form);

  const run = runCli(fixture, fullArgv(fixture), aliased);

  assert.equal(run.status, 0, form + " — " + run.stdout + run.stderr);
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...DECLARED].sort(), form);
});
