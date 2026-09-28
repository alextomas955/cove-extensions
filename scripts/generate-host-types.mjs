#!/usr/bin/env node
// Emits TypeScript declarations for the host runtime modules into `.host-types/`, from a Cove checkout.
//
// Cove serves `@cove/runtime/components` and `@cove/runtime/api` through its import map and ships no
// types for them. Each UI tsconfig maps those two specifiers into the directory this script writes, so
// a call site is checked against the host's real component props rather than against a transcription.
//
// The declarations are emitted rather than read from Cove's source directly. Pointing a tsconfig at
// `extension-shared.ts` pulls Cove's .ts files into this repo's program, where this repo's stricter
// options apply to them: `noUnusedLocals` and `verbatimModuleSyntax` both report inside Cove's own
// files, and there is no per-file escape. `skipLibCheck` covers a .d.ts, so the emitted form checks
// call sites here without auditing Cove's source.
//
// The output is gitignored and rebuilt on demand. Nothing here is committed, so no machine's checkout
// path reaches the repo: the tsconfigs name `.host-types/`, and only this script resolves Cove.

import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import path from "node:path";

const repoRoot = path.resolve(import.meta.dirname, "..");
const coveRepo = process.env.COVE_REPO ?? path.resolve(repoRoot, "../cove");
const coveUi = path.join(coveRepo, "ui");
const barrel = path.join(coveUi, "src/components/extension-shared.ts");

// Fails rather than emitting a smaller set. A missing checkout makes the host's real props unreachable,
// and the alternative - falling back to a hand-written stand-in - is the transcription this replaces.
if (!existsSync(barrel)) {
  console.error(
    `generate-host-types: no Cove checkout at ${coveRepo} (looked for ${barrel}). ` +
      `Set COVE_REPO or add a ../cove sibling.`,
  );
  process.exit(1);
}

const tsc = path.join(coveUi, "node_modules/typescript/bin/tsc");
if (!existsSync(tsc)) {
  console.error(
    `generate-host-types: ${coveUi} has no installed TypeScript at ${tsc}. The declarations name types ` +
      `from react, lucide-react and react-query, so run "npm ci" there first.`,
  );
  process.exit(1);
}

const outDir = path.join(repoRoot, ".host-types");
const posix = (p) => p.replaceAll("\\", "/");

// Extends Cove's own tsconfig so its `paths`, jsx mode and module settings apply, and narrows `include`
// to the two entry points. Cove's generated runtime shims are left out: they re-export whole packages
// and cannot emit declarations without naming types from inside those packages' own bundles, which
// fails the emit for modules nothing here imports. `vite-env.d.ts` comes in because Cove's components
// read `import.meta.glob` and `import.meta.env`.
//
// `types: []` because a type library resolves against this file's own directory, which is a temp dir
// with no node_modules. The reference inside vite-env.d.ts resolves from Cove's tree instead.
const emitConfig = {
  extends: posix(path.join(coveUi, "tsconfig.json")),
  compilerOptions: {
    noEmit: false,
    declaration: true,
    emitDeclarationOnly: true,
    skipLibCheck: true,
    types: [],
    tsBuildInfoFile: null,
    outDir: posix(outDir),
    rootDir: posix(path.join(coveUi, "src")),
  },
  include: [
    posix(path.join(coveUi, "src/vite-env.d.ts")),
    posix(barrel),
    posix(path.join(coveUi, "src/extensions/extension-api.ts")),
  ],
};

const configDir = mkdtempSync(path.join(tmpdir(), "cove-host-types-"));
const configPath = path.join(configDir, "tsconfig.json");

try {
  rmSync(outDir, { recursive: true, force: true });
  writeFileSync(configPath, JSON.stringify(emitConfig, null, 2));

  // Cove's own TypeScript, invoked through node rather than a shell: the emit then matches what Cove
  // compiles with, and no argument reaches a command line for the shell to re-split.
  const result = spawnSync(process.execPath, [tsc, "-p", configPath], {
    cwd: coveUi,
    stdio: "inherit",
  });

  if (result.status !== 0) {
    console.error(`generate-host-types: declaration emit failed for ${coveUi}.`);
    process.exit(result.status ?? 1);
  }
} finally {
  rmSync(configDir, { recursive: true, force: true });
}

// The emit reports success for a run that wrote nothing an extension can import, so the two specifiers
// the tsconfigs map are checked by name.
for (const required of ["components/extension-shared.d.ts", "extensions/extension-api.d.ts"]) {
  if (!existsSync(path.join(outDir, required))) {
    console.error(`generate-host-types: the emit produced no ${required} under ${outDir}.`);
    process.exit(1);
  }
}

console.log(`generate-host-types: wrote host declarations to ${outDir} from ${coveRepo}`);
