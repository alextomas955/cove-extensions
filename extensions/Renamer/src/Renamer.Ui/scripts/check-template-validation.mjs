#!/usr/bin/env node
/**
 * Offline gate for templateUsesToken, cloned from check-primitives-logic.mjs: no new dependency,
 * no test framework. templateValidation.ts is not zero-import (it imports TOKENS from the .tsx
 * TokenLegend module), so unlike primitivesLogic.ts's single-file compile, this harness also lists
 * TokenLegend.tsx in the scratch tsconfig's files array and enables the jsx compiler option so tsc
 * can resolve that import. A single-file compile with the local TypeScript compiler yields a
 * runnable ESM module; the suite then runs under Node's built-in test runner. Two follow-up fixes
 * are required for that module to actually run under Node: tsc's "bundler" moduleResolution leaves
 * the TokenLegend import as a bare specifier with no `.js` extension (Node's ESM loader requires
 * one), and TokenLegend.js's JSX runtime import needs a resolvable node_modules from outside the
 * scratch dir — both are patched below rather than adding a full bundler dependency. The compiled
 * module's path is handed to the suite via a distinct env var (TEMPLATE_VALIDATION_MODULE) so the
 * two runners never collide. The scratch dir is removed on exit so a failed run never leaves a
 * stale compiled module behind.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const logicTs = path.join(uiRoot, "src", "templateValidation.ts");
const tokenLegendTsx = path.join(uiRoot, "src", "TokenLegend.tsx");
const testFile = path.join(here, "template-validation.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "rename-template-validation-"));

function run() {
  // templateValidation.ts imports TOKENS from the .tsx TokenLegend module, so both files are
  // listed as compile inputs and jsx is enabled — a scratch tsconfig with `types: []` keeps tsc
  // from pulling the project's ambient @types/* (node/react/babel) — which a bare single-file
  // compile would try to resolve and fail on — and skipLibCheck avoids type-checking those .d.ts
  // at all. moduleResolution bundler mirrors the project tsconfig.
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        jsx: "react-jsx",
        types: [],
        skipLibCheck: true,
        outDir,
      },
      files: [logicTs, tokenLegendTsx],
    }),
  );
  // Mark the scratch dir as ESM so Node loads the emitted .js as a module without a reparse warning.
  writeFileSync(path.join(outDir, "package.json"), JSON.stringify({ type: "module" }));
  const compile = spawnSync(process.execPath, [tsc, "--project", tsconfigPath], {
    stdio: "inherit",
  });
  if (compile.status !== 0) {
    console.error("check-template-validation: tsc compile FAILED");
    return compile.status ?? 1;
  }

  // tsc's "bundler" moduleResolution does not rewrite relative specifiers with a `.js`
  // extension, but Node's ESM loader requires one — rewrite the one bare specifier
  // templateValidation.js emits for its TokenLegend import.
  const compiledPath = path.join(outDir, "templateValidation.js");
  const compiledSource = readFileSync(compiledPath, "utf8");
  writeFileSync(
    compiledPath,
    compiledSource.replace('from "./TokenLegend"', 'from "./TokenLegend.js"'),
  );

  // TokenLegend.js's JSX runtime import (react/jsx-runtime) needs a resolvable node_modules;
  // the scratch dir sits outside uiRoot's own node_modules, so link one in.
  symlinkSync(path.join(uiRoot, "node_modules"), path.join(outDir, "node_modules"), "junction");

  const compiledModule = pathToFileURL(path.join(outDir, "templateValidation.js")).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, TEMPLATE_VALIDATION_MODULE: compiledModule },
  });
  return test.status ?? 1;
}

let code = 1;
try {
  code = run();
} finally {
  rmSync(outDir, { recursive: true, force: true });
}
process.exit(code);
