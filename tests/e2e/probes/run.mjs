// Runs the committed evidence probes against the e2e fixtures and writes one record per row, so an
// image bump is re-verified with one command rather than by hand.
//
// This lives beside `tests/`, never inside it: Playwright's config globs a project's test directory,
// so a probe placed there would be swept into a suite run, and the rows that reach a live third-party
// provider cannot run in CI at all.
//
// It is not part of `npm test`. Nothing here is a merge gate; a row's job is to RECORD what a
// running fixture does, including a refusal or an absence.
import { readdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import process from "node:process";

import { aggregateRequirements, startProbeContext } from "./lib/context.mjs";
import { buildRecord, redactRecord, writeRecord } from "./lib/record.mjs";

const ROWS_DIR = join(import.meta.dirname, "rows");

const USAGE = "Usage: run.mjs --out <dir> [--row <id>]... [--json] [--live]";

// ---- Pure helpers: no network, no disk, so a selection can be reasoned about before anything boots. ----

export function parseArguments(argv) {
  const ids = [];
  let out = null;
  let json = false;
  let live = false;

  for (let i = 0; i < argv.length; i += 1) {
    const argument = argv[i];
    if (argument === "--out" || argument === "--row") {
      const value = argv[i + 1];
      if (value === undefined) throw new Error(`${argument} needs an argument. ${USAGE}`);
      if (argument === "--out") out = value;
      else ids.push(value);
      i += 1;
    } else if (argument === "--json") {
      json = true;
    } else if (argument === "--live") {
      live = true;
    } else {
      throw new Error(`Unrecognised argument '${argument}'. ${USAGE}`);
    }
  }

  if (out === null) {
    throw new Error(
      `--out names the directory the records are written to and is required. ${USAGE}`,
    );
  }
  return { ids, out, json, live };
}

/**
 * The rows to run, in the order they were discovered, refusing an id that names none of them.
 *
 * An unknown id is refused rather than ignored: a typo would otherwise produce an empty run that
 * exits 0 and looks like a pass.
 */
export function selectRows(rows, ids) {
  if (ids.length === 0) return rows;
  const known = new Set(rows.map((row) => row.id));
  const missing = ids.filter((id) => !known.has(id));
  if (missing.length > 0) {
    throw new Error(
      `--row named ${missing.join(", ")}, which no row declares. Declared rows: ${[...known].join(", ") || "<none>"}.`,
    );
  }
  return rows.filter((row) => ids.includes(row.id));
}

/**
 * Why a row will not run, or null when it will.
 *
 * A live row reaches a service this project does not own, so it stays off unless the caller opts in
 * on the command line.
 */
export function skipReasonFor(row, { live }) {
  if (row.requires?.live === true && !live) {
    return `${row.id} reaches a live third-party provider; pass --live to opt in.`;
  }
  return null;
}

// ---- Everything below starts containers or writes files. ----

/**
 * Every row module in `rows/`, discovered by scanning the directory.
 *
 * There is no registry file to edit, so adding a row is adding one file.
 */
async function discoverRows() {
  const files = readdirSync(ROWS_DIR)
    .filter((name) => name.endsWith(".mjs"))
    .sort();
  const rows = [];
  for (const name of files) {
    const module = await import(pathToFileURL(join(ROWS_DIR, name)).href);
    const { row } = module;
    if (!row || typeof row.run !== "function" || typeof row.id !== "string") {
      throw new Error(
        `probes/rows/${name} must export a \`row\` of { id, label, requires, run }; got ${Object.keys(module).join(", ") || "<no exports>"}.`,
      );
    }
    rows.push(row);
  }
  if (rows.length === 0) {
    // Otherwise a run against an empty directory writes nothing and exits 0, which reads as a pass.
    throw new Error(`no row modules found in ${ROWS_DIR}.`);
  }
  return rows;
}

async function main(argv) {
  const { ids, out, json, live } = parseArguments(argv);
  const selected = selectRows(await discoverRows(), ids);

  const skipped = selected.filter((row) => skipReasonFor(row, { live }) !== null);
  const runnable = selected.filter((row) => skipReasonFor(row, { live }) === null);

  const records = [];
  for (const row of skipped) {
    records.push(
      buildRecord({
        id: row.id,
        label: row.label,
        requires: row.requires ?? {},
        builds: null,
        method: null,
        verdict: "skipped",
        skip: skipReasonFor(row, { live }),
      }),
    );
  }

  let failed = 0;
  if (runnable.length > 0) {
    const context = await startProbeContext(aggregateRequirements(runnable), { outDir: out });
    try {
      for (const row of runnable) {
        try {
          const outcome = await row.run(context);
          records.push(
            buildRecord({
              id: row.id,
              label: row.label,
              requires: row.requires ?? {},
              builds: context.builds,
              ...outcome,
            }),
          );
        } catch (cause) {
          failed += 1;
          records.push(
            buildRecord({
              id: row.id,
              label: row.label,
              requires: row.requires ?? {},
              builds: context.builds,
              method: null,
              verdict: "errored",
              observed: { error: cause.message },
            }),
          );
        }
      }
    } finally {
      await context.stop();
    }
  }

  for (const record of records) {
    console.error(`${record.id}: ${record.verdict} -> ${writeRecord(out, record)}`);
  }
  if (json) console.log(JSON.stringify(records.map(redactRecord), null, 2));

  return failed === 0 ? 0 : 1;
}

await main(process.argv.slice(2)).then(
  (code) => {
    process.exitCode = code;
  },
  (error) => {
    console.error(`probes: ${error.message}`);
    process.exitCode = 1;
  },
);
