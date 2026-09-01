// The notice below is derived from `dotnet format`'s own report rather than from a probe for a Cove
// checkout, because the tool names what it actually failed to load.

import { spawnSync } from "node:child_process";

const UNLOADED_REFERENCES = /^Required references did not load for (.+?) or referenced project\./gm;

export function projectsWithUnloadedReferences(output) {
  const names = new Set();
  for (const match of output.matchAll(UNLOADED_REFERENCES)) {
    names.add(match[1]);
  }
  return [...names];
}

if (import.meta.main) {
  // The staged file list reaches this argument vector, so the shell stays out of it.
  const run = spawnSync("dotnet", ["format", "CoveExtensions.slnx", ...process.argv.slice(2)], {
    encoding: "utf8",
    shell: false,
  });

  const stdout = run.stdout ?? "";
  const stderr = run.stderr ?? "";
  process.stdout.write(stdout);
  process.stderr.write(stderr);
  if (run.error) process.stderr.write(`${run.error.message}\n`);

  const unloaded = projectsWithUnloadedReferences(stdout + stderr);
  if (unloaded.length > 0) {
    console.log(
      `check-csharp-format: PARTIAL - ${unloaded.join(", ")} loaded without their references, so only whitespace was checked there and no analyzer finding could be reported for them. Set COVE_REPO or add a ../cove sibling to include them.`,
    );
  }

  // This reports; it does not gate. Working without a Cove checkout is supported here.
  process.exit(run.status ?? 1);
}
