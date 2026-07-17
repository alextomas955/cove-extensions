#!/usr/bin/env node
/**
 * Offline gate for the pure primitive logic, cloned from check-options-roundtrip.mjs: no new
 * dependency, no test framework. primitivesLogic.ts is a pure module with zero imports, so a
 * single-file compile with the local TypeScript compiler yields a runnable ESM module; the suite then
 * runs under Node's built-in test runner. The compiled module's path is handed to the suite via a
 * distinct env var (PRIMITIVES_LOGIC_MODULE) so the two runners never collide. The scratch dir is
 * removed on exit so a failed run never leaves a stale compiled module behind.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
// primitivesLogic.ts is byte-identical across both extensions, so it now lives once in the shared
// UI module; this offline gate compiles that single shared source in isolation.
const sharedSrc = path.resolve(uiRoot, "../../../../shared/cove-extensions-ui/src");
const logicTs = path.join(sharedSrc, "primitivesLogic.ts");
const testFile = path.join(here, "primitives-logic.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "rename-primitives-"));

function run() {
  // primitivesLogic.ts is a pure module with zero imports, so compile it in isolation. A scratch
  // tsconfig with `types: []` keeps tsc from pulling the project's ambient @types/* (node/react/babel)
  // — which a bare single-file compile would try to resolve and fail on — and skipLibCheck avoids
  // type-checking those .d.ts at all. moduleResolution bundler mirrors the project tsconfig.
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        rootDir: sharedSrc,
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
    console.error("check-primitives-logic: tsc compile FAILED");
    return compile.status ?? 1;
  }

  const compiledModule = pathToFileURL(path.join(outDir, "primitivesLogic.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, PRIMITIVES_LOGIC_MODULE: compiledModule },
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
