#!/usr/bin/env node
// Asserts every `lucide-react` named import in every extension UI exists in the module the HOST serves.
//
// lucide-react is a host import-map external (shared/ui-shared/vite/createExtensionViteConfig.ts),
// so the bundle never carries it: at runtime the name is resolved against the host's generated re-export
// shim, not against the version installed here for typechecking. Those two can diverge, and lucide renames
// icons between releases, so a name that exists only in the local copy typechecks, builds, and passes every
// check — then throws an ESM SyntaxError at load that kills the WHOLE bundle, so every surface of
// the extension renders "component not found".
//
// The host's list is authoritative and generated from its own package exports, so it is read from the
// sibling checkout rather than mirrored here. With no sibling this cannot be checked at all, so it skips
// loudly rather than passing quietly (the sdk-drift-check precedent).

import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dirname, "..");
const coveRepo = process.env.COVE_REPO ?? path.resolve(repoRoot, "../cove");
const shim = path.join(coveRepo, "ui/src/generated/extensions/runtime/v1/lucide-react.ts");

if (!existsSync(shim)) {
  console.log(
    `check-host-imports: SKIPPED — no host shim at ${shim} (set COVE_REPO or add a ../cove sibling)`,
  );
  process.exit(0);
}

const hostExports = new Set(
  [...readFileSync(shim, "utf8").matchAll(/^export const (\w+) =/gm)].map((m) => m[1]),
);

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
      .map((u) => path.join(repoRoot, "extensions", e.name, "src", u.name, "src")),
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
        if (!hostExports.has(name)) missing.push({ file: path.relative(repoRoot, file), name });
      }
    }
  }
}

if (missing.length > 0) {
  console.error(
    `check-host-imports: ${missing.length} lucide-react import(s) the host does not export:\n`,
  );
  for (const { file, name } of missing) {
    const alt = [...hostExports]
      .filter((e) => e.includes(name.replace(/Icon$/, "")) || name.includes(e))
      .slice(0, 3);
    const offers = alt.length ? ` — the host offers: ${alt.join(", ")}` : "";
    console.error(`  ${file}\n    "${name}" is absent from the host runtime shim${offers}`);
  }
  console.error(
    `\nThis would fail the whole bundle at load with an ESM SyntaxError, not just the icon.`,
  );
  console.error(`Host shim: ${path.relative(repoRoot, shim)} (${hostExports.size} exports)`);
  process.exit(1);
}

console.log(
  `check-host-imports: OK (${checked} lucide-react imports across ${uiRoots.length} UI bundles, ${hostExports.size} host exports)`,
);
