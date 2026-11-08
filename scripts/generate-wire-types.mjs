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

// Types, the fetch client, one function per operation, and the TanStack Query helpers over them.
// Everything the UI needs to reach a route comes from the document: the path, the method, the
// request body, the response, the error, and the query key.
//
// The client plugin takes a runtime config module from the consuming UI. That module is what binds
// the host's authenticated `extensionFetch` in, so nothing generated here ever calls `fetch`
// directly, where a request would carry no credential and answer 401.
const CLIENT_CONFIG_SUBPATH = "src/common/lib/wireClient.ts";

const generatorPlugins = (clientConfigPath) => [
  "@hey-api/typescript",
  { name: "@hey-api/client-fetch", runtimeConfigPath: clientConfigPath },
  "@hey-api/sdk",
  "@tanstack/react-query",
];

// Repo-relative and POSIX-spelled: path.join normalizes the separator, and the same string spells
// the message a reader has to match against the catalog.
//
// A directory, not a file: the generator owns everything inside it and empties it on every run.
const OUTPUT_SUBPATH = "src/wire";
// Written by the generator; named here so a run that produced neither fails by name.
const REQUIRED_OUTPUT = ["types.gen.ts", "sdk.gen.ts", "client.gen.ts"];
const DOCUMENT_SUBPATH = "wire/openapi.json";

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

  await createClient({
    input: documentPath,
    output: outputPath,
    plugins,
    logs: { level: "silent" },
  });

  // The emit reports success for a run that wrote nothing a call site can import.
  for (const required of REQUIRED_OUTPUT) {
    if (!fs.existsSync(path.join(outputPath, required))) {
      throw new Error(`the generator wrote no ${required} in ${outputPath}`);
    }
  }
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

    // Resolved against this entry's own UI, so the generated client binds that bundle's host fetch.
    const clientConfigPath = path.join(absoluteRoot, entry.uiPath, CLIENT_CONFIG_SUBPATH);
    if (!fs.existsSync(clientConfigPath)) {
      failures.push(
        `MISSING: ${entry.id} declares a uiPath, so ${entry.uiPath}/${CLIENT_CONFIG_SUBPATH} must exist; it does not. Without it the generated client calls fetch directly and every request answers 401.`,
      );
      continue;
    }

    await generate({
      documentPath,
      outputPath,
      plugins: generatorPlugins(clientConfigPath),
      entry,
    });

    generated.push({ id: entry.id, documentPath, outputPath });
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
