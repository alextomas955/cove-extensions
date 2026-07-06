#!/usr/bin/env node
/**
 * Offline gate for templateUsesToken, cloned from check-primitives-logic.mjs: no new dependency,
 * no test framework. templateValidation.ts is not zero-import (it imports TOKENS from the .tsx
 * TokenLegend module, which since the Chip extraction also imports from primitives.tsx, which in
 * turn imports primitivesLogic.ts and entityPickerLogic.ts), so unlike primitivesLogic.ts's
 * single-file compile, this harness lists the whole local-import closure in the scratch
 * tsconfig's files array and enables the jsx compiler option so tsc can resolve it. A multi-file
 * compile with the local TypeScript compiler yields runnable ESM modules; the suite then runs
 * under Node's built-in test runner. Two follow-up fixes are required for those modules to
 * actually run under Node: tsc's "bundler" moduleResolution leaves relative imports as bare
 * specifiers with no `.js` extension (Node's ESM loader requires one), and any compiled module's
 * JSX runtime import needs a resolvable node_modules from outside the scratch dir — both are
 * patched below rather than adding a full bundler dependency. The compiled module's path is
 * handed to the suite via a distinct env var (TEMPLATE_VALIDATION_MODULE) so the two runners
 * never collide. The scratch dir is removed on exit so a failed run never leaves a stale compiled
 * module behind.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const logicTs = path.join(uiRoot, "src", "templateValidation.ts");
// templateValidation.ts's local-import closure: TokenLegend imports from primitives, which
// imports from primitivesLogic and entityPickerLogic. Both logic files are leaves (no further
// local imports) — confirmed by inspection, not assumed; re-check this list if that changes.
const closureFiles = [
  "TokenLegend.tsx",
  "primitives.tsx",
  "primitivesLogic.ts",
  "entityPickerLogic.ts",
].map((f) => path.join(uiRoot, "src", f));
const testFile = path.join(here, "template-validation.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "rename-template-validation-"));

function run() {
  // A scratch tsconfig with `types: []` keeps tsc from pulling the project's ambient @types/*
  // (node/react/babel) — which a bare multi-file compile would try to resolve and fail on — and
  // skipLibCheck avoids type-checking those .d.ts at all. moduleResolution bundler mirrors the
  // project tsconfig.
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        jsx: "react-jsx",
        rootDir: path.join(uiRoot, "src"),
        types: [],
        skipLibCheck: true,
        outDir,
      },
      files: [logicTs, ...closureFiles],
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
  // extension, but Node's ESM loader requires one — rewrite every bare relative specifier
  // (`from "./Foo"` or `from "../Foo"`) across every emitted .js file in the scratch dir.
  for (const emitted of readdirSync(outDir)) {
    if (!emitted.endsWith(".js")) continue;
    const emittedPath = path.join(outDir, emitted);
    const source = readFileSync(emittedPath, "utf8");
    const patched = source.replace(/from "(\.\.?\/[^"]+)"/g, (match, spec) =>
      spec.endsWith(".js") ? match : `from "${spec}.js"`,
    );
    if (patched !== source) writeFileSync(emittedPath, patched);
  }

  // Any compiled module's JSX runtime import (react/jsx-runtime) needs a resolvable
  // node_modules; the scratch dir sits outside uiRoot's own node_modules, so link one in.
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
