// The notice below is derived from `dotnet format`'s own report rather than from a probe for a Cove
// checkout, because the tool names what it actually failed to load.

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const UNLOADED_REFERENCES = /^Required references did not load for (.+?) or referenced project\./gm;

export function projectsWithUnloadedReferences(output) {
  const names = new Set();
  for (const match of output.matchAll(UNLOADED_REFERENCES)) {
    names.add(match[1]);
  }
  return [...names];
}

/**
 * Whether this process was STARTED from this file, rather than importing it for its helpers.
 *
 * Both sides are realpathed rather than compared as resolved strings: Node realpaths the module URL
 * and leaves process.argv[1] as the caller spelled it, so an invocation through a junction or symlink
 * — the shape this repo's worktree workflow uses — compares unequal and the refusal below never
 * fires. Windows drive-letter casing is normalised for the same reason.
 */
function invokedAsScript() {
  const entry = process.argv[1];
  if (typeof entry !== "string" || entry === "") return false;
  const canonical = (value) => {
    let resolved = path.resolve(value);
    try {
      resolved = fs.realpathSync.native(resolved);
    } catch {
      // Left as resolved: a path that cannot be realpathed is one that does not exist, and comparing
      // the resolved form is no weaker than not comparing at all.
    }
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
  };
  return canonical(entry) === canonical(import.meta.filename);
}

// `import.meta.main` is a boolean from Node 22.18 onward and `undefined` before it, so a bare
// `if (import.meta.main)` takes the not-main branch on an older runtime: run as a CLI, this script
// then checks nothing and exits 0. The root package.json declares `engines.node: ">=22.18"`, but
// without engine-strict npm prints EBADENGINE and installs anyway, and a script run directly never
// consults it at all, so the absent feature is refused BY NAME here.
//
// Scoped to the CLI on purpose: the exported parse works fine on an older Node, and refusing at
// import time would break this file's own tests for a feature only the entry guard needs.
if (typeof import.meta.main !== "boolean") {
  if (invokedAsScript()) {
    console.error(
      `check-csharp-format: this Node (${process.version}) does not implement import.meta.main, so this script cannot tell it was run rather than imported and would check nothing while exiting 0. Node 22.18 or newer is required to run it.`,
    );
    process.exit(1);
  }
} else if (import.meta.main) {
  // `shell: false`, so a filename lefthook interpolated into its own command string is not split a
  // second time here.
  const run = spawnSync("dotnet", ["format", "CoveExtensions.slnx", ...process.argv.slice(2)], {
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
  if (unloaded.length > 0) {
    console.log(
      `check-csharp-format: PARTIAL - references did not load for ${unloaded.join(", ")}, so only whitespace was checked there and no analyzer finding could be reported. Set COVE_REPO or add a ../cove sibling to restore analyzer coverage.`,
    );
  }

  // This reports; it does not gate.
  process.exit(run.status ?? 1);
}
