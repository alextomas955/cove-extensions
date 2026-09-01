// The notice below is derived from `dotnet format`'s own report rather than from a probe for a Cove
// checkout, because the tool names what it actually failed to load.

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const UNLOADED_REFERENCES = /^Required references did not load for (.+?) or referenced project\./gm;

export function projectsWithUnloadedReferences(output) {
  const names = new Set();
  for (const match of output.matchAll(UNLOADED_REFERENCES)) {
    names.add(match[1]);
  }
  return [...names];
}

/**
 * Splits this wrapper's own flags out of the arguments meant for `dotnet format`.
 *
 * `--fail-on-partial` is the wrapper's, and `dotnet format` rejects an argument it does not know, so
 * a flag left in the passthrough set would fail every run that used it.
 */
export function splitWrapperArguments(argv) {
  return {
    failOnPartial: argv.includes("--fail-on-partial"),
    passthrough: argv.filter((arg) => arg !== "--fail-on-partial"),
  };
}

/**
 * Whether `entry` (a `process.argv[1]`) and `self` (a module's own path) name the same file.
 *
 * Both sides are realpathed rather than compared as resolved strings: Node realpaths the module URL
 * and leaves process.argv[1] as the caller spelled it, so an invocation through a junction or symlink
 * — the shape this repo's worktree workflow uses — compares unequal. Windows drive-letter casing is
 * normalised for the same reason, which is what `platform` selects.
 *
 * An absent or empty `entry` is false, which is what an import rather than a CLI run looks like.
 */
export function isSameFile(entry, self, platform = process.platform) {
  if (typeof entry !== "string" || entry === "") return false;
  if (typeof self !== "string" || self === "") return false;

  const canonical = (value) => {
    let resolved = path.resolve(value);
    try {
      resolved = fs.realpathSync.native(resolved);
    } catch {
      // Left as resolved: a path that cannot be realpathed is one that does not exist, and comparing
      // the resolved form is no weaker than not comparing at all.
    }
    return platform === "win32" ? resolved.toLowerCase() : resolved;
  };
  return canonical(entry) === canonical(self);
}

// import.meta.filename landed in Node 20.11, which is inside the range the guard below exists for, so
// the URL form is the spelling available on every runtime that reaches it.
const selfPath = import.meta.filename ?? fileURLToPath(import.meta.url);

// `import.meta.main` is a boolean from Node 22.18 onward and `undefined` before it, so a bare
// `if (import.meta.main)` takes the not-main branch on an older runtime: run as a CLI, this script
// then checks nothing and exits 0. The root package.json declares `engines.node: ">=22.18"`, but
// without engine-strict npm prints EBADENGINE and installs anyway, and a script run directly never
// consults it at all, so the absent feature is refused BY NAME here.
//
// Scoped to the CLI on purpose: the exported parse works fine on an older Node, and refusing at
// import time would break this file's own tests for a feature only the entry guard needs.
if (typeof import.meta.main !== "boolean") {
  if (isSameFile(process.argv[1], selfPath)) {
    console.error(
      `check-csharp-format: this Node (${process.version}) does not implement import.meta.main, so this script cannot tell it was run rather than imported and would check nothing while exiting 0. Node 22.18 or newer is required to run it.`,
    );
    process.exitCode = 1;
  }
} else if (import.meta.main) {
  const { failOnPartial, passthrough } = splitWrapperArguments(process.argv.slice(2));

  // `shell: false`, so a filename lefthook interpolated into its own command string is not split a
  // second time here.
  const run = spawnSync("dotnet", ["format", "CoveExtensions.slnx", ...passthrough], {
    encoding: "utf8",
    shell: false,
    maxBuffer: 64 * 1024 * 1024,
  });

  const stdout = run.stdout ?? "";
  const stderr = run.stderr ?? "";
  process.stdout.write(stdout);
  process.stderr.write(stderr);
  if (run.error) process.stderr.write(`${run.error.message}\n`);

  const unloaded = projectsWithUnloadedReferences(stdout + stderr);
  let refusedForPartialCoverage = false;
  if (unloaded.length > 0) {
    console.log(
      `check-csharp-format: PARTIAL - references did not load for ${unloaded.join(", ")}, so only whitespace was checked there and no analyzer finding could be reported. Set COVE_REPO or add a ../cove sibling to restore analyzer coverage.`,
    );
    if (failOnPartial) {
      refusedForPartialCoverage = true;
      console.error(
        "check-csharp-format: refusing to pass on partial coverage; this run was configured to have a Cove checkout.",
      );
    }
  }

  // Without --fail-on-partial this reports and does not gate: working with no Cove checkout is a
  // supported local state.
  //
  // Node's stdout is asynchronous on a pipe, and process.exit does not drain it: exiting here would
  // discard the disclosure above and most of the tool's own output while keeping the status.
  const status = run.status ?? 1;
  process.exitCode = status === 0 && refusedForPartialCoverage ? 1 : status;
}
