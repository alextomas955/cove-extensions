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

import { assemblePackage } from "./assemble-package.mjs";

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
 * `artifacts` overrides the declared set; `publishFiles` overrides what the fake build emitted;
 * `entry` and `manifest` merge into the catalog entry and the source manifest.
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
    ...(artifacts === undefined ? {} : { artifacts }),
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
  assert.ok(
    r.failures.some((f) => f.startsWith("MISSING:") && f.includes("Fixture.Extra.dll")),
    "expected a MISSING failure naming Fixture.Extra.dll, got: " + r.failures.join("; "),
  );
  assert.equal(fs.existsSync(fixture.packageDir), false, "a failed assemble must leave no package directory behind");
});
