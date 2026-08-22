// Generates each catalog entry's UI wire types from the OpenAPI document its backend emits.
//
// Catalog-driven with no extension named here: an entry declaring a uiPath is generated from, one
// without is not. The document sits at a fixed place inside the extension, so only the extension's
// own directory has to be declared.
//
// The output is gitignored — it is a deterministic function of a committed document — which makes
// this run a prerequisite of every job that builds the UI TypeScript program.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const scriptRoot = path.resolve(import.meta.dirname, "..");

// These are what make the output top-level aliases (`export type LastBatchSummary = …`) rather than
// a nest under components['schemas'], so a call site imports the name the C# type has.
const GENERATOR_FLAGS = [
  "--root-types",
  "--root-types-no-schema-prefix",
  "--root-types-keep-casing",
];

// Repo-relative and POSIX-spelled: path.join normalizes the separator, and the same string spells
// the message a reader has to match against the catalog.
const OUTPUT_SUBPATH = "src/wire/api.ts";
const DOCUMENT_SUBPATH = "wire/openapi.json";

/**
 * The real document-to-types step.
 *
 * The package is imported lazily so this MODULE stays loadable where it is absent: the tooling test
 * injects a fake in its place, and the catalog-validation job installs nothing yet runs every
 * scripts/*.test.mjs.
 *
 * openapi-typescript declares `typescript: ^5.x` as a peer and this repo is on 6, so `npm install`
 * refuses the pair without the root package.json `overrides` entry that forces it. The combination is
 * deliberate and unsupported by the package: it drives the TypeScript compiler API, so a bump on
 * either side can break the emit for reasons the error will not name. 7.13.0 is the newest release
 * and still says ^5.x, so the override cannot be retired by upgrading — check that first when it does
 * break.
 */
async function generateWithOpenApiTypescript({ documentPath, outputPath, flags }) {
  const { default: openapiTS, astToString } = await import("openapi-typescript");
  const ast = await openapiTS(pathToFileURL(documentPath), {
    rootTypes: flags.includes("--root-types"),
    rootTypesNoSchemaPrefix: flags.includes("--root-types-no-schema-prefix"),
    rootTypesKeepCasing: flags.includes("--root-types-keep-casing"),
  });
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, astToString(ast));
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

/**
 * Generates the wire types for every catalog entry that declares a UI.
 *
 * @param {object} opts
 * @param {string} [opts.root] - the repo root holding `extensions/catalog.json`.
 * @param {string|null} [opts.extension] - restrict to one entry by `id` or `name`.
 * @param {(step: {documentPath: string, outputPath: string, flags: string[], entry: object}) => Promise<void>} [opts.generate]
 *        the document-to-types step; injected by the tooling test so the catalog logic is covered
 *        without the generator package.
 * @param {(line: string) => void} [opts.log]
 * @returns {Promise<{ ok: boolean, failures: string[], generated: Array<{id: string, documentPath: string, outputPath: string}>, examined: number }>}
 */
export async function generateWireTypes({
  root = scriptRoot,
  extension = null,
  generate = generateWithOpenApiTypescript,
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

    // A hard failure naming the entry and the path, never a skip: declaring a UI is what makes the
    // extension import a generated module, so a run that quietly generated nothing leaves the next
    // step failing somewhere with no cause attached.
    if (!fs.existsSync(documentPath)) {
      failures.push(
        `MISSING: ${entry.id} declares a uiPath, so ${entry.path}/${DOCUMENT_SUBPATH} must exist; it does not.`,
      );
      continue;
    }

    await generate({ documentPath, outputPath, flags: GENERATOR_FLAGS, entry });
    generated.push({ id: entry.id, documentPath, outputPath });
  }

  // Reports what it examined, not only what it did: a run that inspected zero input and exited 0
  // otherwise reads exactly like a run that generated everything asked of it.
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
