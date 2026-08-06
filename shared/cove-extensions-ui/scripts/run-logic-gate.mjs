#!/usr/bin/env node
/**
 * One parameterized offline logic gate, replacing the 25 near-identical check-*-logic.mjs copies.
 *
 * Each gate compiles a pure `*Logic.ts` module (or a small import closure) in isolation with the
 * consuming package's own TypeScript — no new dependency, no bundler — then runs its
 * `node --test` suite against the exact compiled artifact, handed over via a per-gate env var so
 * the migration touches zero `.test.mjs` files. The scratch out dir is removed on exit.
 *
 * Gates are declared as data in a per-package `scripts/gates.json` manifest; everything that used
 * to vary between the copies is either a manifest field or an absorbed comment.
 *
 * Usage:
 *   node run-logic-gate.mjs --manifest <gates.json> [--manifest <gates2.json>] [--only <name>]
 *   node run-logic-gate.mjs --module <ts>... --test <file> [--env VAR] [--entry <emitted.js>]
 *                           [--prefix <tmp->] [--jsx] [--alias name=@shared/x.ts]... [--link-node-modules]
 */
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const runnerDir = path.dirname(fileURLToPath(import.meta.url));
const sharedSrc = path.resolve(runnerDir, "..", "src");
const repoRoot = path.resolve(runnerDir, "..", "..", "..");

// The two UI packages that install TypeScript; a gate resolves tsc from its own package first, then
// falls back to these (the shared package installs no node_modules of its own).
const uiTscCandidates = [
  path.join(
    repoRoot,
    "extensions",
    "Renamer",
    "src",
    "Renamer.Ui",
    "node_modules",
    "typescript",
    "bin",
    "tsc",
  ),
];

/** `@shared/x` → the shared UI module's own src/x; anything else is relative to the package root. */
function resolveToken(token, pkgRoot) {
  if (token.startsWith("@shared/"))
    return path.join(sharedSrc, token.slice("@shared/".length));
  return path.resolve(pkgRoot, token);
}

/** Deepest directory that contains every module file (rootDir for the scratch compile). */
function commonAncestor(files) {
  const dirs = files.map((f) => path.dirname(f).split(path.sep));
  const min = Math.min(...dirs.map((d) => d.length));
  let i = 0;
  for (; i < min; i++) {
    if (!dirs.every((d) => d[i] === dirs[0][i])) break;
  }
  return dirs[0].slice(0, i).join(path.sep) || path.sep;
}

const jsExt = (p) => p.replace(/\.tsx?$/, ".js");

function findTsc(pkgRoot) {
  const own = path.join(pkgRoot, "node_modules", "typescript", "bin", "tsc");
  const found = [own, ...uiTscCandidates].find((p) => existsSync(p));
  if (!found)
    throw new Error(
      "no TypeScript compiler found in the package or either UI's node_modules",
    );
  return found;
}

function emittedJsFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory()
      ? emittedJsFiles(path.join(dir, e.name))
      : e.name.endsWith(".js")
        ? [path.join(dir, e.name)]
        : [],
  );
}

/** Run one gate; returns a process-style exit code (0 = pass). */
function runGate(gate, pkgRoot) {
  const modules = gate.modules.map((m) => resolveToken(m, pkgRoot));
  const testFile = path.resolve(pkgRoot, gate.test);
  const rootDir = commonAncestor(modules);
  const tsc = findTsc(pkgRoot);

  const entryModule =
    (gate.entry &&
      modules.find((m) => path.basename(jsExt(m)) === gate.entry)) ||
    modules[0];
  const envVar =
    gate.envVar ??
    `${gate.name.toUpperCase().replace(/[^A-Z0-9]+/g, "_")}_MODULE`;
  const prefix = gate.tmpPrefix ?? `${path.basename(pkgRoot)}-${gate.name}-`;

  const outDir = mkdtempSync(path.join(tmpdir(), prefix));
  try {
    const compilerOptions = {
      target: "ESNext",
      module: "ESNext",
      moduleResolution: "bundler",
      ...(gate.jsx ? { jsx: "react-jsx" } : {}),
      rootDir,
      types: [],
      skipLibCheck: true,
      outDir,
    };

    const paths = {};
    if (gate.aliases) {
      for (const [name, token] of Object.entries(gate.aliases)) {
        paths[name] = [resolveToken(token, pkgRoot)];
      }
    }
    if (gate.jsx) {
      // The shared primitives live outside this package's node_modules, so map the externalized
      // react/lucide specifiers to this package's installed copies for compile-time resolution; at
      // runtime they resolve through the node_modules junction linked in below.
      paths.react = [path.join(pkgRoot, "node_modules", "@types", "react")];
      paths["react/jsx-runtime"] = [
        path.join(pkgRoot, "node_modules", "@types", "react", "jsx-runtime"),
      ];
      paths["react-dom"] = [
        path.join(pkgRoot, "node_modules", "@types", "react-dom"),
      ];
      paths["react-dom/client"] = [
        path.join(pkgRoot, "node_modules", "@types", "react-dom", "client"),
      ];
      paths["lucide-react"] = [
        path.join(pkgRoot, "node_modules", "lucide-react"),
      ];
    }
    if (Object.keys(paths).length) compilerOptions.paths = paths;

    const tsconfigPath = path.join(outDir, "tsconfig.json");
    writeFileSync(
      tsconfigPath,
      JSON.stringify({ compilerOptions, files: modules }),
    );
    // Mark the scratch dir as ESM so Node loads the emitted .js as a module without a reparse warning.
    writeFileSync(
      path.join(outDir, "package.json"),
      JSON.stringify({ type: "module" }),
    );

    const compile = spawnSync(
      process.execPath,
      [tsc, "--project", tsconfigPath],
      { stdio: "inherit" },
    );
    if (compile.status !== 0) {
      console.error(`${gate.name}: tsc compile FAILED`);
      return compile.status ?? 1;
    }

    // Multi-module gates emit relative cross-module specifiers; tsc's "bundler" moduleResolution
    // leaves them extension-less, but Node's ESM loader requires `.js`, so patch every emitted
    // module. Alias gates additionally rewrite the aliased bare specifier to its emitted relative
    // target (tsc leaves the bare specifier untouched).
    const aliasTargets = gate.aliases
      ? Object.fromEntries(
          Object.entries(gate.aliases).map(([name, token]) => [
            name,
            path.join(
              outDir,
              jsExt(path.relative(rootDir, resolveToken(token, pkgRoot))),
            ),
          ]),
        )
      : {};
    for (const emittedPath of emittedJsFiles(outDir)) {
      const source = readFileSync(emittedPath, "utf8");
      let patched = source.replaceAll(
        /from "(\.\.?\/[^"]+)"/g,
        (match, spec) => (spec.endsWith(".js") ? match : `from "${spec}.js"`),
      );
      for (const [name, target] of Object.entries(aliasTargets)) {
        patched = patched.replaceAll(`from "${name}"`, () => {
          // path.relative yields the platform separator, and this value lands inside a JS string
          // literal in the emitted module — so on Windows every backslash is consumed as an escape
          // ("..\..\shared\..." parses as "......shared...") and Node rejects the collapsed
          // specifier with ERR_INVALID_MODULE_SPECIFIER. Module specifiers are "/"-separated on
          // every platform, so normalizing is correct rather than a Windows special case.
          let rel = path
            .relative(path.dirname(emittedPath), target)
            .split(path.sep)
            .join("/");
          if (!rel.startsWith(".")) rel = `./${rel}`;
          return `from "${rel}"`;
        });
      }
      if (patched !== source) writeFileSync(emittedPath, patched);
    }

    if (gate.linkNodeModules) {
      // Emitted modules' react / jsx-runtime / lucide imports need a resolvable node_modules; the
      // scratch dir sits outside both source trees, so junction one in — Node walks up to it.
      symlinkSync(
        path.join(pkgRoot, "node_modules"),
        path.join(outDir, "node_modules"),
        "junction",
      );
    }

    const compiledModule = pathToFileURL(
      path.join(outDir, jsExt(path.relative(rootDir, entryModule))),
    ).href;
    const test = spawnSync(process.execPath, ["--test", testFile], {
      stdio: "inherit",
      env: { ...process.env, [envVar]: compiledModule },
    });
    return test.status ?? 1;
  } finally {
    rmSync(outDir, { recursive: true, force: true });
  }
}

function parseArgs(argv) {
  const manifests = [];
  const modules = [];
  const aliases = {};
  let only = null;
  const direct = {
    test: null,
    envVar: undefined,
    entry: undefined,
    tmpPrefix: undefined,
    jsx: false,
    linkNodeModules: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--manifest") manifests.push(argv[++i]);
    else if (a === "--only") only = argv[++i];
    else if (a === "--module") modules.push(argv[++i]);
    else if (a === "--test") direct.test = argv[++i];
    else if (a === "--env") direct.envVar = argv[++i];
    else if (a === "--entry") direct.entry = argv[++i];
    else if (a === "--prefix") direct.tmpPrefix = argv[++i];
    else if (a === "--jsx") direct.jsx = true;
    else if (a === "--link-node-modules") direct.linkNodeModules = true;
    else if (a === "--alias") {
      const [name, ...rest] = argv[++i].split("=");
      aliases[name] = rest.join("=");
    } else throw new Error(`unknown argument: ${a}`);
  }
  return { manifests, only, modules, aliases, direct };
}

function main() {
  const { manifests, only, modules, aliases, direct } = parseArgs(
    process.argv.slice(2),
  );

  // Collect (gate, pkgRoot) pairs from every manifest, plus an optional direct-flag gate.
  const jobs = [];
  for (const manifestArg of manifests) {
    const manifestPath = path.resolve(process.cwd(), manifestArg);
    const pkgRoot = path.dirname(path.dirname(manifestPath));
    const { gates } = JSON.parse(readFileSync(manifestPath, "utf8"));
    for (const gate of gates) jobs.push({ gate, pkgRoot });
  }
  if (modules.length) {
    if (!direct.test) throw new Error("--module requires --test");
    jobs.push({
      pkgRoot: process.cwd(),
      gate: {
        name:
          direct.entry ??
          path.basename(direct.test).replace(/\.test\.mjs$/, ""),
        modules,
        test: direct.test,
        envVar: direct.envVar,
        entry: direct.entry,
        tmpPrefix: direct.tmpPrefix,
        jsx: direct.jsx,
        aliases: Object.keys(aliases).length ? aliases : undefined,
        linkNodeModules: direct.linkNodeModules,
      },
    });
  }

  const selected = only ? jobs.filter((j) => j.gate.name === only) : jobs;
  if (!selected.length) {
    console.error(
      only
        ? `run-logic-gate: no gate named "${only}"`
        : "run-logic-gate: no gates to run",
    );
    process.exit(1);
  }

  const failures = [];
  for (const { gate, pkgRoot } of selected) {
    console.log(`\n▶ logic gate: ${gate.name}`);
    const code = runGate(gate, pkgRoot);
    if (code !== 0) {
      console.error(`✖ logic gate FAILED: ${gate.name}`);
      failures.push(gate.name);
    }
  }

  if (failures.length) {
    console.error(`\nrun-logic-gate: FAILED gates → ${failures.join(", ")}`);
    process.exit(1);
  }
  console.log(
    `\nrun-logic-gate: OK (${selected.length} gate${selected.length === 1 ? "" : "s"})`,
  );
}

main();
