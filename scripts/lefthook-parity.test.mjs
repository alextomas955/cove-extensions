// Asserts the pre-commit hook and the gate registry invoke the same scripts the same way.
//
// The repo's stated invariant is that every gate is declared ONCE, in `scripts/verify-all.mjs`, so the
// two cannot drift. That was not true of `lefthook.yml`: it restates each command verbatim and nothing
// read it, so the two lists could disagree silently — and did. `sdk-drift-check` ran with `--warn` in the
// hook, the only environment where it can detect anything (CI has no `../cove` sibling, so the registry's
// gating run always no-ops there), which left SDK drift unable to fail anywhere. This test is what makes
// that class of disagreement fail instead of pass.
//
// It compares the FLAGS, not the presence of a gate: the hook is deliberately a fast staged-files subset,
// so a registry gate with no hook entry is expected and is not checked here. Divergences that ARE
// intended are listed in DELIBERATE below, each with the reason — an unlisted divergence fails.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { GATES } from "./verify-all.mjs";

const root = path.resolve(import.meta.dirname, "..");

// hook command name → the reason its flags may differ from the registry's.
const DELIBERATE = new Map([
  [
    "comment-hygiene",
    "The hook carries no glob, so a doc-only commit yields an empty diff and reports NO INPUT; --warn " +
      "keeps that from blocking. The blocking run is the registry's, over main..HEAD.",
  ],
  [
    "wire-usage-check",
    "Local-only by recorded ruling — the registry record is in the non-gating local-warn group and " +
      "carries the flip condition, so --warn on both sides is the ruling, not drift.",
  ],
]);

/** The `scripts/<name>.mjs` a run: line invokes, or null when it does not invoke one. */
function scriptOf(run) {
  return /node\s+(scripts\/[\w-]+\.mjs)/.exec(run)?.[1] ?? null;
}

/** The flags a command line passes to that script, in sorted order. */
function flagsOf(run) {
  return [...run.matchAll(/\s(--[\w-]+)/g)].map((m) => m[1]).sort();
}

// Read with a two-pattern scan rather than a YAML parser: the only YAML library on this tree is
// js-yaml, and it is here transitively through markdownlint-cli2 rather than declared — importing it
// would make this test depend on another tool's dependency graph. The shape needed is two lines
// (`  <name>:` then a `run:` under it), and the "compared no hook commands" assertion below fails loudly
// if this scan ever stops matching, so a silently-empty parse cannot read as a clean repo.
const hookSrc = fs.readFileSync(path.join(root, "lefthook.yml"), "utf8");
const commands = {};
{
  let current = null;
  for (const line of hookSrc.split("\n")) {
    const name = /^ {4}([a-z0-9][\w-]*):\s*$/.exec(line);
    if (name) {
      current = name[1];
      continue;
    }
    const run = /^\s+run:\s*(.+?)\s*$/.exec(line);
    if (run && current !== null) {
      commands[current] = { run: run[1] };
      current = null;
    }
  }
}

test("lefthook.yml is readable and declares pre-commit commands", () => {
  // A parse that yields nothing would make every assertion below vacuously pass.
  assert.ok(
    Object.keys(commands).length > 0,
    "no pre-commit commands parsed from lefthook.yml",
  );
});

test("every hook command invoking a repo script matches the registry's flags", () => {
  const byScript = new Map();
  for (const g of GATES) {
    const s = g.command?.args?.find((a) => /^scripts\/[\w-]+\.mjs$/.test(a));
    if (s) byScript.set(s, g);
  }

  const failures = [];
  let compared = 0;
  for (const [name, cmd] of Object.entries(commands)) {
    const script = scriptOf(cmd.run ?? "");
    if (script === null) continue; // prettier/eslint/dotnet-format: not registry-script gates
    const gate = byScript.get(script);
    if (gate === undefined) {
      failures.push(
        `${name}: runs ${script}, which no registry gate declares — add it to GATES or remove the hook.`,
      );
      continue;
    }
    compared++;
    const hookFlags = flagsOf(cmd.run);
    const gateFlags = gate.command.args.filter((a) => a.startsWith("--")).sort();
    if (hookFlags.join(" ") === gateFlags.join(" ")) continue;
    if (DELIBERATE.has(name)) continue;
    failures.push(
      `${name}: hook runs ${script} ${hookFlags.join(" ") || "(no flags)"} but registry gate ` +
        `"${gate.id}" runs it ${gateFlags.join(" ") || "(no flags)"}. Reconcile them, or add ${name} ` +
        "to DELIBERATE with the reason.",
    );
  }

  assert.ok(compared > 0, "compared no hook commands — the matcher is broken, not the repo clean");
  assert.deepEqual(failures, [], `\n${failures.join("\n")}\n`);
});

test("every DELIBERATE entry names a hook command that still exists", () => {
  // A reason that outlives its command is the same rot this test exists to catch, one level up.
  const stale = [...DELIBERATE.keys()].filter((k) => !(k in commands));
  assert.deepEqual(stale, [], `stale DELIBERATE entr(ies): ${stale.join(", ")}`);
});
