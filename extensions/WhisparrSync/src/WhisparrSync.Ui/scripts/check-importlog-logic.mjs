#!/usr/bin/env node
/**
 * Offline logic gate for the import-log UI logic, mirroring check-reconciliation-logic.mjs: no new
 * dependency, no test framework beyond Node's built-in runner. importLogLogic.ts is a pure module with zero
 * imports, so a single-file compile with the local TypeScript compiler yields a runnable ESM module; the
 * suite then runs under `node --test`. The compiled module's path is handed to the suite via an env var so
 * the test imports the exact artifact this runner produced. The scratch dir is removed on exit.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const logicTs = path.join(uiRoot, "src", "importLogLogic.ts");
const testFile = path.join(here, "importLogLogic.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "whisparr-importlog-"));

function run() {
  // importLogLogic.ts is a pure module with zero imports, so compile it in isolation. `types: []` keeps tsc
  // from pulling the project's ambient @types/* (node/react); skipLibCheck avoids checking those .d.ts;
  // moduleResolution bundler mirrors the project tsconfig.
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
    console.error("check-importlog-logic: tsc compile FAILED");
    return compile.status ?? 1;
  }

  const compiledModule = pathToFileURL(path.join(outDir, "importLogLogic.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, IMPORT_LOG_LOGIC_MODULE: compiledModule },
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
