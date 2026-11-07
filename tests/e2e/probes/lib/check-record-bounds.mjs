// Asserts that a written probe record stays small enough to read, to transcribe and to keep.
//
// It is a file rather than an expression a caller inlines because an inline `node -e` on this
// machine can do nothing and still exit 0, which makes a check that passed and a check that never
// ran the same observation. So this prints one token on success and callers guard it with
// `| grep -q RECORD_BOUNDS_OK`: a run that printed nothing then fails.
//
// The token spelling is a contract. Callers grep for it verbatim.
//
// Nothing is exported: this is only ever run, and an export would invite an import that also ran it.
import { readFileSync } from "node:fs";
import process from "node:process";

const MAX_ELEMENTS = 25;
const SUCCESS_TOKEN = "RECORD_BOUNDS_OK";
const USAGE = "Usage: check-record-bounds.mjs <record.json> [<record.json> ...]";

/** RFC 6901 escaping, so a key holding a slash cannot forge a pointer segment. */
const escapeSegment = (segment) => String(segment).replaceAll("~", "~0").replaceAll("/", "~1");

/**
 * Every over-long array in `value`, as JSON pointers.
 *
 * Reports all of them rather than the first, so one run names every place a record grew instead of
 * naming one place per run.
 *
 * @returns {{pointer: string, length: number}[]}
 */
function findOversizedArrays(value, pointer = "") {
  const found = [];
  if (Array.isArray(value)) {
    if (value.length > MAX_ELEMENTS) found.push({ pointer: pointer || "/", length: value.length });
    value.forEach((held, index) => found.push(...findOversizedArrays(held, `${pointer}/${index}`)));
  } else if (value !== null && typeof value === "object") {
    for (const [key, held] of Object.entries(value)) {
      found.push(...findOversizedArrays(held, `${pointer}/${escapeSegment(key)}`));
    }
  }
  return found;
}

function main(argv) {
  if (argv.length === 0) {
    console.error(`check-record-bounds: name at least one record to check. ${USAGE}`);
    return 1;
  }

  const failures = [];
  for (const path of argv) {
    let document;
    try {
      document = JSON.parse(readFileSync(path, "utf8"));
    } catch (cause) {
      failures.push(`${path}: could not be read as JSON (${cause.message})`);
      continue;
    }
    for (const { pointer, length } of findOversizedArrays(document)) {
      failures.push(
        `${path}: ${pointer} holds ${length} elements, above the limit of ${MAX_ELEMENTS}`,
      );
    }
  }

  if (failures.length > 0) {
    for (const failure of failures) console.error(`check-record-bounds: ${failure}`);
    return 1;
  }
  // Nothing else reaches stdout, so the caller's grep is the whole pass condition.
  console.log(SUCCESS_TOKEN);
  return 0;
}

process.exitCode = main(process.argv.slice(2));
