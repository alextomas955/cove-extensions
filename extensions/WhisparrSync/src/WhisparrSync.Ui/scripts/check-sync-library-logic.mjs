#!/usr/bin/env node
/**
 * Offline logic gate for the sync-library copy/body logic, mirroring check-connection-availability-logic.mjs:
 * syncLibraryLogic.ts is a zero-import pure module, so a single-file tsc compile yields a runnable ESM
 * module and the suite runs under `node --test`. The compiled module path is handed to the suite via an env
 * var; the scratch dir is removed on exit.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const logicTs = path.join(uiRoot, "src", "syncLibraryLogic.ts");
const testFile = path.join(here, "sync-library-logic.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "whisparr-synclib-"));

function run() {
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        rootDir: path.join(uiRoot, "src"),
        types: [],
        skipLibCheck: true,
        outDir,
      },
      files: [logicTs],
    }),
  );
  writeFileSync(path.join(outDir, "package.json"), JSON.stringify({ type: "module" }));
  const compile = spawnSync(process.execPath, [tsc, "--project", tsconfigPath], {
    stdio: "inherit",
  });
  if (compile.status !== 0) {
    console.error("check-sync-library-logic: tsc compile FAILED");
    return compile.status ?? 1;
  }

  const compiledModule = pathToFileURL(path.join(outDir, "syncLibraryLogic.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, SYNC_LIBRARY_LOGIC_MODULE: compiledModule },
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
