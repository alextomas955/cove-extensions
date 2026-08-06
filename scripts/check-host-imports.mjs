#!/usr/bin/env node
// Asserts every `lucide-react` named import in every extension UI exists in the module the HOST serves.
//
// lucide-react is a host import-map external (shared/cove-extensions-ui/vite/createExtensionViteConfig.ts),
// so the bundle never carries it: at runtime the name is resolved against the host's generated re-export
// shim, not against the version installed here for typechecking. Two copies at the same version still
// resolve out of two different files, so a name present only in the local one typechecks, builds and
// passes every offline gate — then throws an ESM SyntaxError at load that kills the WHOLE bundle, and
// every surface of the extension renders "component not found". Nothing else in the repo can catch that:
// every other check reads the local copy.
//
// The oracle is the SIBLING CHECKOUT's generated shim named below — gitignored output reflecting whatever
// that checkout has installed, NOT the released host image this repo targets. So the two can disagree in
// either direction and this gate would not know. Reading the release artifact instead is a known open
// question, deliberately left alone here rather than half-answered.
//
// Local-only by construction: that oracle lives in a Cove host checkout, which CI does not have, so CI
// structurally cannot run this gate and it can never block a merge. It is wired into lefthook's
// pre-commit and Renamer.Ui's `verify`, and nowhere else.

import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dirname, "..");
const coveRepo = process.env.COVE_REPO ?? path.resolve(repoRoot, "../cove");
const shim = path.join(
  coveRepo,
  "ui/src/generated/extensions/runtime/v1/lucide-react.ts",
);

if (!existsSync(shim)) {
  console.log(
    [
      `check-host-imports: SKIPPED — nothing found at ${shim}.`,
      `  This gate is local-only: its oracle is generated output inside a Cove host checkout, which CI`,
      `  does not have, so CI structurally cannot run it and it can never block a merge. Skipping`,
      `  because no host checkout was found at the path above.`,
    ].join("\n"),
  );
  process.exit(0);
}

const hostExports = new Set(
  [...readFileSync(shim, "utf8").matchAll(/^export const (\w+) =/gm)].map(
    (m) => m[1],
  ),
);

// A shim yielding no exports is a broken oracle, not a clean bill of health: every import would be
// reported missing for a reason that has nothing to do with the imports. Caught before the scan so the
// two empty-input defects can never be confused for one another.
if (hostExports.size === 0) {
  console.error("check-host-imports: FAILED");
  console.error(
    `  the host shim parsed to 0 exports: ${path.relative(repoRoot, shim)}`,
  );
  console.error(
    `  Either the file is not the generated re-export shim, or its shape changed and the parser no`,
  );
  console.error(
    `  longer matches it. Nothing was verified against it — this is not a pass.`,
  );
  process.exit(1);
}

function* sources(dir) {
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) yield* sources(full);
    else if (/\.tsx?$/.test(name)) yield full;
  }
}

const uiRoots = readdirSync(path.join(repoRoot, "extensions"), {
  withFileTypes: true,
})
  .filter((e) => e.isDirectory())
  .flatMap((e) =>
    readdirSync(path.join(repoRoot, "extensions", e.name, "src"), {
      withFileTypes: true,
    })
      .filter((u) => u.isDirectory() && u.name.endsWith(".Ui"))
      .map((u) =>
        path.join(repoRoot, "extensions", e.name, "src", u.name, "src"),
      ),
  )
  .filter(existsSync);

let checked = 0;
const missing = [];
for (const root of uiRoots) {
  for (const file of sources(root)) {
    for (const m of readFileSync(file, "utf8").matchAll(
      /import\s+(type\s+)?\{([^}]*)\}\s*from\s*["']lucide-react["']/g,
    )) {
      // A type-only import is erased before the module is ever fetched, so it never reaches the host
      // module and cannot fail at load — in either the whole-clause or the inline-specifier form.
      if (m[1]) continue;
      for (const clause of m[2].split(",")) {
        const specifier = clause.trim();
        if (specifier.startsWith("type ")) continue;
        const name = specifier.split(/\s+as\s+/)[0].trim();
        if (!name) continue;
        checked++;
        if (!hostExports.has(name))
          missing.push({ file: path.relative(repoRoot, file), name });
      }
    }
  }
}

// Zero inspected imports is the other empty-input defect, and the one this gate spent its whole
// existence in: a success line reporting a count of 0 reads as coverage while providing none.
if (checked === 0) {
  console.error("check-host-imports: FAILED");
  console.error(
    `  no lucide-react imports were inspected across ${uiRoots.length} UI bundle(s)`,
  );
  console.error(
    `  Either the scan roots no longer match the repo layout, or no extension UI imports lucide-react`,
  );
  console.error(
    `  by name. A gate that examined nothing cannot report a pass.`,
  );
  process.exit(1);
}

if (missing.length > 0) {
  console.error(
    `check-host-imports: ${missing.length} lucide-react import(s) the host does not export:\n`,
  );
  for (const { file, name } of missing) {
    const alt = [...hostExports]
      .filter((e) => e.includes(name.replace(/Icon$/, "")) || name.includes(e))
      .slice(0, 3);
    console.error(
      `  ${file}\n    "${name}" is absent from the host runtime shim${alt.length ? ` — the host offers: ${alt.join(", ")}` : ""}`,
    );
  }
  console.error(
    `\nThis would fail the whole bundle at load with an ESM SyntaxError, not just the icon.`,
  );
  console.error(
    `Host shim: ${path.relative(repoRoot, shim)} (${hostExports.size} exports)`,
  );
  process.exit(1);
}

console.log(
  `check-host-imports: OK (${checked} lucide-react imports across ${uiRoots.length} UI bundles, ${hostExports.size} host exports from ${path.relative(repoRoot, shim)})`,
);
