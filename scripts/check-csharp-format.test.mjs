// Every expectation is transcribed by hand from the sentence `dotnet format` prints, so a drift in
// that sentence fails here rather than silently emptying the parse.
import { test } from "node:test";
import assert from "node:assert/strict";

import { projectsWithUnloadedReferences, splitWrapperArguments } from "./check-csharp-format.mjs";

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
