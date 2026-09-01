// Every expectation is transcribed by hand from the sentence `dotnet format` prints, so a drift in
// that sentence fails here rather than silently emptying the parse.
import { test } from "node:test";
import assert from "node:assert/strict";

import { mkdtempSync, mkdirSync, writeFileSync, symlinkSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import {
  projectsWithUnloadedReferences,
  splitWrapperArguments,
  isSameFile,
} from "./check-csharp-format.mjs";

const REAL_LINE =
  "Required references did not load for Renamer.Cove.Tests or referenced project. Run `dotnet restore` prior to formatting.";

test("names the project the tool reported", () => {
  assert.deepEqual(projectsWithUnloadedReferences(REAL_LINE), ["Renamer.Cove.Tests"]);
});

test("finds the sentence among the tool's other output", () => {
  const output = [
    "Warnings were encountered while loading the workspace. Set the verbosity option to the 'diagnostic' level to log warnings.",
    REAL_LINE,
    "",
  ].join("\n");
  assert.deepEqual(projectsWithUnloadedReferences(output), ["Renamer.Cove.Tests"]);
});

test("collapses the line the tool emits more than once for one project", () => {
  assert.deepEqual(projectsWithUnloadedReferences([REAL_LINE, REAL_LINE].join("\n")), [
    "Renamer.Cove.Tests",
  ]);
});

test("returns both projects in first-appearance order", () => {
  const output = [
    "Required references did not load for Beta.Tests or referenced project. Run `dotnet restore` prior to formatting.",
    "Required references did not load for Alpha.Tests or referenced project. Run `dotnet restore` prior to formatting.",
  ].join("\n");
  assert.deepEqual(projectsWithUnloadedReferences(output), ["Beta.Tests", "Alpha.Tests"]);
});

test("reports nothing when the tool reported nothing", () => {
  assert.deepEqual(projectsWithUnloadedReferences(""), []);
  assert.deepEqual(
    projectsWithUnloadedReferences("Determining projects to restore...\nFormat complete.\n"),
    [],
  );
});

test("does not guess a project name from a line that is not the tool's sentence", () => {
  assert.deepEqual(
    projectsWithUnloadedReferences(
      "Some required references did not load for Renamer.Cove.Tests or referenced project.",
    ),
    [],
  );
  assert.deepEqual(
    projectsWithUnloadedReferences("Required references did not load for Renamer.Cove.Tests."),
    [],
  );
});

test("--fail-on-partial is kept out of the arguments dotnet format receives", () => {
  // dotnet format rejects an argument it does not know, so a wrapper flag left in the passthrough set
  // would fail every run that used it.
  const { failOnPartial, passthrough } = splitWrapperArguments([
    "--verify-no-changes",
    "--fail-on-partial",
    "--severity",
    "warn",
  ]);

  assert.equal(failOnPartial, true);
  assert.deepEqual(passthrough, ["--verify-no-changes", "--severity", "warn"]);
});

test("the wrapper flag is absent by default and every argument passes through", () => {
  const { failOnPartial, passthrough } = splitWrapperArguments(["--severity", "warn"]);

  assert.equal(failOnPartial, false);
  assert.deepEqual(passthrough, ["--severity", "warn"]);
});

test("a path reached through a symlink names the same file as the real one", () => {
  // The entry guard compares process.argv[1], which the caller spelled, against this module's own
  // realpathed location. This repo's worktree workflow reaches files through links, and an unequal
  // answer there means the guard silently never fires.
  const root = mkdtempSync(path.join(tmpdir(), "same-file-"));
  try {
    const real = path.join(root, "real");
    mkdirSync(real);
    const target = path.join(real, "script.mjs");
    writeFileSync(target, "export default 1;\n");
    const link = path.join(root, "link");
    symlinkSync(real, link, "dir");

    assert.equal(isSameFile(path.join(link, "script.mjs"), target, "linux"), true);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("two different files never name the same file", () => {
  const root = mkdtempSync(path.join(tmpdir(), "same-file-"));
  try {
    const a = path.join(root, "a.mjs");
    const b = path.join(root, "b.mjs");
    writeFileSync(a, "export default 1;\n");
    writeFileSync(b, "export default 1;\n");

    assert.equal(isSameFile(a, b, "linux"), false);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("case differing only in spelling names the same file on win32 and not elsewhere", () => {
  // Drive-letter casing varies by how the caller spelled the path, and Windows treats the two as one
  // file. Neither path exists, so this pins the casing branch rather than the realpath one.
  assert.equal(isSameFile("/Repo/Script.mjs", "/repo/script.mjs", "win32"), true);
  assert.equal(isSameFile("/Repo/Script.mjs", "/repo/script.mjs", "linux"), false);
});

test("an absent entry is not a script invocation, which is what an import looks like", () => {
  // Reading a member off an undefined argv[1] is how this threw on the runtimes it targets.
  assert.equal(isSameFile(undefined, "/repo/script.mjs", "linux"), false);
  assert.equal(isSameFile("", "/repo/script.mjs", "linux"), false);
  assert.equal(isSameFile("/repo/script.mjs", undefined, "linux"), false);
});

test("the URL spelling of a module's own path equals the filename spelling", () => {
  // import.meta.filename landed in Node 20.11, inside the range the entry guard exists for, so the
  // guard falls back to the URL form. The two have to name the same file for that fallback to hold.
  assert.equal(fileURLToPath(import.meta.url), import.meta.filename);
});
