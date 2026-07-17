#!/usr/bin/env node
/**
 * Offline gate for templateUsesToken, cloned from check-primitives-logic.mjs: no new dependency,
 * no test framework. templateValidation.ts is not zero-import (it imports TOKENS from the .tsx
 * TokenLegend module, which imports the field primitives from the shared UI module `@cove-ext/ui-shared`,
 * whose primitives.tsx in turn imports primitivesLogic.ts and entityPickerLogic.ts). So unlike a
 * zero-import single-file compile, this harness lists the whole import closure — spanning this
 * package's `src/` and the shared module's `src/` — in the scratch tsconfig's files array, compiles
 * from the repo root so both trees emit under one out dir, and enables the jsx compiler option so tsc
 * can resolve it. Three follow-up fixes make those emitted modules runnable under Node: tsc's
 * "bundler" moduleResolution leaves relative imports as bare specifiers with no `.js` extension
 * (Node's ESM loader requires one); the `@cove-ext/ui-shared` alias is likewise not rewritten by tsc,
 * so it is repointed to the emitted barrel's relative path; and any compiled module's JSX-runtime /
 * react / lucide import needs a resolvable node_modules from outside the scratch dir. All three are
 * patched below rather than adding a full bundler dependency. The compiled module's path is handed to
 * the suite via TEMPLATE_VALIDATION_MODULE so the runners never collide. The scratch dir is removed on
 * exit so a failed run never leaves a stale compiled module behind.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, "..");
const repoRoot = path.resolve(uiRoot, "..", "..", "..", "..");
const sharedSrc = path.join(repoRoot, "shared", "cove-extensions-ui", "src");
const tsc = path.join(uiRoot, "node_modules", "typescript", "bin", "tsc");
const logicTs = path.join(uiRoot, "src", "templateValidation.ts");
// templateValidation.ts's import closure: TokenLegend imports the primitives from the shared UI
// module barrel, whose primitives.tsx imports primitivesLogic.ts and entityPickerLogic.ts. Those two
// logic files are leaves (no further imports) — confirmed by inspection, not assumed; re-check this
// list if that changes.
const closureFiles = [
  path.join(uiRoot, "src", "TokenLegend.tsx"),
  path.join(sharedSrc, "index.ts"),
  path.join(sharedSrc, "primitives.tsx"),
  path.join(sharedSrc, "primitivesLogic.ts"),
  path.join(sharedSrc, "entityPickerLogic.ts"),
];
const sharedBarrel = path.join(sharedSrc, "index.ts");
const testFile = path.join(here, "template-validation.test.mjs");

const outDir = mkdtempSync(path.join(tmpdir(), "rename-template-validation-"));

// Walk the scratch dir for emitted .js files (the closure spans nested subtrees under repoRoot).
function emittedJsFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    if (e.isDirectory()) return emittedJsFiles(path.join(dir, e.name));
    return e.name.endsWith(".js") ? [path.join(dir, e.name)] : [];
  });
}

function run() {
  // A scratch tsconfig with `types: []` keeps tsc from pulling the project's ambient @types/* — which
  // a bare multi-file compile would try to resolve and fail on — and skipLibCheck avoids type-checking
  // those .d.ts at all. The shared primitives live outside this package's node_modules, so the
  // externalized react/lucide specifiers are mapped to this package's installed copies for compile-
  // time resolution; at runtime they resolve through the node_modules junction linked in below.
  const tsconfigPath = path.join(outDir, "tsconfig.json");
  writeFileSync(
    tsconfigPath,
    JSON.stringify({
      compilerOptions: {
        target: "ESNext",
        module: "ESNext",
        moduleResolution: "bundler",
        jsx: "react-jsx",
        rootDir: repoRoot,
        types: [],
        skipLibCheck: true,
        outDir,
        paths: {
          "@cove-ext/ui-shared": [sharedBarrel],
          react: [path.join(uiRoot, "node_modules", "@types", "react")],
          "react/jsx-runtime": [path.join(uiRoot, "node_modules", "@types", "react", "jsx-runtime")],
          "react-dom": [path.join(uiRoot, "node_modules", "@types", "react-dom")],
          "lucide-react": [path.join(uiRoot, "node_modules", "lucide-react")],
        },
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

  // The barrel emits under rootDir (repoRoot) at shared/cove-extensions-ui/src/index.js.
  const emittedBarrel = path.join(outDir, "shared", "cove-extensions-ui", "src", "index.js");
  for (const emittedPath of emittedJsFiles(outDir)) {
    const source = readFileSync(emittedPath, "utf8");
    // tsc's "bundler" moduleResolution does not rewrite relative specifiers with a `.js` extension,
    // but Node's ESM loader requires one; and it leaves the `@cove-ext/ui-shared` alias untouched, so
    // repoint it at the emitted barrel via a relative path Node can resolve.
    const patched = source
      .replaceAll(/from "(\.\.?\/[^"]+)"/g, (match, spec) =>
        spec.endsWith(".js") ? match : `from "${spec}.js"`,
      )
      .replaceAll('from "@cove-ext/ui-shared"', () => {
        let rel = path.relative(path.dirname(emittedPath), emittedBarrel);
        if (!rel.startsWith(".")) rel = `./${rel}`;
        return `from "${rel}"`;
      });
    if (patched !== source) writeFileSync(emittedPath, patched);
  }

  // Any compiled module's react / react/jsx-runtime / lucide-react import needs a resolvable
  // node_modules; the scratch dir sits outside both source trees' node_modules, so link one in — Node
  // walks up from every nested emitted file to this junction.
  symlinkSync(path.join(uiRoot, "node_modules"), path.join(outDir, "node_modules"), "junction");

  const compiledModule = pathToFileURL(
    path.join(outDir, "extensions", "Renamer", "src", "Renamer.Ui", "src", "templateValidation.js"),
  ).href;
  const test = spawnSync(process.execPath, ["--test", testFile], {
    stdio: "inherit",
    env: { ...process.env, TEMPLATE_VALIDATION_MODULE: compiledModule },
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
