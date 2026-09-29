import { test } from "node:test";
import assert from "node:assert/strict";

import { checkRelativePath, parseMsBuildProperties } from "./repo-files.mjs";

test("the property reader expands a $(Name) reference to the value already read", () => {
  const props = parseMsBuildProperties(`
    <Project><PropertyGroup>
      <CoveMinVersion>1.1.0</CoveMinVersion>
      <CoveSdkVersion Condition="'$(CoveSdkVersion)' == ''">$(CoveMinVersion)</CoveSdkVersion>
    </PropertyGroup></Project>
  `);

  assert.equal(props.CoveSdkVersion, "1.1.0");
});

test("a repo-relative catalog path passes, in either separator", () => {
  assert.equal(checkRelativePath("path", "extensions/Renamer"), null);
  assert.equal(checkRelativePath("path", "extensions\\Renamer\\e2e"), null);
  // A segment that merely starts with dots is a name, not a climb.
  assert.equal(checkRelativePath("path", "extensions/..hidden/x"), null);
});

test("an empty, absolute or climbing catalog path is refused, naming the field", () => {
  for (const [value, reason] of [
    ["", /must be a non-empty string/],
    [undefined, /must be a non-empty string/],
    ["/etc/extensions", /must be repo-relative, found an absolute path/],
    // Absolute on Windows only, so path.isAbsolute on a Linux runner would let it through.
    ["C:\\extensions", /must be repo-relative, found an absolute path/],
    ["c:extensions", /must be repo-relative, found an absolute path/],
    ["extensions/../../outside", /must contain no "\.\." segment/],
    ["extensions\\..\\outside", /must contain no "\.\." segment/],
  ]) {
    const result = checkRelativePath("e2ePath", value);
    assert.ok(result !== null, `${JSON.stringify(value)} was accepted`);
    assert.match(result, /^e2ePath /);
    assert.match(result, reason);
  }
});
