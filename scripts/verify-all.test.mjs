// Behavior coverage for the gate runner. Drives the importable core with synthetic registries whose
// commands are `node -e` one-liners, so no real gate and no real toolchain runs. Exercises the
// registry-load rejections, the three-state counting, and the exit-code arms through runGates, and the
// malformed-invocation and NO-INPUT exit codes through the CLI entry point.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { GATES, GATING_GROUPS, NON_GATING_GROUPS, assertRegistry, gatesForExtension, runGates } from "./verify-all.mjs";

const scriptPath = fileURLToPath(new URL("./verify-all.mjs", import.meta.url));
const root = path.resolve(fileURLToPath(new URL(".", import.meta.url)), "..");

function repoGate(id, exitCode, group = "fast") {
  return {
    id,
    scope: "repo",
    needs: [],
    group,
    command: { bin: "node", args: ["-e", "process.exit(" + exitCode + ")"] },
  };
}

// An each-extension gate whose path field no catalog entry declares resolves to zero invocations, so
// it reports SKIPPED deterministically without depending on what is installed on the machine.
function unrunnableGate(id) {
  return {
    id,
    scope: "each-extension",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["-e", "process.exit(0)"], pathField: "noSuchCatalogField" },
  };
}

test("a gate whose command exits 1 counts as a failure and contributes a non-zero exit", () => {
  const result = runGates({ gates: [repoGate("boom", 1)] });
  assert.equal(result.fail, 1);
  assert.equal(result.pass, 0);
  assert.notEqual(result.exitCode, 0);
  assert.ok(result.results.some((r) => r.id === "boom" && r.state === "FAIL"));
});

test("a SKIPPED never masks a FAIL: both are counted apart and the exit stays non-zero", () => {
  const result = runGates({ gates: [repoGate("boom", 1), unrunnableGate("cannot-run")] });
  assert.equal(result.fail, 1);
  assert.equal(result.skipped, 1);
  assert.equal(result.pass, 0);
  assert.notEqual(result.exitCode, 0);
  const skip = result.results.find((r) => r.id === "cannot-run");
  assert.equal(skip.state, "SKIPPED");
  assert.ok(skip.reason.includes("noSuchCatalogField"));
});

test("a skip alone exits 0, and --require-no-skips turns that same skip into a non-zero exit", () => {
  const gates = [unrunnableGate("cannot-run")];
  const lenient = runGates({ gates });
  assert.equal(lenient.skipped, 1);
  assert.equal(lenient.pass, 0);
  assert.equal(lenient.exitCode, 0);

  const strict = runGates({ gates, requireNoSkips: true });
  assert.equal(strict.skipped, 1);
  assert.notEqual(strict.exitCode, 0);
});

test("two records sharing an id are rejected rather than resolving to the last one", () => {
  assert.throws(
    () => assertRegistry([repoGate("twice", 0), repoGate("twice", 1)]),
    (error) => error.message.includes("duplicate gate id") && error.message.includes("twice"),
  );
});

test("a non-gating record needs a flipCondition; the same record with one loads", () => {
  const bare = { ...repoGate("nudge", 0, "local-warn") };
  assert.throws(
    () => assertRegistry([bare]),
    (error) => error.message.includes("nudge") && error.message.includes("local-warn"),
  );

  const withCondition = { ...bare, flipCondition: "while the gate reports a non-zero count, local-only stands" };
  assert.doesNotThrow(() => assertRegistry([withCondition]));
});

test("a gating record must NOT carry a flipCondition", () => {
  const gating = { ...repoGate("blocks", 0, "fast"), flipCondition: "n/a" };
  assert.throws(
    () => assertRegistry([gating]),
    (error) => error.message.includes("blocks") && error.message.includes("must not carry"),
  );
});

test("a group in neither declared set is rejected naming that group, even with a flipCondition", () => {
  const undeclared = { ...repoGate("orphan", 0, "someday"), flipCondition: "a condition is present" };
  assert.throws(
    () => assertRegistry([undeclared]),
    (error) => error.message.includes("someday") && error.message.includes("exactly one"),
  );
  assert.equal(GATING_GROUPS.has("someday"), false);
  assert.equal(NON_GATING_GROUPS.has("someday"), false);
});

test("a selection matching zero gates reports NO INPUT and contributes a non-zero exit", () => {
  const result = runGates({ gates: [repoGate("only-fast", 0)], group: ["nosuchgroup"] });
  assert.equal(result.results.length, 0);
  assert.notEqual(result.exitCode, 0);
  assert.ok(result.noInput);
  assert.ok(result.noInput.includes("nosuchgroup"));
});

test("results come back in registry declaration order for the same selection across two calls", () => {
  const gates = [repoGate("first", 0), repoGate("second", 0), repoGate("third", 0)];
  const ids = (r) => r.results.map((entry) => entry.id);
  const once = runGates({ gates, only: ["third", "first", "second"] });
  const twice = runGates({ gates, only: ["second", "third", "first"] });
  assert.deepEqual(ids(once), ["first", "second", "third"]);
  assert.deepEqual(ids(twice), ["first", "second", "third"]);
});

test("CLI: an unknown --only id exits with the malformed-invocation code, not a finding code", () => {
  const unknown = spawnSync(process.execPath, [scriptPath, "--only", "no-such-gate"], { encoding: "utf8" });
  assert.equal(unknown.status, 2, unknown.stdout + unknown.stderr);
  assert.ok(unknown.stderr.includes("no-such-gate"));

  const empty = spawnSync(process.execPath, [scriptPath, "--group", "nosuchgroup"], { encoding: "utf8" });
  assert.equal(empty.status, 1, empty.stdout + empty.stderr);
  assert.ok(/NO INPUT/.test(empty.stderr));
});

test("every catalog UI's verify script is a pure delegation resolving exactly the registry's declaration", () => {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  const uiEntries = catalog.extensions.filter((entry) => entry.uiPath);
  assert.ok(uiEntries.length > 0, "the catalog declares no UI to check");

  for (const entry of uiEntries) {
    const manifest = JSON.parse(fs.readFileSync(path.join(root, entry.uiPath, "package.json"), "utf8"));
    const verify = manifest.scripts.verify;

    // A re-added hand-written gate shows up as either a chain operator or an extra `npm run <script>`
    // token, and either one makes what the script runs disagree with what the registry declares.
    assert.ok(verify.includes("verify-all.mjs"), entry.name + ": verify no longer delegates");
    assert.ok(verify.includes("--extension " + entry.name), entry.name + ": verify passes the wrong name");
    assert.ok(!verify.includes("&&"), entry.name + ": verify chains a hand-written gate list");
    assert.deepEqual([...verify.matchAll(/npm run (\S+)/g)].map((m) => m[1]), [], entry.name + ": verify runs extra scripts");

    const declared = gatesForExtension(GATES, entry.name).map((gate) => gate.id);
    assert.ok(declared.length > 0, entry.name + ": the registry declares no gate for it");
    const expected = GATES.filter(
      (gate) =>
        gate.scope === "extension:" + entry.name ||
        (gate.scope === "each-extension" && gate.command.cwdField === "uiPath"),
    ).map((gate) => gate.id);
    assert.deepEqual(declared, expected);
  }
});
