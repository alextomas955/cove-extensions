// Behavior coverage for the markdown gate's file set. Two claims nothing else asserts: that the
// linted set reaches files other than README.md, and that a violation in one of those files fails.
// The set needs a check because markdownlint-cli2 MERGES a command-line glob with the config's own
// `globs` array instead of replacing it — so no workflow input can narrow or widen coverage, and the
// whole set rests on one key in .markdownlint-cli2.jsonc. The failing arm runs in a temp dir holding
// a copy of that config, so the tracked tree is never modified and two concurrent runs cannot
// observe each other's induced violation.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const root = path.resolve(import.meta.dirname, "..");
const configName = ".markdownlint-cli2.jsonc";
const npx = process.platform === "win32" ? "npx.cmd" : "npx";

// The same argument list the gate passes — none at all: without a glob, the config's globs key is the
// only thing selecting files. Reached through npx rather than the root script the gate uses, because
// this fixture runs in a temp dir where the repo's own installed binary is out of reach.
function runMarkdownlint(cwd) {
  const result = spawnSync(npx, ["markdownlint-cli2"], { cwd, encoding: "utf8" });
  return { status: result.status, output: (result.stdout ?? "") + (result.stderr ?? "") };
}

// markdownlint-cli2 prints its own resolved selection ("Finding: …") and the size of the set it then
// linted ("Linting: N files"). A resolved set of zero files reports NO INPUT rather than a clean
// run: a green over nothing inspected is the failure mode this check exists to catch.
function readCoverage(output) {
  const failures = [];
  const finding = /^Finding: (.+)$/m.exec(output);
  const linting = /^Linting: (\d+) files?$/m.exec(output);
  if (!finding) {
    failures.push("NO INPUT — no resolved-selection line, so the linted file set is unknown");
  }
  if (!linting) {
    failures.push("NO INPUT — no linted-file count, so nothing is known to have been inspected");
  } else if (Number(linting[1]) === 0) {
    failures.push("NO INPUT — the resolved file set is empty, so a clean result proves nothing");
  }
  return {
    ok: failures.length === 0,
    count: linting ? Number(linting[1]) : 0,
    selection: finding ? finding[1] : "",
    failures,
  };
}

function fixtureDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "markdown-coverage-"));
  fs.copyFileSync(path.join(root, configName), path.join(dir, configName));
  return dir;
}

const cleanFixture = ["# Fixture", "", "## Section", "", "Body text.", ""].join("\n");

// A skipped heading level (h1 straight to h4) trips MD001; the blank-line pair trips MD012.
const violatingFixture = [
  "# Fixture",
  "",
  "#### Skipped two levels",
  "",
  "Body text.",
  "",
  "",
  "Text after a blank-line pair.",
  "",
].join("\n");

test("the repo's resolved file set is more than one file and is selected repo-wide", () => {
  const { output } = runMarkdownlint(root);
  const coverage = readCoverage(output);
  assert.equal(coverage.ok, true, coverage.failures.join("; "));
  assert.ok(
    coverage.count > 1,
    `expected the gate to lint more than one file, got ${String(coverage.count)}: ${coverage.selection}`,
  );
  assert.ok(
    coverage.selection.includes("**/*.md"),
    `expected a repo-wide pattern in the resolved selection, got: ${coverage.selection}`,
  );
});

test("readCoverage reports NO INPUT for an empty or unreadable resolved set rather than a pass", () => {
  const empty = readCoverage("Finding: **/*.md\nLinting: 0 files\nSummary: 0 issues in 0 files\n");
  assert.equal(empty.ok, false);
  assert.ok(empty.failures.some((f) => f.includes("NO INPUT") && f.includes("empty")));

  const silent = readCoverage("Summary: 0 issues in 0 files\n");
  assert.equal(silent.ok, false);
  assert.ok(silent.failures.some((f) => f.includes("NO INPUT") && f.includes("resolved-selection line")));
  assert.ok(silent.failures.some((f) => f.includes("NO INPUT") && f.includes("linted-file count")));
  assert.equal(silent.count, 0);

  const real = readCoverage("Finding: **/*.md !**/dist/**\nLinting: 38 files\nSummary: 0 issues in 0 files\n");
  assert.equal(real.ok, true, real.failures.join("; "));
  assert.equal(real.count, 38);
});

test("a heading skip in a file that is not README.md fails the gate, and removing it passes again", () => {
  const dir = fixtureDir();
  const fixtureName = "docs-fixture.md";
  const fixturePath = path.join(dir, fixtureName);

  fs.writeFileSync(fixturePath, cleanFixture);
  const baseline = runMarkdownlint(dir);
  assert.equal(baseline.status, 0, baseline.output);
  assert.ok(readCoverage(baseline.output).ok, "the fixture run must resolve a non-empty file set");

  fs.writeFileSync(fixturePath, violatingFixture);
  const induced = runMarkdownlint(dir);
  assert.notEqual(induced.status, 0, induced.output);
  assert.ok(induced.output.includes(fixtureName), induced.output);
  assert.ok(induced.output.includes("MD001"), induced.output);

  fs.writeFileSync(fixturePath, cleanFixture);
  const reverted = runMarkdownlint(dir);
  assert.equal(reverted.status, 0, reverted.output);

  fs.rmSync(dir, { recursive: true, force: true });
});
