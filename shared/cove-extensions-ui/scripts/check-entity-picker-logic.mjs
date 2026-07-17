#!/usr/bin/env node
/**
 * Offline gate for the shared entity-picker subset (`availableOptions` + `ValueOption`). Modeled on
 * the per-extension pure-logic gates: entityPickerLogic.ts is a zero-import module, so a single-file
 * compile with the local TypeScript compiler yields a runnable ESM module the suite runs under Node's
 * built-in test runner. The compiled module's path is handed to the suite via PICKER_LOGIC_MODULE.
 * The scratch dir is removed on exit so a failed run never leaves a stale compiled module behind.
 *
 * `tsc` is resolved from whichever consuming extension installed it (this shared module has no npm
 * install of its own); the extensions pin the same TypeScript version, so either resolves identically.
 */
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const moduleRoot = path.resolve(here, "..");
const repoRoot = path.resolve(moduleRoot, "..", "..");
const srcDir = path.join(moduleRoot, "src");
const logicTs = path.join(srcDir, "entityPickerLogic.ts");
const testFile = path.join(here, "entity-picker-logic.test.mjs");

// Locate a TypeScript compiler from a consuming extension's node_modules (this module installs none).
const tscCandidates = [
  path.join(repoRoot, "extensions", "Renamer", "src", "Renamer.Ui", "node_modules", "typescript", "bin", "tsc"),
  path.join(repoRoot, "extensions", "WhisparrSync", "src", "WhisparrSync.Ui", "node_modules", "typescript", "bin", "tsc"),
];
const tsc = tscCandidates.find((p) => existsSync(p));
if (!tsc) {
  console.error("check-entity-picker-logic: no TypeScript compiler found in either UI package's node_modules");
  process.exit(1);
}

const outDir = mkdtempSync(path.join(tmpdir(), "shared-entity-picker-"));

function run() {
  // entityPickerLogic.ts is a pure module with zero imports, so compile it in isolation. A scratch
  // tsconfig with `types: []` keeps tsc from pulling ambient @types/* — which a bare single-file
  // compile would try to resolve and fail on — and skipLibCheck avoids type-checking those .d.ts at
  // all. moduleResolution bundler mirrors the project tsconfig.
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        rootDir: srcDir,
        types: [],
        skipLibCheck: true,
        outDir,
      },
      files: [logicTs],
    }),
  );
  // Mark the scratch dir as ESM so Node loads the emitted .js as a module without a reparse warning.
  writeFileSync(path.join(outDir, "package.json"), JSON.stringify({ type: "module" }));
  const compile = spawnSync(process.execPath, [tsc, "--project", tsconfigPath], {
    stdio: "inherit",
  });
  if (compile.status !== 0) {
    console.error("check-entity-picker-logic: tsc compile FAILED");
    return compile.status ?? 1;
  }

  const compiledModule = pathToFileURL(path.join(outDir, "entityPickerLogic.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, PICKER_LOGIC_MODULE: compiledModule },
  });
  return test.status ?? 1;
}

let code;
try {
  code = run();
} finally {
  rmSync(outDir, { recursive: true, force: true });
}
process.exit(code);
