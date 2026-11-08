// Generates each catalog entry's UI wire types from the OpenAPI document its backend emits.
//
// Catalog-driven with no extension named here: an entry declaring a uiPath is generated from, one
// without is not. The document sits at a fixed place inside the extension, so only the extension's
// own directory has to be declared.
//
// The output is gitignored - it is a deterministic function of a committed document - which makes
// this run a prerequisite of every job that builds the UI TypeScript program.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { readJson } from "./repo-files.mjs";

const scriptRoot = path.resolve(import.meta.dirname, "..");

// Types and nothing else. The default plugin set also emits a client, an SDK and their serializers:
// runtime modules that would ship inside the bundle and call `fetch` directly, where every request
// has to go through the host's `extensionFetch` to carry a credential.
const GENERATOR_PLUGINS = ["@hey-api/typescript"];

// Repo-relative and POSIX-spelled: path.join normalizes the separator, and the same string spells
// the message a reader has to match against the catalog.
const OUTPUT_SUBPATH = "src/wire/api.ts";
const ROUTES_SUBPATH = "src/wire/routes.ts";
const DOCUMENT_SUBPATH = "wire/openapi.json";

// The request helpers prepend it, and `extensionFetch` refuses a path outside it, so a route that
// carried it would address `/api/api/...`.
const REQUEST_PREFIX = "/api";

/** The TypeScript a path parameter's declared schema binds to. */
function parameterType(schema) {
  return schema?.type === "integer" || schema?.type === "number" ? "number" : "string";
}

/**
 * Emits one callable per operation, keyed by the `operationId` the backend declares.
 *
 * A function rather than a string because three routes take a path parameter: naming and encoding it
 * here is what keeps the template in the one module generated from the document. An operation with
 * no `operationId` is skipped by name, because the alternative is a key derived from the method and
 * path that silently renames itself whenever a route moves.
 */
function routesModule(document) {
  const lines = [];
  const skipped = [];

  for (const [route, methods] of Object.entries(document.paths ?? {})) {
    for (const [method, operation] of Object.entries(methods)) {
      const id = operation?.operationId;
      if (!id) {
        skipped.push(`${method.toUpperCase()} ${route}`);
        continue;
      }

      const pathParams = (operation.parameters ?? []).filter((p) => p.in === "path");
      const signature = pathParams.length
        ? `p: { ${pathParams.map((p) => `${p.name}: ${parameterType(p.schema)}`).join("; ")} }`
        : "";

      let template = route.startsWith(REQUEST_PREFIX) ? route.slice(REQUEST_PREFIX.length) : route;
      for (const p of pathParams) {
        template = template.replace(`{${p.name}}`, `\${encodeURIComponent(String(p.${p.name}))}`);
      }

      lines.push(`  ${id}: (${signature}): string => \`${template}\`,`);
    }
  }

  return {
    skipped,
    source: [
      "// Generated from the committed OpenAPI document. Do not edit.",
      "//",
      "// One callable per operation, named by its operationId, so no call site writes a route out by",
      `// hand. The paths carry no ${REQUEST_PREFIX} prefix: the request helpers add it.`,
      "",
      "export const routes = {",
      ...lines,
      "} as const;",
      "",
    ].join("\n"),
  };
}

/**
 * The real document-to-types step.
 *
 * The package is imported lazily so this module stays loadable where it is absent: the tooling test
 * injects a fake in its place, and the catalog-validation job installs nothing yet runs every
 * scripts/*.test.mjs.
 *
 * The generator writes a directory it owns, and names the file inside it for itself. The output is
 * renamed to one module at a fixed path, so no call site imports a name the generator chose and
 * swapping generators again stays a change to this function.
 */
async function generateWithHeyApi({ documentPath, outputPath, plugins }) {
  const { createClient } = await import("@hey-api/openapi-ts");
  const outDir = path.dirname(outputPath);

  // Emptied by the generator on every run, so nothing else may live here and anything written
  // alongside the types has to be written after this call.
  await createClient({ input: documentPath, output: outDir, plugins, logs: { level: "silent" } });

  const emitted = path.join(outDir, "types.gen.ts");
  if (!fs.existsSync(emitted)) {
    throw new Error(`the generator wrote no ${path.basename(emitted)} in ${outDir}`);
  }
  fs.renameSync(emitted, outputPath);
  // Its re-export barrel names the file that just moved, so it resolves nothing.
  fs.rmSync(path.join(outDir, "index.ts"), { force: true });
}

/**
 * Generates the wire types for every catalog entry that declares a UI.
 *
 * @param {object} opts
 * @param {string} [opts.root] - the repo root holding `extensions/catalog.json`.
 * @param {string|null} [opts.extension] - restrict to one entry by `id` or `name`.
 * @param {(step: {documentPath: string, outputPath: string, plugins: string[], entry: object}) => Promise<void>} [opts.generate]
 *        the document-to-types step; injected by the tooling test so the catalog logic is covered
 *        without the generator package.
 * @param {(line: string) => void} [opts.log]
 * @returns {Promise<{ ok: boolean, failures: string[], generated: Array<{id: string, documentPath: string, outputPath: string}>, examined: number }>}
 */
export async function generateWireTypes({
  root = scriptRoot,
  extension = null,
  generate = generateWithHeyApi,
  log = console.log,
} = {}) {
  const failures = [];
  const generated = [];

  const absoluteRoot = path.resolve(root);
  const catalog = readJson(path.join(absoluteRoot, "extensions", "catalog.json"));
  const all = Array.isArray(catalog.extensions) ? catalog.extensions : [];

  const selected =
    extension == null
      ? all
      : all.filter((entry) => entry.id === extension || entry.name === extension);

  if (extension != null && selected.length === 0) {
    failures.push(`INVALID: no catalog entry matches id/name: ${extension}`);
    return { ok: false, failures, generated, examined: 0 };
  }

  const declaring = selected.filter((entry) => entry.uiPath);

  for (const entry of declaring) {
    const documentPath = path.join(absoluteRoot, entry.path, DOCUMENT_SUBPATH);
    const outputPath = path.join(absoluteRoot, entry.uiPath, OUTPUT_SUBPATH);

    // A hard failure, never a skip: declaring a UI is what makes the extension import a generated
    // module, so a run that generated nothing leaves the next step failing with no cause attached.
    if (!fs.existsSync(documentPath)) {
      failures.push(
        `MISSING: ${entry.id} declares a uiPath, so ${entry.path}/${DOCUMENT_SUBPATH} must exist; it does not.`,
      );
      continue;
    }

    await generate({ documentPath, outputPath, plugins: GENERATOR_PLUGINS, entry });

    // After the types, never before: the generator empties the directory it writes into.
    const routesPath = path.join(absoluteRoot, entry.uiPath, ROUTES_SUBPATH);
    const routes = routesModule(readJson(documentPath));
    fs.mkdirSync(path.dirname(routesPath), { recursive: true });
    fs.writeFileSync(routesPath, routes.source);

    // An unnamed operation is reachable only by a hand-written path, which is what this module
    // exists to remove, so it is named here rather than left to be noticed at the call site.
    for (const operation of routes.skipped) {
      failures.push(
        `UNNAMED: ${entry.id} declares ${operation} with no operationId, so no route is generated for it.`,
      );
    }

    generated.push({ id: entry.id, documentPath, outputPath, routesPath });
  }

  // Reports what it examined, not only what it did: a run that inspected zero input and exited 0
  // otherwise reads like a run that generated everything asked of it.
  log(
    `Examined ${selected.length} catalog entr${selected.length === 1 ? "y" : "ies"}; ` +
      (declaring.length === 0
        ? "no entry declares a uiPath, so nothing was generated."
        : `generated from ${generated.length} of ${declaring.length} declared wire document(s).`),
  );
  for (const item of generated) {
    log(`  ${item.id}: ${path.relative(absoluteRoot, item.outputPath)}`);
  }

  return { ok: failures.length === 0, failures, generated, examined: selected.length };
}

const FLAGS = new Set(["--extension", "--root"]);

function usage() {
  console.error("Usage: node scripts/generate-wire-types.mjs [--extension <id>] [--root <dir>]");
}

async function main(argv) {
  const options = new Map();
  for (let i = 0; i < argv.length; i += 2) {
    if (!FLAGS.has(argv[i]) || argv[i + 1] == null || options.has(argv[i])) {
      usage();
      return 1;
    }
    options.set(argv[i], argv[i + 1]);
  }

  const result = await generateWireTypes({
    root: options.get("--root") ? path.resolve(options.get("--root")) : scriptRoot,
    extension: options.get("--extension") ?? null,
  });

  if (!result.ok) {
    for (const failure of result.failures) console.error(failure);
    console.error("Wire type generation FAILED.");
    return 1;
  }

  return 0;
}

if (import.meta.main) {
  process.exit(await main(process.argv.slice(2)));
}
