// The probe runner's output layer, and the one place a value can be redacted before it is written.
//
// A record outlives the run that produced it: it is read later by a person and transcribed into a
// document. So a provider entry never reaches a file with its key on it, and it cannot reach one
// around this module either, because every value written goes through the redactor below.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { describeServers } from "../../lib/cove-providers.mjs";

// A record's id becomes a filename. Restricting it here means a row id can never reach into a
// directory the caller did not name.
const SAFE_ID = /^[a-z0-9][a-z0-9-]*$/;

// A companion's name is assembled from something a third party reported, so it is held to the same
// rule plus the dots and underscores a version string carries. No separator matches, so the name
// cannot leave the directory the caller named.
const SAFE_COMPANION_NAME = /^[a-z0-9][a-z0-9._-]*$/;

/**
 * Replaces every provider entry anywhere in `value` with its presence-and-length description.
 *
 * A provider entry is recognised by carrying a string `apiKey`, which is the shape a lifted
 * credential travels in, whether it came off the install, out of the placeholder set, or back from
 * the fixture Cove's own configuration surface.
 */
function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value !== null && typeof value === "object") {
    if (typeof value.apiKey === "string") return describeServers([value])[0];
    return Object.fromEntries(Object.entries(value).map(([key, held]) => [key, redact(held)]));
  }
  return value;
}

/**
 * Shapes one row's outcome into the record that gets written.
 *
 * `builds` carries what the run actually observed rather than what it configured, so a record read
 * a year from now says which images produced it.
 *
 * @param {{id: string, label: string, requires: object, builds: object, method: object,
 *          verdict: string, observed?: unknown, skip?: string}} outcome
 */
export function buildRecord({ id, label, requires, builds, method, verdict, observed, skip }) {
  return {
    id,
    label,
    recordedAt: new Date().toISOString(),
    builds,
    requires,
    method,
    verdict,
    ...(skip === undefined ? {} : { skip }),
    observed: observed ?? null,
  };
}

/**
 * Writes one record whole, into `outDir`, under its own id.
 *
 * One file per row and never a shared one, so an interrupted run leaves the records already written
 * intact, and a re-run replaces a record rather than appending to it.
 *
 * @returns {string} the path written
 */
export function writeRecord(outDir, record) {
  if (!SAFE_ID.test(record.id)) {
    throw new Error(
      `writeRecord: row id "${record.id}" is not a lowercase, hyphenated name, and it is used as a filename.`,
    );
  }
  mkdirSync(outDir, { recursive: true });
  const path = join(outDir, `${record.id}.json`);
  writeFileSync(path, `${JSON.stringify(redact(record), null, 2)}\n`);
  return path;
}

/**
 * Writes one file beside the records, for evidence too large to belong inside one.
 *
 * A companion is written verbatim: it is the artefact itself, kept so a later run can diff it, and
 * a row summarises it into its own record rather than embedding it.
 *
 * @returns {string} the path written
 */
export function writeCompanion(outDir, name, contents) {
  if (!SAFE_COMPANION_NAME.test(name) || name.includes("..")) {
    throw new Error(
      `writeCompanion: "${name}" is not a plain lowercase filename, and it is used as one.`,
    );
  }
  mkdirSync(outDir, { recursive: true });
  const path = join(outDir, name);
  writeFileSync(path, contents);
  return path;
}

/** The redacted form of a record, for a caller that prints rather than writes. */
export function redactRecord(record) {
  return redact(record);
}
