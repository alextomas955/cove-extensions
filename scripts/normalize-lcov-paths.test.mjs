import assert from "node:assert/strict";
import test from "node:test";

import { normalizeLcov, readUiPaths } from "./normalize-lcov-paths.mjs";

const UI_PATH = "extensions/Renamer/src/Renamer.Ui";

test("a path inside the UI package is anchored at the repository root", () => {
  const { text } = normalizeLcov("SF:src/settings/Foo.tsx\nend_of_record\n", UI_PATH);
  assert.match(text, /^SF:extensions\/Renamer\/src\/Renamer\.Ui\/src\/settings\/Foo\.tsx$/m);
});

test("a path climbing out of the UI package resolves to its real location", () => {
  // The form vitest writes for the shared package, which is a second project rooted outside the one
  // the config sits in. Left alone it names no file from the repository root.
  const { text } = normalizeLcov("SF:../../../../shared/ui-shared/src/overlay.ts\n", UI_PATH);
  assert.match(text, /^SF:shared\/ui-shared\/src\/overlay\.ts$/m);
});

test("backslashes become forward slashes, so a report written on Windows resolves on Linux", () => {
  const { text } = normalizeLcov(
    "SF:..\\..\\..\\..\\shared\\ui-shared\\src\\overlay.ts\n",
    UI_PATH,
  );
  assert.match(text, /^SF:shared\/ui-shared\/src\/overlay\.ts$/m);
  assert.ok(!text.includes("\\"), "no backslash may survive the rewrite");
});

test("every SF record is counted, and no other line is touched", () => {
  const input = [
    "TN:",
    "SF:src/a.ts",
    "DA:1,1",
    "LF:1",
    "LH:1",
    "end_of_record",
    "SF:src/b.ts",
    "end_of_record",
    "",
  ].join("\n");
  const { text, rewritten } = normalizeLcov(input, UI_PATH);
  assert.equal(rewritten, 2);
  for (const kept of ["TN:", "DA:1,1", "LF:1", "LH:1", "end_of_record"]) {
    assert.ok(text.includes(kept), `${kept} must survive unchanged`);
  }
});

test("a report with no SF records is reported as rewriting nothing", () => {
  // The caller refuses on this rather than writing a file the scanner reads as zero coverage.
  const { rewritten } = normalizeLcov("TN:\n", UI_PATH);
  assert.equal(rewritten, 0);
});

test("only catalog entries declaring a frontend are collected", () => {
  const uiPaths = readUiPaths(
    JSON.stringify({
      extensions: [{ name: "A", uiPath: "a/ui" }, { name: "B" }, { name: "C", uiPath: "c/ui" }],
    }),
  );
  assert.deepEqual(uiPaths, ["a/ui", "c/ui"]);
});
