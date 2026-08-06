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
// The oracle is the SIBLING CHECKOUT's generated shim — gitignored output reflecting whatever that
// checkout has installed, NOT the released host image this repo targets. So the two can disagree in
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

// An absent host checkout is the ONE condition that may skip, and it is the only one this gate can
// establish by looking at coveRepo itself. Diagnosing any other unresolvable path as "no host
// checkout" asserts a cause that was never checked — which is how a wrong oracle path read as a
// clean exit 0 for this gate's whole existence, with the sibling sitting right there.
if (!existsSync(coveRepo)) {
  console.log(
    [
      `check-host-imports: SKIPPED — no Cove host checkout at ${coveRepo}.`,
      `  This gate is local-only: its oracle is generated output inside a Cove host checkout, which CI`,
      `  does not have, so CI structurally cannot run it and it can never block a merge. Nothing was`,
      `  verified; set COVE_REPO to a host checkout to run it.`,
    ].join("\n"),
  );
  process.exit(0);
}

// Past this point a checkout IS present, so every unresolvable path below is a broken oracle rather
// than an absent one — a hard failure, never a skip.
//
// The runtime version comes from the host's own tracked contract, the same file the host generates
// the shim directory name from. Mirroring it as a literal here would mean a host bump silently
// resolves to a path that does not exist, restoring the exact blind-gate state this replaces.
const contractPath = path.join(coveRepo, "ui/scripts/extension-runtime-contract.ts");
const runtimeVersion = existsSync(contractPath)
  ? readFileSync(contractPath, "utf8").match(
      /extensionRuntimeVersion\s*=\s*"([^"]+)"/,
    )?.[1]
  : undefined;

if (!runtimeVersion) {
  console.error("check-host-imports: FAILED");
  console.error(`  a directory is present at ${coveRepo}, but no extensionRuntimeVersion could be`);
  console.error(`  read from the host runtime contract it must carry:`);
  console.error(`    ${contractPath}`);
  console.error(
    `  Either that path is not a Cove host checkout, or the contract's shape changed.`,
  );
  console.error(`  Nothing was verified — this is not a pass.`);
  process.exit(1);
}

const shim = path.join(
  coveRepo,
  `ui/src/generated/extensions/runtime/${runtimeVersion}/lucide-react.ts`,
);

if (!existsSync(shim)) {
  console.error("check-host-imports: FAILED");
  console.error(
    `  host checkout found at ${coveRepo}, but its generated ${runtimeVersion} shim is absent:`,
  );
  console.error(`    ${shim}`);
  console.error(
    `  The shim is gitignored codegen output, so a fresh clone has none: run`,
  );
  console.error(
    `  \`npm run generate:extension-runtime\` in that checkout. Nothing was verified.`,
  );
  process.exit(1);
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

const extensionDirs = readdirSync(path.join(repoRoot, "extensions"), {
  withFileTypes: true,
})
  .filter((e) => e.isDirectory())
  .map((e) => e.name);

// A manifestOnly catalog entry (kind=bundle / scraper-pack) has no `src/` at all, so an extension
// directory without one is a supported shape rather than a broken tree — scanning it blind died on
// the first such entry with a raw ENOENT, taking the pre-commit hook with it. What cannot be scanned
// is named in the report instead: silently dropping it is how a UI bundle that moved out from under
// the scan roots would read as one with nothing to check.
const unscannable = extensionDirs.filter(
  (name) => !existsSync(path.join(repoRoot, "extensions", name, "src")),
);
const unscannedNote =
  unscannable.length > 0
    ? `; not scanned, no src/: ${unscannable.join(", ")}`
    : "";

const uiRoots = extensionDirs
  .filter((name) => !unscannable.includes(name))
  .flatMap((name) =>
    readdirSync(path.join(repoRoot, "extensions", name, "src"), {
      withFileTypes: true,
    })
      .filter((u) => u.isDirectory() && u.name.endsWith(".Ui"))
      .map((u) => path.join(repoRoot, "extensions", name, "src", u.name, "src")),
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
    `  no lucide-react imports were inspected across ${uiRoots.length} UI bundle(s)${unscannedNote}`,
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
  `check-host-imports: OK (${checked} lucide-react imports across ${uiRoots.length} UI bundles, ${hostExports.size} host exports from ${path.relative(repoRoot, shim)}${unscannedNote})`,
);
