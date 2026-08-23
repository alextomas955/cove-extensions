// Every case is driven from a fixture tree in a temp dir — a fake catalog plus a fake publish
// output — so no dotnet build, npm build or sibling Cove checkout is needed.
//
// The fixture's file names deliberately differ from any real extension's, so a passing case proves
// the resolution came from the catalog rather than from a name baked into the packer.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

// One module, reached both ways: assemblePackage is imported here exactly as the E2E harness imports
// it, while the CLI cases below SPAWN the same file, so the command line is exercised as a caller
// actually reaches it rather than by calling the function that sits behind it.
import { assemblePackage } from "./assemble-package.mjs";

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
function fixtureRoot({
  artifacts = DECLARED,
  publishFiles = null,
  entry = {},
  manifest = {},
} = {}) {
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
  write(
    root,
    "extensions/catalog.json",
    JSON.stringify({ schemaVersion: 1, extensions: [catalogEntry] }, null, 2) + "\n",
  );

  const sourceManifest = {
    id: ID,
    name: NAME,
    version: "0.1.0",
    entryDll: "Fixture.dll",
    jsBundle: "bundle.mjs",
    ...manifest,
  };
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
  return assemblePackage({
    root,
    publishDir,
    packageDir,
    idOrName: ID,
    version: VERSION,
    ...overrides,
  });
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

  assert.equal(
    r.ok,
    false,
    "expected a MISSING failure for the artifact the fake build did not emit",
  );
  const missing = r.failures.find(
    (f) => f.startsWith("MISSING:") && f.includes("Fixture.Extra.dll"),
  );
  assert.ok(
    missing,
    "expected a MISSING failure naming Fixture.Extra.dll, got: " + r.failures.join("; "),
  );
  for (const searchedRoot of ["artifacts", "extensions", "Fixture.Extra.dll"]) {
    assert.ok(
      missing.includes(searchedRoot),
      "the MISSING message must list the roots searched, got: " + missing,
    );
  }
  assert.equal(
    fs.existsSync(fixture.packageDir),
    false,
    "a failed assemble must leave no package directory behind",
  );
});

// This packer never deletes, so run-to-run contamination is refused rather than cleaned up: a second
// run into a directory holding the first run's output fails, naming the directory, and everything that
// was there stays there. The caller that re-uses a directory is the one that clears it.
test("refuses a package directory that is not empty, naming it, and removes nothing from it", () => {
  const fixture = fixtureRoot();
  assert.equal(assemble(fixture).ok, true);
  write(fixture.packageDir, "Leftover.dll", "MZ");

  const r = assemble(fixture);

  assert.equal(r.ok, false, "a second run into a populated package directory must be refused");
  const refusal = r.failures.find((f) => f.startsWith("INVALID:") && f.includes("not empty"));
  assert.ok(refusal, "expected a not-empty refusal, got: " + r.failures.join("; "));
  assert.ok(
    refusal.includes(fixture.packageDir),
    "the refusal must name the directory it refused, got: " + refusal,
  );
  assert.equal(r.copied.length, 0);
  assert.deepEqual(
    fs.readdirSync(fixture.packageDir).sort(),
    [...DECLARED, "Leftover.dll"].sort(),
    "a refusal must leave the earlier run's output and the leftover exactly as they were",
  );
});

// The negative control for the refusal above: a refusal that fired on every directory would pass that
// case while making the packer unusable. An absent directory is created, and an existing empty one is
// used as-is.
test("assembles into an absent package directory, and into an existing empty one", () => {
  for (const { form, prepare } of [
    { form: "absent", prepare: () => {} },
    { form: "existing but empty", prepare: (dir) => fs.mkdirSync(dir, { recursive: true }) },
  ]) {
    const fixture = fixtureRoot();
    prepare(fixture.packageDir);

    const r = assemble(fixture);

    assert.equal(r.ok, true, form + " — " + r.failures.join("; "));
    assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...DECLARED].sort(), form);
  }
});

// A packageDir pointed at a source tree is refused because that tree is not empty, and since
// nothing here deletes, the tree is byte-for-byte intact afterwards. Every destructive spelling —
// a drive-letter case alias, a junction, a source-bearing descendant — reduces to this one case.
test("a packageDir pointed at a populated source tree is refused and destroys nothing", () => {
  const fixture = fixtureRoot();
  const before = snapshotTree(fixture.root);

  const r = assemble(fixture, { packageDir: fixture.root });

  assert.equal(r.ok, false, "the repo root is not empty and must be refused");
  // Named, so this case cannot pass on some other refusal firing first — the claim is that the
  // not-empty refusal is what makes the retired protected-path set unnecessary.
  assert.ok(
    r.failures.some((f) => f.startsWith("INVALID:") && f.includes("not empty")),
    "expected the not-empty refusal, got: " + r.failures.join("; "),
  );
  for (const [relative, size] of before) {
    const full = path.join(fixture.root, relative);
    assert.equal(fs.existsSync(full), true, "a refusal removed " + relative);
    assert.equal(fs.statSync(full).size, size, "a refusal rewrote " + relative);
  }
  // Named as well as swept, because these are what each phase-22 reproduction was measured destroying.
  for (const survivor of ["extensions/catalog.json", "extensions/Fixture/README.md", "LICENSE"]) {
    assert.equal(fs.existsSync(path.join(fixture.root, survivor)), true, survivor + " was removed");
  }
});

test("fails HARD: an empty artifacts array, rather than reporting a green zero-file copy", () => {
  const fixture = fixtureRoot({ artifacts: [] });
  const r = assemble(fixture);

  assert.equal(
    r.ok,
    false,
    "an assemble that copies nothing inspected nothing — it must not exit green",
  );
  assert.equal(r.copied.length, 0);
  assert.ok(
    r.failures.some((f) => f.startsWith("INVALID:")),
    r.failures.join("; "),
  );
});

// The contract test for the promote decision: the shipped set is declared in exactly one place, so a
// catalog entry carrying only the older narrow field must fail rather than quietly resolve from it.
test("fails HARD: an entry declaring requiredBundledDlls but no artifacts — never a fallback", () => {
  const fixture = fixtureRoot({
    artifacts: null,
    entry: { requiredBundledDlls: ["Fixture.Extra"] },
  });
  const r = assemble(fixture);

  assert.equal(
    r.ok,
    false,
    "requiredBundledDlls must not be honoured as an alternative declaration",
  );
  assert.equal(r.copied.length, 0);
  assert.ok(
    r.failures.some((f) => f.startsWith("MISSING:") && f.includes("declares no artifacts array")),
    r.failures.join("; "),
  );
});

test("refuses to write a shipped json carrying a Windows drive-root path, naming file and line", () => {
  // The drive letter, colon and separator are assembled from parts so this file's own source does not
  // read as a leak to the very scan it is exercising.
  const driveRoot =
    "C:" +
    String.fromCodePoint(92) +
    String.fromCodePoint(92) +
    "build" +
    String.fromCodePoint(92) +
    String.fromCodePoint(92) +
    "out";
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
  assert.equal(
    fs.existsSync(fixture.packageDir),
    false,
    "the refused json must not reach the package",
  );
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
  assert.equal(
    fs.existsSync(fixture.packageDir),
    false,
    "the refused json must not reach the package",
  );
});

// An absolute path reaches a shipped json in more spellings than the two cases above cover, and a
// Windows path inside json is escaped — so the text the scan actually meets is not the text a human
// writes. Every needle below is assembled from character codes for the same reason those two are.
function leakyFixture(value) {
  return fixtureRoot({
    artifacts: ["Fixture.dll", "Leaky.json"],
    publishFiles: { "Fixture.dll": "MZ", "Leaky.json": '{\n  "target": "' + value + '"\n}\n' },
  });
}

const BACKSLASH = String.fromCodePoint(92);
const FORWARD_SLASH = String.fromCodePoint(47);
// A drive path as a json file escapes it.
const DRIVE_ESCAPED = "C:" + BACKSLASH + BACKSLASH + "build" + BACKSLASH + BACKSLASH + "out";
// A network share in both spellings: raw, and escaped the way a generated json carries it.
const SHARE_RAW = BACKSLASH + BACKSLASH + "buildsrv" + BACKSLASH + "share" + BACKSLASH + "out";
const SHARE_ESCAPED =
  BACKSLASH +
  BACKSLASH +
  BACKSLASH +
  BACKSLASH +
  "buildsrv" +
  BACKSLASH +
  BACKSLASH +
  "share" +
  BACKSLASH +
  BACKSLASH +
  "out";

test("refuses a shipped json carrying a network share path, in the spelling a generated json contains", () => {
  for (const { form, value } of [
    { form: "escaped, as a generated json spells it", value: SHARE_ESCAPED },
    { form: "raw, as a build tool writes it", value: SHARE_RAW },
  ]) {
    const fixture = leakyFixture(value);
    const r = assemble(fixture);

    assert.equal(r.ok, false, form + " — a network share path is absolute and must be refused");
    assert.ok(
      r.failures.some(
        (f) => f.startsWith("LEAK:") && f.includes("Leaky.json") && f.includes(":2:"),
      ),
      form + " — expected a LEAK failure naming Leaky.json line 2, got: " + r.failures.join("; "),
    );
    assert.equal(
      fs.existsSync(fixture.packageDir),
      false,
      form + " — the refused json must not reach the package",
    );
  }
});

// Separate markers, so a future narrowing of one cannot silently narrow the other — which is worth
// nothing unless each still refuses only its own class. A share marker written against the raw
// spelling alone collapses into the drive one, since an escaped drive path also carries doubled
// backslashes.
test("the two absolute-path marker families stay distinguishable", () => {
  const drive = assemble(leakyFixture(DRIVE_ESCAPED))
    .failures.filter((f) => f.startsWith("LEAK:"))
    .join("; ");
  const share = assemble(leakyFixture(SHARE_ESCAPED))
    .failures.filter((f) => f.startsWith("LEAK:"))
    .join("; ");

  assert.ok(
    drive.includes("drive root"),
    "a drive line must be named as a drive root, got: " + drive,
  );
  assert.ok(
    !drive.includes("network share"),
    "a drive line must not read as a network share, got: " + drive,
  );
  assert.ok(
    share.includes("network share"),
    "a share line must be named as a network share, got: " + share,
  );
  assert.ok(
    !share.includes("drive root"),
    "a share line must not read as a drive root, got: " + share,
  );
});

// The whole subject of the comment beside the markers: a project url is a scheme, a colon and two
// forward slashes, and the manifest carrying it is itself a shipped json this scan reads. Without this
// case the widening would rest on the same untested reasoning the comment it replaced did.
test("the three marker families produce no hit on a project url value", () => {
  const url = "https" + ":" + FORWARD_SLASH + FORWARD_SLASH + "github.com/alextomas955/extensions";
  const fixture = fixtureRoot({ manifest: { url } });
  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  assert.equal(
    r.failures.filter((f) => f.startsWith("LEAK:")).length,
    0,
    "a project url is not an absolute build path and must not be refused as one",
  );
  // Asserted rather than assumed: a fixture whose value never reached the scanned text would pass this
  // case while proving nothing.
  const packaged = JSON.parse(
    fs.readFileSync(path.join(fixture.packageDir, "extension.json"), "utf8"),
  );
  assert.equal(
    packaged.url,
    url,
    "the url must have reached the packaged manifest for this case to mean anything",
  );
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
  assert.equal(
    fs.existsSync(fixture.packageDir),
    false,
    "nothing may be written for an unloadable declaration",
  );
});

test("fails: a manifest whose entry assembly is not in the declared set", () => {
  const fixture = fixtureRoot({ artifacts: ["extension.json", "Fixture.deps.json"] });
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a manifest naming an assembly the package does not carry cannot load");
  assert.ok(
    r.failures.some(
      (f) => f.startsWith("UNLOADABLE:") && f.includes("entryDll") && f.includes("Fixture.dll"),
    ),
    "expected an UNLOADABLE failure naming both the field and its value, got: " +
      r.failures.join("; "),
  );
});

// The other half of the same check: the loadability check walks one list of optional manifest fields,
// and a field the manifest does not carry makes no claim, so it must not be failed for. Without this
// the entryDll case above could pass on a check that refused every field, present or not.
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

// The present half of the pair above. The ui-bundle rule covers BOTH bundle fields, and the assertion
// is on the RESOLVED ROOT rather than on `ok`: a run that found the stylesheet in the publish directory
// or beside the manifest would succeed just as happily, and that is exactly the resolution mistake this
// case exists to pin.
test("a declared stylesheet bundle resolves from the UI build output", () => {
  const fixture = fixtureRoot({
    manifest: { cssBundle: "bundle.css" },
    artifacts: [...DECLARED, "bundle.css"],
  });
  write(
    fixture.root,
    "extensions/" + NAME + "/src/" + NAME + ".Ui/dist/bundle.css",
    ".fixture-root .pb-20 { padding-bottom: 5rem }\n",
  );

  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  const copied = r.copied.find((f) => f.name === "bundle.css");
  assert.ok(copied, "the declared stylesheet was not copied at all");
  assert.equal(copied.root, "ui-bundle");
});

test("fails: a declared cssBundle the artifacts array does not carry", () => {
  const fixture = fixtureRoot({ manifest: { cssBundle: "bundle.css" } });
  const r = assemble(fixture);

  assert.equal(
    r.ok,
    false,
    "a manifest naming a stylesheet the package does not carry cannot load",
  );
  assert.ok(
    r.failures.some(
      (f) => f.startsWith("UNLOADABLE:") && f.includes("cssBundle") && f.includes("bundle.css"),
    ),
    "expected an UNLOADABLE failure naming both the field and its value, got: " +
      r.failures.join("; "),
  );
});

// The loadability failures are pushed rather than returned, so one run still reports everything wrong
// with a declaration. The two LEAK cases above are the other half of that cover: they declare a subset
// carrying no manifest, so a short-circuiting refusal would fire before the scan they exist to exercise.
test("an unloadable declaration and an absolute path in a shipped json are reported in one result", () => {
  const driveRoot =
    "C:" +
    String.fromCodePoint(92) +
    String.fromCodePoint(92) +
    "build" +
    String.fromCodePoint(92) +
    String.fromCodePoint(92) +
    "out";
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
    "an unloadable declaration must not short-circuit the absolute-path scan, got: " +
      r.failures.join("; "),
  );
});

test("a .pdb and a .xml sitting in the publish output cannot reach the package", () => {
  // A loadable declaration — the manifest and the two files it names — that still omits the symbol and
  // documentation files the fake build emitted beside them. The point is what a declaration leaves out,
  // so it has to be a declaration that would otherwise ship.
  const shipped = ["extension.json", "bundle.mjs", "Fixture.dll", "Fixture.deps.json"];
  const fixture = fixtureRoot({
    artifacts: shipped,
    publishFiles: {
      "Fixture.dll": "MZ",
      "Fixture.deps.json": "{}\n",
      "Fixture.pdb": "symbols",
      "Fixture.xml": "<doc/>",
    },
  });
  const r = assemble(fixture);

  assert.equal(r.ok, true, r.failures.join("; "));
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...shipped].sort());
  for (const excluded of ["Fixture.pdb", "Fixture.xml"]) {
    assert.equal(
      fs.existsSync(path.join(fixture.packageDir, excluded)),
      false,
      excluded +
        " was emitted by the build and must not reach the package, because it is not declared",
    );
  }
});

test("stamps the packaged manifest with the version passed in, byte-for-byte and only there", () => {
  for (const version of ["0.0.0", "1.02.3"]) {
    const fixture = fixtureRoot();
    const r = assemble(fixture, { version });
    assert.equal(r.ok, true, r.failures.join("; "));

    const packaged = JSON.parse(
      fs.readFileSync(path.join(fixture.packageDir, "extension.json"), "utf8"),
    );
    assert.equal(
      packaged.version,
      version,
      "the version must be written exactly as given, with no semver coercion",
    );

    const source = JSON.parse(
      fs.readFileSync(
        path.join(fixture.root, "extensions", NAME, "src", NAME, "extension.json"),
        "utf8",
      ),
    );
    assert.equal(source.version, "0.1.0", "the source manifest must be left untouched");
  }
});

test("the three real caller shapes still assemble", () => {
  // The refusal only holds if every live caller hands over a fresh directory. Each shape is exercised
  // against a fixture rather than assumed safe.
  const shapes = [
    {
      name: "CI: artifacts/publish/<Name> -> artifacts/<Name>",
      build: (f) => ({
        publishDir: ciShapedPublish(f),
        packageDir: path.join(f.root, "artifacts", NAME),
      }),
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

// Resolution is an existence check and the copy happens later, so a source that resolves can still
// fail to be written. The failure is REPORTED rather than thrown — a caller that reads failures would
// otherwise see an exception escape the exported function instead — and the loop stops there rather
// than reporting a count for a package that is not on disk.
test("a write that fails partway is a reported WRITE failure, not a throw, and stops the loop", () => {
  const fixture = fixtureRoot();
  // A directory bearing a declared artifact's name. The existence check that resolves a source is
  // satisfied by a directory, so resolution succeeds and the copy is what fails — and it is the
  // fourth declared name, so the loop has already written three files when it does.
  const planted = "Fixture.Extra.dll";
  fs.rmSync(path.join(fixture.publishDir, planted));
  fs.mkdirSync(path.join(fixture.publishDir, planted));

  // Called with no try/catch on purpose: an exception escaping the exported function fails this case
  // as itself, which is the behaviour this exists to keep out.
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a write that could not happen must not report success");
  assert.ok(
    r.failures.some((f) => f.startsWith("WRITE:") && f.includes(planted)),
    "expected a WRITE failure naming " + planted + ", got: " + r.failures.join("; "),
  );
  assert.deepEqual(
    r.copied.map((f) => f.name),
    ["extension.json", "bundle.mjs", "Fixture.dll"],
    "the reported count must be what landed, and the loop must stop at the failure",
  );
});

// The case above plants a .dll, which the pre-write json scan skips. A declared .json reaches that
// scan first, and it reads the source rather than only testing that it exists — so the same planted
// directory arrives at a read instead of at a copy, on the path that runs BEFORE anything is written.
test("a declared json whose source cannot be read is a reported failure, not a throw", () => {
  const planted = "Fixture.deps.json";
  const fixture = fixtureRoot();
  fs.rmSync(path.join(fixture.publishDir, planted));
  fs.mkdirSync(path.join(fixture.publishDir, planted));

  // No try/catch, for the reason the case above states.
  const r = assemble(fixture);

  assert.equal(r.ok, false, "a json that could not be read must not report success");
  assert.ok(
    r.failures.some((f) => f.startsWith("UNREADABLE:") && f.includes(planted)),
    "expected an UNREADABLE failure naming " + planted + ", got: " + r.failures.join("; "),
  );
  assert.deepEqual(r.copied, [], "the refusal happens before the first write");
  assert.equal(
    fs.existsSync(fixture.packageDir),
    false,
    "a run refused before writing must leave no package directory behind",
  );
});

// ── CLI leg ─────────────────────────────────────────────────────────────────────────────────────

function runCli(fixture, argv, script = scriptPath) {
  return spawnSync(process.execPath, [script, "--root", fixture.root, ...argv], {
    encoding: "utf8",
  });
}

/**
 * The entry script reached through an alias of its own directory — the mechanism this repository's
 * worktree workflow uses, so an invocation path that changes behaviour is a real defect here.
 *
 * A failure to create the link is asserted rather than skipped: a case that quietly does not run is
 * the zero-input-green shape these cases exist to remove.
 */
function aliasedEntryScript() {
  const linkType = process.platform === "win32" ? "junction" : "dir";
  const link = path.join(tmpDir(), "scripts-alias");
  try {
    fs.symlinkSync(scriptDir, link, linkType);
  } catch (error) {
    assert.fail(
      "could not create a " +
        linkType +
        " alias of the scripts directory in this environment, so the " +
        "aliased-invocation case could not run: " +
        error.message,
    );
  }
  return {
    aliased: path.join(link, path.basename(scriptPath)),
    form: linkType + " alias of the scripts directory",
  };
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

test("CLI: two runs over identical input print byte-identical output", () => {
  const fixture = fixtureRoot();
  const first = runCli(fixture, fullArgv(fixture));
  // The caller clears the directory between runs, which is what the packer's refusal now requires of
  // one that re-uses a path — the same remove-and-recreate the dev deploy does. Without it the second
  // run is refused, and determinism would be measured over two different code paths.
  fs.rmSync(fixture.packageDir, { recursive: true, force: true });
  const second = runCli(fixture, fullArgv(fixture));

  assert.equal(first.status, 0, first.stdout + first.stderr);
  assert.equal(second.status, 0, second.stdout + second.stderr);
  assert.equal(second.stdout, first.stdout);
});

// The exported function reports what landed, and the command line has to say the same thing. A write
// that fails partway is the one refusal that leaves files behind, and the next run is refused for a
// directory that is not empty — so a caller told nothing was written has no account of what put them
// there.
test("CLI: a write that fails partway names what it wrote, and does not claim it wrote nothing", () => {
  const planted = "Fixture.Extra.dll";
  const fixture = fixtureRoot();
  fs.rmSync(path.join(fixture.publishDir, planted));
  fs.mkdirSync(path.join(fixture.publishDir, planted));

  const run = runCli(fixture, fullArgv(fixture));

  assert.equal(run.status, 1, run.stdout + run.stderr);
  assert.match(run.stderr, /^WRITE: /m);
  assert.doesNotMatch(
    run.stderr,
    /nothing was written/,
    "files were written, so the summary must not say otherwise: " + run.stderr,
  );
  assert.match(run.stderr, /INCOMPLETE/);
  assert.deepEqual(
    fs.readdirSync(fixture.packageDir).sort(),
    ["Fixture.dll", "bundle.mjs", "extension.json"],
    "the assertion above is only meaningful while these files really are on disk",
  );
});

// One directory has many spellings, and which one a caller uses must not decide whether the script
// does anything. Run with no arguments the entry point must refuse; run with a full set it must
// assemble identically through an alias and through the real path.
test("CLI: invoked through an aliased path, no arguments still prints usage and exits non-zero", (t) => {
  const { aliased, form } = aliasedEntryScript();
  t.diagnostic("exercised form: " + form);

  const run = spawnSync(process.execPath, [aliased], { encoding: "utf8" });

  assert.notEqual(
    run.status,
    0,
    form +
      " — a run that assembled nothing must not exit 0; stdout: " +
      JSON.stringify(run.stdout) +
      " stderr: " +
      JSON.stringify(run.stderr),
  );
  assert.match(
    run.stderr,
    /Usage:/,
    form + " — expected usage on stderr, got: " + JSON.stringify(run.stderr),
  );
});

test("CLI: invoked through an aliased path, a full invocation still assembles the declared set", (t) => {
  const fixture = fixtureRoot();
  const { aliased, form } = aliasedEntryScript();
  t.diagnostic("exercised form: " + form);

  const run = runCli(fixture, fullArgv(fixture), aliased);

  assert.equal(run.status, 0, form + " — " + run.stdout + run.stderr);
  assert.deepEqual(fs.readdirSync(fixture.packageDir).sort(), [...DECLARED].sort(), form);
});
