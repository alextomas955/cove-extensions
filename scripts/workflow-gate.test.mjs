// The workflow gate's two failure modes, driven through an injected environment so each one has a
// reading that does not depend on the machine the suite happens to run on.
//
// The silent-degradation case is the reason this file exists. Recording once that the shell checker was
// present when the gate landed leaves nothing behind; what has to hold is that the gate FAILS when the
// checker goes away, rather than reporting zero findings — and that it does not even reach the linter,
// since a linter run without its shell half is the misleading green being prevented.
import { test } from "node:test";
import assert from "node:assert/strict";

import { checkWorkflows, diskContext } from "./check-workflows.mjs";
import { GATES, GATING_GROUPS } from "./verify-all.mjs";

function context({ files = [".github/workflows/build.yml"], shell = { ok: true, detail: "0.11.0" }, code = 0 } = {}) {
  const calls = [];
  return {
    calls,
    ctx: {
      listFiles: () => files,
      probeShellcheck: () => shell,
      lint: (given) => {
        calls.push(given);
        return code;
      },
    },
  };
}

test("an empty workflow file set is a failure, not a clean result", () => {
  const { ctx, calls } = context({ files: [] });
  const result = checkWorkflows(ctx);
  assert.equal(result.code, 1);
  assert.match(result.reason, /nothing was inspected/);
  assert.equal(calls.length, 0, "the linter must not run when there is nothing to lint");
});

test("an unavailable shell checker fails the gate and never reaches the linter", () => {
  const { ctx, calls } = context({ shell: { ok: false, detail: "shellcheck --version exited 127" } });
  const result = checkWorkflows(ctx);
  assert.equal(result.code, 1);
  assert.match(result.reason, /exited 127/);
  assert.match(result.reason, /drops its shell checking silently/);
  assert.equal(calls.length, 0, "a linter run missing its shell half is the misleading green being prevented");
});

test("a linter finding is a failure that names its exit code", () => {
  const { ctx } = context({ code: 1 });
  const result = checkWorkflows(ctx);
  assert.equal(result.code, 1);
  assert.match(result.reason, /exited 1/);
});

test("a clean run reports the file count and the checker version it ran with", () => {
  const { ctx, calls } = context({ files: ["a.yml", "b.yaml"] });
  const result = checkWorkflows(ctx);
  assert.equal(result.code, 0);
  assert.match(result.reason, /2 workflow file\(s\)/);
  assert.match(result.reason, /shellcheck 0\.11\.0/);
  assert.deepEqual(calls, [["a.yml", "b.yaml"]]);
});

test("the real workflow directory resolves a stable, non-empty file list", () => {
  const first = diskContext().listFiles();
  const second = diskContext().listFiles();
  assert.ok(first.length > 0, "the workflow files always exist, so an empty read is a broken scan");
  assert.deepEqual(first, second, "two reads must agree, so the linter's report order cannot drift");
  assert.deepEqual([...first].sort(), first, "the list is sorted, which is what fixes the report order");
  for (const file of first) assert.match(file, /^\.github\/workflows\/.+\.ya?ml$/);
});

test("the registry record gates, needs the linter binary, and carries no flip condition", () => {
  const record = GATES.find((gate) => gate.id === "actionlint");
  assert.ok(record, "the workflow gate must be registered, or no CI job can select it");
  assert.ok(GATING_GROUPS.has(record.group), "a gate that lands blocking must sit in a gating group");
  assert.deepEqual(record.needs, ["actionlint"]);
  assert.equal(record.flipCondition, undefined, "a gating record must not carry a flip condition");
});
