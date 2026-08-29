// Drives the row's three skip branches — no install, an install naming another provider, and an
// entry carrying no key — against a temporary data root, so the branches a machine with the
// credential never takes are exercised anyway.
//
// The row returns before its first outbound call on every one of them, so nothing here reaches the
// network.
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { buildRecord, redactRecord } from "../lib/record.mjs";
import { row } from "./row-07-tpdb-rest-token.mjs";

// A drive-letter path, a UNC path, or a rooted POSIX path — the three shapes a data root arrives in.
const ABSOLUTE_PATH = /[A-Za-z]:[\\/]|\\\\[A-Za-z]|(?:^|[\s"'(])\/[\w.]/;

/** A data root holding `document`, or holding nothing at all when none is given. */
function dataRoot(document) {
  const root = mkdtempSync(join(tmpdir(), "row-07-"));
  if (document !== undefined) {
    writeFileSync(join(root, "cove-config.json"), JSON.stringify(document));
  }
  return root;
}

async function runAgainst(root) {
  const saved = process.env.COVE_HOME;
  process.env.COVE_HOME = root;
  try {
    return await row.run();
  } finally {
    if (saved === undefined) delete process.env.COVE_HOME;
    else process.env.COVE_HOME = saved;
  }
}

const withServers = (metadataServers) => ({ scraping: { metadataServers } });

const CASES = [
  { name: "no install at all", root: () => dataRoot() },
  {
    name: "an install naming another provider",
    root: () =>
      dataRoot(withServers([{ name: "stashdb", endpoint: "https://s/graphql", apiKey: "k" }])),
  },
  {
    name: "an entry carrying no key",
    root: () =>
      dataRoot(withServers([{ name: "ThePornDB", endpoint: "https://t/graphql", apiKey: "" }])),
  },
];

for (const { name, root } of CASES) {
  test(`${name} skips the row rather than failing it`, async () => {
    const outcome = await runAgainst(root());
    assert.equal(outcome.verdict, "skipped");
    assert.equal(outcome.method, null);
    assert.ok(outcome.skip.startsWith("ThePornDB: "), outcome.skip);
    assert.equal(outcome.observed.calls, 0);
  });

  test(`${name} reaches the record naming the file and not where it lives`, async () => {
    const where = root();
    const outcome = await runAgainst(where);
    const record = redactRecord(
      buildRecord({
        id: row.id,
        label: row.label,
        requires: row.requires,
        builds: null,
        ...outcome,
      }),
    );
    const written = JSON.stringify(record);
    assert.ok(record.skip.includes("cove-config.json"), record.skip);
    assert.ok(!written.includes(where), `the record carries the data root: ${record.skip}`);
    assert.doesNotMatch(record.skip, ABSOLUTE_PATH);
  });
}
