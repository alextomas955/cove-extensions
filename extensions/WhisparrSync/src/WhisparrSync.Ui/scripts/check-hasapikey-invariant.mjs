#!/usr/bin/env node
/**
 * Offline logic gate for the client-model invariant, in the spirit of check-connection-result.mjs.
 * options.ts type-imports WhisparrVersion from connectionResult.ts, so both are compiled together in
 * isolation with the local TypeScript compiler; the suite then runs under `node --test` against the compiled
 * options module (path handed over via OPTIONS_MODULE). The scratch dir is removed on exit.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const optionsTs = path.join(uiRoot, "src", "options.ts");
const connectionTs = path.join(uiRoot, "src", "connectionResult.ts");
const testFile = path.join(here, "hasapikey-invariant.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "whisparr-hasapikey-"));

function run() {
  // Compile options.ts + connectionResult.ts together (options type-imports WhisparrVersion). `types: []`
  // keeps tsc from pulling ambient @types/*; skipLibCheck avoids checking those .d.ts; moduleResolution
  // bundler mirrors the project tsconfig.
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
      files: [optionsTs, connectionTs],
    }),
  );
  writeFileSync(path.join(outDir, "package.json"), JSON.stringify({ type: "module" }));
  const compile = spawnSync(process.execPath, [tsc, "--project", tsconfigPath], {
    stdio: "inherit",
  });
  if (compile.status !== 0) {
    console.error("check-hasapikey-invariant: tsc compile FAILED");
    return compile.status ?? 1;
  }

  const compiledModule = pathToFileURL(path.join(outDir, "options.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, OPTIONS_MODULE: compiledModule },
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
