import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { checkReleaseTag, filterLegs, listField, selectEntries } from "./ci-catalog.mjs";

const renamer = {
  name: "Renamer",
  id: "com.example.renamer",
  path: "extensions/Renamer",
  tagPrefix: "renamer/",
  manifestPath: "extensions/Renamer/extension.json",
  registryManifestPath: "extensions/Renamer/registry.json",
  uiPath: "extensions/Renamer/ui",
  testProjectPath: "extensions/Renamer/Renamer.Tests.csproj",
};
const other = {
  name: "Other",
  id: "com.example.other",
  path: "extensions/Other",
  tagPrefix: "other/",
};

function fixture({ manifestVersion = "1.2.0", registryVersions = ["1.2.0", "1.1.0"] } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ci-catalog-"));
  const write = (relative, value) => {
    fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
    fs.writeFileSync(path.join(root, relative), JSON.stringify(value));
  };
  write("extensions/catalog.json", { schemaVersion: 1, extensions: [renamer, other] });
  write(renamer.manifestPath, { id: renamer.id, version: manifestVersion });
  if (registryVersions !== null) {
    write(renamer.registryManifestPath, {
      versions: registryVersions.map((version) => ({ version })),
    });
  }
  return root;
}

test("a branch or pull-request ref selects every entry, a release tag only the one it names", () => {
  const entries = [renamer, other];
  assert.deepEqual(selectEntries(entries, "refs/pull/7/merge"), entries);
  assert.deepEqual(selectEntries(entries, "refs/heads/main"), entries);
  assert.deepEqual(selectEntries(entries, "refs/tags/renamer/v1.2.0"), [renamer]);
});

test("a release tag agreeing with the manifest and the newest registry row passes", () => {
  const line = checkReleaseTag(fixture(), "refs/tags/renamer/v1.2.0");
  assert.match(line, /releases com\.example\.renamer 1\.2\.0/);
  assert.match(line, /versions\[0\] is 1\.2\.0/);
});

test("a release tag is refused when it matches no prefix or carries no semver version", () => {
  assert.throws(() => checkReleaseTag(fixture(), "refs/tags/nothing/v1.2.0"), /matched 0/);
  assert.throws(() => checkReleaseTag(fixture(), "refs/tags/renamer/v1.2"), /not valid semver/);
});

test("a release tag is refused when the manifest declares a different version", () => {
  assert.throws(
    () => checkReleaseTag(fixture({ manifestVersion: "1.1.0" }), "refs/tags/renamer/v1.2.0"),
    /declares version 1\.1\.0/,
  );
});

test("a release tag is refused when the registry's newest row is not this release", () => {
  assert.throws(
    () => checkReleaseTag(fixture({ registryVersions: ["1.1.0"] }), "refs/tags/renamer/v1.2.0"),
    /prepend a versions\[\] row for 1\.2\.0 \(versions\[0\] is 1\.1\.0\)/,
  );
});

test("an extension with no registry manifest yet is released without a row check", () => {
  const line = checkReleaseTag(fixture({ registryVersions: null }), "refs/tags/renamer/v1.2.0");
  assert.match(line, /no registry manifest/);
});

test("legs are filtered to the tagged entry, and an empty result is refused", () => {
  const legs = {
    include: [
      { extension: renamer, cove: { tag: "1.4.1", role: "floor", advisory: false } },
      { extension: other, cove: { tag: "1.4.1", role: "floor", advisory: false } },
    ],
  };
  assert.equal(filterLegs(legs, [renamer, other], "refs/pull/1/merge").include.length, 2);
  assert.deepEqual(
    filterLegs(legs, [renamer, other], "refs/tags/renamer/v1.0.0").include.map(
      (leg) => leg.extension.id,
    ),
    [renamer.id],
  );
  assert.throws(() => filterLegs({ include: [] }, [renamer], "refs/pull/1/merge"), /no leg/);
});

test("a field list skips entries that do not declare the field and refuses an empty list", () => {
  assert.deepEqual(listField([renamer, other], "uiPath"), [renamer.uiPath]);
  assert.throws(() => listField([other], "uiPath"), /No catalog entry declares uiPath/);
});
