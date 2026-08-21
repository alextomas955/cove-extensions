// Turns each catalog entry's committed OpenAPI document into the TypeScript its UI imports, so the
// response types the UI reads are derived from the C# handler signatures rather than restated by hand.
// A hand-written wire interface type-checks whether or not it matches the server, and then every field
// reads undefined at runtime with nothing failing — which is the defect this generator removes at its
// source rather than guards against afterwards.
//
// Catalog-driven with no extension named anywhere: an entry declaring a uiPath is generated from, and
// one without is not. A second extension needs a catalog entry and no edit here. The document itself
// is at a fixed place inside the extension, so its location is derived rather than declared — a
// catalog field for it would restate the layout and give the C# emitter a second copy to drift from.
//
// The generated file is gitignored, because it is a deterministic function of a committed document and
// storing it would only create a staleness problem to guard. That makes regeneration a prerequisite of
// every job that builds the UI TypeScript program.
//
// One file carries both the importable API and the command line, behind the `import.meta.main` guard at
// the foot. The alternative — a separate entry point whose whole body is the call — is a second file to
// keep in step for no decision it gets to make.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows that
// yields a leading-slash form which resolves to a doubled drive prefix.
const scriptRoot = path.resolve(import.meta.dirname, "..");

// The flags are what make the output a set of top-level aliases (`export type LastBatchSummary = …`)
// instead of a nest under components['schemas'], so a call site imports the name the C# type has.
// Without them the repo would need a hand-written re-export module — a new hand-maintained wire
// surface, which is the thing being removed.
const GENERATOR_FLAGS = [
  "--root-types",
  "--root-types-no-schema-prefix",
  "--root-types-keep-casing",
];

const OUTPUT_SUBPATH = path.join("src", "wire", "api.ts");
const DOCUMENT_SUBPATH = path.join("wire", "openapi.json");

/**
 * The real document-to-types step.
 *
 * The package is loaded lazily, inside the call, and must never become a top-level import: a top-level
 * import makes this MODULE unloadable wherever the package is absent, so the tooling test's own import
 * would fail before it could inject a fake — and the catalog-validation job, which installs nothing and
 * runs every scripts/*.test.mjs, would go red on a missing package.
 */
async function generateWithOpenApiTypescript({ documentPath, outputPath, flags }) {
  const { default: openapiTS, astToString } = await import("openapi-typescript");
  const ast = await openapiTS(new URL(`file://${documentPath.split(path.sep).join("/")}`), {
    rootTypes: flags.includes("--root-types"),
    rootTypesNoSchemaPrefix: flags.includes("--root-types-no-schema-prefix"),
    rootTypesKeepCasing: flags.includes("--root-types-keep-casing"),
  });
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, astToString(ast));
}

// Built from its code point rather than written literally: a byte-order mark in source is invisible
// in every editor and is flagged as irregular whitespace by the lint rule that reads this file.
const BOM = new RegExp("^" + String.fromCodePoint(0xfeff));

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(BOM, ""));
}

/**
 * Generates the wire types for every catalog entry that declares both a wire document and a UI.
 *
 * @param {object} opts
 * @param {string} [opts.root] - the repo root holding `extensions/catalog.json`.
 * @param {string|null} [opts.extension] - restrict to one entry by `id` or `name`.
 * @param {(step: {documentPath: string, outputPath: string, flags: string[], entry: object}) => Promise<void>} [opts.generate]
 *        the document-to-types step; injected by the tooling test so the catalog logic is covered
 *        without the generator package, which is what keeps the catalog-validation job needing only node.
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

    // An absent document is a hard failure naming the entry and the path, never a skip: declaring a
    // UI is what makes the extension import a generated module, so a run that quietly generated
    // nothing leaves the next step failing somewhere with no cause attached.
    if (!fs.existsSync(documentPath)) {
      failures.push(
        `MISSING: ${entry.id} declares a uiPath, so ${entry.path}/wire/openapi.json must exist; it does not.`,
      );
      continue;
    }

    await generate({ documentPath, outputPath, flags: GENERATOR_FLAGS, entry });
    generated.push({ id: entry.id, documentPath, outputPath });
  }

  // Reports what it examined rather than only what it did, and says so explicitly when nothing
  // declared a document — a run that inspected zero input and exited 0 reads exactly like a run that
  // generated everything asked of it.
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

// import.meta.main arrived in Node 22.18 and 24.2, and is `undefined` before that — so a bare
// truthiness guard would make `node scripts/generate-wire-types.mjs` exit 0 having generated nothing.
// The root package.json's engines floor asks for 22.18, but npm only warns on a mismatch and a direct
// `node` run consults nothing, so an older runtime still reaches this line. Every job that builds a UI
// TypeScript program depends on this run having happened, and the failure would surface later as an
// unresolved import of a module nobody wrote. So the absent feature is refused by name rather than
// read as false.
if (import.meta.main === undefined) {
  throw new Error(
    "scripts/generate-wire-types.mjs requires import.meta.main (Node 22.18+ or 24.2+); " +
      `this is ${process.version}. Use the version pinned in the root package.json's volta field.`,
  );
}

if (import.meta.main) {
  process.exit(await main(process.argv.slice(2)));
}
